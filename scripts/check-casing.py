#!/usr/bin/env python3
"""MSS-043 exact-case/collision guard.

Fails (non-zero exit) when any of these hold, so Linux CI catches the
filesystem/import-casing problems that a case-insensitive Windows checkout
would silently tolerate:

1. Tracked file paths collide under case-insensitive comparison
   (e.g. ``RevealBoard.tsx`` vs ``revealboard.tsx``). A clean Windows
   checkout cannot hold both, so one of them would be lost on checkout.
2. A relative frontend import (``src/MtgSoloSports.Web/src/**/*.ts[x]``)
   does not resolve to a real file with exact-case spelling. On Linux
   (containers, CI) such an import is a build break; on Windows it may
   appear to work and then break everyone else.
3. A ``COPY`` source in the root ``Dockerfile`` (or a ``context:`` /
   ``dockerfile:`` reference in ``compose.yaml`` / ``compose.dev.yaml``)
   does not exist with exact-case spelling, which would break or
   misdirect the Linux image build.

Usage:
    python3 scripts/check-casing.py [--repo-root <path>]

Only the standard library is used. Never mutates the tree.
"""

from __future__ import annotations

import argparse
import glob
import os
import re
import subprocess
import sys

FRONTEND_SRC = os.path.join("src", "MtgSoloSports.Web", "src")
DOCKERFILE = "Dockerfile"
COMPOSE_FILES = ("compose.yaml", "compose.dev.yaml")

IMPORT_RE = re.compile(
    r"""from\s+['"](\.[^'"]+)['"]"""
    r"""|import\s*\(\s*['"](\.[^'"]+)['"]\s*\)"""
    r"""|import\s+['"](\.[^'"]+)['"]"""
)

COPY_RE = re.compile(r"^\s*COPY\s+(.*)$", re.IGNORECASE)
COMPOSE_REF_RE = re.compile(r"^\s*(?:dockerfile|context)\s*:\s*(\S+)\s*$")
TS_EXTENSIONS = (".ts", ".tsx")


def repo_files(root: str) -> list[str]:
    out = subprocess.run(
        ["git", "ls-files"],
        cwd=root,
        capture_output=True,
        text=True,
        check=False,
    )
    if out.returncode != 0:
        # Not a git checkout (e.g. a source archive): fall back to a full
        # filesystem walk excluding .git so the guard still runs.
        found: list[str] = []
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = sorted(
                d
                for d in dirnames
                if d not in (".git", "node_modules", "bin", "obj", "dist")
            )
            for name in sorted(filenames):
                found.append(
                    os.path.relpath(os.path.join(dirpath, name), root)
                )
        return found
    return [line for line in out.stdout.splitlines() if line]


def check_collisions(files: list[str]) -> list[str]:
    seen: dict[str, str] = {}
    errors: list[str] = []
    for path in files:
        lowered = path.lower()
        if lowered in seen and seen[lowered] != path:
            errors.append(
                f"case-only tracked-path collision: '{seen[lowered]}' vs '{path}' "
                "(a clean Windows checkout cannot hold both)"
            )
        else:
            seen.setdefault(lowered, path)
    return errors


def resolve_relative_import(importer_dir: str, spec: str, root: str) -> str | None:
    # Strip Vite-style query/hash suffixes (e.g. './x.css?inline').
    clean = spec.split("?", 1)[0].split("#", 1)[0]
    target = os.path.normpath(os.path.join(importer_dir, clean))
    candidates = [target]
    if not os.path.splitext(target)[1]:
        candidates.extend(target + ext for ext in (*TS_EXTENSIONS, ".css"))
        candidates.extend(
            os.path.join(target, "index" + ext) for ext in TS_EXTENSIONS
        )
    for candidate in candidates:
        if os.path.isfile(os.path.join(root, candidate)):
            return candidate
    return None


def check_frontend_imports(root: str, files: list[str]) -> list[str]:
    errors: list[str] = []
    sources = [
        f
        for f in files
        if f.startswith(FRONTEND_SRC + os.sep)
        and f.endswith(TS_EXTENSIONS)
        and os.path.isfile(os.path.join(root, f))
    ]
    for source in sorted(sources):
        with open(
            os.path.join(root, source), encoding="utf-8"
        ) as handle:
            content = handle.read()
        importer_dir = os.path.dirname(source)
        for match in IMPORT_RE.finditer(content):
            spec = match.group(1) or match.group(2) or match.group(3)
            if resolve_relative_import(importer_dir, spec, root) is None:
                errors.append(
                    f"inexact/unresolvable relative import: '{source}' "
                    f"imports '{spec}' (no exact-case file match)"
                )
    return errors


def _strip_inline_comment(line: str) -> str:
    # A '#' starts a comment in Dockerfiles outside of quotes; COPY sources
    # here never legitimately contain one, so a simple split is sufficient.
    return line.split("#", 1)[0].rstrip()


def dockerfile_copy_sources(root: str) -> list[str]:
    path = os.path.join(root, DOCKERFILE)
    sources: list[str] = []
    if not os.path.isfile(path):
        return sources
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = _strip_inline_comment(raw)
            match = COPY_RE.match(line)
            if not match:
                continue
            tokens = match.group(1).split()
            if any(t.startswith("--from") for t in tokens):
                # Cross-stage copy: sources live in a previous image stage,
                # not in the build context, so there is nothing to check here.
                continue
            # Drop remaining --flags (--chown/--chmod/--link/--parents); the
            # final token is the destination.
            operands = [t for t in tokens if not t.startswith("--")]
            sources.extend(operands[:-1])
    return sources


def check_docker_sources(root: str) -> list[str]:
    errors: list[str] = []
    for source in dockerfile_copy_sources(root):
        if any(ch in source for ch in "*?[]"):
            matches = glob.glob(os.path.join(root, source))
            if not matches:
                errors.append(
                    f"Dockerfile COPY glob matches nothing: '{source}'"
                )
            continue
        if not os.path.exists(os.path.join(root, source)):
            errors.append(
                f"Dockerfile COPY source missing (exact case): '{source}'"
            )
    for compose in COMPOSE_FILES:
        path = os.path.join(root, compose)
        if not os.path.isfile(path):
            errors.append(f"missing Compose file: '{compose}'")
            continue
        with open(path, encoding="utf-8") as handle:
            for lineno, raw in enumerate(handle, start=1):
                stripped = raw.split("#", 1)[0]
                match = COMPOSE_REF_RE.match(stripped)
                if match and not os.path.exists(
                    os.path.join(root, match.group(1))
                ):
                    errors.append(
                        f"{compose}:{lineno}: referenced path missing "
                        f"(exact case): '{match.group(1)}'"
                    )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--repo-root",
        default=None,
        help="Repository root (defaults to the git top-level).",
    )
    args = parser.parse_args()

    if args.repo_root:
        root = os.path.abspath(args.repo_root)
    else:
        probe = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            check=False,
        )
        root = (
            probe.stdout.strip()
            if probe.returncode == 0
            else os.path.abspath(
                os.path.join(os.path.dirname(__file__), os.pardir)
            )
        )

    files = repo_files(root)
    errors = [
        *check_collisions(files),
        *check_frontend_imports(root, files),
        *check_docker_sources(root),
    ]
    if errors:
        print("check-casing: FAIL")
        for error in errors:
            print(f"  - {error}")
        return 1
    print(
        f"check-casing: OK "
        f"({len(files)} tracked paths, frontend imports and "
        f"Docker sources exact-case clean)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
