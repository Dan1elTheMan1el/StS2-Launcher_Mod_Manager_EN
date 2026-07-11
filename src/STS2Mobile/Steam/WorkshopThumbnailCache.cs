using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STS2Mobile.Steam;

// Downloads and caches Workshop item preview images to internal app storage
// (AppPaths.ThumbCacheDir). No Godot dependency — this class only guarantees a
// local file path exists for a given preview url; callers (UI code) own loading
// that path into a texture.
public static class WorkshopThumbnailCache
{
    private static readonly HttpClient _http = new();
    private static readonly object _locksGate = new();
    private static readonly Dictionary<string, SemaphoreSlim> _locks = new();

    // Returns the local file path for previewUrl, downloading it first if not
    // already cached. Returns null (after logging once) on any failure — callers
    // should treat that as "no thumbnail available", not a hard error.
    public static async Task<string> GetOrDownloadAsync(
        string previewUrl,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(previewUrl))
            return null;

        string path;
        try
        {
            path = BuildCachePath(previewUrl);
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Workshop] Thumbnail cache path build failed for '{previewUrl}': {ex.Message}"
            );
            return null;
        }

        if (File.Exists(path))
            return path;

        var gate = GetLock(previewUrl);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // May have been filled by a concurrent caller while we were waiting.
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tempPath = path + ".downloading";

            using (
                var resp = await _http
                    .GetAsync(previewUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false)
            )
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp
                    .Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                await using var dst = File.Create(tempPath);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Workshop] Thumbnail download failed for '{previewUrl}': {ex.Message}"
            );
            TryDelete(path + ".downloading");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildCachePath(string previewUrl)
    {
        var hash = Convert
            .ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(previewUrl)))
            .ToLowerInvariant();

        string ext = ".img";
        try
        {
            var uriPath = new Uri(previewUrl).AbsolutePath;
            var candidate = Path.GetExtension(uriPath);
            // Sanity-bound the extension (".jpeg" == 5 chars) so a malformed url
            // segment can't be smuggled into the filename.
            if (!string.IsNullOrEmpty(candidate) && candidate.Length <= 5)
                ext = candidate;
        }
        catch
        {
            // Malformed url — fall back to the default extension.
        }

        return Path.Combine(AppPaths.ThumbCacheDir, hash + ext);
    }

    private static SemaphoreSlim GetLock(string key)
    {
        lock (_locksGate)
        {
            if (!_locks.TryGetValue(key, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[key] = sem;
            }
            return sem;
        }
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
}
