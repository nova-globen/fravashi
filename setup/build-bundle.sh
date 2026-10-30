#!/usr/bin/env bash
# Builds a self-contained Nova Globen Fravashi bundle (ADR 0003 V3): the studio image saved to a
# tarball, the merged fravashi.exe published self-contained, and the matching install script. The bundle's own folder and image tar keep their fravashi names: they are
# internal, and the web setup and every older install script look for them. Requires a container runtime and the
# .NET SDK on the build machine.
#
# Releases are built on the dev host by setup/release/build-release-local.ps1 (decided 2026-10-24,
# #370), which builds from a PUSHED, CI-GREEN tag and publishes the signed assets on the public repository's GitHub
# Releases. This file is the Linux-shell way to build a bundle, kept for a build machine without that
# script. That script is not a revival of the old Windows twin,
# build-bundle.ps1, deleted on 2026-09-19 because a build from a working tree stamped a version already in use (0.9.22
# was built locally twice from different code, and FRAVASHI_VERSION is the single source of truth for the running
# version) and recorded no commit. The new one refuses exactly that: it builds only `git archive` of a tag that is on
# origin and green on CI, and names the commit in every result. Do not add a working-tree release builder back. See
# the releasing-fravashi skill.
#
# tools/release/dev-image.sh is NOT that counterpart and must not be deleted as one: it builds no bundle and no
# release, only a dev IMAGE for this machine during a fix-and-retry loop (decided 2026-09-29), stamped
# <next patch>-dev.<commit> so it cannot collide with a release, built from git archive HEAD, and tested on Linux
# first.
#
# Runtime-agnostic (ADR 0009): detects docker -> podman -> wslc. build/save take the same args on all three.
#
# Usage: ./build-bundle.sh [rid] [image-tag] [output-root] [runtime] [version] [variant]
#   SKIP_IMAGE=1  reuse an existing fravashi-image.tar in the bundle instead of rebuilding + re-saving the
#                 image. Use when only the launcher/installer changed and the tar is already current. No
#                 container runtime is required in that mode.
#   PLATFORM=...  build the image for an explicit target platform (e.g. linux/arm64), passed straight to
#                 `<runtime> build --platform`. Empty (the default) builds for the build machine's own
#                 architecture, for a build machine whose architecture differs from the target's. Cross-architecture builds need
#                 binfmt/QEMU registered on the build host; without it the runtime fails loudly here.
set -euo pipefail

rid="${1:-win-x64}"
image_tag="${2:-fravashi:latest}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/.." && pwd)"
output_root="${3:-$here/dist}"
runtime="${4:-}"
# Version + variant baked into the image (F1/F4, read by IProductInfo as the single source of truth for the
# running version, and by the T8 integrity pin). A plain local build with the defaults reports 0.0.0/full.
version="${5:-0.0.0}"
variant="${6:-full}"
skip_image="${SKIP_IMAGE:-0}"
platform="${PLATFORM:-}"

# Fravashi ships Windows-only for now (merged fravashi.exe, plan Phase 1), same guard as build-bundle.ps1.
# Note this script itself runs on Linux/macOS: fravashi.exe is CROSS-published from here, see below.
case "$rid" in
    win-*) ;;
    *) echo "Fravashi ships Windows-only for now (merged fravashi.exe, plan Phase 1); RID '$rid' is not supported." >&2; exit 1;;
esac

bundle="$output_root/fravashi-$rid"
mkdir -p "$bundle"
image_tar="$bundle/fravashi-image.tar"

if [ "$skip_image" = "1" ]; then
    [ -f "$image_tar" ] || { echo "SKIP_IMAGE=1 was set but $image_tar does not exist. Run once without it to build the image." >&2; exit 1; }
    echo "1-2/3  Reusing existing image tarball ($image_tar)."
else
    if [ -z "$runtime" ]; then
        for candidate in docker podman wslc; do
            if command -v "$candidate" >/dev/null 2>&1; then runtime="$candidate"; break; fi
        done
    fi
    [ -n "$runtime" ] || { echo "No container runtime found (looked for docker, podman, wslc)." >&2; exit 1; }
    echo "Using container runtime: $runtime"

    platform_args=""
    if [ -n "$platform" ]; then
        platform_args="--platform $platform"
        echo "     Target platform: $platform (cross-architecture builds need binfmt/QEMU on this host)."
    fi

    echo "1/3  Building the studio image ($image_tag, version $version, variant $variant) ..."
    # shellcheck disable=SC2086 # platform_args is deliberately word-split: it is either empty or two words.
    "$runtime" build $platform_args -t "$image_tag" \
        --build-arg "VERSION=$version" --build-arg "VARIANT=$variant" "$repo_root"

    echo "2/3  Saving the image to a tarball ..."
    # Save under the localhost/ namespace on purpose. DOCKER normalizes a bare "fravashi:latest" to
    # docker.io/library/fravashi:latest inside the tarball, while PODMAN on the owner's machine
    # resolves the launcher's bare "fravashi:latest" to localhost/fravashi:latest. A tarball saved
    # from docker therefore loads under a name the launcher never looks at, and the launcher keeps running
    # the OLD container while the new image sits invisible (found 2026-08-08: the first CI-built
    # install ran v0.9.15 for ten minutes while v0.9.16 sat loaded under docker.io/library/).
    # Tagging localhost/ explicitly makes docker and podman produce the same loadable name; podman
    # treats it as its own default namespace, and install-windows.ps1 retags defensively either way.
    "$runtime" tag "$image_tag" "localhost/$image_tag"
    "$runtime" save "localhost/$image_tag" -o "$image_tar"
    "$runtime" rmi "localhost/$image_tag" >/dev/null 2>&1 || true

    # Housekeeping (directive 2026-07-28): rebuilding moved the tag, demoting the previous image to a
    # dangling <none>, and the multi-stage build left its SDK build stage behind untagged. Prune dangling
    # images only (tagged and in-use images are never touched) so repeated release builds do not fill the
    # build machine's disk. Never a broad prune: this may be the owner's own machine.
    if "$runtime" image prune -f >/dev/null 2>&1; then
        echo "     Pruned dangling images left by the rebuild."
    else
        echo "     Note: '$runtime image prune -f' failed; dangling build leftovers stay on disk (harmless, but they add up)."
    fi
fi

echo "3/3  Publishing fravashi.exe (merged launcher + studio window + tray) for $rid ..."
# EnableWindowsTargeting is what lets a NON-Windows host build this project at all: fravashi.exe is
# net10.0-windows with UseWindowsForms, and without it the SDK stops with NETSDK1100 ("set
# EnableWindowsTargeting to true"). With it, the WindowsDesktop reference and runtime packs are restored
# from NuGet like any other pack and the publish produces a genuine win-x64 GUI PE. It is a no-op on a
# Windows host, so it is passed unconditionally rather than probed for.
# Stamped with the bundle's version (backlog 381): the numeric part as the file version, the whole as the informational
# one, as setup/release/build-release-local.ps1 does.
dotnet publish "$repo_root/src/Fravashi.Desktop/Fravashi.Desktop.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableWindowsTargeting=true \
    "-p:Version=${version%%[!0-9.]*}" \
    "-p:InformationalVersion=$version" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -o "$bundle/launcher"


# Bundle ripgrep beside fravashi.exe for the host bridge's search (plan 2a). Pinned release, sha256 verified
# before extraction; idempotent (skips the download when the cached zip already matches). ripgrep is
# dual-licensed MIT OR Unlicense (both on .githooks/licenses.approved); it is a bundled tool, not a NuGet
# dependency, so the pre-commit package manifest is not involved.
# KEEP THIS VERSION AND HASH EQUAL TO build-bundle.ps1's.
rg_version="14.1.1"
rg_sha256="d0f534024c42afd6cb4d38907c25cd2b249b79bbe6cc1dbee8e3e37c2b6e25a1"
rg_zip="${TMPDIR:-/tmp}/ripgrep-$rg_version-x86_64-pc-windows-msvc.zip"
rg_url="https://github.com/BurntSushi/ripgrep/releases/download/$rg_version/ripgrep-$rg_version-x86_64-pc-windows-msvc.zip"

rg_hash_of() { sha256sum "$1" | cut -d' ' -f1; }
if [ ! -f "$rg_zip" ] || [ "$(rg_hash_of "$rg_zip")" != "$rg_sha256" ]; then
    echo "     Downloading ripgrep $rg_version for the bundle ..."
    curl -fsSL --retry 3 -o "$rg_zip" "$rg_url"
fi
if [ "$(rg_hash_of "$rg_zip")" != "$rg_sha256" ]; then
    echo "ripgrep download failed its sha256 check; refusing to bundle it." >&2
    exit 1
fi

# The ripgrep Windows release is a .zip and GNU tar cannot read one. unzip is the normal tool; bsdtar can
# also do it. Say which package is missing rather than failing on a bare "command not found" three lines
# later, because on a fresh runner this is the likeliest thing to be absent.
rg_extract="${TMPDIR:-/tmp}/fravashi-rg-extract"
rm -rf "$rg_extract"
mkdir -p "$rg_extract"
if command -v unzip >/dev/null 2>&1; then
    unzip -q "$rg_zip" -d "$rg_extract"
elif command -v bsdtar >/dev/null 2>&1; then
    (cd "$rg_extract" && bsdtar -xf "$rg_zip")
else
    echo "Cannot unpack $rg_zip: neither 'unzip' nor 'bsdtar' is installed." >&2
    echo "Install one of them (apt-get install -y unzip) and run this again." >&2
    exit 1
fi
rg_dir="$(find "$rg_extract" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
[ -n "$rg_dir" ] || { echo "The ripgrep archive did not contain the expected directory." >&2; exit 1; }
cp "$rg_dir/rg.exe" "$bundle/launcher/rg.exe"
cp "$rg_dir/COPYING" "$bundle/launcher/rg-LICENSE.txt"
rm -rf "$rg_extract"
echo "     Bundled ripgrep $rg_version (rg.exe) beside fravashi.exe."

cp "$here/install/install-windows.ps1" "$bundle/"
# The installer's one elevated step (backlog 374), run from beside it.
cp "$here/install/firewall-rule.ps1" "$bundle/"

echo "Bundle ready: $bundle"
