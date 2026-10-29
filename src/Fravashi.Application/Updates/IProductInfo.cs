namespace Fravashi.Application.Updates;

/// <summary>
/// The running product's own identity - its release <see cref="Version"/> and <see cref="Variant"/> (ADR 0006 /
/// D03-3). This is the <b>single source of truth for "what version am I"</b>: it is embedded into the container
/// image at build time (the <c>FRAVASHI_VERSION</c> / <c>FRAVASHI_VARIANT</c> build args become environment
/// variables in the Dockerfile), because the running image <em>is</em> that release, definitionally. On a
/// developer host (no build-time embedding) it falls back to the app assembly's informational version and the
/// default variant, so the value is always honest about what is actually running.
/// </summary>
/// <remarks>
/// "Is a critical/available <em>update</em> pending" is a <b>different</b> question with a different source of
/// truth - the signed release manifest, evaluated by the <c>fravashi</c> launcher and surfaced to the app through
/// <see cref="IProductUpdateStatus"/>. The app never re-derives criticality itself (it does not carry the
/// manifest fetch or the release key the launcher verifies it with, backlog 242); it displays what the launcher evaluated.
/// </remarks>
public interface IProductInfo
{
    /// <summary>The installed product version (e.g. <c>1.2.0</c>), or <c>0.0.0</c> when it cannot be determined.</summary>
    string Version { get; }

    /// <summary>The installed variant name (e.g. <c>full</c>). Update checks stay within this variant (D03-3).</summary>
    string Variant { get; }
}
