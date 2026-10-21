#!/usr/bin/env python3
"""
QuickSTRM Release & Manifest Synchronization Tool.
Automates version updates across QuickSTRM.csproj, meta.json, build.yaml,
README.md, and manifest.json using CHANGELOG.md or Git tags as the single source of truth.
Zero external dependencies (pure Python 3 standard library).
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys
from pathlib import Path


def get_repo_root() -> Path:
    """Finds repository root relative to this script."""
    script_dir = Path(__file__).resolve().parent
    for candidate in [script_dir.parent.parent, script_dir.parent, script_dir]:
        if (candidate / "QuickSTRM.csproj").exists():
            return candidate
    return Path.cwd()


def get_target_version(repo_root: Path, explicit_version: str | None = None) -> str:
    """Resolves target version from CLI arg, Git environment, or CHANGELOG.md."""
    if explicit_version:
        clean = explicit_version.lstrip("v").strip()
        if clean:
            return clean

    # Check GitHub Actions environment tag
    gh_ref = os.environ.get("GITHUB_REF", "").strip()
    gh_ref_name = os.environ.get("GITHUB_REF_NAME", "").strip()
    if gh_ref.startswith("refs/tags/v") or (gh_ref_name.startswith("v") and len(gh_ref_name) > 1 and gh_ref_name[1].isdigit()):
        return gh_ref_name.lstrip("v")

    # Read from top of CHANGELOG.md
    changelog_path = repo_root / "CHANGELOG.md"
    if changelog_path.exists():
        content = changelog_path.read_text(encoding="utf-8")
        match = re.search(r"^##\s*\[(\d+\.\d+(?:\.\d+)?)\]", content, re.MULTILINE)
        if match:
            return match.group(1)

    # Fallback to meta.json
    meta_path = repo_root / "meta.json"
    if meta_path.exists():
        try:
            with open(meta_path, "r", encoding="utf-8") as f:
                data = json.load(f)
                ver = data.get("version", "").strip()
                if ver:
                    return ver
        except Exception:
            pass

    # Fallback to QuickSTRM.csproj
    csproj_path = repo_root / "QuickSTRM.csproj"
    if csproj_path.exists():
        content = csproj_path.read_text(encoding="utf-8")
        match = re.search(r"<Version>([^<]+)</Version>", content)
        if match:
            return match.group(1).strip()

    raise RuntimeError("Unable to determine target release version.")


def extract_changelog(repo_root: Path, version: str) -> tuple[str, str]:
    """
    Extracts release notes for the target version from CHANGELOG.md.
    Returns (raw_markdown_notes, manifest_changelog_string).
    """
    changelog_path = repo_root / "CHANGELOG.md"
    if not changelog_path.exists():
        fallback = f"QuickSTRM v{version} release."
        return fallback, fallback

    content = changelog_path.read_text(encoding="utf-8")
    
    # Match from '## [version]' to the next '## [' or EOF
    pattern = rf"^##\s*\[{re.escape(version)}\][^\r\n]*\r?\n(.*?)(?=\r?\n##\s*\[|\Z)"
    match = re.search(pattern, content, re.DOTALL | re.MULTILINE)
    
    if not match:
        fallback = f"QuickSTRM v{version} release."
        return fallback, fallback

    raw_notes = match.group(1).strip()
    raw_notes = re.sub(r"\n---\s*$", "", raw_notes).strip()
    
    # For manifest changelog, create a clean multiline string
    manifest_lines = []
    for line in raw_notes.splitlines():
        s = line.rstrip()
        if s.startswith("### "):
            manifest_lines.append(s.replace("### ", "").strip())
        elif s.startswith("- ") or s.startswith("  - "):
            manifest_lines.append(s)
        elif s and not s.startswith("#") and not s.startswith("---"):
            manifest_lines.append(s)

    manifest_changelog = "\n".join(manifest_lines).strip()
    if not manifest_changelog:
        manifest_changelog = raw_notes

    return raw_notes, manifest_changelog


def sync_csproj(repo_root: Path, version: str) -> bool:
    """Updates Version tags in QuickSTRM.csproj."""
    csproj_path = repo_root / "QuickSTRM.csproj"
    if not csproj_path.exists():
        return False

    content = csproj_path.read_text(encoding="utf-8")
    original = content
    content = re.sub(r"<Version>[^<]*</Version>", f"<Version>{version}</Version>", content)
    content = re.sub(r"<AssemblyVersion>[^<]*</AssemblyVersion>", f"<AssemblyVersion>{version}</AssemblyVersion>", content)
    content = re.sub(r"<FileVersion>[^<]*</FileVersion>", f"<FileVersion>{version}</FileVersion>", content)

    if content != original:
        csproj_path.write_text(content, encoding="utf-8")
        return True
    return False


def sync_meta_json(repo_root: Path, version: str) -> bool:
    """Updates version in meta.json."""
    meta_path = repo_root / "meta.json"
    if not meta_path.exists():
        return False

    with open(meta_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    if data.get("version") == version:
        return False

    data["version"] = version
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    return True


def sync_build_yaml(repo_root: Path, version: str) -> bool:
    """Updates version in build.yaml."""
    yaml_path = repo_root / "build.yaml"
    if not yaml_path.exists():
        return False

    content = yaml_path.read_text(encoding="utf-8")
    original = content
    content = re.sub(r'version:\s*"[^"]*"', f'version: "{version}"', content)

    if content != original:
        yaml_path.write_text(content, encoding="utf-8")
        return True
    return False


def sync_readme(repo_root: Path, version: str) -> bool:
    """Updates QuickSTRM version occurrences in README.md."""
    readme_path = repo_root / "README.md"
    if not readme_path.exists():
        return False

    content = readme_path.read_text(encoding="utf-8")
    original = content
    content = re.sub(r'(QuickSTRM v)\d+\.\d+(?:\.\d+)?', rf'\g<1>{version}', content)

    if content != original:
        readme_path.write_text(content, encoding="utf-8")
        return True
    return False


def sync_manifest(
    repo_root: Path,
    version: str,
    changelog: str,
    checksum: str | None = None,
    timestamp: str | None = None,
    source_url: str = "https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases/download/latest/QuickSTRM.zip",
    target_abi: str = "12.2.0.0",
) -> bool:
    """Updates or prepends target version entry in manifest.json."""
    manifest_path = repo_root / "manifest.json"
    if not manifest_path.exists():
        return False

    with open(manifest_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    target_plugin = data[0] if isinstance(data, list) else data
    versions_list = target_plugin.setdefault("versions", [])

    if timestamp is None:
        timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # Check if target version already exists at the top of versions
    if len(versions_list) > 0 and versions_list[0].get("version") == version:
        entry = versions_list[0]
        changed = False
        if entry.get("changelog") != changelog and changelog:
            entry["changelog"] = changelog
            changed = True
        if entry.get("targetAbi") != target_abi:
            entry["targetAbi"] = target_abi
            changed = True
        if entry.get("sourceUrl") != source_url:
            entry["sourceUrl"] = source_url
            changed = True
        if checksum and entry.get("checksum") != checksum:
            entry["checksum"] = checksum
            changed = True
        if checksum and entry.get("timestamp") != timestamp:
            entry["timestamp"] = timestamp
            changed = True
        if not changed:
            return False
    else:
        # Prepend new version entry
        new_entry = {
            "version": version,
            "changelog": changelog,
            "targetAbi": target_abi,
            "sourceUrl": source_url,
            "checksum": checksum or "",
            "timestamp": timestamp,
        }
        versions_list.insert(0, new_entry)

    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    return True


def calculate_md5(file_path: Path) -> str:
    """Computes uppercase MD5 hash of a file."""
    hasher = hashlib.md5()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            hasher.update(chunk)
    return hasher.hexdigest().upper()


def main():
    parser = argparse.ArgumentParser(description="Synchronize QuickSTRM version metadata and manifest.")
    parser.add_argument("--version", "-v", help="Explicit version string (e.g. 3.0.1)")
    parser.add_argument("--extract-notes", action="store_true", help="Extract release notes to RELEASE_NOTES.md")
    parser.add_argument("--notes-file", default="RELEASE_NOTES.md", help="Path to write release notes")
    parser.add_argument("--update-checksum", help="Path to zip artifact to compute MD5 and stamp into manifest.json")
    parser.add_argument("--check-only", action="store_true", help="Print detected version and exit")

    args = parser.parse_args()
    repo_root = get_repo_root()

    version = get_target_version(repo_root, args.version)
    print(f"[QuickSTRM Release Sync] Target Version: {version}")

    # Export to GitHub Actions output if in GHA environment
    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output and os.path.exists(gh_output):
        with open(gh_output, "a", encoding="utf-8") as f:
            f.write(f"version={version}\n")

    if args.check_only:
        sys.exit(0)

    raw_notes, manifest_notes = extract_changelog(repo_root, version)

    if args.extract_notes or args.notes_file:
        notes_path = repo_root / args.notes_file
        notes_path.write_text(raw_notes + "\n", encoding="utf-8")
        print(f"[QuickSTRM Release Sync] Extracted release notes to {notes_path}")

    # Synchronize repository metadata files
    changed_csproj = sync_csproj(repo_root, version)
    changed_meta = sync_meta_json(repo_root, version)
    changed_yaml = sync_build_yaml(repo_root, version)
    changed_readme = sync_readme(repo_root, version)
    print(f"[QuickSTRM Release Sync] Updated QuickSTRM.csproj: {changed_csproj}, meta.json: {changed_meta}, build.yaml: {changed_yaml}, README.md: {changed_readme}")

    checksum = None
    timestamp = None
    if args.update_checksum:
        zip_path = Path(args.update_checksum)
        if not zip_path.is_absolute():
            zip_path = repo_root / zip_path
        if zip_path.exists():
            checksum = calculate_md5(zip_path)
            timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            print(f"[QuickSTRM Release Sync] Computed MD5 for {zip_path.name}: {checksum} at {timestamp}")
        else:
            print(f"[QuickSTRM Release Sync] Warning: Zip artifact {zip_path} not found.")

    changed_manifest = sync_manifest(
        repo_root=repo_root,
        version=version,
        changelog=manifest_notes,
        checksum=checksum,
        timestamp=timestamp,
    )
    print(f"[QuickSTRM Release Sync] Updated manifest.json: {changed_manifest}")


if __name__ == "__main__":
    main()
