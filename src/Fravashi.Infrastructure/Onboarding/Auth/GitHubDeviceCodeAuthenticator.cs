using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// GitHub OAuth device-flow authenticator. Device flow needs only the client id of an owner-registered GitHub
/// App / OAuth App (no client secret); it must be explicitly enabled on the app. The <c>repo</c> scope covers
/// the repository and its wiki. The acquired token drops straight into the existing GitHub usage - a Bearer
/// token for <c>api.github.com</c> and a git URL credential for <c>ls-remote</c>/clone/push - so nothing
/// downstream changes.
/// </summary>
public sealed class GitHubDeviceCodeAuthenticator : IDeviceCodeAuthenticator
{
    private readonly OAuthDeviceFlow _flow;
    private readonly AuthConfigStore _config;

    public GitHubDeviceCodeAuthenticator(OAuthDeviceFlow flow, AuthConfigStore config)
    {
        _flow = flow;
        _config = config;
    }

    public string Provider => OnboardingProviders.GitHub;

    public Task<DeviceCodeStart> StartAsync(CancellationToken cancellationToken = default) =>
        _flow.StartAsync(ResolveOrThrow(), cancellationToken);

    public Task<DevicePollResult> PollOnceAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        _flow.PollOnceAsync(ResolveOrThrow(), deviceCode, cancellationToken);

    private OAuthDeviceFlow.Config ResolveOrThrow()
    {
        var resolved = _config.ResolveGitHub()
            ?? throw new InvalidOperationException("GitHub sign-in is not configured - set a client id in Sign-in configuration.");
        return new OAuthDeviceFlow.Config(resolved.DeviceCodeUrl, resolved.TokenUrl, resolved.ClientId, resolved.Scope);
    }
}
