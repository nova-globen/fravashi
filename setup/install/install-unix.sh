#!/usr/bin/env bash
# Installs Fravashi on Linux/macOS from a LOCAL bundle produced by build-bundle.sh: loads the studio image
# from the bundled fravashi-image.tar into the detected container runtime, places the launcher, symlinks
# `fravashi` into ~/.local/bin (F9), and adds a desktop entry (Linux). The notification-area tray is Windows-only
# today; on macOS/Linux the `fravashi` verbs are the full surface. Run from inside the bundle folder (the one
# holding fravashi-image.tar and launcher/). This is the offline/local-tar installer. There is no network
# installer for Linux/macOS: the pre-merge setup/install.sh was retired (backlog 118).
#
# Runtime-agnostic (ADR 0009): the product runs on whatever engine is installed rather than hard-coding
# Docker. Precedence mirrors Fravashi.Launcher/ContainerRuntimeDetector - an explicit pin (--runtime or a
# launcher.json 'runtime') wins, otherwise the first installed engine in the order docker -> podman -> wslc.
#
# Usage: ./install-unix.sh [--runtime docker|podman|wslc]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

runtime=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --runtime) runtime="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

# Probe a runtime the way the launcher does: the CLI must be on PATH and answer `<cli> version` (exit 0).
runtime_ok() { command -v "$1" >/dev/null 2>&1 && "$1" version >/dev/null 2>&1; }

# Honour an existing launcher.json 'runtime' pin so the image loads into the same engine `fravashi` will drive.
if [ -z "$runtime" ]; then
    launcher_json="$HOME/.local/share/Fravashi/launcher.json"
    if [ -f "$launcher_json" ]; then
        pinned="$(sed -n 's/.*"runtime"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$launcher_json" | head -n1)"
        [ -n "$pinned" ] && runtime="$pinned"
    fi
fi

if [ -n "$runtime" ]; then
    runtime_ok "$runtime" || { echo "The requested container runtime '$runtime' is not available. Install and start it (or clear the launcher.json 'runtime' pin), then re-run." >&2; exit 1; }
else
    for c in docker podman wslc; do
        if runtime_ok "$c"; then runtime="$c"; break; fi
    done
fi
[ -n "$runtime" ] || { echo "No container runtime found. Install Docker Desktop, Podman, or WSLC, then re-run." >&2; exit 1; }

echo "Loading the Fravashi image via $runtime ..."
"$runtime" load --input "$here/fravashi-image.tar"

# Housekeeping: prune dangling <none> images left by earlier installs (dangling only; tagged and in-use
# images are never touched). The image this load just superseded is still referenced by the existing
# container, so it survives this prune; the launcher retires it after recreating the container from the new image.
"$runtime" image prune -f >/dev/null 2>&1 || echo "Note: '$runtime image prune -f' failed; dangling leftovers stay on disk (harmless)."

target="$HOME/.local/share/Fravashi"
mkdir -p "$target"
cp -R "$here/launcher/." "$target/"
chmod +x "$target/fravashi"

# --- F9: put `fravashi` on PATH via ~/.local/bin ---------------------------------------------------------
bindir="$HOME/.local/bin"
mkdir -p "$bindir"
ln -sf "$target/fravashi" "$bindir/fravashi"

case ":$PATH:" in
    *":$bindir:"*) on_path=1 ;;
    *)             on_path=0 ;;
esac

if [ "$(uname)" = "Linux" ]; then
    apps="$HOME/.local/share/applications"
    mkdir -p "$apps"
    # The launcher entry is named for the command the owner types, which since the rename is the product
    # name too (backlog 32).
    cat > "$apps/fravashi.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Fravashi
Exec=$target/fravashi
Terminal=false
Categories=Development;
EOF
fi

echo ""
echo "Installed. Run 'fravashi' to start Fravashi. Commands:"
echo "  fravashi             Start Fravashi and open the browser."
echo "  fravashi onboarding  Open the onboarding / settings wizard."
echo "  fravashi stop        Stop the container (the reserved port is kept)."
echo "  fravashi status      Show container state, port, URL, data dir, image version."
echo "  fravashi website     Open the product website."
echo "  fravashi logs        Print recent container logs (--tail N)."
echo "  fravashi help        Full command reference."
echo ""
echo "Note: the notification-area tray is Windows-only today; these commands are the full surface here."
if [ "$on_path" -eq 0 ]; then
    echo "WARNING: $bindir is not on your PATH. Add this to your shell profile:"
    echo "  export PATH=\"\$HOME/.local/bin:\$PATH\""
fi
