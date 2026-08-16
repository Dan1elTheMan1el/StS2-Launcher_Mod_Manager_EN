using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using STS2Mobile.Patches;

namespace STS2Mobile.Launcher;

// Records branch/buildId/commit/version of the most recently downloaded game
// payload. Written by LauncherModel after a successful download. Kept around
// for diagnostics and future mismatch-detection use cases — the true
// root cause of issue #5 was .NET assembly synchronization (GodotApp.setupAssemblies), so
// stamp comparison at PLAY time was removed in v0.3.18. Kept only as metadata.
public class CacheStamp
{
    [JsonPropertyName("branch")]
    public string Branch { get; set; }

    [JsonPropertyName("buildId")]
    public string BuildId { get; set; }

    [JsonPropertyName("commit")]
    public string Commit { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("recordedAt")]
    public string RecordedAt { get; set; }

    private static string StampPath => Path.Combine(OS.GetDataDir(), ".cache_stamp");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CacheStamp Read()
    {
        try
        {
            if (!File.Exists(StampPath))
                return null;
            var text = File.ReadAllText(StampPath);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return JsonSerializer.Deserialize<CacheStamp>(text, SerializerOptions);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[CacheStamp] Read failed: {ex.Message}");
            return null;
        }
    }

    public void Write()
    {
        try
        {
            RecordedAt = DateTime.UtcNow.ToString("o");
            var text = JsonSerializer.Serialize(this, SerializerOptions);
            // Write to .tmp first, then replace, so a process kill mid-write
            // can't leave a half-truncated stamp that fails to deserialize.
            var tmpPath = StampPath + ".tmp";
            File.WriteAllText(tmpPath, text);
            if (File.Exists(StampPath))
                File.Delete(StampPath);
            File.Move(tmpPath, StampPath);
            PatchHelper.Log(
                $"[CacheStamp] Wrote stamp: branch={Branch} buildId={BuildId} commit={Commit} version={Version}"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[CacheStamp] Write failed: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(StampPath))
            {
                File.Delete(StampPath);
                PatchHelper.Log("[CacheStamp] Stamp deleted");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[CacheStamp] Delete failed: {ex.Message}");
        }
    }
}
