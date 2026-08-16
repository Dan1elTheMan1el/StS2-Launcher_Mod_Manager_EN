#!/usr/bin/env python3
"""
translate_korean.py

Walks a repository, finds text files that still contain Korean (Hangul)
text, and translates that text to English using Google's Gemini API
(free tier — see https://ai.google.dev/pricing for current limits).

Code structure, identifiers, string formatting, and non-Korean text are
left untouched — only human-readable Korean is translated. This is meant
to run in CI on every new release, so it only rewrites files that still
contain Korean; already-translated files are left alone on the next run.

Files that already call a Loc.Tr(korean, english) style localization
helper are skipped entirely — the Korean text there is a lookup key
paired with an existing English value, not untranslated content.

Usage:
    python3 translate_korean.py --repo-root . --api-key $GEMINI_API_KEY

Exit code is 0 even if nothing needed translating, so it's safe to call
unconditionally from a workflow. It exits non-zero only on a hard
API/config failure.
"""
from __future__ import annotations

import argparse
import fnmatch
import os
import re
import sys
import time
import json
import random
import urllib.request
import urllib.error

# --- Configuration ---------------------------------------------------------

# Any of the Hangul unicode blocks below being present is our trigger for
# "this file needs translation".
HANGUL_RE = re.compile(r"[\uac00-\ud7a3\u1100-\u11ff\u3130-\u318f]")

# Files that already call a Loc.Tr(korean, english) style localization
# helper have their Korean/English pairs intentional — the Korean string is
# a lookup key, not untranslated text. Translating the file would corrupt
# those calls, so any file containing one is skipped entirely.
LOC_TR_RE = re.compile(r"Loc\s*\.\s*Tr\s*\(")

# Extensions we bother scanning. Keep this to text/source formats — do not
# add binary or asset extensions.
SCAN_EXTENSIONS = {
    ".cs", ".json", ".xml", ".yml", ".yaml", ".txt", # ".md",
    ".gradle", ".java", ".kt", ".kts", ".py", ".sh", ".properties",
    ".gd", ".tscn", ".tres", ".cfg", ".ini",
}

# Directories we never descend into.
EXCLUDE_DIRS = {
    ".git", ".github", "bin", "obj", "build", "node_modules",
    ".godot", "vendor", "upstream", "_workspace",
}

# Filenames / patterns that are deliberately Korean-only and should be left
# alone (e.g. a dedicated README.ko.md sitting next to an English README).
EXCLUDE_FILE_PATTERNS = ("*.ko.md", "*.ko.*", "README.ko.md")

# Model alias for Google's free-tier Gemini API. "gemini-flash-lite-latest"
# is a Google-maintained alias that always points at the current
# Flash-Lite release — it has more generous free-tier rate limits than
# full Flash and has been reliable for this straightforward translation
# task. Google hot-swaps what the alias resolves to as models are
# retired/replaced, so this shouldn't need updating here again. See
# https://ai.google.dev/gemini-api/docs/models for details, or pass a
# specific version via --model if you want to pin one deliberately.
GEMINI_MODEL = "gemini-flash-lite-latest"
GEMINI_ENDPOINT = (
    "https://generativelanguage.googleapis.com/v1beta/models/"
    "{model}:generateContent"
)

# Split very large files into chunks (by line, not mid-token) so we stay
# comfortably within request/response size limits. Gemini's flash models
# have a large context window, so this is set high deliberately — chunking
# is where translation is most likely to corrupt code (a split can land
# mid-function, and the model only sees one side of a brace pair), so we
# avoid it unless a file is genuinely huge.
MAX_CHARS_PER_CHUNK = 100_000

MAX_RETRIES = 6
RETRY_BACKOFF_SECONDS = 8
FALLBACK_MODEL = "gemini-flash-latest"


# --- Translation call --------------------------------------------------------

PROMPT_TEMPLATE = """You are translating Korean text embedded in a source/config file to English, for a software repository. Follow these rules exactly:

1. Translate ONLY human-readable Korean (Hangul) text: comments, string literals meant for display, docs/prose, log messages.
2. Do NOT translate or alter: code syntax, identifiers, variable/method/class names, file paths, URLs, JSON/XML keys, format placeholders (e.g. {{0}}, %s, {{{{name}}}}), escape sequences, or any non-Korean text.
3. Preserve the exact structure: same number of lines where possible, same indentation, same quoting/escaping style, same comment markers.
4. Do not add commentary, explanations, or markdown fences. Output ONLY the full resulting file content, nothing else.

Filename: {filename}

--- FILE CONTENT START ---
{content}
--- FILE CONTENT END ---
"""

PROMPT_TEXT_TEMPLATE = """Translate the following GitHub release {kind} from Korean to English. It may be plain text or Markdown.

Rules:
1. Translate all Korean (Hangul) text fully and naturally.
2. Preserve Markdown formatting exactly: headings, lists, links, code spans/blocks, emphasis. Do not translate text inside code spans/blocks, URLs, or usernames (e.g. @someone).
3. Keep any already-English text as-is.
4. Do not add commentary, a preamble, or markdown fences around your answer. Output ONLY the translated {kind}, nothing else. If the input is empty, output nothing.

--- {kind_upper} START ---
{content}
--- {kind_upper} END ---
"""


def translate_plain_text(api_key: str, content: str, kind: str) -> str | None:
    """Translate free-form text (release title/body), not source code."""
    if not content.strip():
        return ""
    prompt = PROMPT_TEXT_TEMPLATE.format(kind=kind, kind_upper=kind.upper(), content=content)
    model = GEMINI_MODEL
    for attempt in range(1, MAX_RETRIES + 1):
        text, status = _post_to_gemini(api_key, model, prompt)
        if text is not None:
            return text
        transient = status in (429, 500, 502, 503, 504) or status is None
        if not transient:
            print(f"  ! HTTP {status} translating release {kind}: not retrying", file=sys.stderr)
            return None
        if status == 503 and model != FALLBACK_MODEL and attempt >= max(2, MAX_RETRIES // 2):
            model = FALLBACK_MODEL
        if attempt < MAX_RETRIES:
            wait = min(RETRY_BACKOFF_SECONDS * (2 ** (attempt - 1)), 90) + random.uniform(0, 3)
            print(f"  ! HTTP {status} translating release {kind} (attempt {attempt}/{MAX_RETRIES}), "
                  f"retrying in {wait:.0f}s", file=sys.stderr)
            time.sleep(wait)
    print(f"  ! giving up translating release {kind}", file=sys.stderr)
    return None


def _post_to_gemini(api_key: str, model: str, prompt: str) -> tuple[str | None, int | None]:
    """Returns (text, http_status). text is None on failure; http_status is
    the HTTP status code when the failure was an HTTPError, else None."""
    body = {
        "contents": [{"parts": [{"text": prompt}]}],
        "generationConfig": {"temperature": 0.1},
    }
    url = GEMINI_ENDPOINT.format(model=model) + f"?key={api_key}"
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
        candidates = payload.get("candidates", [])
        if not candidates:
            return None, None
        parts = candidates[0].get("content", {}).get("parts", [])
        return "".join(p.get("text", "") for p in parts), None
    except urllib.error.HTTPError as e:
        return None, e.code
    except Exception:  # noqa: BLE001
        return None, None


def call_gemini(api_key: str, filename: str, content: str) -> str | None:
    prompt = PROMPT_TEMPLATE.format(filename=filename, content=content)

    model = GEMINI_MODEL
    switched_to_fallback = False

    for attempt in range(1, MAX_RETRIES + 1):
        text, status = _post_to_gemini(api_key, model, prompt)
        if text is not None:
            return text

        transient = status in (429, 500, 502, 503, 504) or status is None
        if not transient:
            print(f"  ! HTTP {status} on {filename}: not retrying (non-transient)", file=sys.stderr)
            return None

        # If the primary model keeps getting overloaded (503) partway
        # through our retry budget, switch to a lighter, less-contended
        # model for the remaining attempts rather than burning the whole
        # budget against the same overloaded model.
        if (
            not switched_to_fallback
            and status == 503
            and attempt >= max(2, MAX_RETRIES // 2)
            and model != FALLBACK_MODEL
        ):
            print(f"  ! {filename}: {model} still overloaded after {attempt} attempts, "
                  f"switching to {FALLBACK_MODEL}", file=sys.stderr)
            model = FALLBACK_MODEL
            switched_to_fallback = True

        if attempt < MAX_RETRIES:
            # Exponential backoff with jitter, capped so we don't wait
            # forever in CI.
            wait = min(RETRY_BACKOFF_SECONDS * (2 ** (attempt - 1)), 90)
            wait += random.uniform(0, 3)
            print(f"  ! HTTP {status} on {filename} (attempt {attempt}/{MAX_RETRIES}), "
                  f"retrying {model} in {wait:.0f}s", file=sys.stderr)
            time.sleep(wait)
        else:
            print(f"  ! giving up on {filename} after {MAX_RETRIES} attempts (last status: {status})",
                  file=sys.stderr)

    return None


def chunk_lines(content: str, max_chars: int) -> list[str]:
    lines = content.splitlines(keepends=True)
    chunks: list[str] = []
    current: list[str] = []
    size = 0
    for line in lines:
        if size + len(line) > max_chars and current:
            chunks.append("".join(current))
            current = []
            size = 0
        current.append(line)
        size += len(line)
    if current:
        chunks.append("".join(current))
    return chunks


def translate_file(api_key: str, path: str, content: str) -> str | None:
    if len(content) <= MAX_CHARS_PER_CHUNK:
        return call_gemini(api_key, path, content)

    # Chunk, translate each piece, reassemble. Only chunks that actually
    # contain Hangul get sent to the API; the rest pass through untouched.
    chunks = chunk_lines(content, MAX_CHARS_PER_CHUNK)
    out = []
    changed = False
    for i, chunk in enumerate(chunks):
        if HANGUL_RE.search(chunk):
            translated = call_gemini(api_key, f"{path} (part {i + 1}/{len(chunks)})", chunk)
            if translated is None:
                return None
            out.append(translated)
            changed = True
        else:
            out.append(chunk)
    return "".join(out) if changed else content


# Structural characters that should appear exactly as often in the
# translation as in the original — if the model dropped or invented a
# brace/paren while translating (most likely to happen on a chunk that was
# split mid-function), the counts won't match and we should refuse to write
# the result rather than hand back code that won't compile.
STRUCTURAL_CHARS = "{}()[]"


def structurally_intact(original: str, translated: str) -> tuple[bool, str]:
    mismatches = []
    for ch in STRUCTURAL_CHARS:
        oc, tc = original.count(ch), translated.count(ch)
        if oc != tc:
            mismatches.append(f"'{ch}': {oc} -> {tc}")
    if mismatches:
        return False, ", ".join(mismatches)
    return True, ""


# --- File walking ------------------------------------------------------------

def is_excluded_file(filename: str) -> bool:
    return any(fnmatch.fnmatch(filename, pat) for pat in EXCLUDE_FILE_PATTERNS)


def find_candidate_files(repo_root: str) -> list[str]:
    candidates = []
    for dirpath, dirnames, filenames in os.walk(repo_root):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS and not d.startswith(".")]
        for fname in filenames:
            ext = os.path.splitext(fname)[1].lower()
            if ext not in SCAN_EXTENSIONS:
                continue
            if is_excluded_file(fname):
                continue
            full = os.path.join(dirpath, fname)
            candidates.append(full)
    return candidates


def main() -> int:
    global GEMINI_MODEL

    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", default=".")
    parser.add_argument("--api-key", default=os.environ.get("GEMINI_API_KEY", ""))
    parser.add_argument("--dry-run", action="store_true", help="List files that would change, don't call the API")
    parser.add_argument("--model", default=GEMINI_MODEL, help="Override the Gemini model/alias to call")
    parser.add_argument(
        "--translate-release", action="store_true",
        help="Instead of scanning the repo, translate release notes/title from "
             "the RELEASE_BODY / RELEASE_NAME env vars and write "
             "translated_notes.md / translated_title.txt",
    )
    parser.add_argument("--notes-out", default="translated_notes.md")
    parser.add_argument("--title-out", default="translated_title.txt")
    args = parser.parse_args()

    GEMINI_MODEL = args.model

    if not args.api_key and not (args.dry_run and not args.translate_release):
        print("No API key provided (set GEMINI_API_KEY or pass --api-key). "
              "Skipping translation for this run.", file=sys.stderr)
        return 0

    if args.translate_release:
        return translate_release_notes(args)

    return translate_repo(args)


def translate_release_notes(args: argparse.Namespace) -> int:
    body = os.environ.get("RELEASE_BODY", "")
    name = os.environ.get("RELEASE_NAME", "")

    translated_body = translate_plain_text(args.api_key, body, "notes") if body.strip() else ""
    if body.strip() and translated_body is None:
        print("  ! falling back to original (untranslated) release notes", file=sys.stderr)
        translated_body = body

    translated_title = translate_plain_text(args.api_key, name, "title") if name.strip() else ""
    if name.strip() and translated_title is None:
        print("  ! falling back to original (untranslated) release title", file=sys.stderr)
        translated_title = name

    with open(args.notes_out, "w", encoding="utf-8") as f:
        f.write(translated_body)
    with open(args.title_out, "w", encoding="utf-8") as f:
        f.write(translated_title.strip())

    print(f"Wrote {args.notes_out} and {args.title_out}")
    return 0


def translate_repo(args: argparse.Namespace) -> int:

    candidates = find_candidate_files(args.repo_root)
    touched = []

    for path in candidates:
        try:
            with open(path, "r", encoding="utf-8") as f:
                content = f.read()
        except (UnicodeDecodeError, OSError):
            continue

        if not HANGUL_RE.search(content):
            continue

        rel = os.path.relpath(path, args.repo_root)

        if LOC_TR_RE.search(content):
            print(f"skipping (already uses Loc.Tr localization pairs): {rel}")
            continue

        if args.dry_run:
            print(f"would translate: {rel}")
            touched.append(rel)
            continue

        print(f"translating: {rel}")
        translated = translate_file(args.api_key, rel, content)
        if translated is None:
            print(f"  ! skipped (translation failed): {rel}", file=sys.stderr)
            continue
        if translated.strip() == "":
            print(f"  ! skipped (empty response): {rel}", file=sys.stderr)
            continue

        ok, detail = structurally_intact(content, translated)
        if not ok:
            print(f"  ! skipped (structural mismatch, likely corrupted syntax) {rel}: {detail}",
                  file=sys.stderr)
            print(f"    file left untouched — will retry on next run", file=sys.stderr)
            continue

        with open(path, "w", encoding="utf-8") as f:
            f.write(translated)
        touched.append(rel)
        # Be polite to the free tier's rate limits.
        time.sleep(1)

    print(f"\n{len(touched)} file(s) translated.")
    # Emit for the workflow to pick up via $GITHUB_OUTPUT
    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output:
        with open(gh_output, "a", encoding="utf-8") as f:
            f.write(f"translated_count={len(touched)}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())