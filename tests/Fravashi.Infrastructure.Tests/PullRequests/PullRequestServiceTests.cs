using System.Net;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Domain.Git;
using Fravashi.Domain.PullRequests;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.PullRequests;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class PullRequestServiceTests
{
    private sealed class FakeGit(string? remoteUrl) : IGitService
    {
        public bool IsAvailable => true;

        public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult("main");

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitBranch>>([]);

        public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepositoryNode>>([]);

        public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryFile?>(null);

        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitCommit>>([]);

        public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitCommitDetail?>(null);

        public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitCommit>>([]);

        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult(remoteUrl);

        public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitRemoteBranch>>([]);

        public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitGraphCommit>>([]);

        public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitLineBlame?>(null);

        public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitFileChange>>([]);

        public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class OneConnection(string? credentialRef) : IProviderConnectionStore
    {
        public IReadOnlyList<ProviderConnection> GetAll() => GetByKind(ProviderKind.Source);

        public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind) =>
            kind == ProviderKind.Source && credentialRef is not null
                ? [new ProviderConnection("source", ProviderKind.Source, "https://example", credentialRef, "Verified", DateTimeOffset.UnixEpoch)]
                : [];

        public ProviderConnection? Get(string id) => GetByKind(ProviderKind.Source).FirstOrDefault();

        public void Upsert(ProviderConnection connection)
        {
        }

        public void Remove(string id)
        {
        }
    }

    private sealed class OneSetting(string key, string value) : IConfigurationStore
    {
        public string? Get(string k) => k == key ? value : null;

        public IReadOnlyDictionary<string, string?> GetAll() => new Dictionary<string, string?> { [key] = value };

        public void Set(string k, string? v)
        {
        }

        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
        }

        public void Remove(string k)
        {
        }
    }

    private sealed class OneSecret(string? value) : ISecretStore
    {
        public void Set(string reference, string plaintext)
        {
        }

        public string? Get(string reference) => value;

        public void Remove(string reference)
        {
        }
    }

    private sealed record Captured(List<HttpRequestMessage> Requests, List<string> Bodies);

    private sealed class MapHosts(params GitHostRecord[] records) : IGitHostStore
    {
        private readonly Dictionary<string, GitHostRecord> _map =
            records.ToDictionary(r => r.Host, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GitHostRecord> GetAll() => [.. _map.Values];

        public GitHostRecord? Get(string host) => _map.GetValueOrDefault(host);

        public void Upsert(GitHostRecord record) => _map[record.Host] = record;

        public void Remove(string host) => _map.Remove(host);
    }

    private static (PullRequestService Service, List<HttpRequestMessage> Requests, List<string> Bodies) Create(
        string remoteUrl,
        Func<HttpRequestMessage, (HttpStatusCode, string)> responder,
        string sourceProvider = "GitHub",
        string? token = "tok-123",
        GitHostRecord[]? gitHosts = null)
    {
        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            captured.Add(request);
            // Snapshot the body now; the service disposes the request after sending.
            bodies.Add(request.Content is { } content ? content.ReadAsStringAsync().Result : string.Empty);
            return responder(request);
        }));
        var access = new Fravashi.Infrastructure.Git.GitHostAccess(
            new MapHosts(gitHosts ?? []),
            new OneSecret(token is null ? null : CredentialEnvelope.Serialize(StoredCredential.ForPat(token))),
            new OneConnection(token is null ? null : "source:git"),
            new OneSetting("Onboarding:Source:Provider", sourceProvider));
        var service = new PullRequestService(http, new FakeGit(remoteUrl), access);
        return (service, captured, bodies);
    }

    [Fact]
    public async Task Github_remote_is_detected_and_listed_with_a_bearer()
    {
        var (service, requests, bodies) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.OK, """[{"number":7,"title":"Add thing","user":{"login":"ada"},"head":{"ref":"feat"},"base":{"ref":"main"},"state":"open","html_url":"https://github.com/acme/widget/pull/7","created_at":"2026-07-01T00:00:00Z"}]"""));

        (await service.GetProviderAsync()).Should().Be(PullRequestProvider.GitHub);
        var prs = await service.ListAsync(PullRequestState.Open);

        prs.Should().ContainSingle();
        prs[0].Number.Should().Be(7);
        prs[0].SourceBranch.Should().Be("feat");
        requests[0].RequestUri!.Host.Should().Be("api.github.com");
        requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        requests[0].Headers.Authorization!.Parameter.Should().Be("tok-123");
    }

    [Fact]
    public async Task Unknown_host_needs_a_git_hosts_entry_or_the_legacy_flag()
    {
        var (withoutAnything, _, _) = Create("https://git.example.org/acme/widget.git", _ => (HttpStatusCode.OK, "[]"), sourceProvider: "GitHub");
        (await withoutAnything.GetProviderAsync()).Should().Be(PullRequestProvider.None);
        (await withoutAnything.IsConfiguredAsync()).Should().BeFalse();

        // Transitional: the pre-2c onboarding flag still vouches for Forgejo.
        var (withLegacyFlag, _, _) = Create("https://git.example.org/acme/widget.git", _ => (HttpStatusCode.OK, "[]"), sourceProvider: "Forgejo");
        (await withLegacyFlag.GetProviderAsync()).Should().Be(PullRequestProvider.Forgejo);
        (await withLegacyFlag.IsConfiguredAsync()).Should().BeTrue();
        (await withLegacyFlag.ListAsync(PullRequestState.Open)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_git_hosts_entry_names_the_provider_without_any_legacy_flag()
    {
        var (service, requests, _) = Create(
            "https://forge.example.test/nova-globen/fravashi.git",
            _ => (HttpStatusCode.OK, "[]"),
            sourceProvider: "GitHub",
            gitHosts: [new GitHostRecord("forge.example.test", "Forgejo", "githost:forge.example.test:token", DateTimeOffset.UnixEpoch)]);

        (await service.GetProviderAsync()).Should().Be(PullRequestProvider.Forgejo);
        (await service.ListAsync(PullRequestState.Open)).Should().BeEmpty();
        requests[0].RequestUri!.ToString().Should().StartWith("https://forge.example.test/api/v1/repos/nova-globen/fravashi/pulls");
        requests[0].Headers.Authorization!.Scheme.Should().Be("token");
        requests[0].Headers.Authorization!.Parameter.Should().Be("tok-123");
    }

    [Fact]
    public async Task Forgejo_lists_with_the_token_scheme_against_the_instance_api()
    {
        var (service, requests, bodies) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.OK, """[{"number":3,"title":"Fix bug","user":{"login":"grace"},"head":{"ref":"fix"},"base":{"ref":"main"},"state":"open","html_url":"https://git.example.org/acme/widget/pulls/3","created_at":"2026-07-02T00:00:00Z"}]"""),
            sourceProvider: "Forgejo");

        var prs = await service.ListAsync(PullRequestState.Open);

        prs.Should().ContainSingle();
        prs[0].Title.Should().Be("Fix bug");
        requests[0].RequestUri!.ToString().Should().StartWith("https://git.example.org/api/v1/repos/acme/widget/pulls");
        requests[0].Headers.Authorization!.Scheme.Should().Be("token");
    }

    [Fact]
    public async Task Forgejo_create_posts_head_and_base()
    {
        var (service, requests, bodies) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.Created, """{"number":9,"title":"New","user":{"login":"me"},"head":{"ref":"feat"},"base":{"ref":"main"},"state":"open","created_at":"2026-07-03T00:00:00Z"}"""),
            sourceProvider: "Forgejo");

        var created = await service.CreateAsync(new CreatePullRequestRequest("New", "body", "feat", "main"));

        created.Number.Should().Be(9);
        bodies[0].Should().Contain("\"head\":\"feat\"").And.Contain("\"base\":\"main\"");
    }

    [Fact]
    public async Task Forgejo_merge_posts_the_do_verb()
    {
        var (service, requests, bodies) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.OK, "{}"),
            sourceProvider: "Forgejo");

        await service.MergeAsync(4, MergeMethod.Squash);

        requests[0].RequestUri!.ToString().Should().EndWith("/pulls/4/merge");
        bodies[0].Should().Contain("squash");
    }

    [Fact]
    public async Task Ado_remote_uses_basic_pat()
    {
        var (service, requests, bodies) = Create(
            "https://dev.azure.com/org/project/_git/repo",
            _ => (HttpStatusCode.OK, """{"value":[{"pullRequestId":5,"title":"ADO PR","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feat","targetRefName":"refs/heads/main","status":"active","creationDate":"2026-07-04T00:00:00Z"}]}"""));

        var prs = await service.ListAsync(PullRequestState.Open);

        prs.Should().ContainSingle(p => p.Number == 5 && p.SourceBranch == "feat");
        requests[0].Headers.Authorization!.Scheme.Should().Be("Basic");
    }

    [Fact]
    public async Task No_stored_token_still_lists_anonymously_but_refuses_mutations()
    {
        var (service, requests, _) = Create("https://github.com/acme/widget.git", _ => (HttpStatusCode.OK, "[]"), token: null);

        (await service.IsConfiguredAsync()).Should().BeTrue();
        (await service.ListAsync(PullRequestState.Open)).Should().BeEmpty();
        requests[0].Headers.Authorization.Should().BeNull();

        var act = async () => await service.CreateAsync(new CreatePullRequestRequest("t", "b", "a", "main"));
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*stored token*");
    }

    [Fact]
    public async Task Api_failures_surface_the_status_and_body()
    {
        var (service, requests, bodies) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.Forbidden, """{"message":"scope missing"}"""));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*403*scope missing*");
    }

    // ── Backlog 76: a provider violating the support contract must not read like our bug ──────────────────

    [Fact]
    public async Task An_expired_token_names_the_provider_the_repository_and_the_credential()
    {
        var (service, _, _) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        thrown.Which.Message.Should().StartWith("GitHub rejected the credential for acme/widget on github.com (401)")
            .And.Contain("expired, revoked");
    }

    [Fact]
    public async Task A_renamed_or_deleted_repository_is_reported_as_the_providers_state_not_a_generic_error()
    {
        var (service, _, _) = Create(
            "https://dev.azure.com/org/project/_git/repo",
            _ => (HttpStatusCode.NotFound, """{"message":"TF200016: The project does not exist"}"""));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("Azure DevOps does not have org/project/repo on dev.azure.com (404)")
            .And.Contain("renamed, moved or deleted")
            .And.Contain("TF200016");
    }

    [Fact]
    public async Task A_body_that_is_not_the_expected_json_is_named_a_schema_change_not_a_parse_crash()
    {
        var (service, _, _) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.OK, "<html>login</html>"));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("GitHub answered")
            .And.Contain("API shape has changed");
    }

    [Fact]
    public async Task A_field_this_build_reads_going_missing_says_whose_contract_moved()
    {
        // Valid JSON, right shape, no "number": the case that used to escape as KeyNotFoundException
        // ("The given key was not present in the dictionary."), which the Git page does not even catch.
        var (service, _, _) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.OK, """[{"title":"Add thing","state":"open"}]"""));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("GitHub returned a pull request without the 'number' field")
            .And.Contain("API shape has changed");
    }

    [Fact]
    public async Task A_GitHub_Enterprise_host_is_refused_by_name_instead_of_being_asked_of_github_com()
    {
        // A git_hosts entry may name "GitHub" for any host, but every URL in the adapter is api.github.com.
        var (service, requests, _) = Create(
            "https://github.acme.corp/acme/widget.git",
            _ => (HttpStatusCode.OK, "[]"),
            gitHosts: [new GitHostRecord("github.acme.corp", "GitHub", "githost:github.acme.corp:token", DateTimeOffset.UnixEpoch)]);

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("github.acme.corp")
            .And.Contain("GitHub Enterprise Server")
            .And.Contain("not supported yet");
        requests.Should().BeEmpty("the wrong server must not be asked about someone else's repository");
    }

    [Fact]
    public async Task An_on_premises_Azure_DevOps_Server_host_is_refused_by_name()
    {
        var (service, requests, _) = Create(
            "https://tfs.acme.corp/org/project/_git/repo",
            _ => (HttpStatusCode.OK, """{"value":[]}"""),
            gitHosts: [new GitHostRecord("tfs.acme.corp", "AzureDevOps", "githost:tfs.acme.corp:token", DateTimeOffset.UnixEpoch)]);

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("tfs.acme.corp").And.Contain("Azure DevOps Server");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_Azure_DevOps_Services_ssh_remote_is_served_not_refused_as_on_premises()
    {
        // The canonical Services SSH remote puts ssh.dev.azure.com in the origin URL, and RemoteInfo
        // parses it on purpose (its v3 branch). Matching dev.azure.com exactly refused Services itself
        // with an on-premises message that was false for that host, which is the regression this covers.
        var (service, requests, _) = Create(
            "git@ssh.dev.azure.com:v3/org/project/repo",
            _ => (HttpStatusCode.OK, """{"value":[{"pullRequestId":11,"title":"Over ssh","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feat","targetRefName":"refs/heads/main","status":"active","creationDate":"2026-07-05T00:00:00Z"}]}"""));

        (await service.GetProviderAsync()).Should().Be(PullRequestProvider.AzureDevOps);
        var prs = await service.ListAsync(PullRequestState.Open);

        prs.Should().ContainSingle(p => p.Number == 11);
        // The API is always dev.azure.com even when the remote is the ssh host.
        requests[0].RequestUri!.Host.Should().Be("dev.azure.com");
        requests[0].RequestUri!.AbsolutePath.Should().StartWith("/org/project/_apis/git/repositories/repo");
    }

    [Fact]
    public async Task The_legacy_visualstudio_com_ssh_remote_is_addressed_at_the_right_repository()
    {
        // vs-ssh.{org}.visualstudio.com carries v3/{org}/{project}/{repo} like ssh.dev.azure.com does.
        // Read as the web form it parsed to Owner=vs-ssh.org, Project=v3, Repo=org, which passed the
        // Services guard (the host ends in .visualstudio.com) and then asked dev.azure.com about a
        // repository nobody owns: a 404 reported as "renamed, moved or deleted", which was false.
        var (service, requests, _) = Create(
            "git@vs-ssh.org.visualstudio.com:v3/org/project/repo",
            _ => (HttpStatusCode.OK, """{"value":[{"pullRequestId":12,"title":"Legacy ssh","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feat","targetRefName":"refs/heads/main","status":"active","creationDate":"2026-07-05T00:00:00Z"}]}"""));

        (await service.GetProviderAsync()).Should().Be(PullRequestProvider.AzureDevOps);
        var prs = await service.ListAsync(PullRequestState.Open);

        prs.Should().ContainSingle(p => p.Number == 12);
        requests[0].RequestUri!.Host.Should().Be("dev.azure.com");
        requests[0].RequestUri!.AbsolutePath.Should().StartWith("/org/project/_apis/git/repositories/repo");
    }

    [Fact]
    public async Task A_Forgejo_list_that_stops_being_an_array_is_named_as_Forgejos()
    {
        // Valid JSON, wrong shape. Unguarded this surfaced .NET's own "The requested operation requires
        // an element of type 'Array' ...", the generic message this contract exists to prevent.
        var (service, _, _) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.OK, """{"message":"no"}"""),
            sourceProvider: "Forgejo");

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("Forgejo returned the pull-request list")
            .And.Contain("API shape has changed");
    }

    [Fact]
    public async Task A_review_list_that_stops_being_an_array_is_named_on_both_hub_providers()
    {
        var (github, _, _) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.OK, """{"message":"no"}"""));

        var githubAct = async () => await github.GetReviewsAsync(7);
        (await githubAct.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("GitHub returned the review list");

        var (forgejo, _, _) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.OK, """{"message":"no"}"""),
            sourceProvider: "Forgejo");

        var forgejoAct = async () => await forgejo.GetReviewsAsync(3);
        (await forgejoAct.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("Forgejo returned the review list");
    }

    [Fact]
    public async Task An_envelope_that_stops_being_an_array_is_reported_against_the_provider()
    {
        var (service, _, _) = Create(
            "https://dev.azure.com/org/project/_git/repo",
            _ => (HttpStatusCode.OK, """{"count":0}"""));

        var act = async () => await service.ListAsync(PullRequestState.Open);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Azure DevOps returned the pull-request list without the 'value' field");
    }

    [Fact]
    public async Task Forgejo_reviews_list_and_submit_use_the_reviews_endpoint()
    {
        var (service, requests, bodies) = Create(
            "https://git.example.org/acme/widget.git",
            request => request.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """[{"user":{"login":"grace"},"state":"APPROVED","body":"ship it","submitted_at":"2026-07-20T10:00:00Z"}]""")
                : (HttpStatusCode.OK, "{}"),
            sourceProvider: "Forgejo");

        var reviews = await service.GetReviewsAsync(3);
        reviews.Should().ContainSingle();
        reviews[0].Author.Should().Be("grace");
        reviews[0].State.Should().Be("APPROVED");
        requests[0].RequestUri!.ToString().Should().EndWith("/pulls/3/reviews");

        await service.SubmitReviewAsync(3, ReviewVerdict.Approve, "looks good");
        bodies[1].Should().Contain("\"event\":\"APPROVED\"").And.Contain("looks good");

        await service.SubmitReviewAsync(3, ReviewVerdict.RequestChanges, "please fix");
        bodies[2].Should().Contain("\"event\":\"REQUEST_CHANGES\"");
    }

    [Fact]
    public async Task Forgejo_close_patches_the_state()
    {
        var (service, requests, bodies) = Create(
            "https://git.example.org/acme/widget.git",
            _ => (HttpStatusCode.Created, "{}"),
            sourceProvider: "Forgejo");

        await service.CloseAsync(6);

        requests[0].Method.Method.Should().Be("PATCH");
        requests[0].RequestUri!.ToString().Should().EndWith("/pulls/6");
        bodies[0].Should().Contain("\"state\":\"closed\"");
    }

    [Fact]
    public async Task Github_review_events_use_githubs_wording_and_close_patches_state()
    {
        var (service, requests, bodies) = Create(
            "https://github.com/acme/widget.git",
            _ => (HttpStatusCode.OK, "{}"));

        await service.SubmitReviewAsync(7, ReviewVerdict.Approve, string.Empty);
        bodies[0].Should().Contain("\"event\":\"APPROVE\"");
        requests[0].RequestUri!.ToString().Should().EndWith("/pulls/7/reviews");

        await service.CloseAsync(7);
        requests[1].Method.Method.Should().Be("PATCH");
        bodies[1].Should().Contain("\"state\":\"closed\"");
    }

    [Fact]
    public async Task Ado_review_votes_are_deferred_honestly_but_reviews_read_from_reviewer_votes()
    {
        var (service, requests, _) = Create(
            "https://dev.azure.com/org/project/_git/repo",
            _ => (HttpStatusCode.OK, """{"reviewers":[{"displayName":"Ada","vote":10},{"displayName":"Bob","vote":0}]}"""));

        var reviews = await service.GetReviewsAsync(5);
        reviews.Should().ContainSingle();
        reviews[0].Author.Should().Be("Ada");
        reviews[0].State.Should().Be("approved");

        var act = async () => await service.SubmitReviewAsync(5, ReviewVerdict.Approve, string.Empty);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ADO PAT*");
    }

    [Fact]
    public async Task Ado_close_abandons_the_pull_request()
    {
        var (service, requests, bodies) = Create(
            "https://dev.azure.com/org/project/_git/repo",
            _ => (HttpStatusCode.OK, "{}"));

        await service.CloseAsync(5);

        requests[0].Method.Method.Should().Be("PATCH");
        bodies[0].Should().Contain("\"status\":\"abandoned\"");
    }
}
