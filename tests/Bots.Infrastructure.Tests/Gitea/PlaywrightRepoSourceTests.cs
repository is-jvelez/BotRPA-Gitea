using Bots.Domain;
using Bots.Infrastructure.Gitea;
using Bots.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bots.Infrastructure.Tests.Gitea;

/// <summary>
/// PlaywrightRepoSource contra un Gitea falso con fixtures HTML (el CI no alcanza el Gitea real).
/// Requiere Chromium de Playwright instalado (ver README).
/// </summary>
public sealed class PlaywrightRepoSourceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero);

    private readonly FakeGitea _gitea = FakeGitea.WithDefaultRepos();
    private readonly List<PlaywrightRepoSource> _sources = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _gitea.StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }

        await _gitea.DisposeAsync();
    }

    [Fact]
    public async Task Lists_owned_repos_across_pages_and_matches_the_counter()
    {
        var keys = await Source().ListRepoKeysAsync(Ct);

        Assert.Equal(["ana/con-readme", "ana/muchas-ramas", "ana/sin-readme", "ana/vacio"], keys);
    }

    [Fact]
    public async Task Fewer_repos_than_the_counter_is_reported_as_inconsistent()
    {
        _gitea.ReposCounterOverride = 5;

        await Assert.ThrowsAsync<GiteaInconsistentDataException>(() => Source().ListRepoKeysAsync(Ct));
    }

    [Fact]
    public async Task Repo_with_readme_description_and_open_and_closed_pull_requests()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);

        var repo = await source.GetRepoAsync("ana/con-readme", Ct);

        Assert.Equal("ana/con-readme", repo.ExternalKey);
        Assert.Equal("con-readme", repo.Title);
        Assert.Equal("Repo con README", repo.Description);
        Assert.True(repo.HasReadme);
        Assert.Equal(5, repo.PullRequestCount);
        Assert.Equal([new BranchSnapshot("main", 6)], repo.Branches);
        Assert.Equal(Now, repo.CapturedAtUtc);
    }

    [Fact]
    public async Task Repo_without_readme_md_and_with_pull_requests_disabled()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);

        var repo = await source.GetRepoAsync("ana/sin-readme", Ct);

        Assert.False(repo.HasReadme); // readme.txt no cuenta: solo README.md
        Assert.Null(repo.Description);
        Assert.Equal(0, repo.PullRequestCount);
        Assert.Equal([new BranchSnapshot("main", 1)], repo.Branches);
    }

    [Fact]
    public async Task Several_branches_with_their_own_commit_counts()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);

        var repo = await source.GetRepoAsync("ana/muchas-ramas", Ct);

        // Paginado (la rama por defecto se repite en cada página), "1,234" con separador de miles, una
        // rama con "/", dos que solo difieren en mayúsculas y una borrada que no debe aparecer.
        Assert.Equal(
            [new BranchSnapshot("main", 1234), new BranchSnapshot("feature/x", 7), new BranchSnapshot("Main", 2)],
            repo.Branches);
    }

    [Fact]
    public async Task Empty_repo_has_no_branches()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);

        var repo = await source.GetRepoAsync("ana/vacio", Ct);

        Assert.Empty(repo.Branches);
        Assert.False(repo.HasReadme);
        Assert.Equal("Repo sin commits", repo.Description);
        Assert.Equal(0, repo.PullRequestCount);
    }

    [Fact]
    public async Task Wrong_password_fails_without_retrying()
    {
        var source = Source(password: "incorrecta");

        var ex = await Assert.ThrowsAsync<GiteaLoginException>(() => source.ListRepoKeysAsync(Ct));

        Assert.Contains("incorrect", ex.Message);
        Assert.Equal(1, _gitea.LoginPosts);
    }

    [Fact]
    public async Task Expired_session_logs_in_again_and_continues()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);
        _gitea.ExpireSessionsOnNextRequest = true;

        var repo = await source.GetRepoAsync("ana/con-readme", Ct);

        Assert.Equal("ana/con-readme", repo.ExternalKey);
        Assert.Equal(2, _gitea.LoginPosts);
    }

    [Fact]
    public async Task Changed_page_structure_is_reported_and_not_retried()
    {
        _gitea.Repos.Add(new FakeRepo("roto", "x", ["a"], [new FakeBranch("main", "1")], BrokenHome: true));
        var source = Source();
        await source.ListRepoKeysAsync(Ct);

        var ex = await Assert.ThrowsAsync<GiteaPageStructureException>(() => source.GetRepoAsync("ana/roto", Ct));

        Assert.Contains("contador de ramas", ex.Message);
    }

    [Fact]
    public async Task Redirect_to_another_origin_after_login_is_a_configuration_error()
    {
        await using var other = new FakeGitea();
        await other.StartAsync();
        _gitea.LoginRedirect = other.BaseUrl;

        var ex = await Assert.ThrowsAsync<GiteaConfigurationException>(() => Source().ListRepoKeysAsync(Ct));

        Assert.Contains("ROOT_URL", ex.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        var source = Source();
        await source.ListRepoKeysAsync(Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetRepoAsync("ana/con-readme", cts.Token));
    }

    private PlaywrightRepoSource Source(string password = FakeGitea.Password)
    {
        var options = new GiteaOptions
        {
            BaseUrl = _gitea.BaseUrl,
            User = FakeGitea.User,
            Password = password,
            NavigationTimeout = TimeSpan.FromSeconds(15),
            ActionTimeout = TimeSpan.FromSeconds(5),
        };
        var resilience = new GiteaResilience(
            new ResilienceOptions { MaxRetryAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(50) },
            TimeProvider.System,
            NullLogger<GiteaResilience>.Instance);

        var source = new PlaywrightRepoSource(options, resilience, new FakeTimeProvider(Now), NullLogger<PlaywrightRepoSource>.Instance);
        _sources.Add(source);
        return source;
    }
}
