#!/bin/sh
# Leaves the image with no binary that can raise privileges (ADR 0028, D4), then CHECKS it and fails the build if
# anything remains. Run as root in the Dockerfile, just before it switches to uid 1000.
#
# Why: wslc cannot drop capabilities or set no-new-privileges, and the studio already runs as uid 1000 with no
# effective capabilities (CapEff 0, measured on wslc 3.0.1). With no setuid/setgid binary and no file capability in the
# image, nothing that user runs can gain one back. On Docker and Podman, which do drop them, it is a second wall.
# The base image ships twelve setuid/setgid binaries (su, mount, passwd, chfn and others); nothing in the studio needs
# them, and Chromium runs with --no-sandbox (docs/plans/agent-browser.md), so no sandbox helper does either.
# File capabilities are read through extended attributes with python3, because the image has no getcap.
set -eu

find / -xdev -perm /6000 -type f -exec chmod a-s {} +

python3 - <<'EOF'
import os

ATTR = "security.capability"
root_device = os.stat("/").st_dev
found = []
for folder, subfolders, files in os.walk("/"):
    subfolders[:] = [s for s in subfolders if os.lstat(os.path.join(folder, s)).st_dev == root_device]
    for name in files:
        path = os.path.join(folder, name)
        try:
            if not os.path.islink(path) and ATTR in os.listxattr(path, follow_symlinks=False):
                found.append(path)
        except OSError:
            pass

for path in found:
    os.removexattr(path, ATTR, follow_symlinks=False)
    if ATTR in os.listxattr(path, follow_symlinks=False):
        raise SystemExit(f"strip-privileges: could not remove the file capability on {path}")

print(f"strip-privileges: removed {len(found)} file capability set(s)")
EOF

left="$(find / -xdev -perm /6000 -type f)"
if [ -n "$left" ]; then
    echo "strip-privileges: setuid/setgid binaries remain:" >&2
    echo "$left" >&2
    exit 1
fi

echo "strip-privileges: no setuid or setgid binary remains"
