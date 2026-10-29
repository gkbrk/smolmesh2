#!/bin/sh
set -eu

usage() {
    echo "Usage: $0 [--publish-only]"
    echo "Publish a self-contained macOS executable and enable root/setuid (prompts for sudo)."
    echo "  --publish-only  Build without changing ownership or enabling setuid."
}

setuid=true
case "${1:-}" in
    "") ;;
    --publish-only) setuid=false ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac
if [ "$#" -gt 1 ]; then
    usage >&2
    exit 2
fi

if [ "$(uname -s)" != Darwin ]; then
    echo "This script is for macOS only." >&2
    exit 1
fi
if [ "$(id -u)" -eq 0 ]; then
    echo "Run this script as your normal user; only the final permission changes use sudo." >&2
    exit 1
fi
case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir="$script_dir/publish"
binary="$output_dir/smolmesh"
staging=$(mktemp -d "${TMPDIR:-/tmp}/smolmesh-publish.XXXXXX")
trap 'rm -rf "$staging"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Always build unprivileged into a fresh directory: the previous binary may be root-owned.
dotnet publish "$script_dir/Smolmesh/Smolmesh.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    -p:PublishAot=false \
    -p:DebugType=embedded \
    -o "$staging/publish"

mkdir -p "$output_dir"
# Replacing rather than overwriting also works after an earlier root-owned publish.
mv -f "$staging/publish/smolmesh" "$binary"
chmod 755 "$binary"

if [ "$setuid" = true ]; then
    sudo chown root:wheel "$binary"
    sudo chmod 4755 "$binary"
    echo "Published with root/setuid: $binary"
else
    echo "Published without setuid: $binary"
fi
