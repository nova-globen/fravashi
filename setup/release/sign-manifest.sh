#!/usr/bin/env bash
# Signs the update manifest with the release key (backlog 242, ruling 2026-10-07: ECDSA P-256, no new binary).
#
#   RELEASE_MANIFEST_SIGNING_KEY=<PEM> sign-manifest.sh <release-manifest.json> <release-manifest.sig> [public.pem]
#
# Writes an ECDSA P-256 / SHA-256 signature over the EXACT bytes of the manifest, DER encoded, as base64 on one line:
# the shape Fravashi.Launcher.Releases.ReleaseManifestSignature verifies. The private key is written to a mktemp file
# (mode 0600) that is removed on every exit, and never echoed. When a public key is named (release.yml names the
# committed setup/release/manifest-signing-public.pem, the one every build embeds), the signature is verified against
# it before this succeeds, so a secret that does not match what installs trust is caught here and not by every install.
#
# Exit codes: 0 signed and verified; 3 no key in the environment; 4 the key did not sign or does not match the public
# key. On 3 and 4 no signature file is left behind. ReleaseManifestSigningScriptTests runs this very script.
set -eu

MANIFEST="${1:?usage: sign-manifest.sh <manifest> <out.sig> [public.pem]}"
OUT="${2:?usage: sign-manifest.sh <manifest> <out.sig> [public.pem]}"
PUBLIC="${3:-}"

rm -f "$OUT"
if [ -z "${RELEASE_MANIFEST_SIGNING_KEY:-}" ]; then
    echo "sign-manifest: RELEASE_MANIFEST_SIGNING_KEY is not set; nothing signed." >&2
    exit 3
fi

umask 077
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
printf '%s\n' "$RELEASE_MANIFEST_SIGNING_KEY" > "$WORK/key.pem"

if ! openssl dgst -sha256 -sign "$WORK/key.pem" -out "$WORK/sig.der" "$MANIFEST" 2> "$WORK/err"; then
    echo "sign-manifest: openssl could not sign with RELEASE_MANIFEST_SIGNING_KEY:" >&2
    head -c 400 "$WORK/err" >&2 || true
    exit 4
fi

if [ -n "$PUBLIC" ] && ! openssl dgst -sha256 -verify "$PUBLIC" -signature "$WORK/sig.der" "$MANIFEST" > /dev/null 2>&1; then
    echo "sign-manifest: the signature does not verify against $PUBLIC, so RELEASE_MANIFEST_SIGNING_KEY is not the" >&2
    echo "private half of the key every build trusts. Nothing signed." >&2
    exit 4
fi

openssl base64 -A -in "$WORK/sig.der" > "$OUT"
printf '\n' >> "$OUT"
