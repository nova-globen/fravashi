using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Variants;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// The sign-in gate's truth source (plan Phase 3): whether the gate ENFORCES and whether the
/// stored session is currently valid (hard expiry only; renewal is the refresher's job, proposed
/// decision 4). Enforcement (directive 2026-08-03): the variant's AuthRequired flag makes
/// the gate mandatory BY DEFAULT (an unconfigured install then sets up its IdP on the gate page
/// itself); with the flag published off, the gate still enforces once coordinates are configured.
/// Baked image env coordinates (BYOD releases) win over and lock the settings rows. Reads live
/// on every call, so a settings change acts on the next request.
/// </summary>
public sealed class IdpSessionState(
    IConfigurationStore settings,
    ISecretStore secrets,
    IFeatureFlags? features = null,
    TimeProvider? time = null,
    Func<string, string?>? env = null,
    IdpSessionGrace? grace = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;

    /// <summary>
    /// The gate enforces when the variant requires auth OR the install EXPLICITLY opted in
    /// (settings rows or baked env). The shipped vendor default alone never forces the gate,
    /// so AUTH_REQUIRED=false publishes remain genuinely gate-free out of the box.
    /// </summary>
    public bool Required =>
        (features?.Flags.AuthRequired ?? false) || IdpCoordinates.ExplicitlyConfigured(settings, _env);

    /// <summary>IdP coordinates present (baked env first, settings rows otherwise).</summary>
    public bool Configured => IdpCoordinates.Configured(settings, _env);

    /// <summary>True when the coordinates are image-baked: the settings fields are read-only.</summary>
    public bool Locked => IdpCoordinates.Locked(_env);

    /// <summary>A session envelope exists and its hard expiry has not passed.</summary>
    public bool SignedIn
    {
        get
        {
            var credential = CredentialEnvelope.Parse(secrets.Get(AuthSettings.IdpSessionSecretRef));
            if (credential is null)
            {
                return false;
            }

            return credential.ExpiresUtc is not { } expiry || _time.GetUtcNow() < expiry;
        }
    }

    /// <summary>
    /// What the gate admits on (backlog 234): signed in, or inside the sign-in grace, which holds for one hour past the
    /// session's expiry while its refresh could not reach the identity provider and nothing was refused.
    /// </summary>
    public bool Usable => SignedIn || (grace?.Admits(_time.GetUtcNow(), ExpiresUtc) ?? false);

    /// <summary>
    /// The refresh's outage as the person is told it, or null when there is none: since when, why, and when the session
    /// locks (or locked). <see cref="IdpGraceView.Lapsed"/> is true once the hour is up and the gate has closed.
    /// </summary>
    public IdpGraceView? Grace
    {
        get
        {
            if (grace?.Outage is not { } down || grace.LocksAt(ExpiresUtc) is not { } locks)
            {
                return null;
            }

            return new IdpGraceView(down.Since, down.Reason, locks, Lapsed: _time.GetUtcNow() >= locks);
        }
    }

    /// <summary>What the identity provider said when it last refused to renew the session, or null.</summary>
    public string? RefusalWords => grace?.Refusal?.Words;

    /// <summary>The stored session's expiry; null when there is none or it never expires.</summary>
    public DateTimeOffset? ExpiresUtc =>
        CredentialEnvelope.Parse(secrets.Get(AuthSettings.IdpSessionSecretRef))?.ExpiresUtc;

    public string? DisplayName => settings.Get(AuthSettings.IdpSignedInName);

    public string? Email => settings.Get(AuthSettings.IdpSignedInEmail);
}

/// <summary>
/// The sign-in grace as the person sees it (backlog 234). The instants are carried as instants, never as clock text, so
/// the page formats them in the person's own time zone.
/// </summary>
/// <param name="Since">When the first refresh of this outage failed.</param>
/// <param name="Reason">Why, in plain words (a host and what failed; never a token).</param>
/// <param name="LocksAt">The session's expiry plus the grace.</param>
/// <param name="Lapsed">True once <paramref name="LocksAt"/> has passed and the gate has closed.</param>
public sealed record IdpGraceView(DateTimeOffset Since, string Reason, DateTimeOffset LocksAt, bool Lapsed);
