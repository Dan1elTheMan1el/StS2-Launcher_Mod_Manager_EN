using System;
using System.Text.Json;

namespace STS2Mobile.Steam;

// issue #59 — Steam refresh tokens are JWTs (header.payload.signature, base64url,
// unpadded). Decoding the payload's `exp` claim locally lets the launcher warn
// before a stale saved token actually fails a real login, with zero network cost
// and no new dependency (System.Text.Json, already used throughout this project,
// is enough — no JWT library needed).
//
// Every entry point here fails OPEN: a token whose format doesn't match what we
// expect (Valve changes it, a legacy/non-JWT token, corrupt data) must never be
// treated as "expired" — that would force a re-login regression for something we
// simply couldn't parse. Only a successfully-decoded `exp` in the past counts.
public static class RefreshTokenExpiry
{
    private const string Tag = "[Issue59]";

    // Attempts to decode the `exp` (Unix seconds) claim from a JWT's payload
    // segment. Returns false — never throws — for anything that isn't a
    // parseable 3-part JWT with a numeric `exp` claim.
    public static bool TryGetExpiry(string jwt, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrEmpty(jwt))
            return false;

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                return false;

            // base64url → base64: swap the two substituted characters back and
            // restore the '=' padding base64url omits.
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            var bytes = Convert.FromBase64String(padded);

            using var doc = JsonDocument.Parse(bytes);
            if (
                !doc.RootElement.TryGetProperty("exp", out var expElement)
                || expElement.ValueKind != JsonValueKind.Number
                || !expElement.TryGetInt64(out var expUnixSeconds)
            )
                return false;

            expiry = DateTimeOffset.FromUnixTimeSeconds(expUnixSeconds);
            return true;
        }
        catch (Exception ex)
        {
            // Never log the token itself — only that parsing didn't work.
            PatchHelper.Log($"{Tag} RefreshTokenExpiry: parse failed: {ex.Message}");
            return false;
        }
    }

    // True only when the token's exp claim was successfully decoded AND is
    // already in the past. Unparseable → false (fail-open, see class doc).
    public static bool IsExpired(string jwt)
    {
        return TryGetExpiry(jwt, out var expiry) && expiry <= DateTimeOffset.UtcNow;
    }

    // True only when the token's exp claim was successfully decoded AND falls
    // within the next `withinDays` days (but isn't already in the past —
    // callers that also care about the already-expired case should check
    // IsExpired separately, see LauncherModel.StartSession). Unparseable →
    // false.
    public static bool IsExpiringSoon(string jwt, int withinDays)
    {
        if (!TryGetExpiry(jwt, out var expiry))
            return false;

        var now = DateTimeOffset.UtcNow;
        return expiry > now && expiry <= now.AddDays(withinDays);
    }
}
