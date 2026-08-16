#!/usr/bin/env python3
"""
finish_release.py

The manual half of the release pipeline, collapsed into one command:
  1. Fetches and checks out the translate/<tag> branch the GitHub Action
     produced.
  2. Runs the upstream build script to produce the APK.
  3. Uploads the APK to the matching <tag>-en GitHub (pre)release.
  4. Flips that release out of prerelease status so it becomes the
     "latest" release — this is the point where update-watchers like
     Obtainium will actually pick it up, since it deliberately ignores
     prereleases by default.

Requires the GitHub CLI (`gh`), installed and authenticated
(`gh auth login`) with push access to your fork. Run this from inside
your local clone of the fork.

Usage:
    python3 scripts/finish_release.py v1.2.3
    python3 scripts/finish_release.py v1.2.3 --skip-build   # APK already built
"""
from __future__ import annotations

import argparse
import glob
import os
import subprocess
import sys


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    print(f"$ {' '.join(cmd)}")
    return subprocess.run(cmd, check=True, **kwargs)


def find_apk(pattern: str) -> str | None:
    matches = glob.glob(pattern, recursive=True)
    if not matches:
        return None
    matches.sort(key=os.path.getmtime, reverse=True)
    return matches[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("tag", help="Original release tag that was translated, e.g. v1.2.3")
    parser.add_argument("--release-tag", default=None,
                         help="Translated release tag to update (defaults to <tag>-en)")
    parser.add_argument("--branch", default=None,
                         help="Translated branch to pull (defaults to translate/<tag>)")
    parser.add_argument("--build-cmd", default="bash scripts/build.sh",
                         help="Command that produces the APK")
    parser.add_argument(
        "--apk-glob", default="android/build/outputs/apk/**/*.apk",
        help="Glob (relative to repo root) used to locate the built APK afterward",
    )
    parser.add_argument("--skip-build", action="store_true",
                         help="Skip the build step; just locate, upload, and finalize")
    args = parser.parse_args()

    release_tag = args.release_tag or f"{args.tag}-en"
    branch = args.branch or f"translate/{args.tag}"

    try:
        run(["gh", "auth", "status"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except FileNotFoundError:
        print("! GitHub CLI ('gh') isn't installed. Install it from https://cli.github.com/", file=sys.stderr)
        return 1
    except subprocess.CalledProcessError:
        print("! GitHub CLI isn't authenticated. Run `gh auth login` first.", file=sys.stderr)
        return 1

    print(f"Fetching and checking out '{branch}' ...")
    run(["git", "fetch", "origin", branch])
    run(["git", "checkout", branch])
    run(["git", "pull", "origin", branch])

    if not args.skip_build:
        print(f"\nBuilding via: {args.build_cmd}")
        try:
            run(args.build_cmd.split())
        except subprocess.CalledProcessError:
            print(
                "\n! Build failed. Fix the build in your working tree, then re-run with "
                "--skip-build once the APK exists to skip straight to upload.",
                file=sys.stderr,
            )
            return 1
    else:
        print("Skipping build step (--skip-build).")

    apk_path = find_apk(args.apk_glob)
    if not apk_path:
        print(
            f"! No APK found matching '{args.apk_glob}'. If your build script writes "
            f"the APK somewhere else, pass --apk-glob to point at it.",
            file=sys.stderr,
        )
        return 1
    print(f"\nFound APK: {apk_path}")

    print(f"Uploading to release '{release_tag}' ...")
    run(["gh", "release", "upload", release_tag, apk_path, "--clobber"])

    print(f"Marking '{release_tag}' as a full (non-pre) release ...")
    run(["gh", "release", "edit", release_tag, "--prerelease=false", "--latest"])

    view = subprocess.run(
        ["gh", "release", "view", release_tag, "--json", "url", "-q", ".url"],
        capture_output=True, text=True, check=True,
    )
    print(f"\nDone: {view.stdout.strip()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())