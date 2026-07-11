using System.Collections.Generic;

namespace STS2Mobile.Steam;

// Thin domain DTO over SteamKit2's PublishedFileDetails protobuf. Only the fields
// the launcher needs to browse, download, and sync a Workshop item are surfaced,
// so callers (WorkshopDownloader, the sync diff engine, the browser UI) don't take
// a dependency on the SteamKit2.Internal generated types.
public class WorkshopItemDetails
{
    public ulong PublishedFileId;
    public string Title;
    public string Description;

    // Full BBCode description (PublishedFileDetails.file_description), populated by
    // GetDetails. Description above may be the shorter QueryFiles summary; the
    // detail page prefers this when present.
    public string FullDescription;

    // Author's Steam account (creator). 0 when not returned.
    public ulong Creator;

    // Preferred download path: manifest id of the item's content. When non-zero,
    // download through the CDN depot pipeline (appid == depotid == 2868840).
    public ulong HContentFile;

    // Legacy UGC path: a direct HTTP url to the item's file. Only used when
    // HContentFile is 0 (older items uploaded before the depot-backed scheme).
    public string FileUrl;
    public string FileName;
    public ulong FileSize;

    public long TimeUpdated;
    public long TimeCreated;
    public string PreviewUrl;
    public List<string> Tags = new();

    // Community stats surfaced on the detail page. num_comments_public is only a
    // count — SteamKit2's PublishedFile service has no RPC for the comment bodies
    // themselves, so the detail page shows the count and links out to the web.
    public uint NumComments;
    public uint Views;
    public uint Favorited;

    // Dependency items (publishedfileid only) reported by GetDetails/QueryFiles
    // when includechildren/return_children is requested. Empty for items with no
    // Workshop dependencies.
    public List<ulong> Children = new();
    public float VoteScore;
    public uint Subscriptions;

    // Availability state — banned/hidden items must not be auto-downloaded.
    public bool Banned;
    public string BanReason;

    // 0 = public, 1 = friends only, 2 = private, 3 = unlisted.
    public uint Visibility;

    // True when the item exposes downloadable content by either scheme.
    public bool HasContent => HContentFile != 0 || !string.IsNullOrEmpty(FileUrl);
}

// One dated entry in a Workshop item's change history ("업데이트 노트"), from
// PublishedFile.GetChangeHistory. Description is BBCode as authored on Steam.
public class WorkshopChangeEntry
{
    public long Timestamp; // unix seconds
    public string Description;
}
