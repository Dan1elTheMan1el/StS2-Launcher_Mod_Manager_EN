using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2Mobile.Modding;

// POCO matching the StS2 mod_manifest.json schema. Parsed directly by the launcher
// for UI; the game's own ModManager reads the same file independently at game start.
public class ModManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    // Minimum game version the mod declares support for. Display-only; absent in
    // older mods, which is harmless.
    [JsonPropertyName("min_game_version")]
    public string MinGameVersion { get; set; }

    [JsonPropertyName("has_pck")]
    public bool HasPck { get; set; }

    [JsonPropertyName("has_dll")]
    public bool HasDll { get; set; }

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = new();

    [JsonPropertyName("affects_gameplay")]
    public bool AffectsGameplay { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModManifest TryParse(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModManifest>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    // Locates a mod's manifest by the game's actual convention (verified against
    // real Workshop items + a decompile of MegaCrit.Sts2.Core.Modding.ModManager):
    // the game recurses a mod directory reading EVERY "*.json" and treats any file
    // that parses with a non-empty id as a manifest, then loads "<id>.dll"/"<id>.pck"
    // from that manifest's folder. There is no fixed "mod_manifest.json" name — that
    // was a launcher WIP-era assumption real mods never satisfied.
    //
    // Returns the first valid manifest found, searched breadth-first so a top-level
    // manifest wins over any nested one. Files without an id (e.g. mod_config.json)
    // naturally fail IsValid() and are skipped, matching the game.
    public static bool TryFindManifest(string dir, out ModManifest manifest, out string manifestPath)
    {
        manifest = null;
        manifestPath = null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;

        foreach (var path in EnumerateJsonBreadthFirst(dir))
        {
            var m = TryParse(path);
            if (m != null && m.IsValid())
            {
                manifest = m;
                manifestPath = path;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateJsonBreadthFirst(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch
            {
                files = System.Array.Empty<string>();
            }
            foreach (var f in files)
                yield return f;

            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch
            {
                subs = System.Array.Empty<string>();
            }
            foreach (var s in subs)
                queue.Enqueue(s);
        }
    }
}
