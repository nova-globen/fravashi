#!/usr/bin/env bash
# Builds a DEV image of the studio from the committed HEAD, tests the commit on Linux first, smoke-tests the image,
# and installs it into THIS machine's fravashi. Decided 2026-09-29: dev images are for a fix-and-retry loop (the
# self-hosting trials), where a release per round costs about forty minutes of a shared runner and a download.
# Releases stay CI-only (directive 2026-09-19, the releasing-fravashi skill), and nothing here makes one.
#
# What keeps the two reasons behind that directive true for a local build:
#   - the version can never collide with a release: the NEXT patch as a pre-release carrying the commit
#     (0.9.40-dev.9812867 after v0.9.39), which sorts after the release it follows and before the real next one,
#   - the commit is recorded in the image (label org.fravashi.commit) as well as in the version,
#   - the image is built from `git archive HEAD`, never the working tree, and a tree with uncommitted changes to
#     tracked files is refused, so what runs is exactly a commit,
#   - it is never tagged, pushed, uploaded or handed to anyone: it exists on this machine only.
# And the third thing a local build used to skip: the Linux test run CI does. The whole solution is tested in a
# throwaway SDK container with Node and the pinned ripgrep, temp inside a git repository, as the runner has them. The
# commit goes in as ONE tarball and is unpacked on the container's own disk, so no small-file work crosses wslc's
# Windows bind mount (slow there); the NuGet cache is a named volume for the same reason.
#
# The engine is wslc, the dev host's only one since 2026-10-04 (ADR 0029). It is not on PATH, has no `wait` and takes no
# Go templates, so this script calls it by its path, polls inspect, and reads JSON.
#
# Only the IMAGE is rebuilt. fravashi.exe (Desktop, Launcher, Bridge, AppRunner) stays the installed release's, so a commit
# that changed any of those since the release this machine runs is refused: that needs a real release.
#
# Usage (Git Bash, from anywhere in the repository):
#   tools/release/dev-image.sh                 test, build, smoke-test, install
#   SKIP_TESTS=1 tools/release/dev-image.sh    only when THIS commit's Linux tests already passed (a rebuild)
#   NO_INSTALL=1 tools/release/dev-image.sh    stop after the smoke test
#   TESTS_ONLY=1 tools/release/dev-image.sh    only the Linux test run (it prints how long it took)
#   FRAVASHI_DEBUG_PORT=9333 tools/release/dev-image.sh
#                                              restart fravashi with its window's DevTools port open (the window
#                                              probes); finish with `fravashi exit` and a normal start to close it
# Back out: wslc tag fravashi:backup-<previous version> fravashi:latest, clear pinnedImageDigest in
# %LOCALAPPDATA%\Fravashi\launcher.json, `fravashi exit`, `fravashi stop`, start fravashi.
set -euo pipefail
export MSYS_NO_PATHCONV=1

die() { echo "dev-image: $*" >&2; exit 1; }

E="/c/Program Files/WSL/wslc.exe"
[ -x "$E" ] || die "wslc is not installed at $E."
LATEST=fravashi:latest

# A field of the first object a `wslc inspect` prints, by path (State Running); booleans print lower case.
jget() { python -c 'import json,sys
d=json.load(sys.stdin)
v=d[0] if isinstance(d,list) else d
for k in sys.argv[1:]: v=v[k]
print(str(v).lower() if isinstance(v,bool) else v)' "$@"; }

# Waits for a container to stop and prints its exit code (wslc has no `wait`).
wait_for() {
    while [ "$("$E" inspect --type container "$1" 2>/dev/null | jget State Running 2>/dev/null || echo false)" = "true" ]; do
        sleep 5
    done
    "$E" inspect --type container "$1" | jget State ExitCode
}

repo="$(git rev-parse --show-toplevel)"
cd "$repo"

[ -z "$(git status --porcelain --untracked-files=no)" ] || die "tracked files have uncommitted changes; commit first (the image is built from HEAD, never from the working tree)."
sha="$(git rev-parse HEAD)"
short="$(git rev-parse --short=7 HEAD)"
if git describe --exact-match --tags --match 'v*' HEAD >/dev/null 2>&1; then
    die "HEAD is the release $(git describe --exact-match --tags --match 'v*' HEAD); install that release instead of a dev image of it."
fi

release="$(git describe --tags --abbrev=0 --match 'v[0-9]*' HEAD)"
IFS=. read -r major minor patch <<<"${release#v}"
version="${major}.${minor}.$((patch + 1))-dev.${short}"

desktop_side=(src/Fravashi.Desktop src/Fravashi.Launcher src/Fravashi.Bridge src/Fravashi.AppRunner Directory.Build.props Directory.Packages.props)
if [ "${TESTS_ONLY:-0}" != "1" ] && ! git diff --quiet "$release" HEAD -- "${desktop_side[@]}"; then
    git diff --stat "$release" HEAD -- "${desktop_side[@]}" | tail -5 >&2
    die "fravashi.exe's own code changed since $release; a dev image cannot carry that. Cut a release."
fi
echo "Dev image ${version} from ${sha} (fravashi.exe stays ${release}'s; this machine must be running ${release})."

# Two spellings of every path: MSYS_NO_PATHCONV (needed so /in and /work reach wslc untouched) also stops Git Bash
# translating /c/... for NATIVE programs, so git, wslc, node and fravashi.exe are handed the Windows form.
work="$(cygpath -u "$LOCALAPPDATA")/Temp/fravashi-dev-${short}"
rm -rf "$work"; mkdir -p "$work/context" "$work/devtest"
work_win="$(cygpath -w "$work")"
git archive --format=tar -o "${work_win}\\source.tar" HEAD
tar --force-local -xf "$work/source.tar" -C "$work/context"

test_container="fravashi-devtest-${short}"
if [ "${SKIP_TESTS:-0}" != "1" ]; then
    # The test image: the SDK plus what CI's runner has and the SDK image lacks. Built once and kept (a tagged image,
    # never pruned by the dangling-only rule), because apt and the download on every round would cost a minute each.
    devtest="localhost/fravashi-devtest:sdk10-node-rg14.1.1"
    if ! "$E" image inspect "$devtest" >/dev/null 2>&1; then
        cat > "$work/devtest/Containerfile" <<'CONTAINERFILE'
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends nodejs && rm -rf /var/lib/apt/lists/*
# The same pinned ripgrep, checked against the same hash, that tools/ci/Dockerfile.ci installs for CI.
RUN curl -fsSL --retry 3 -o /tmp/rg.tgz https://github.com/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-unknown-linux-musl.tar.gz \
 && echo "4cf9f2741e6c465ffdb7c26f38056a59e2a2544b51f7cc128ef28337eeae4d8e  /tmp/rg.tgz" | sha256sum -c - \
 && tar -xzf /tmp/rg.tgz -C /usr/local/bin --strip-components=1 ripgrep-14.1.1-x86_64-unknown-linux-musl/rg \
 && rm /tmp/rg.tgz
CONTAINERFILE
        echo "Building the Linux test image once (${devtest}) ..."
        "$E" build -t "$devtest" -f "$work_win\\devtest\\Containerfile" "$work_win\\devtest" >/dev/null
    fi
    "$E" remove --force "$test_container" >/dev/null 2>&1 || true
    tests_started="$(date +%s)"
    # Temp lives inside a git repository, as it does on the runner, where it once decided a test (runs 247, 249).
    "$E" run --detach --name "$test_container" --volume "${work_win}:/in" --volume fravashi-devtest-nuget:/root/.nuget/packages \
        --env DOTNET_NOLOGO=1 --env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$devtest" bash -c '
        set -o pipefail
        mkdir -p /work && tar -xf /in/source.tar -C /work && cd /work && git init -q \
          && git config --global user.email dev@localhost && git config --global user.name dev \
          && mkdir -p .tmp && export TMPDIR=/work/.tmp TMP=/work/.tmp TEMP=/work/.tmp \
          && dotnet build Fravashi.sln -c Release -p:EnableWindowsTargeting=true > /in/build.log 2>&1 \
          && dotnet test Fravashi.sln -c Release --no-build -p:EnableWindowsTargeting=true > /in/test.log 2>&1' >/dev/null
    echo "Linux tests started in ${test_container}${TESTS_ONLY:+; waiting for them}."
fi

finish_tests() {
    rc="$(wait_for "$test_container")"
    echo "Linux test run took $(( $(date +%s) - tests_started )) s."
    "$E" remove --force "$test_container" >/dev/null 2>&1 || true
    if [ ! -f "$work/test.log" ]; then
        tail -20 "$work/build.log" >&2 || true
        die "the Linux build failed before any test ran (log: $work/build.log)."
    fi
    grep -E "^\s+Failed |Passed!|Failed!" "$work/test.log" || true
    [ "$rc" = "0" ] || die "Linux tests failed (log: $work/test.log). Nothing was installed."
    echo "Linux tests green."
}

if [ "${TESTS_ONLY:-0}" = "1" ]; then
    finish_tests
    exit 0
fi

"$E" build -t "localhost/fravashi:dev-${short}" \
    --build-arg "VERSION=${version}" --label "org.fravashi.commit=${sha}" --label "org.fravashi.build=dev" \
    "$work_win\\context" > "$work/image.log" 2>&1 || { tail -20 "$work/image.log" >&2; die "the image did not build (log: $work/image.log)."; }
echo "Image built: localhost/fravashi:dev-${short}"
"$E" image prune -f >/dev/null 2>&1 || echo "Note: 'wslc image prune -f' failed; dangling build leftovers stay on disk."

if [ "${SKIP_TESTS:-0}" != "1" ]; then
    finish_tests
else
    echo "Linux tests SKIPPED (SKIP_TESTS=1): only right when this commit's Linux run already passed."
fi

# Smoke test: own name, own data folder, own port, all removed afterwards. wslc publishes on 127.0.0.1, which answers
# from Windows directly.
smoke_data="$work/smoke-data"; mkdir -p "$smoke_data"
"$E" remove --force fravashi-smoke >/dev/null 2>&1 || true
"$E" run --detach --name fravashi-smoke --volume "$(cygpath -w "$smoke_data"):/data" --publish 127.0.0.1:19090:8080 \
    --user 1000:1000 --env FRAVASHI_DATA_DIR=/data --env ASPNETCORE_HTTP_PORTS=8080 "localhost/fravashi:dev-${short}" >/dev/null
code=000
for _ in $(seq 1 30); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:19090/healthz 2>/dev/null || true)"
    [ "$code" = "200" ] && break
    sleep 3
done
smoke_version="$("$E" exec fravashi-smoke sh -c 'printf %s "$FRAVASHI_VERSION"' 2>/dev/null || true)"
[ "$code" = "200" ] || "$E" logs --tail 30 fravashi-smoke >&2 || true
"$E" remove --force fravashi-smoke >/dev/null 2>&1 || true
rm -rf "$smoke_data"
[ "$code" = "200" ] || die "the smoke test did not reach healthz 200 (got ${code}); nothing installed."
[ "$smoke_version" = "$version" ] || die "the image reports version '${smoke_version}', not '${version}'; nothing installed."
echo "Smoke test: healthz 200, version ${smoke_version}."

if [ "${NO_INSTALL:-0}" = "1" ]; then
    echo "NO_INSTALL=1: stopping here. Install later by re-running without it."
    exit 0
fi

# Install exactly as install-windows.ps1 does for an image-only change: keep a handle on the current image, move
# fravashi:latest, clear the digest pin (trust on first use, ADR 0007), and let the launcher recreate the container.
current="$("$E" exec fravashi sh -c 'printf %s "$FRAVASHI_VERSION"' 2>/dev/null || echo unknown)"
# TWO names, because the launcher retires the superseded image by id after recreating the container, and the engine
# removes an image by id only when exactly one tag names it; with two it refuses (backlog 165).
"$E" tag "$LATEST" "fravashi:backup-${current}" 2>/dev/null || true
"$E" tag "$LATEST" "fravashi:backup-${current}.hold" 2>/dev/null || true
"$E" tag "localhost/fravashi:dev-${short}" "$LATEST"
"$E" image prune -f >/dev/null 2>&1 || true
# fravashi.exe in Programs\Fravashi and the data in %LOCALAPPDATA%\Fravashi. A machine still on a release from before
# the rename has the earlier folders: install the renamed release there first (its first start moves the data).
program_folder=Fravashi; program_exe=fravashi.exe; home_folder=Fravashi
if [ ! -f "$(cygpath -u "$LOCALAPPDATA")/Programs/$program_folder/$program_exe" ]; then
    die "No $program_exe in Programs/$program_folder; install the renamed release on this machine first."
fi
node -e '
  const fs = require("fs"), p = process.argv[1];
  if (fs.existsSync(p)) { const c = JSON.parse(fs.readFileSync(p, "utf8")); if (c.pinnedImageDigest) { c.pinnedImageDigest = null; fs.writeFileSync(p, JSON.stringify(c, null, 2)); console.log("Cleared the image digest pin; fravashi re-pins the dev image on start."); } }
' "$(cygpath -w "$LOCALAPPDATA")\\$home_folder\\launcher.json"

fravashi="$(cygpath -w "$LOCALAPPDATA")\\Programs\\$program_folder\\$program_exe"
fravashi_run="$(cygpath -u "$LOCALAPPDATA")/Programs/$program_folder/$program_exe"
# Windows first, while the studio still answers, so the editor's hot-exit backups are written; then the container.
"$fravashi_run" exit >/dev/null 2>&1 || true
"$fravashi_run" stop >/dev/null 2>&1 || true
if [ -n "${FRAVASHI_DEBUG_PORT:-}" ]; then
    powershell.exe -NoProfile -Command "\$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=${FRAVASHI_DEBUG_PORT} --remote-debugging-address=127.0.0.1'; Start-Process '${fravashi}'"
else
    powershell.exe -NoProfile -Command "Start-Process '${fravashi}'"
fi

want="$("$E" image inspect "localhost/fravashi:dev-${short}" | jget Id)"
running=""
for _ in $(seq 1 50); do
    running="$("$E" inspect --type container fravashi 2>/dev/null | python -c 'import json,sys
d=json.load(sys.stdin)[0]; print(d["Image"], str(d["State"]["Running"]).lower())' 2>/dev/null || true)"
    [ "$running" = "${want} true" ] && break
    sleep 3
done
[ "$running" = "${want} true" ] || die "fravashi did not bring the container up on the dev image (saw '${running}'). Back out with: wslc tag fravashi:backup-${current} ${LATEST}"

# Earlier rounds' tags go, so a loop of ten fixes does not keep ten images: every dev-* tag but this one, and every
# backup of a DEV version but the one just made. A release's backup tag is never touched. rmi BY NAME drops that one
# name, and the image only with its last one; the dangling-only prune then removes an image nothing else names or runs.
for tag in $("$E" images --format json | python -c 'import json,sys
for line in sys.stdin:
    line = line.strip()
    if line:
        r = json.loads(line); print(r["Repository"] + ":" + r["Tag"])' | grep -E '^(localhost/)?fravashi:(dev-|backup-[0-9.]+-dev\.)' || true); do
    case "$tag" in
        "localhost/fravashi:dev-${short}" | "fravashi:backup-${current}" | "fravashi:backup-${current}.hold") ;;
        *) "$E" rmi "$tag" >/dev/null 2>&1 || true ;;
    esac
done
"$E" image prune -f >/dev/null 2>&1 || true

echo "INSTALLED: the studio runs ${version} (${sha}). Back out: wslc tag fravashi:backup-${current} ${LATEST}, clear the pin, restart fravashi."
