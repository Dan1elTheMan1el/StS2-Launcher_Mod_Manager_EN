#!/usr/bin/env python3
"""
notify_discord.py

Posts a Discord webhook message pointing at a translated pre-release,
telling you to pull, build, and attach the APK. Reads everything from
environment variables so the workflow never has to interpolate untrusted
release text (title/notes) directly into a shell command.

Required env vars:
    DISCORD_WEBHOOK_URL   Discord webhook URL (repo secret)
    RELEASE_URL           HTML URL of the translated GitHub release
    RELEASE_TAG           Tag of the translated release (e.g. v1.2.3-en)
    SOURCE_TAG            Tag of the original (untranslated) release

Exits 0 even on failure to notify — a missing/broken webhook shouldn't
fail the whole workflow run, it just means you won't get pinged.
"""
import json
import os
import sys
import urllib.request
import urllib.error


def main() -> int:
    webhook_url = os.environ.get("DISCORD_WEBHOOK_URL", "")
    if not webhook_url:
        print("DISCORD_WEBHOOK_URL not set, skipping Discord notification.", file=sys.stderr)
        return 0

    release_url = os.environ.get("RELEASE_URL", "")
    release_tag = os.environ.get("RELEASE_TAG", "")
    source_tag = os.environ.get("SOURCE_TAG", "")

    content = (
        f"**Translated release ready: `{release_tag}`** (from `{source_tag}`)\n"
        f"{release_url}\n\n"
        f"Pull the `translate/{source_tag}` branch, build, and attach the APK. "
        f"You can do all of that in one step with:\n"
        f"```\npython3 scripts/finish_release.py {source_tag}\n```"
    )

    body = json.dumps({"content": content}).encode("utf-8")
    req = urllib.request.Request(
        webhook_url, data=body, headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            resp.read()
        print("Discord notification sent.")
    except urllib.error.HTTPError as e:
        print(f"! Discord webhook failed: HTTP {e.code} {e.read().decode(errors='replace')}", file=sys.stderr)
    except Exception as e:  # noqa: BLE001
        print(f"! Discord webhook failed: {e}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())