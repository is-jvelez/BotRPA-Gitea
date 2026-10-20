using Bots.Application.Ports;
using Bots.Domain;
using Bots.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Bots.Infrastructure.Gitea;

/// <summary>
/// Lee los repos del usuario logueado navegando la UI web de Gitea con Playwright (RPA; no usa la API).
/// </summary>
/// <remarks>
/// <para>Cada corrida empieza en <see cref="ListRepoKeysAsync"/> con un contexto de navegador nuevo y un
/// login fresco; <see cref="GetRepoAsync"/> reutiliza esa sesión. El navegador se mantiene entre corridas
/// y se cierra en <see cref="DisposeAsync"/>. Cada lectura abre su página y la cierra siempre.</para>
/// <para>Para no guardar datos incompletos, los listados se cotejan con los contadores que muestra Gitea
/// (repos de la pestaña y ramas de la cabecera): si no cuadran, se lanza
/// <see cref="GiteaInconsistentDataException"/>.</para>
/// </remarks>
public sealed class PlaywrightRepoSource : IRepoSource, IAsyncDisposable
{
    private readonly GiteaOptions _options;
    private readonly GiteaResilience _resilience;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlaywrightRepoSource> _logger;
    private readonly Uri _baseUrl;
    private readonly string _basePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public PlaywrightRepoSource(GiteaOptions options, GiteaResilience resilience, TimeProvider timeProvider, ILogger<PlaywrightRepoSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.User);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Password);

        _options = options;
        _resilience = resilience;
        _timeProvider = timeProvider;
        _logger = logger;
        // Con barra final, las rutas relativas se resuelven bajo un posible subpath (http://host/gitea/).
        _baseUrl = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
        _basePath = _baseUrl.AbsolutePath.TrimEnd('/');
    }

    public async Task<IReadOnlyList<string>> ListRepoKeysAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await _resilience.Pipeline.ExecuteAsync(async token =>
            {
                await StartSessionAsync(token);
                return await WithPageAsync(page => ReadOwnedRepoKeysAsync(page, token), token);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RepoSnapshot> GetRepoAsync(string externalKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKey);

        await _gate.WaitAsync(ct);
        try
        {
            return await _resilience.Pipeline.ExecuteAsync(async token =>
            {
                if (_context is null)
                {
                    await StartSessionAsync(token);
                }

                try
                {
                    return await WithPageAsync(page => ReadRepoAsync(page, externalKey, token), token);
                }
                catch (GiteaSessionExpiredException)
                {
                    // El reintento de Polly abrirá una sesión nueva.
                    await CloseContextAsync();
                    throw;
                }
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseContextAsync();
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _gate.Dispose();
    }

    // ------------------------------------------------------------------ sesión

    private async Task StartSessionAsync(CancellationToken ct)
    {
        await CloseContextAsync();

        _playwright ??= await Playwright.CreateAsync();
        if (_browser is null || !_browser.IsConnected)
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _options.Headless });
        }

        // Idioma fijo: el formato de los números ("1,234") y los textos no dependen del host.
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions { Locale = "en-US" });
        _context.SetDefaultNavigationTimeout((float)_options.NavigationTimeout.TotalMilliseconds);
        _context.SetDefaultTimeout((float)_options.ActionTimeout.TotalMilliseconds);

        await WithPageAsync(page => LoginAsync(page, ct), ct);
    }

    private async Task CloseContextAsync()
    {
        if (_context is not null)
        {
            var context = _context;
            _context = null;
            await context.CloseAsync();
        }
    }

    private async Task<bool> LoginAsync(IPage page, CancellationToken ct)
    {
        await GotoAsync(page, GiteaSelectors.LoginPath, expectSession: false, ct);
        await RequireAsync(page, GiteaSelectors.LoginUserInput, "formulario de login");

        await page.FillAsync(GiteaSelectors.LoginUserInput, _options.User);
        await page.FillAsync(GiteaSelectors.LoginPasswordInput, _options.Password);

        // Gitea responde al POST con una redirección si el login es correcto, o con la misma página y un
        // mensaje de error si no lo es.
        var response = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync(GiteaSelectors.LoginSubmit),
            r => r.Request.Method == "POST" && IsPath(r.Url, GiteaSelectors.LoginPath));

        if (response.Status is >= 300 and < 400)
        {
            await page.WaitForURLAsync(url => !IsPath(url, GiteaSelectors.LoginPath), new PageWaitForURLOptions { WaitUntil = WaitUntilState.Load });
        }
        else
        {
            await page.WaitForLoadStateAsync(LoadState.Load);
        }

        ct.ThrowIfCancellationRequested();
        EnsureSameOrigin(page.Url);

        if (IsPath(page.Url, GiteaSelectors.TwoFactorPath))
        {
            throw new GiteaLoginException("Gitea pide un segundo factor (2FA) para este usuario: el login no es automatizable.");
        }

        if (IsPath(page.Url, GiteaSelectors.LoginPath))
        {
            var flash = page.Locator(GiteaSelectors.FlashError);
            var message = await flash.CountAsync() > 0 ? (await flash.First.InnerTextAsync()).Trim() : "sin mensaje de error";
            throw new GiteaLoginException($"Login rechazado para '{_options.User}': {message}");
        }

        if (response.Status >= 400)
        {
            throw new GiteaHttpException(response.Status, GiteaSelectors.LoginPath);
        }

        _logger.LogInformation("Sesión iniciada en Gitea como {GiteaUser}.", _options.User);
        return true;
    }

    // ------------------------------------------------------------------ listado de repos

    private async Task<IReadOnlyList<string>> ReadOwnedRepoKeysAsync(IPage page, CancellationToken ct)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? expected = null;

        for (var pageNumber = 1; ; pageNumber++)
        {
            if (pageNumber > _options.MaxPages)
            {
                throw new GiteaPageStructureException($"El listado de repos superó {_options.MaxPages} páginas; la paginación no termina.");
            }

            await GotoAsync(page, GiteaSelectors.UserReposPath(_options.User, pageNumber), expectSession: true, ct);
            expected ??= ParseCount(await RequireTextAsync(page, GiteaSelectors.UserReposCounter(_options.User), "contador de repos"));

            var hrefs = await page.Locator(GiteaSelectors.RepoListItemLink)
                .EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))");
            if (hrefs.Length == 0)
            {
                break;
            }

            var added = 0;
            foreach (var href in hrefs)
            {
                var key = ToRepoKey(href);
                if (key is not null && seen.Add(key))
                {
                    keys.Add(key);
                    added++;
                }
            }

            if (added == 0)
            {
                // Página repetida: el servidor ignora el número de página. Sin esto el bucle no terminaría.
                break;
            }
        }

        if (keys.Count != expected)
        {
            throw new GiteaInconsistentDataException(
                $"Se leyeron {keys.Count} repos pero Gitea indica {expected} para '{_options.User}'.");
        }

        _logger.LogInformation("Gitea: {RepoCount} repos del usuario {GiteaUser}.", keys.Count, _options.User);
        return keys;
    }

    /// <summary>"/owner/repo" (con posible subpath) → "owner/repo" si el owner es el usuario logueado.</summary>
    private string? ToRepoKey(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(new Uri(_baseUrl, href).AbsolutePath);
        if (_basePath.Length > 0 && path.StartsWith(_basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[_basePath.Length..];
        }

        var parts = path.Trim('/').Split('/');
        if (parts.Length != 2 || !string.Equals(parts[0], _options.User, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Se ignora el enlace {Href}: no es un repo propio de {GiteaUser}.", href, _options.User);
            return null;
        }

        return $"{parts[0]}/{parts[1]}";
    }

    // ------------------------------------------------------------------ detalle de un repo

    private async Task<RepoSnapshot> ReadRepoAsync(IPage page, string key, CancellationToken ct)
    {
        await GotoAsync(page, $"/{key}", expectSession: true, ct);

        var description = await ReadDescriptionAsync(page);
        var isEmpty = await page.Locator(GiteaSelectors.EmptyRepoMarker).CountAsync() > 0;

        var hasReadme = false;
        var branches = new List<BranchSnapshot>();
        if (!isEmpty)
        {
            var fileNames = await page.Locator(GiteaSelectors.RootFileEntry)
                .EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('title') || a.textContent.trim())");
            hasReadme = fileNames.Any(n => string.Equals(n, GiteaSelectors.ReadmeFileName, StringComparison.OrdinalIgnoreCase));

            var expectedBranches = ParseCount(await RequireTextAsync(page, GiteaSelectors.BranchesCounter, "contador de ramas"));
            var names = await ReadBranchNamesAsync(page, key, ct);
            if (names.Count != expectedBranches)
            {
                throw new GiteaInconsistentDataException(
                    $"Se leyeron {names.Count} ramas en '{key}' pero Gitea indica {expectedBranches}.");
            }

            foreach (var name in names)
            {
                branches.Add(new BranchSnapshot(name, await ReadBranchCommitCountAsync(page, key, name, ct)));
            }
        }

        var pullRequests = await ReadPullRequestCountAsync(page, key, ct);

        // Gitea no tiene un título aparte: Title = nombre del repo.
        return new RepoSnapshot(key, key.Split('/')[1], description, hasReadme, pullRequests, branches, _timeProvider.GetUtcNow());
    }

    private static async Task<string?> ReadDescriptionAsync(IPage page)
    {
        var element = page.Locator(GiteaSelectors.Description);
        if (await element.CountAsync() == 0)
        {
            return null;
        }

        var text = (await element.First.InnerTextAsync()).Trim();
        return text.Length == 0 ? null : text;
    }

    private async Task<IReadOnlyList<string>> ReadBranchNamesAsync(IPage page, string key, CancellationToken ct)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 1; ; pageNumber++)
        {
            if (pageNumber > _options.MaxPages)
            {
                throw new GiteaPageStructureException($"El listado de ramas de '{key}' superó {_options.MaxPages} páginas.");
            }

            await GotoAsync(page, GiteaSelectors.BranchesPath(key, pageNumber), expectSession: true, ct);
            var pageNames = await page.Locator(GiteaSelectors.BranchNameLink)
                .EvaluateAllAsync<string[]>("links => links.map(a => a.textContent.trim())");

            // La rama por defecto se repite en cada página: se para cuando una página no aporta ramas nuevas.
            var added = 0;
            foreach (var name in pageNames.Where(n => n.Length > 0 && seen.Add(n)))
            {
                names.Add(name);
                added++;
            }

            if (added == 0)
            {
                break;
            }
        }

        return names;
    }

    private async Task<int> ReadBranchCommitCountAsync(IPage page, string key, string branch, CancellationToken ct)
    {
        await GotoAsync(page, GiteaSelectors.BranchPath(key, branch), expectSession: true, ct);

        var link = page.Locator(GiteaSelectors.BranchCommitsLink);
        await RequireAsync(page, GiteaSelectors.BranchCommitsLink, $"enlace de commits de la rama '{branch}'");

        // Comprueba que la página muestra la rama pedida y no otra (p. ej. la rama por defecto).
        var href = Uri.UnescapeDataString(await link.First.GetAttributeAsync("href") ?? string.Empty);
        if (!href.EndsWith("/commits/branch/" + branch, StringComparison.Ordinal))
        {
            throw new GiteaPageStructureException($"La página de la rama '{branch}' de '{key}' enlaza a '{href}'.");
        }

        return ParseCount(await link.First.Locator("b").InnerTextAsync());
    }

    private async Task<int> ReadPullRequestCountAsync(IPage page, string key, CancellationToken ct)
    {
        try
        {
            await GotoAsync(page, GiteaSelectors.PullsPath(key), expectSession: true, ct);
        }
        catch (GiteaHttpException ex) when (ex.StatusCode == 404)
        {
            // Repo con los pull requests desactivados: no tiene PRs.
            _logger.LogDebug("'{RepoKey}' no tiene la sección de pull requests (404): 0 PRs.", key);
            return 0;
        }

        // PullRequestCount = abiertos + cerrados (los mergeados cuentan como cerrados).
        await RequireAsync(page, GiteaSelectors.PullStateMenu, "menú de PRs abiertos/cerrados");
        var tabs = await page.Locator(GiteaSelectors.PullStateMenu).First.Locator(GiteaSelectors.PullStateTab).AllInnerTextsAsync();
        if (tabs.Count != 2)
        {
            throw new GiteaPageStructureException(
                $"Se esperaban 2 pestañas (abiertos y cerrados) en {page.Url} y hay {tabs.Count}.");
        }

        return ParseCount(tabs[0]) + ParseCount(tabs[1]);
    }

    // ------------------------------------------------------------------ utilidades de navegación

    private async Task<T> WithPageAsync<T>(Func<IPage, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = _context ?? throw new InvalidOperationException("No hay sesión de navegador abierta.");
        var page = await context.NewPageAsync();

        // Playwright no acepta CancellationToken: al cancelar se cierra la página y la operación en curso falla.
        await using var registration = ct.Register(() => page.CloseAsync().ContinueWith(
            t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default));
        try
        {
            return await action(page);
        }
        catch (PlaywrightException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            if (!page.IsClosed)
            {
                await page.CloseAsync();
            }
        }
    }

    private async Task GotoAsync(IPage page, string relativePath, bool expectSession, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var url = new Uri(_baseUrl, relativePath.TrimStart('/')).AbsoluteUri;
        var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        EnsureSameOrigin(page.Url);
        if (expectSession && IsPath(page.Url, GiteaSelectors.LoginPath))
        {
            throw new GiteaSessionExpiredException(relativePath);
        }

        if (response is not null && response.Status >= 400)
        {
            throw new GiteaHttpException(response.Status, relativePath);
        }
    }

    /// <summary>
    /// Si Gitea redirige a otro host (su ROOT_URL), la navegación desde Docker se rompería: se falla con un
    /// mensaje claro en lugar de seguir con datos de otra URL.
    /// </summary>
    private void EnsureSameOrigin(string pageUrl)
    {
        var current = new Uri(pageUrl);
        if (!IsSameOrigin(current))
        {
            throw new GiteaConfigurationException(
                $"Gitea redirigió a {current.GetLeftPart(UriPartial.Authority)} en vez de {_baseUrl.GetLeftPart(UriPartial.Authority)} (revisa ROOT_URL de Gitea o Gitea:BaseUrl).");
        }
    }

    /// <summary>¿La URL es <paramref name="path"/> en NUESTRO Gitea? Otra origen nunca cuenta (ver ROOT_URL).</summary>
    private bool IsPath(string url, string path)
    {
        var uri = new Uri(url);
        return IsSameOrigin(uri)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), _basePath + path, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSameOrigin(Uri uri) =>
        Uri.Compare(uri, _baseUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private async Task RequireAsync(IPage page, string selector, string what)
    {
        if (await page.Locator(selector).CountAsync() == 0)
        {
            var saved = await SaveDiagnosticsAsync(page);
            throw new GiteaPageStructureException(
                $"No se encontró el {what} ('{selector}') en {page.Url}.{(saved is null ? string.Empty : $" HTML guardado en {saved}.")}");
        }
    }

    private async Task<string> RequireTextAsync(IPage page, string selector, string what)
    {
        await RequireAsync(page, selector, what);
        return await page.Locator(selector).First.InnerTextAsync();
    }

    /// <summary>Guarda el HTML de la página (si hay carpeta de diagnóstico). Nunca hace fallar la lectura.</summary>
    private async Task<string?> SaveDiagnosticsAsync(IPage page)
    {
        if (string.IsNullOrWhiteSpace(_options.DiagnosticsDirectory))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_options.DiagnosticsDirectory);
            var slug = string.Concat(new Uri(page.Url).AbsolutePath.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')).Trim('_');
            var path = Path.Combine(_options.DiagnosticsDirectory, $"{_timeProvider.GetUtcNow():yyyyMMddTHHmmssfff}-{slug}.html");
            await File.WriteAllTextAsync(path, await page.ContentAsync());
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar el HTML de diagnóstico de {Url}.", page.Url);
            return null;
        }
    }

    /// <summary>Extrae un entero de textos como "6", "1,234", "1.234" o "2 Closed".</summary>
    internal static int ParseCount(string text)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var value))
        {
            throw new GiteaPageStructureException($"Se esperaba un número y se encontró '{text.Trim()}'.");
        }

        return value;
    }
}
