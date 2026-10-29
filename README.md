# Nova Globen Fravashi

Fravashi is a self-hosted, agent-native studio for software work. It runs on your own Windows machine, in a container,
and puts a coding agent next to your work items, your wiki, your code and your repository, with every change the agent
makes approved by you first.

- **Sessions with a coding agent** (Claude Code), in the studio's own chat, every tool call approved in the window.
- **A tracker and a wiki** of its own, with sync to Jira, Azure DevOps and Confluence.
- **A code page** with a real editor, and a **git page** with branches, history, diffs and pull requests on GitHub,
  Azure DevOps and Forgejo.
- **An autopilot** that takes ready work items through a plan, a build and a review.

## Install

Download `FravashiSetup.exe` from the newest release on the
[Releases page](https://github.com/nova-globen/fravashi/releases) and run it. It checks the release's signed manifest,
downloads the right files for your machine, verifies them, and installs. A container runtime (Docker, Podman or WSL's
`wslc`) must already be installed.

Every release also carries the files the setup downloads, `SHA256SUMS.txt` and the signed `release-manifest.json`, for
an install by hand (see `INSTALL.md` inside `fravashi-win-x64.tar.gz`).

## Build from source

You need the .NET 10 SDK.

```
dotnet build Fravashi.sln
dotnet test Fravashi.sln
```

`src/Fravashi.Desktop` (the `fravashi.exe` window and launcher) builds on Windows only; elsewhere build with
`-p:EnableWindowsTargeting=true`. The studio image is built from the `Dockerfile` at the root.

## Licence

Copyright (C) Nova Globen AB.

Fravashi is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version. See [`LICENSE`](LICENSE). `SPDX-License-Identifier: AGPL-3.0-or-later`

The licence carries one additional permission under its section 7, for linking or combining Fravashi with Microsoft's
WebView2 runtime and SDK. Its exact text is at the top of [`LICENSE`](LICENSE).

The studio image carries third-party programs under their own licences, listed in each package's notice, and `fravashi.exe`
uses Microsoft's WebView2 runtime. The image does not carry the Claude Code CLI: at its first start the studio
installs the version it is pinned to from npm, which you then use under Anthropic's own terms.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).
