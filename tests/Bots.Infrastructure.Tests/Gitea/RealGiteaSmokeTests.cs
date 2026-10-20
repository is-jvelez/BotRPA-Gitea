using Bots.Infrastructure.Gitea;
using Bots.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bots.Infrastructure.Tests.Gitea;

/// <summary>
/// Prueba opcional contra un Gitea real. Se omite salvo que existan GITEA_E2E_BASE_URL, GITEA_E2E_USER
/// y GITEA_E2E_PASSWORD (el CI no las define: no alcanza el Gitea del desarrollador).
/// </summary>
public sealed class RealGiteaSmokeTests
{
    [Fact]
    public async Task Reads_every_repo_of_the_logged_in_user()
    {
        var baseUrl = Environment.GetEnvironmentVariable("GITEA_E2E_BASE_URL");
        var user = Environment.GetEnvironmentVariable("GITEA_E2E_USER");
        var password = Environment.GetEnvironmentVariable("GITEA_E2E_PASSWORD");
        Assert.SkipWhen(
            string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password),
            "Define GITEA_E2E_BASE_URL, GITEA_E2E_USER y GITEA_E2E_PASSWORD para ejecutarla.");

        var ct = TestContext.Current.CancellationToken;
        await using var source = new PlaywrightRepoSource(
            new GiteaOptions
            {
                BaseUrl = new Uri(baseUrl!),
                User = user!,
                Password = password!,
                DiagnosticsDirectory = Environment.GetEnvironmentVariable("GITEA_E2E_DIAGNOSTICS"),
            },
            new GiteaResilience(new ResilienceOptions(), TimeProvider.System, NullLogger<GiteaResilience>.Instance),
            TimeProvider.System,
            NullLogger<PlaywrightRepoSource>.Instance);

        var keys = await source.ListRepoKeysAsync(ct);
        Assert.NotEmpty(keys);

        var lines = new List<string>();
        foreach (var key in keys)
        {
            var repo = await source.GetRepoAsync(key, ct);
            lines.Add($"{repo.ExternalKey} | readme={repo.HasReadme} | prs={repo.PullRequestCount} | desc={repo.Description} | ramas: " +
                string.Join(", ", repo.Branches.Select(b => $"{b.Name}={b.CommitCount}")));
        }

        // Opcional: GITEA_E2E_REPORT=ruta para revisar lo leído.
        if (Environment.GetEnvironmentVariable("GITEA_E2E_REPORT") is { Length: > 0 } report)
        {
            await File.WriteAllLinesAsync(report, lines, ct);
        }
    }
}
