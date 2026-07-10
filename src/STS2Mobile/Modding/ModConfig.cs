using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2Mobile.Modding;

// User-editable enable/order state for installed mods. Persisted alongside the
// mods themselves at AppPaths.ExternalModConfigFile so it survives app reinstalls
// (same storage as the Mods/ folder).
// v2 (issue #58): entries carry workshop provenance (source/publishedFileId/
// timeUpdated) so Steam Workshop subscriptions can be distinguished from
// manually installed mods. v1 files load as-is — missing fields mean "local".
public class ModConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("mods")]
    public List<ModConfigEntry> Mods { get; set; } = new();

    // Workshop items whose manifest id is already installed from another source
    // (a manual install, possibly in a differently-named folder). Remembered by
    // published-file id + the item's time_updated so the sync engine stops
    // re-downloading a subscribed item every visit just because it can't be
    // committed (issue #58). Omitted from the file when empty.
    private List<WorkshopConflictEntry> _conflicts = new();

    [JsonPropertyName("workshopConflicts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkshopConflictEntry> WorkshopConflicts
    {
        get => _conflicts.Count == 0 ? null : _conflicts;
        set => _conflicts = value ?? new List<WorkshopConflictEntry>();
    }

    [JsonIgnore]
    public IReadOnlyList<WorkshopConflictEntry> ConflictRecords => _conflicts;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ModConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ExternalModConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ExternalModConfigFile);
                var cfg = JsonSerializer.Deserialize<ModConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Mods ??= new List<ModConfigEntry>();
                    // v1 -> v2 migration is purely additive; stamp the current
                    // version so the next Save writes the v2 shape.
                    cfg.Version = 2;
                    return cfg;
                }
            }
        }
        catch { }
        return new ModConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            var json = JsonSerializer.Serialize(this, Options);
            // Atomic write: a WriteAllText interrupted by app death truncates the
            // registry, which Load() would then treat as "no config" — wiping all
            // workshop provenance. Write to a temp file and rename over instead.
            var tmp = AppPaths.ExternalModConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ExternalModConfigFile, overwrite: true);
        }
        catch (System.Exception ex)
        {
            PatchHelper.Log($"[Mods] Failed to save mod_config.json: {ex.Message}");
        }
    }

    // Reconciles the on-disk scan (BOTH roots — Mods/ and ModsDisabled/) with the
    // saved config. Disk is the source of truth (issue #58): entries whose folder
    // exists in neither root are dropped, newly discovered mods are appended, and
    // each entry's Disabled flag is (re)derived from which root its folder is in.
    // Returns the reconciled (and saved) entries sorted by Order.
    public List<ModConfigEntry> Reconcile(IEnumerable<ModEntryInfo> scanned)
    {
        var scanById = new Dictionary<string, ModEntryInfo>(System.StringComparer.Ordinal);
        foreach (var s in scanned)
        {
            if (s?.Id != null && !scanById.ContainsKey(s.Id))
                scanById[s.Id] = s;
        }

        Mods = Mods.Where(m => m.Id != null && scanById.ContainsKey(m.Id)).ToList();

        var known = new HashSet<string>(Mods.Select(m => m.Id));
        var nextOrder = Mods.Count == 0 ? 0 : Mods.Max(m => m.Order) + 1;
        foreach (var id in scanById.Keys)
        {
            if (known.Add(id))
                Mods.Add(
                    new ModConfigEntry
                    {
                        Id = id,
                        Enabled = true,
                        Order = nextOrder++,
                    }
                );
        }

        foreach (var m in Mods)
            m.Disabled = scanById[m.Id].Disabled;

        Mods = Mods.OrderBy(m => m.Order).ToList();
        for (int i = 0; i < Mods.Count; i++)
            Mods[i].Order = i;

        Save();
        return Mods;
    }

    public ModConfigEntry Get(string id) => Mods.FirstOrDefault(m => m.Id == id);

    public void Add(string id, bool enabled)
    {
        var existing = Get(id);
        if (existing != null)
        {
            existing.Enabled = enabled;
            return;
        }
        var nextOrder = Mods.Count == 0 ? 0 : Mods.Max(m => m.Order) + 1;
        Mods.Add(
            new ModConfigEntry
            {
                Id = id,
                Enabled = enabled,
                Order = nextOrder,
            }
        );
    }

    public void Remove(string id) => Mods.RemoveAll(m => m.Id == id);

    // Records that a subscribed Workshop item (pfid) resolves to a mod id that is
    // already installed from another source, so the sync engine can stop retrying
    // it until the item is updated on Steam (time_updated advances).
    public void RememberConflict(ulong pfid, string modId, long timeUpdated, string workshopVersion)
    {
        if (pfid == 0)
            return;
        var e = _conflicts.FirstOrDefault(c => c.PublishedFileId == pfid);
        if (e == null)
        {
            e = new WorkshopConflictEntry { PublishedFileId = pfid };
            _conflicts.Add(e);
        }
        e.ModId = modId;
        e.TimeUpdated = timeUpdated;
        e.WorkshopVersion = workshopVersion;
    }

    public void ClearConflict(ulong pfid) => _conflicts.RemoveAll(c => c.PublishedFileId == pfid);

    public void Move(string id, int delta)
    {
        Mods = Mods.OrderBy(m => m.Order).ToList();
        var idx = Mods.FindIndex(m => m.Id == id);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= Mods.Count)
            return;
        (Mods[idx], Mods[target]) = (Mods[target], Mods[idx]);
        for (int i = 0; i < Mods.Count; i++)
            Mods[i].Order = i;
    }
}

public class ModConfigEntry
{
    public const string SourceWorkshop = "workshop";

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    // v2 (issue #58): null/absent means a manually installed ("local") mod, so
    // local entries keep the compact v1 shape on disk.
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Source { get; set; }

    // Steam Workshop published file id; join key against the user's
    // subscription list (GetUserFiles) during sync.
    [JsonPropertyName("publishedFileId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong PublishedFileId { get; set; }

    // Workshop item's time_updated (unix seconds) at download time; a newer
    // value on Steam means the item needs a re-download.
    [JsonPropertyName("timeUpdated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TimeUpdated { get; set; }

    // DERIVED from disk on every Reconcile — true when the mod's folder currently
    // lives under ModsDisabled/ (the stash). Never authoritative: if this and the
    // folder location disagree, the folder wins and this gets rewritten.
    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }

    [JsonIgnore]
    public bool IsWorkshop => Source == SourceWorkshop;
}

// A subscribed Workshop item whose mod id collides with an already-installed mod
// from another source. Kept separate from Mods (which is keyed by id — the id is
// taken by the existing install) so the sync engine can remember the collision by
// published-file id and avoid re-downloading it every visit.
public class WorkshopConflictEntry
{
    [JsonPropertyName("publishedFileId")]
    public ulong PublishedFileId { get; set; }

    [JsonPropertyName("id")]
    public string ModId { get; set; }

    [JsonPropertyName("timeUpdated")]
    public long TimeUpdated { get; set; }

    // The Workshop copy's manifest version, captured at download time. Compared
    // against the installed (manual) copy's version so version drift between the
    // two is visible and resolvable, instead of the manual copy silently going
    // stale forever.
    [JsonPropertyName("workshopVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string WorkshopVersion { get; set; }
}
