using Godot;

namespace STS2Mobile.Launcher.Components;

// Minimal locale-aware string picker (issue #58). The launcher had messages in a
// mix of Korean and English; this routes user-facing copy through one call that
// returns Korean when the device language is Korean, English otherwise. Button
// labels and proper nouns (SUBSCRIBE, WORKSHOP, mod ids…) stay English by design —
// only sentences/notifications are localized.
public static class Loc
{
    private static bool? _isKo;

    public static bool IsKo
    {
        get
        {
            if (_isKo == null)
            {
                try
                {
                    var lang = OS.GetLocaleLanguage();
                    _isKo = !string.IsNullOrEmpty(lang) && lang.StartsWith("ko");
                }
                catch
                {
                    _isKo = false;
                }
            }
            return _isKo.Value;
        }
    }

    public static string Tr(string ko, string en) => IsKo ? ko : en;
}
