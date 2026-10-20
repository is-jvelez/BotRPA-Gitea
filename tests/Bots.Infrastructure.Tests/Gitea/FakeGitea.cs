using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bots.Infrastructure.Tests.Gitea;

public sealed record FakeBranch(string Name, string CommitCountText, bool Deleted = false);

public sealed record FakeRepo(
    string Name,
    string? Description,
    string[] RootFiles,
    FakeBranch[] Branches,
    int OpenPulls = 0,
    int ClosedPulls = 0,
    bool PullsDisabled = false,
    bool BrokenHome = false)
{
    public bool IsEmpty => Branches.Length == 0;
}

/// <summary>
/// Gitea falso para los tests: sirve los fixtures HTML de Fixtures/Gitea (modelados sobre Gitea 28.2)
/// con login por cookie, paginación (2 elementos por página) y las variantes que necesitan los tests.
/// </summary>
public sealed class FakeGitea : IAsyncDisposable
{
    public const string User = "ana";
    public const string Password = "secreta";
    private const int PageSize = 2;
    private const string SessionCookie = "fake_gitea_session";

    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gitea");

    private readonly HashSet<string> _sessions = [];
    private readonly Lock _sync = new();
    private WebApplication? _app;

    public List<FakeRepo> Repos { get; } = [];

    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>Si se fija, el contador de repos de la pestaña muestra este valor en vez del real.</summary>
    public int? ReposCounterOverride { get; set; }

    /// <summary>Si se fija, tras un login correcto se redirige a esta URL (simula un ROOT_URL distinto).</summary>
    public Uri? LoginRedirect { get; set; }

    /// <summary>Si es true, la próxima petición autenticada invalida todas las sesiones (sesión expirada).</summary>
    public bool ExpireSessionsOnNextRequest { get; set; }

    public int LoginPosts { get; private set; }

    public static FakeGitea WithDefaultRepos()
    {
        var gitea = new FakeGitea();
        gitea.Repos.AddRange(
        [
            new FakeRepo("con-readme", "Repo con README", ["src", "LICENSE", "README.md"],
                [new FakeBranch("main", "6")], OpenPulls: 3, ClosedPulls: 2),
            new FakeRepo("muchas-ramas", "Varias ramas", ["docs"],
                [
                    new FakeBranch("main", "1,234"),
                    new FakeBranch("feature/x", "7"),
                    new FakeBranch("borrada", "3", Deleted: true),
                    new FakeBranch("Main", "2"),
                ]),
            new FakeRepo("sin-readme", null, ["src", "readme.txt"], [new FakeBranch("main", "1")], PullsDisabled: true),
            new FakeRepo("vacio", "Repo sin commits", [], []),
        ]);
        return gitea;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync();

        // Tras arrancar, Urls contiene la dirección con el puerto libre asignado.
        BaseUrl = new Uri(_app.Urls.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext http)
    {
        var path = Uri.UnescapeDataString(http.Request.Path.Value ?? "/").TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        if (path == "/user/login")
        {
            await HandleLoginAsync(http);
            return;
        }

        if (!IsAuthenticated(http))
        {
            http.Response.Redirect("/user/login");
            return;
        }

        if (path == "/")
        {
            await HtmlAsync(http, Fixture("dashboard.html"));
            return;
        }

        var parts = path.Trim('/').Split('/', 4);
        if (parts[0] != User)
        {
            http.Response.StatusCode = 404;
            return;
        }

        if (parts.Length == 1 && http.Request.Query["tab"] == "repositories")
        {
            await HtmlAsync(http, ReposPage(PageNumber(http)));
            return;
        }

        var repo = parts.Length >= 2 ? Repos.FirstOrDefault(r => r.Name == parts[1]) : null;
        if (repo is null)
        {
            http.Response.StatusCode = 404;
            return;
        }

        var key = $"{User}/{repo.Name}";
        var html = parts.Length switch
        {
            2 => Home(repo, key),
            3 when parts[2] == "branches" => BranchesPage(repo, key, PageNumber(http)),
            3 when parts[2] == "pulls" && !repo.PullsDisabled => Fixture("pulls.html")
                .Replace("{{OPEN}}", repo.OpenPulls.ToString()).Replace("{{CLOSED}}", repo.ClosedPulls.ToString()),
            4 when parts[2] == "src" && parts[3].StartsWith("branch/", StringComparison.Ordinal) => BranchPage(repo, key, parts[3]["branch/".Length..]),
            _ => null,
        };

        if (html is null)
        {
            http.Response.StatusCode = 404;
            return;
        }

        await HtmlAsync(http, html);
    }

    private async Task HandleLoginAsync(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            await HtmlAsync(http, Fixture("login.html").Replace("{{FLASH}}", string.Empty));
            return;
        }

        LoginPosts++;
        var form = await http.Request.ReadFormAsync();
        if (form["user_name"] == User && form["password"] == Password)
        {
            var session = Guid.NewGuid().ToString("N");
            lock (_sync)
            {
                _sessions.Add(session);
            }

            http.Response.Cookies.Append(SessionCookie, session, new CookieOptions { HttpOnly = true, Path = "/" });
            http.Response.StatusCode = (int)HttpStatusCode.SeeOther;
            http.Response.Headers.Location = LoginRedirect?.AbsoluteUri ?? "/";
            return;
        }

        const string flash = """<div class="ui negative message flash-message flash-error"><p>Username or password is incorrect.</p></div>""";
        await HtmlAsync(http, Fixture("login.html").Replace("{{FLASH}}", flash));
    }

    private bool IsAuthenticated(HttpContext http)
    {
        lock (_sync)
        {
            if (ExpireSessionsOnNextRequest)
            {
                ExpireSessionsOnNextRequest = false;
                _sessions.Clear();
            }

            return http.Request.Cookies.TryGetValue(SessionCookie, out var session) && _sessions.Contains(session);
        }
    }

    private string ReposPage(int page)
    {
        var items = Repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(r => Fixture("repo-item.html").Replace("{{KEY}}", $"{User}/{r.Name}").Replace("{{NAME}}", r.Name));

        return Fixture("repos.html")
            .Replace("{{TOTAL}}", (ReposCounterOverride ?? Repos.Count).ToString())
            .Replace("{{ITEMS}}", string.Join(Environment.NewLine, items));
    }

    private static string Home(FakeRepo repo, string key)
    {
        if (repo.IsEmpty)
        {
            return Fixture("empty-home.html").Replace("{{KEY}}", key).Replace("{{DESCRIPTION_TEXT}}", repo.Description ?? string.Empty);
        }

        var defaultBranch = repo.Branches[0];
        var files = repo.RootFiles.Select(f => Fixture("file-entry.html")
            .Replace("{{KEY}}", key).Replace("{{DEFAULT}}", defaultBranch.Name).Replace("{{FILE}}", f));
        var description = repo.Description is null
            ? string.Empty
            : $"""<div class="repo-description tw-break-anywhere tw-gap-2">{WebUtility.HtmlEncode(repo.Description)}</div>""";

        var html = Fixture("home.html")
            .Replace("{{KEY}}", key)
            .Replace("{{DEFAULT}}", defaultBranch.Name)
            .Replace("{{DEFAULT_COMMITS}}", defaultBranch.CommitCountText)
            .Replace("{{BRANCHES}}", repo.Branches.Count(b => !b.Deleted).ToString())
            .Replace("{{FILES}}", string.Join(Environment.NewLine, files))
            .Replace("{{DESCRIPTION}}", description);

        // "Selector cambió": la cabecera ya no tiene el contador de ramas.
        return repo.BrokenHome ? html.Replace($"href=\"/{key}/branches\"", $"href=\"/{key}/ramas\"") : html;
    }

    private static string BranchesPage(FakeRepo repo, string key, int page)
    {
        var rows = repo.Branches.Skip(1).Skip((page - 1) * PageSize).Take(PageSize).Select(b => b.Deleted
            ? $"""<tr><td class="six wide"><span class="gt-ellipsis branch-name">{b.Name}</span><p class="info">Deleted by ana</p></td><td><button class="ui primary tiny button restore-branch-button">Restore</button></td></tr>"""
            : $"""<tr><td class="six wide"><a class="gt-ellipsis branch-name" href="/{key}/src/branch/{EscapePath(b.Name)}">{b.Name}</a></td></tr>""");

        return Fixture("branches.html")
            .Replace("{{KEY}}", key)
            .Replace("{{DEFAULT}}", repo.Branches[0].Name)
            .Replace("{{ROWS}}", string.Join(Environment.NewLine, rows));
    }

    private static string? BranchPage(FakeRepo repo, string key, string branchName)
    {
        var branch = repo.Branches.FirstOrDefault(b => b.Name == branchName && !b.Deleted);
        return branch is null
            ? null
            : Fixture("branch.html")
                .Replace("{{KEY}}", key)
                .Replace("{{BRANCH_HREF}}", EscapePath(branch.Name))
                .Replace("{{COUNT}}", branch.CommitCountText)
                .Replace("{{BRANCHES}}", repo.Branches.Count(b => !b.Deleted).ToString());
    }

    private static int PageNumber(HttpContext http) =>
        int.TryParse(http.Request.Query["page"], out var page) && page > 0 ? page : 1;

    private static string EscapePath(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixturesDir, name));

    private static async Task HtmlAsync(HttpContext http, string html)
    {
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(html);
    }
}
