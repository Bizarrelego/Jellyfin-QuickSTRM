#!/usr/bin/env bash
# QuickSTRM 1-Step Version Bumper & Synchronizer (Bash)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-}"

echo "==> Synchronizing QuickSTRM Release Metadata..."
if [ -n "$VERSION" ]; then
  python3 "$REPO_ROOT/.github/scripts/sync_release.py" --extract-notes --version "$VERSION"
else
  python3 "$REPO_ROOT/.github/scripts/sync_release.py" --extract-notes
fi

echo "==> Synchronization Complete! All files are in sync."
