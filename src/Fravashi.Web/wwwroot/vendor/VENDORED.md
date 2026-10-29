# Vendored web assets

Third-party, prebuilt browser assets committed as-is (no npm/bundler pipeline exists in this repo; these
are served straight from wwwroot). Each folder keeps its upstream LICENSE. Update by re-copying from the
npm package and bumping the version here.

| Folder | Package | Version | License | Notes |
|---|---|---|---|---|
| `monaco/` | `monaco-editor` (npm) | 0.56.0 | MIT | `min/vs` AMD build (upstream deprecates AMD in favor of ESM, but this pinned vendored copy keeps working regardless; revisit on upgrade). Loaded on demand by `codeEditor.js`, only the Code page pays the cost. |
| `material-icons/` | `material-icon-theme` (npm) | 5.37.0 | MIT | File-tree icon SVGs + the `material-icons.json` mapping (extensions/filenames/folders to icon names). Same icon set the reference editor (a VS Code fork) ships as its default file icon theme. |

These files are excluded from the pre-commit em/en-dash gate (`.githooks/pre-commit` skips
`wwwroot/vendor/`): the no-dash rule governs content we author, not upstream minified bundles, and
scanning thousands of vendored files per commit would also make the hook unusably slow.
