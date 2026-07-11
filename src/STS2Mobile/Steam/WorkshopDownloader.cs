using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace STS2Mobile.Steam;

// Downloads a single Steam Workshop item into a caller-supplied target directory.
//
// This intentionally does NOT reuse DepotDownloader: that class is a verified,
// game-critical pipeline whose behaviour must not change (issue #58 constraint).
// Its internals are also tied to game-download concerns that don't apply here —
// on-disk manifest delta caching keyed by depot, multi-depot iteration, PCK
// patching, and a fixed _gameDir target. A Workshop download is a single depot
// (appid == depotid == 2868840), a per-item manifest (hcontent_file) with no
// delta history, and a throwaway target directory. Forcing both through one code
// path would add branching and regression risk to the game path, so the small
// amount of shared chunk/verify logic is duplicated here instead (~150 LOC).
//
// Two content schemes are handled:
//   - hcontent_file != 0  → CDN depot download (the current Workshop scheme).
//   - file_url present     → legacy UGC direct HTTP download (older items).
public class WorkshopDownloader : IDisposable
{
    private const uint AppId = 2868840;
    private const int MaxRetries = 5;
    private const int MaxConcurrentDownloads = 8;
    private const string Branch = "public";

    private readonly SteamConnection _connection;
    private readonly Client _cdnClient;

    private IReadOnlyList<Server> _servers;
    private int _serverIndex;
    private readonly Dictionary<string, string> _cdnAuthTokens = new();

    public WorkshopDownloader(SteamConnection connection)
    {
        _connection = connection;
        _cdnClient = new Client(connection.Client);
    }

    // Downloads the item's content into targetDir (created/cleaned by the caller
    // or here). Progress, when supplied, is reported in bytes/files. The idle
    // timeout is suspended for the duration so a long download can't be dropped
    // mid-transfer.
    public async Task DownloadAsync(
        WorkshopItemDetails item,
        string targetDir,
        IProgress<DownloadProgress> progress = null,
        CancellationToken ct = default
    )
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));
        if (!item.HasContent)
            throw new InvalidOperationException(
                $"Workshop item {item.PublishedFileId} has no downloadable content "
                    + "(no hcontent_file and no file_url)."
            );

        Directory.CreateDirectory(targetDir);

        _connection.SuspendIdleTimeout();
        try
        {
            if (item.HContentFile != 0)
                await DownloadDepotAsync(item, targetDir, progress, ct).ConfigureAwait(false);
            else
                await DownloadLegacyHttpAsync(item, targetDir, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    // --- Depot (CDN) path -----------------------------------------------------

    private async Task DownloadDepotAsync(
        WorkshopItemDetails item,
        string targetDir,
        IProgress<DownloadProgress> progress,
        CancellationToken ct
    )
    {
        uint depotId = AppId;
        ulong manifestId = item.HContentFile;
        Log($"Item {item.PublishedFileId}: depot download (manifest {manifestId})");

        Log("Getting CDN servers...");
        var allServers = await ContentServerDirectoryService
            .LoadAsync(_connection.Configuration, ct)
            .ConfigureAwait(false);
        if (allServers == null || allServers.Count == 0)
            throw new Exception("No CDN servers available");

        _servers = allServers
            .Where(s => s.Type == "SteamCache" || s.Type == "CDN")
            .OrderBy(s => s.WeightedLoad)
            .ToList();
        if (_servers.Count == 0)
            _servers = allServers.ToList();

        var keyResult = await _connection.Apps.GetDepotDecryptionKey(depotId, AppId);
        if (keyResult.Result != EResult.OK)
            throw new Exception($"Failed to get depot key for {depotId}: {keyResult.Result}");
        var depotKey = keyResult.DepotKey;

        ulong manifestRequestCode = await _connection
            .Content.GetManifestRequestCode(depotId, AppId, manifestId, Branch)
            .ConfigureAwait(false);
        if (manifestRequestCode == 0)
            throw new Exception(
                $"Failed to get manifest request code for depot {depotId}. "
                    + "Ensure the account is subscribed to this item."
            );

        Log($"Downloading manifest for depot {depotId}...");
        DepotManifest manifest = null;
        for (int attempt = 0; attempt < MaxRetries && manifest == null; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var server = GetNextServer();
            try
            {
                manifest = await _cdnClient
                    .DownloadManifestAsync(
                        depotId,
                        manifestId,
                        manifestRequestCode,
                        server,
                        depotKey
                    )
                    .ConfigureAwait(false);
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var token = await GetCdnAuthToken(depotId, server).ConfigureAwait(false);
                if (token != null)
                {
                    manifest = await _cdnClient
                        .DownloadManifestAsync(
                            depotId,
                            manifestId,
                            manifestRequestCode,
                            server,
                            depotKey,
                            cdnAuthToken: token
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                Log($"Manifest download failed (attempt {attempt + 1}): {ex.Message}");
            }
        }

        if (manifest == null)
            throw new Exception(
                $"Failed to download manifest for depot {depotId} after {MaxRetries} attempts"
            );

        var files = manifest.Files.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToList();

        var prog = new DownloadProgress
        {
            TotalFiles = files.Count,
            TotalBytes = files.Sum(f => (long)f.TotalSize),
        };
        progress?.Report(prog);

        Log(
            $"Downloading {files.Count} files ({FormatSize(prog.TotalBytes)}) "
                + $"with {MaxConcurrentDownloads} threads..."
        );

        using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
        var tasks = new List<Task>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        try
                        {
                            await DownloadFileAsync(
                                    file,
                                    depotId,
                                    depotKey,
                                    targetDir,
                                    prog,
                                    progress,
                                    ct
                                )
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref prog.CompletedFiles);
                            progress?.Report(prog);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    },
                    ct
                )
            );
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Log($"Item {item.PublishedFileId}: depot download complete");
    }

    private async Task DownloadFileAsync(
        DepotManifest.FileData file,
        uint depotId,
        byte[] depotKey,
        string targetDir,
        DownloadProgress prog,
        IProgress<DownloadProgress> progress,
        CancellationToken ct
    )
    {
        var fileName = file.FileName.Replace('\\', '/');
        prog.CurrentFile = fileName;
        progress?.Report(prog);

        var filePath = Path.Combine(targetDir, fileName);
        var fileDir = Path.GetDirectoryName(filePath);
        if (fileDir != null)
            Directory.CreateDirectory(fileDir);

        // Write to a temp file, verify hash, then move into place so an interrupted
        // write is never mistaken for a complete file.
        var tempPath = filePath + ".downloading";

        using (var fs = File.Create(tempPath))
        {
            foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
            {
                ct.ThrowIfCancellationRequested();

                var buffer = new byte[chunk.UncompressedLength];
                int written = 0;

                for (int attempt = 0; attempt < MaxRetries; attempt++)
                {
                    var server = GetNextServer();
                    try
                    {
                        written = await _cdnClient
                            .DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKey)
                            .ConfigureAwait(false);

                        if (!VerifyChunkHash(buffer, written, chunk))
                        {
                            if (attempt < MaxRetries - 1)
                            {
                                Log($"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying...");
                                written = 0;
                                continue;
                            }
                            throw new Exception(
                                $"Chunk SHA-1 verification failed for {fileName} "
                                    + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                            );
                        }

                        break;
                    }
                    catch (SteamKitWebRequestException ex)
                        when (ex.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var token = await GetCdnAuthToken(depotId, server).ConfigureAwait(false);
                        if (token != null)
                        {
                            written = await _cdnClient
                                .DownloadDepotChunkAsync(
                                    depotId,
                                    chunk,
                                    server,
                                    buffer,
                                    depotKey,
                                    cdnAuthToken: token
                                )
                                .ConfigureAwait(false);

                            if (!VerifyChunkHash(buffer, written, chunk))
                            {
                                if (attempt < MaxRetries - 1)
                                {
                                    Log(
                                        $"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying..."
                                    );
                                    written = 0;
                                    continue;
                                }
                                throw new Exception(
                                    $"Chunk SHA-1 verification failed for {fileName} "
                                        + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                                );
                            }

                            break;
                        }
                    }
                    catch (Exception ex) when (attempt < MaxRetries - 1)
                    {
                        Log($"Chunk download failed (attempt {attempt + 1}): {ex.Message}");
                    }
                }

                if (written == 0 && chunk.UncompressedLength > 0)
                    throw new Exception(
                        $"Failed to download chunk for {fileName} after {MaxRetries} attempts"
                    );

                fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                fs.Write(buffer, 0, written);

                Interlocked.Add(ref prog.DownloadedBytes, written);
                progress?.Report(prog);
            }
        }

        if (!VerifyFileHash(tempPath, file))
        {
            File.Delete(tempPath);
            throw new Exception($"SHA-1 verification failed for {fileName} after download");
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    // --- Legacy UGC (direct HTTP) path ----------------------------------------

    private async Task DownloadLegacyHttpAsync(
        WorkshopItemDetails item,
        string targetDir,
        IProgress<DownloadProgress> progress,
        CancellationToken ct
    )
    {
        Log($"Item {item.PublishedFileId}: legacy HTTP download from {item.FileUrl}");

        var name = SanitizeFileName(item.FileName);
        if (string.IsNullOrEmpty(name))
            name = SanitizeFileName(Path.GetFileName(new Uri(item.FileUrl).LocalPath));
        if (string.IsNullOrEmpty(name))
            name = $"{item.PublishedFileId}.bin";

        var destPath = Path.Combine(targetDir, name);

        using var http = new HttpClient();
        using var resp = await http.GetAsync(
                item.FileUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            )
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? (long)item.FileSize;
        var prog = new DownloadProgress
        {
            TotalFiles = 1,
            TotalBytes = total,
            CurrentFile = name,
        };
        progress?.Report(prog);

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(destPath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Interlocked.Add(ref prog.DownloadedBytes, read);
                progress?.Report(prog);
            }
        }

        prog.CompletedFiles = 1;
        progress?.Report(prog);

        // A zip payload is expanded in place so downstream install logic finds the
        // mod tree (mod_manifest.json), matching how zips are distributed.
        if (LooksLikeZip(destPath))
        {
            Log("Legacy payload is a zip — extracting in place");
            ExtractZipInPlace(destPath, targetDir);
            TryDelete(destPath);
        }

        Log($"Item {item.PublishedFileId}: legacy HTTP download complete");
    }

    // --- shared helpers -------------------------------------------------------

    private Server GetNextServer()
    {
        var idx = Interlocked.Increment(ref _serverIndex);
        return _servers[((idx % _servers.Count) + _servers.Count) % _servers.Count];
    }

    private async Task<string> GetCdnAuthToken(uint depotId, Server server)
    {
        var key = $"{depotId}|{server.Host}";
        if (_cdnAuthTokens.TryGetValue(key, out var cached))
            return cached;

        var result = await _connection
            .Content.GetCDNAuthToken(AppId, depotId, server.Host)
            .ConfigureAwait(false);
        if (result.Result == EResult.OK)
        {
            _cdnAuthTokens[key] = result.Token;
            return result.Token;
        }
        return null;
    }

    private static bool VerifyChunkHash(byte[] buffer, int length, DepotManifest.ChunkData chunk)
    {
        if (chunk.ChunkID == null || chunk.ChunkID.Length == 0)
            return true;
        var hash = System.Security.Cryptography.SHA1.HashData(buffer.AsSpan(0, length));
        return hash.AsSpan().SequenceEqual(chunk.ChunkID);
    }

    private static bool VerifyFileHash(string path, DepotManifest.FileData file)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != (long)file.TotalSize)
                return false;
            using var fs = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA1.HashData(fs);
            return hash.AsSpan().SequenceEqual(file.FileHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.Length >= 2 && fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
        }
        catch
        {
            return false;
        }
    }

    // Extracts with Zip Slip protection — any entry resolving outside destRoot is
    // rejected. Mirrors ModImporter.SafeExtract's guarantee for downloaded zips.
    private static void ExtractZipInPlace(string zipPath, string destRoot)
    {
        var fullRoot = Path.GetFullPath(destRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
            var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal) && target != fullRoot)
                throw new InvalidOperationException("Zip entry escapes extraction root: " + target);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Replace('\\', '/');
        name = name.Substring(name.LastIndexOf('/') + 1);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static void Log(string msg) => PatchHelper.Log($"[Workshop] {msg}");

    public void Dispose()
    {
        _cdnClient?.Dispose();
    }
}
