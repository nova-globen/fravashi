using System.Text;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// Builds the git credential material the same way everywhere it is needed (the repo clone and the GitHub-wiki
/// read source): an <c>Authorization</c> header carried through <c>GIT_CONFIG_*</c> runtime-config env vars, so
/// the token never lands on the command line or in <c>.git/config</c>.
/// <para>
/// The exact form git-over-HTTPS accepts is host-specific:
/// <list type="bullet">
/// <item>GitHub - <b>every</b> token (PAT and OAuth/SSO alike) goes as HTTP Basic with the
/// <c>x-access-token</c> username. GitHub's smart-HTTP endpoint rejects <c>Authorization: bearer</c>
/// ("remote: invalid credentials"), so an OAuth token must be presented exactly like a PAT.</item>
/// <item>Azure DevOps - a PAT uses the empty-username Basic form (<c>:PAT</c>); an OAuth (Entra) access token
/// is the one case a Bearer token works.</item>
/// </list>
/// </para>
/// </summary>
public static class GitAuthHeader
{
    /// <summary>The <c>AUTHORIZATION:</c> header value for a credential, or null when there is none.</summary>
    public static string? Build(StoredCredential? credential, bool azureDevOpsBasicForm)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            return null;
        }

        var isPat = string.Equals(credential.Method, AuthMethods.Pat, StringComparison.OrdinalIgnoreCase);

        // Azure DevOps is the only host whose git-over-HTTPS accepts an OAuth (Entra) access token as a Bearer
        // token. GitHub rejects Bearer outright - a GitHub OAuth/SSO token must travel as Basic x-access-token,
        // exactly like a GitHub PAT - so only take the Bearer branch for a non-PAT Azure DevOps credential.
        if (azureDevOpsBasicForm && !isPat)
        {
            return $"AUTHORIZATION: bearer {credential.AccessToken}";
        }

        var user = azureDevOpsBasicForm ? string.Empty : "x-access-token";
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{credential.AccessToken}"));
        return $"AUTHORIZATION: basic {basic}";
    }

    /// <summary>The git environment: prompts disabled, plus the credential header (when present) as runtime config.</summary>
    public static Dictionary<string, string> Environment(string? header)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_ASKPASS"] = "echo",
        };

        if (!string.IsNullOrWhiteSpace(header))
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            env["GIT_CONFIG_VALUE_0"] = header;
        }

        return env;
    }
}
