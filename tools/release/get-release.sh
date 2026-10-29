#!/usr/bin/env bash
# Download, join and verify a Fravashi release, keep a handle on the current image, load the new one and
# smoke-test it in an isolated container. Stops BEFORE installing. Usage: get-release.sh v1.0.0 <workdir>
# Uses podman when it is installed, else wslc (the dev host's only engine since 2026-10-04, ADR 0029; wslc.exe is not
# on PATH, so it is called from where WSL installs it).
#
# The release is found through its signed release-manifest.json on the update channel: the manifest's
# signature is checked against setup/release/manifest-signing-public.pem with openssl, as every fravashi.exe checks it, and
# the files are downloaded from the URLs it names, so no file name is derived from the tag. CHANNEL overrides the
# channel (default production's, https://nova-globen.se/fravashi/updates; a test host's channel for a test build).
# STOP_AFTER_VERIFY=1 stops once the files are down and verified, before any container engine is touched.
set -euo pipefail
export MSYS_NO_PATHCONV=1
TAG="$1"; WORK="$2"; VER="${TAG#v}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PUBLIC_KEY="${PUBLIC_KEY:-$REPO_ROOT/setup/release/manifest-signing-public.pem}"
# Git for Windows' openssl is a native program and MSYS_NO_PATHCONV is set, so it is handed a C:/ path there.
command -v cygpath >/dev/null 2>&1 && PUBLIC_KEY="$(cygpath -m "$PUBLIC_KEY")"
# The first Python that actually runs (on Windows "python3" can be the Store alias, which only prints an advert).
PY=python3; "$PY" -c "" >/dev/null 2>&1 || PY=python
mkdir -p "$WORK"; cd "$WORK"

BASE="${CHANNEL:-https://nova-globen.se/fravashi/updates}/${VER}"
SUMS="SHA256SUMS.txt"
curl -fsSL -o release-manifest.json "${BASE}/release-manifest.json" || {
    echo "No release-manifest.json at ${BASE}/. Check the version." >&2; exit 1; }
curl -fsSL -o release-manifest.sig "${BASE}/release-manifest.sig" || { echo "No release-manifest.sig beside the manifest: refusing an unsigned release." >&2; exit 1; }
openssl base64 -d -A -in release-manifest.sig -out release-manifest.sig.der
if ! openssl dgst -sha256 -verify "$PUBLIC_KEY" -signature release-manifest.sig.der release-manifest.json >/dev/null 2>&1; then
    echo "release-manifest.json at ${BASE}/ is NOT signed by the release key: refusing it." >&2; exit 1
fi
rm -f release-manifest.sig.der
echo "manifest: signature verified against $(basename "$PUBLIC_KEY")"
# One KEY=value per line from the verified manifest: the version, then each file's name, sha256 and URLs.
PLAN=$("$PY" - "$VER" <<'PY'
import json, re, sys
m = json.load(open("release-manifest.json", encoding="utf-8-sig"))
want = sys.argv[1]
if m.get("product") != "fravashi": sys.exit("the manifest is not Fravashi's")
if m.get("version") != want: sys.exit("the manifest describes version %s, not %s" % (m.get("version"), want))
v = next((v for v in m.get("variants", []) if v.get("name") == "full"), None) or sys.exit("no full variant")
img = next((i for i in v.get("images", []) if i.get("os") == "linux" and i.get("arch") == "amd64"), None) or sys.exit("no linux/amd64 image")
lau = next((l for l in v.get("launchers", []) if l.get("rid") == "win-x64" and l.get("assetUrl")), None) or sys.exit("no win-x64 launcher")
urls = img.get("partUrls") or ([img["assetUrl"]] if img.get("assetUrl") else sys.exit("no image URL"))
for name in (img["asset"], lau["asset"]):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name): sys.exit("not a plain file name: %r" % name)
for u in urls + [lau["assetUrl"]]:
    if not u.startswith("https://"): sys.exit("not an https URL: %s" % u)
print("IMG=" + img["asset"]); print("IMG_SHA=" + img["sha256"]); print("IMG_URLS=" + " ".join(urls))
print("LAUNCHER=" + lau["asset"]); print("LAUNCHER_SHA=" + lau["sha256"]); print("LAUNCHER_URL=" + lau["assetUrl"])
PY
) || { echo "the manifest at ${BASE}/ cannot be installed from (see above)." >&2; exit 1; }
IMG=$(printf '%s\n' "$PLAN" | sed -n 's/^IMG=//p'); IMG_SHA=$(printf '%s\n' "$PLAN" | sed -n 's/^IMG_SHA=//p')
IMG_URLS=$(printf '%s\n' "$PLAN" | sed -n 's/^IMG_URLS=//p')
LAUNCHER=$(printf '%s\n' "$PLAN" | sed -n 's/^LAUNCHER=//p'); LAUNCHER_SHA=$(printf '%s\n' "$PLAN" | sed -n 's/^LAUNCHER_SHA=//p')
LAUNCHER_URL=$(printf '%s\n' "$PLAN" | sed -n 's/^LAUNCHER_URL=//p')
curl -fsSL -O "${BASE}/${SUMS}"
curl -fsSL --retry 3 -o "$LAUNCHER" "$LAUNCHER_URL"
: > "$IMG"
N=0
for u in $IMG_URLS; do curl -fsSL --retry 3 -o "slice.tmp" "$u"; cat slice.tmp >> "$IMG"; rm -f slice.tmp; N=$((N+1)); done
echo "parts: $N"
# The signed manifest's hashes first, then SHA256SUMS.txt's lines for the same two names: both over WHOLE files.
printf '%s  %s\n%s  %s\n' "$IMG_SHA" "$IMG" "$LAUNCHER_SHA" "$LAUNCHER" | sha256sum -c -
awk -v a="$IMG" -v b="$LAUNCHER" '{ n = $2; sub(/^\*/, "", n) } n == a || n == b' "$SUMS" > sums.selected
[ "$(wc -l < sums.selected)" -eq 2 ] || { echo "$SUMS does not list both $IMG and $LAUNCHER." >&2; exit 1; }
sha256sum -c sums.selected; rm -f sums.selected

if [ "${STOP_AFTER_VERIFY:-}" = "1" ]; then
    echo "VERIFIED: $LAUNCHER and $IMG in $WORK (STOP_AFTER_VERIFY=1)"
    exit 0
fi

if command -v podman >/dev/null 2>&1; then
    ENGINE=podman; E=podman; LATEST=localhost/fravashi:latest
else
    ENGINE=wslc; E="/c/Program Files/WSL/wslc.exe"; LATEST=fravashi:latest
    [ -x "$E" ] || { echo "Neither podman nor wslc is installed." >&2; exit 1; }
fi
echo "engine: $ENGINE"

mkdir -p bundle && tar -xzf "$LAUNCHER" -C bundle

# Keep a handle on the image that is installed now, so a bad one can be backed out with one retag.
CUR=$("$E" exec fravashi sh -c 'printf %s "$FRAVASHI_VERSION"' 2>/dev/null || echo unknown)
# Two names: the launcher retires the superseded image by id, and the engine removes an image by id only when exactly
# one tag names it; with two it refuses (backlog 165). Nothing to keep when no image is installed yet.
if "$E" image inspect "$LATEST" >/dev/null 2>&1; then
    "$E" tag "$LATEST" "${LATEST%:latest}:backup-${CUR}" 2>/dev/null || true
    "$E" tag "$LATEST" "${LATEST%:latest}:backup-${CUR}.hold" 2>/dev/null || true
fi
"$E" load --input "$IMG" | tail -1
# The bundle names the image localhost/fravashi:latest; on wslc that is not the name the launcher reads.
[ "$ENGINE" = wslc ] && "$E" tag localhost/fravashi:latest "$LATEST"

# Smoke test: own name, own data, own port, all removed afterwards.
SMOKE_DATA="$WORK/smoke-data"
if [ "$ENGINE" = podman ]; then
    podman rm -f fravashi-smoke >/dev/null 2>&1 || true
    podman volume rm -f fravashi-smoke-data >/dev/null 2>&1 || true
    podman run -d --name fravashi-smoke -v fravashi-smoke-data:/data -p 19090:8080 \
      -e FRAVASHI_DATA_DIR=/data -e ASPNETCORE_HTTP_PORTS=8080 "$LATEST" >/dev/null
else
    "$E" remove --force fravashi-smoke >/dev/null 2>&1 || true
    rm -rf "$SMOKE_DATA"; mkdir -p "$SMOKE_DATA"
    "$E" run --detach --name fravashi-smoke --volume "$(cygpath -w "$SMOKE_DATA"):/data" --publish 127.0.0.1:19090:8080 \
      --user 1000:1000 --env FRAVASHI_DATA_DIR=/data --env ASPNETCORE_HTTP_PORTS=8080 "$LATEST" >/dev/null
fi
CODE=000
for i in $(seq 1 30); do
  if [ "$ENGINE" = podman ]; then
    # Probed from INSIDE the podman machine, because an ad hoc published port is not always reachable from Windows.
    CODE=$(podman machine ssh "curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:19090/healthz" 2>/dev/null || true)
  else
    # wslc publishes on 127.0.0.1 and it answers from Windows directly.
    CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:19090/healthz 2>/dev/null || true)
  fi
  [ "$CODE" = "200" ] && break
  sleep 3
done
echo "smoke healthz: ${CODE}"
"$E" exec fravashi-smoke sh -c 'echo "image version: $FRAVASHI_VERSION"'
"$E" logs fravashi-smoke 2>&1 | grep -iE "searched by meaning|unhandled|crit|fail" | head -5 || true
if [ "$ENGINE" = podman ]; then
    podman rm -f fravashi-smoke >/dev/null 2>&1; podman volume rm -f fravashi-smoke-data >/dev/null 2>&1
    # podman machine ssh leaves a file named NUL in the current folder under Git Bash (its known-hosts file is Windows'
    # null device, which Git's own ssh takes as a file name); this runs in the work folder, so remove it there.
    powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '\\\\?\\$(cygpath -w "$WORK")\\NUL' -ErrorAction SilentlyContinue" || true
else
    "$E" remove --force fravashi-smoke >/dev/null 2>&1 || true
    rm -rf "$SMOKE_DATA"
fi
[ "$CODE" = "200" ] || { echo "SMOKE TEST FAILED: do not install. Back out with: $ENGINE tag ${LATEST%:latest}:backup-${CUR} $LATEST"; exit 1; }

mv "$IMG" "bundle/fravashi-win-x64/fravashi-image.tar"
echo "READY: run install-windows.ps1 in $(cygpath -w "$WORK/bundle/fravashi-win-x64")"
