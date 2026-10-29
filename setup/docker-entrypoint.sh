#!/bin/sh
# Fravashi container entrypoint (F4 hardening / ADR 0007).
#
# The container runs non-root (uid 1000) and, on Docker/Podman, with a read-only root filesystem - only the
# /data (bind) and /tmp (tmpfs) mounts are writable. HOME and the npm global prefix are
# redirected onto /data (see the Dockerfile), but /data is an empty mount until runtime, so their directories
# must be created here before the app or any tool writes to them. All writes stay on writable mounts, so this
# works identically under a read-only rootfs (Docker/Podman) and a writable one (wslc).
set -e

mkdir -p "${HOME:-/data/home}" "${NPM_CONFIG_PREFIX:-/data/.npm-global}" 2>/dev/null || true

exec dotnet Fravashi.Web.dll "$@"
