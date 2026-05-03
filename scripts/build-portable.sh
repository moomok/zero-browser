#!/usr/bin/env bash
# Build a self-contained portable archive of Zero Browser for the host platform.
# Usage: scripts/build-portable.sh <rid> <version> <out_dir>
#   rid: linux-x64 | osx-x64 | osx-arm64 | win-x64 | win-arm64
set -euo pipefail

RID="${1:?usage: $0 <rid> <version> <out_dir>}"
VERSION="${2:?missing version}"
OUT_DIR="${3:?missing out_dir}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$REPO_ROOT/src/ZeroBrowser.App/ZeroBrowser.App.csproj"
PUBLISH_DIR="$REPO_ROOT/src/ZeroBrowser.App/bin/Release/net8.0/$RID/publish"

mkdir -p "$OUT_DIR"
# Resolve to absolute path so the `zip` subshell (which `cd`s into the publish
# dir) writes the archive to the right place, not to a relative path inside
# $PUBLISH_DIR. `tar -czf` resolves its output before `-C`, but `zip` does not.
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

echo "==> Publishing self-contained $RID build..."
dotnet publish "$PROJ" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=embedded

# Pick archive format per platform
case "$RID" in
    win-*)
        OUT="$OUT_DIR/ZeroBrowser-$RID-$VERSION-portable.zip"
        echo "==> Zipping to $OUT"
        ( cd "$PUBLISH_DIR" && zip -qr "$OUT" . )
        ;;
    *)
        OUT="$OUT_DIR/ZeroBrowser-$RID-$VERSION.tar.gz"
        echo "==> Tarring to $OUT"
        tar -czf "$OUT" -C "$PUBLISH_DIR" .
        ;;
esac

ls -lh "$OUT"
