namespace STS2Mobile.Modding;

// One mod discovered on disk: the directory containing its manifest, the top-level
// Mods/ folder it lives under, its parsed manifest, and an optional README snippet.
// Path and TopLevelDir differ when a mod ships inside a wrapping folder — the game
// finds the manifest by recursion, so folder name is not the id (issue #58).
public class ModEntryInfo
{
    // Directory that holds the manifest file (may be nested under TopLevelDir).
    public string Path { get; set; }

    // The direct child of AppPaths.ExternalModsDir this mod lives under. This is the
    // unit of deletion — folder name is NOT assumed to equal the id.
    public string TopLevelDir { get; set; }

    public ModManifest Manifest { get; set; }
    public string ReadmeSnippet { get; set; }

    public string Id => Manifest?.Id;
}
