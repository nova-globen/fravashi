#!/bin/sh
# The node the agent browser's Playwright driver runs on (backlog 159), named by PLAYWRIGHT_NODEJS_PATH in the
# image. The .NET Playwright package starts its driver as a child of the studio, which would hand the driver, and
# the headless Chromium it launches, the studio's whole environment: the host bridge's token, the window token and
# the integrity key among it. Chromium's own sandbox cannot run in this container, so a renderer compromise would
# read whatever its process tree was given. So the driver starts from an EMPTY environment plus the process basics
# and the three variables the Playwright package sets for its driver, and Chromium inherits exactly that.
set -eu
exec env -i \
  PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" \
  HOME="${HOME:-/tmp}" \
  TMPDIR="/tmp" \
  LANG="C.UTF-8" \
  PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-}" \
  PW_LANG_NAME="${PW_LANG_NAME:-csharp}" \
  PW_LANG_NAME_VERSION="${PW_LANG_NAME_VERSION:-}" \
  PW_CLI_DISPLAY_VERSION="${PW_CLI_DISPLAY_VERSION:-}" \
  /usr/bin/node "$@"
