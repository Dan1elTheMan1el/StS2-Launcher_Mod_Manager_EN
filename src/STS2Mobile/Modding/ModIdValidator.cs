namespace STS2Mobile.Modding;

// Single source of truth for what counts as a usable mod id.
//
// The game keys mods by the manifest id STRING and imposes no charset rule —
// official Workshop items ship ids containing spaces (issue #65, "Aeonglass
// Feminization" pfid 3747661487), which the previous letter/digit/_/-/.
// whitelist rejected after a fully successful download. The launcher also uses
// the id as the folder name Mods/<id>/, so reject only what cannot be a single
// safe path segment; everything else (spaces, CJK, parentheses, ...) is legal.
public static class ModIdValidator
{
    public static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        // Leading/trailing whitespace survives into the folder name and makes
        // Mods/<id>/ visually indistinguishable from its trimmed sibling.
        if (id != id.Trim())
            return false;
        // "." and ".." are path segments, not mod ids — reject them so Mods/<id>
        // can never resolve to the Mods dir itself or its parent.
        if (id == "." || id == "..")
            return false;
        foreach (var c in id)
        {
            if (char.IsControl(c))
                return false;
            switch (c)
            {
                // '/' and '\' would let an id escape Mods/; the rest are the
                // Windows-invalid filename set, rejected on Android too so a
                // Mods/ tree copied off-device (MTP) keeps its folder names.
                case '/':
                case '\\':
                case ':':
                case '*':
                case '?':
                case '"':
                case '<':
                case '>':
                case '|':
                    return false;
            }
        }
        return true;
    }
}
