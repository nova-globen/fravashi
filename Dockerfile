# Fravashi studio image (ADR 0003 V3). Multi-stage: SDK builds and publishes the Blazor Server app,
# the smaller ASP.NET runtime image serves it. All durable state lives on the mounted /data volume
# (the SQLite database + the Data Protection key ring), so the container itself is disposable.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests first so the layer caches across source-only changes.
COPY global.json Directory.Build.props Directory.Packages.props Fravashi.sln ./
COPY src/Fravashi.Domain/Fravashi.Domain.csproj                 src/Fravashi.Domain/
COPY src/Fravashi.Application/Fravashi.Application.csproj        src/Fravashi.Application/
COPY src/Fravashi.Infrastructure/Fravashi.Infrastructure.csproj src/Fravashi.Infrastructure/
COPY src/Fravashi.Web/Fravashi.Web.csproj                       src/Fravashi.Web/
RUN dotnet restore src/Fravashi.Web/Fravashi.Web.csproj

# Variant compile switch (I1 / ADR 0006 ruling 3). The release pipeline passes `--build-arg VARIANT=<name>`;
# threading it into `dotnet publish -p:FravashiVariant=<name>` sets Directory.Build.props' DefineConstants so a
# switched-off feature's default is genuinely baked out of the compiled binary (CompiledVariant reads it via #if).
# `full` is the baseline with every feature on.
ARG VARIANT=full

# The service host (Fravashi.Domain's ServiceHost). Empty is production; a test build passes
# `--build-arg SERVICE_HOST=<host>`, so a test host is a build input and never a line in this repository.
ARG SERVICE_HOST=

# Build and publish the web app. NOTE: publish must run its own restore (no --no-restore). A project-only
# `dotnet restore` followed by `dotnet publish --no-restore` silently omits the framework static web assets
# — including _framework/blazor.web.js — which leaves the Blazor Server circuit unable to load and the whole
# UI stuck in non-interactive SSR (buttons do nothing). The restore layer above still warms the NuGet cache,
# so publish's restore resolves from disk without re-downloading.
COPY src/ src/
# The publish is framework-dependent and portable, so it carries native libraries for EVERY platform a
# package supports. Measured 2026-09-21 once ONNX Runtime arrived (backlog 40): 265 MB under runtimes/, of
# which ios, android, macOS and Windows are 235 MB that a Linux container can never load. They are removed
# HERE, in the build stage, because deleting them in a later layer of the runtime stage would shrink
# nothing. Every linux-* folder stays, glibc and musl, x64 and arm64: .NET picks among them at run time.
# The Playwright package (backlog 159) bundles its own Node (121 MB for Linux alone); it is removed the same way,
# because the driver runs on the image's Node through setup/playwright-node.sh, which also cleans its environment.
RUN dotnet publish src/Fravashi.Web/Fravashi.Web.csproj -c Release -o /app/publish -p:FravashiVariant=${VARIANT} -p:FravashiServiceHost=${SERVICE_HOST} \
    && find /app/publish/runtimes -mindepth 1 -maxdepth 1 -type d ! -name 'linux-*' -exec rm -rf {} + \
    && test -f /app/publish/runtimes/linux-x64/native/libonnxruntime.so \
    && test -f /app/publish/runtimes/linux-x64/native/libe_sqlite3.so \
    && rm -rf /app/publish/.playwright/node /app/publish/playwright.ps1 \
    && test -f /app/publish/.playwright/package/cli.js

# ---- runtime ----
# The SDK the runtime stage copies in for the agents (backlog 151). A stage of its own rather than `build`, so
# the copied layer's content does not depend on what the app build did to its own image and stays cacheable.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Variant selector (ADR 0006 ruling 3 / D03-3). The release pipeline passes `--build-arg VARIANT=<name>`
# per the entries in setup/release/variants.json; `full` is the baseline with every feature on. T7/I1 wires
# this (and further feature build-args) to real compile-time + image feature switches so a switched-off
# variant genuinely omits the code path. Recorded on the image as a label for provenance/tamper-evidence (T8).
ARG VARIANT=full
LABEL org.fravashi.variant="${VARIANT}"
ENV FRAVASHI_VARIANT=${VARIANT}

# The bridge release (docs/plans/rename-internal-names.md, phase 2). A fravashi.exe from before the internal rename
# updates the IMAGE alone (`fravashi update` and the studio's upgrade), reads the variant from its former label name,
# and refuses an image whose variant is not its own BEFORE it touches the running studio (ProductUpdater.RefuseLabels
# in v0.9.102), putting its own image name back. This image needs the renamed fravashi.exe (new container, image and
# environment names), so under the former label it names a variant no install has: an old executable refuses it and
# keeps its studio running, and the release is installed with FravashiSetup.exe, which replaces fravashi.exe too.
# The cleanup release removes this label.
LABEL org.didadeck.variant="bridge-release-install-with-FravashiSetup"

# Variant feature switches as IMAGE features (I1). The release pipeline passes these per variant (setup/release/
# variants.json); the app reads them (VariantFeatureResolver) as overrides of the compiled-in default, so the
# image feature and the compiled default agree. Empty (the default for `full`) means "no override — use the
# compiled default". `corp-vpn` ships GITHUB_LOGIN=false (token-only) + VPN_REQUIRED=true (VPN startup gate).
# `corp-broker` ships BROKER_REQUIRED=true (ADR 0010 / plan 04): brokering on, the local org-key path off, SSO
# sign-in mandatory. The broker endpoint (Broker__BaseUrl) is a DEPLOY-time value (the customer's in-perimeter
# broker host), not a build arg — it is supplied when the container is run, never baked into the image.
ARG GITHUB_LOGIN=
ARG VPN_REQUIRED=
ARG BROKER_REQUIRED=
# Auth-first gate (plan Phase 3, default on): AUTH_REQUIRED can publish the gate off; the IDP_*
# pair BAKES the identity provider for a release (BYOD) so users cannot repoint it - the settings
# fields render read-only, DB rows are ignored, and the image digest pin protects the baked env.
ARG AUTH_REQUIRED=
ARG IDP_ISSUER=
ARG IDP_CLIENT_ID=
ENV FRAVASHI_GITHUB_LOGIN=${GITHUB_LOGIN}
ENV FRAVASHI_VPN_REQUIRED=${VPN_REQUIRED}
ENV FRAVASHI_BROKER_REQUIRED=${BROKER_REQUIRED}
ENV FRAVASHI_AUTH_REQUIRED=${AUTH_REQUIRED}
ENV FRAVASHI_IDP_ISSUER=${IDP_ISSUER}
ENV FRAVASHI_IDP_CLIENT_ID=${IDP_CLIENT_ID}

# Runtime OS dependencies. The aspnet image is minimal; the studio needs several tools at runtime:
#   - git:        the onboarding access probes and repository explorer shell out to `git`
#                 (git ls-remote for read-access proofs). Without it every git-backed probe fails
#                 in-container with "No such file or directory" though host scenario gates pass.
#   - curl:       fetches the NodeSource setup script and is a general dependency of the CLIs.
#   - util-linux: provides `script`, used to give the agent CLIs a pseudo-TTY when their interactive
#                 login flow demands one (the login proxy runs `script -qec "<cmd>" /dev/null`).
#   - ca-certificates: TLS trust for the CLIs' network calls.
#   - ripgrep:    the Code page's find-in-files engine (ADR 0015); without it the app degrades to a
#                 slower managed scanner, so keep it installed.
#   - socat:      NOT used by the studio itself. The host's app runner (backlog 87, ADR 0027) starts a
#                 relay container FROM THIS IMAGE with `--entrypoint socat`, so a verifier run's one
#                 published port needs no third image pulled from anywhere. A run's application sits on
#                 an internal network, which cannot publish a port (measured on podman 6.1), hence the relay.
# Install lean and drop the apt lists.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl ca-certificates util-linux ripgrep socat \
    && rm -rf /var/lib/apt/lists/*

# ---- AI coding-agent CLIs (N1-N3) ----
# N3 decision: a UNIFORM npm install on a pinned Node 22 LTS. Justification:
#   - The Claude Code (@anthropic-ai/claude-code) npm package vendors a per-platform NATIVE binary
#     (Node is only the installer, not the runtime), so npm gives us the same native binary the
#     standalone installer would, through one mechanism. A native curl-installer would add a second,
#     divergent path for no size or capability win once Node is already present.
#   - Node ships in the image anyway: the sync engine's tooling and the nvs layout on the data volume
#     both need it, so nothing is carried here only for the CLI's sake.
# EVERY CLI VERSION IS PINNED (backlog 61 / ADR 0023). The studio drives the agent CLI's stream-json
# protocol, its PreToolUse hook contract and (ADR 0019) an MCP integration, none of which the vendors
# publish as a stable contract, so an unpinned resolve at build time or at launch would let a breaking
# vendor release reach a user's machine unannounced. AgentRuntimeCatalog carries the SAME number, and
# AgentCliPinTests reads this file and fails the build when the two disagree. Promotion is the ordered,
# contract-test-gated procedure in ADR 0023, never a silent resolve.
#
# RETIRED to v2, decided 2026-09-22 (docs/v2.md section 1): the image no longer bakes the Codex or
# Copilot CLIs. This is the largest single effect that decision has on what a user downloads, because both
# were redistributed binaries in every release bundle. Their pins are kept below so v2 starts from the pair
# that was actually shipped and measured, rather than resolving whatever is current by then.
#
# ARG CODEX_VERSION=0.146.0
# ARG COPILOT_VERSION=1.0.78
#         @openai/codex@${CODEX_VERSION} \
#         @github/copilot@${COPILOT_VERSION} \
#
# NOT BAKED since 2026-10-29: the published image does not redistribute the Claude Code CLI. It is installed from npm,
# under Anthropic's own terms, by update-on-launch (AgentRuntimeUpdateService) at the studio's first start, into the
# writable /data/.npm-global prefix below, at exactly this pin; a conversation started before it lands waits for it
# (ClaudeSessionRunner). The ARG stays the one place the pin is written: AgentCliPinTests holds it equal to
# AgentRuntimeCatalog, which is what the install reads.
ARG CLAUDE_CODE_VERSION=2.1.280
# Node is pinned to the 22.x LTS line via NodeSource. The --version call at the end is the loud failure: a version
# that does not exist for this architecture fails the build here, not in the field.
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/* \
    && node --version

# Backlog 40 (decided 2026-09-19): the sentence model behind wiki search by meaning, BAKED IN because
# the container runs offline (the same reason ADR 0012 vendors Monaco). Baking it makes this a redistribution
# question, so it is pinned the way the agent CLIs are: a repository, an exact REVISION and the SHA-256 of
# each file, checked here so a moved or tampered file fails the build instead of shipping. The model is
# all-MiniLM-L6-v2, Apache-2.0 (read from the model card at this revision), 384 dimensions; the plain fp32
# export is used because the quantised ones are per-CPU. EmbeddingModelPinTests reads these lines.
# It sits ABOVE the app layer on purpose (backlog 98): a version bump must not download 90 MB again.
ARG EMBED_MODEL_REPO=sentence-transformers/all-MiniLM-L6-v2
ARG EMBED_MODEL_REVISION=1110a243fdf4706b3f48f1d95db1a4f5529b4d41
ARG EMBED_MODEL_SHA256=6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452
ARG EMBED_VOCAB_SHA256=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
RUN mkdir -p /opt/fravashi/models/embed \
    && cd /opt/fravashi/models/embed \
    && curl -fsSL -o model.onnx "https://huggingface.co/${EMBED_MODEL_REPO}/resolve/${EMBED_MODEL_REVISION}/onnx/model.onnx" \
    && curl -fsSL -o vocab.txt "https://huggingface.co/${EMBED_MODEL_REPO}/resolve/${EMBED_MODEL_REVISION}/vocab.txt" \
    && echo "${EMBED_MODEL_SHA256}  model.onnx" | sha256sum -c - \
    && echo "${EMBED_VOCAB_SHA256}  vocab.txt" | sha256sum -c - \
    && chmod -R a-w /opt/fravashi/models
ENV FRAVASHI_EMBED_MODEL_DIR=/opt/fravashi/models/embed
ENV FRAVASHI_EMBED_MODEL=all-MiniLM-L6-v2@${EMBED_MODEL_REVISION}

# ---- the agent browser (backlog 159, decided 2026-09-27: in this container for this version) ----
# Headless Chromium for the agents' browser tools, BAKED at build time because the container runs offline, and pinned
# the way the CLI is: PLAYWRIGHT_VERSION must equal Microsoft.Playwright in Directory.Packages.props (PlaywrightPinTests
# reads both), because that version's browsers.json fixes the Chromium revision (1243 for 1.63.0). Only the headless
# shell, which is all a headless launch uses. The install also fetches Playwright's own ffmpeg build, which is LGPL and
# used only to record video, a feature nothing here uses; it is removed, because LGPL is not on the approved list. `--with-deps` installs the system libraries Chromium needs, as root and
# before USER; the npm cache it leaves is cleared. Above the app layer so a version bump does not re-ship it (backlog
# 98). PLAYWRIGHT_NODEJS_PATH points the .NET package at the wrapper that starts its driver with a clean environment.
ARG PLAYWRIGHT_VERSION=1.63.0
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/fravashi/ms-playwright
RUN npx -y playwright@${PLAYWRIGHT_VERSION} install --with-deps --only-shell chromium \
    && rm -rf /root/.npm /root/.cache /var/lib/apt/lists/* /opt/fravashi/ms-playwright/ffmpeg-* \
    && test -z "$(find /opt/fravashi/ms-playwright -maxdepth 1 -name 'ffmpeg-*' -print -quit)" \
    && test -n "$(find /opt/fravashi/ms-playwright -maxdepth 1 -name 'chromium_headless_shell-*' -print -quit)" \
    && chmod -R a-w /opt/fravashi/ms-playwright
COPY --chmod=0555 setup/playwright-node.sh /usr/local/bin/fravashi-playwright-node
ENV PLAYWRIGHT_NODEJS_PATH=/usr/local/bin/fravashi-playwright-node

# ---- the .NET SDK, for the agents (backlog 151, decided 2026-09-26) ----
# This version's build runner IS the studio container: an agent session builds and tests a workspace in its
# contained copy with the tools baked here, and a runner a developer chooses (their own image, CI, the host)
# is v2 (docs/v2.md section 5). The SDK is copied whole from the official SDK image of the same 10.0 line the
# runtime above comes from, so it is pinned the way the runtime is and never resolved at launch; it lands over
# the runtime's own /usr/share/dotnet, which the SDK image is a superset of. Roughly 620 MB uncompressed, above
# the app layer so a version bump does not re-ship it (backlog 98). The three variables keep the first `dotnet`
# an agent runs from printing a welcome banner and phoning home; NuGet's cache lands under HOME on the volume.
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

COPY --from=build /app/publish ./

# Release version embedded at build (F1 / ADR 0006). The pipeline passes `--build-arg VERSION=<x.y.z>` (the git
# tag without the leading `v`); a plain local build defaults to 0.0.0. This is the SINGLE SOURCE OF TRUTH for
# "what version am I": the running image IS this release, so the version + variant are surfaced as environment
# variables the app reads (IProductInfo). The update check (T5) and digest pin (T8) key off these.
# Declared HERE, below the apt / NodeSource / npm-CLI layers and the app COPY, on purpose (backlog 98): an ARG
# invalidates the cache of every layer after it, so declaring VERSION at the top of the stage made every
# version bump rebuild and re-ship roughly 1.5 GB of layers whose content had not changed. Nothing above this
# line reads VERSION, so a bump now costs only the label and env layers below.
ARG VERSION=0.0.0
LABEL org.fravashi.version="${VERSION}"
ENV FRAVASHI_VERSION=${VERSION}

# ---- non-root + read-only-rootfs readiness (F4 hardening / ADR 0007) ----
# The launcher runs the container hardened: non-root (--user 1000), and — on Docker/Podman — a read-only rootfs
# with only /data (bind, DB + Data Protection keys) and /tmp (tmpfs) writable. (The /repos working-copy volume
# retired with the in-container clone model, plan 2c: host folders opened as workspaces are the working copies.)
# To make that correct the image (a) bakes a uid-1000 user and (b) redirects every runtime write path
# onto a writable mount:
#   - HOME  -> /data/home        (git/CLI config + credentials; persistent, survives read-only rootfs)
#   - npm global prefix -> /data/.npm-global, prepended to PATH, set AFTER the build-time global install so the
#     baked CLIs stay at /usr (read-only) while update-on-launch (T5) writes its copies to the writable /data
#     prefix that shadows them on PATH. Update-on-launch installs the SAME pinned versions the ARGs above bake
#     (`npm install -g <pkg>@<pinned>`), so the shadowing copy is a repair of a drifted prefix, not an upgrade.
#     Without this, in-container CLI installs would fail under a read-only rootfs. wslc has no read-only rootfs,
#     so it is unaffected either way.
# The entrypoint creates HOME + the npm prefix at start (the /data mount is empty until then).
# The aspnet:10.0 base image already ships a non-root user at uid/gid 1000 (`app`), so we reuse it rather than
# create one: we only need the mount point to exist and be owned by 1000.
# /workspaces is the launcher's named volume. Podman chowns it through the ":U" suffix, but wslc refuses ":U" and
# Docker never had it, so a fresh volume there would come up root-owned and fail the studio's integrity check. Both
# copy the image directory's ownership into a fresh named volume on first mount, so the image ships it owned by 1000
# (backlog 186, measured on wslc).
RUN mkdir -p /data /workspaces && chown -R 1000:1000 /data /workspaces
ENV HOME=/data/home
ENV NPM_CONFIG_PREFIX=/data/.npm-global
ENV PATH=/data/.npm-global/bin:${PATH}

COPY --chmod=0555 setup/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

# ---- no binary that can raise privileges (ADR 0029, D4) ----
# wslc cannot pass --cap-drop or no-new-privileges, so the image does the part it can: strip every setuid/setgid bit
# and file capability, then check that none remains (the script fails the build otherwise). The label is stamped only
# after that check passed, and it is what the launcher reads before crediting this protection on the posture card, so
# an older image is never credited with it. Last root step, so nothing added later in this stage can bring one back.
COPY --chmod=0555 setup/strip-privileges.sh /tmp/strip-privileges.sh
RUN /tmp/strip-privileges.sh && rm /tmp/strip-privileges.sh
LABEL org.fravashi.no-setuid="checked"

USER 1000:1000

# The data directory is the mounted volume; the app resolves it from FRAVASHI_DATA_DIR.
ENV FRAVASHI_DATA_DIR=/data
# Marks that we are running inside the studio container. The agent-runtime update-on-launch service
# (AgentRuntimeUpdateService) only runs when this is set, so a developer running the app on their host
# never has its CLIs touched, all install happens in the container. Update-on-launch is owner-mandated
# (supersedes ADR 0003 V3's "nothing at startup"): it runs in the BACKGROUND, is timeout-bounded, and a
# failure never blocks the app (see the decision log). Since backlog 61 it installs the PINNED version
# from AgentRuntimeCatalog, never an unpinned release, so a launch cannot change which protocol the
# session runner is talking to.
ENV FRAVASHI_IN_CONTAINER=1
# Serve on 8080 inside the container; the launcher maps it to the host port it reserves.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME ["/data"]

# Liveness: the launcher and any orchestrator probe /healthz (200 = up + database opens). The entrypoint first
# creates the writable-mount-backed HOME + npm prefix (needed under a read-only rootfs) then execs the app.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
