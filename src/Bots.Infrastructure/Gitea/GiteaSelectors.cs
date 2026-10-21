namespace Bots.Infrastructure.Gitea;

/// <summary>
/// Todas las rutas y selectores de la UI de Gitea en un solo lugar (verificados contra Gitea 28.2).
/// Si Gitea cambia su HTML, este es el único archivo a tocar. Los selectores no dependen de textos
/// traducibles ("Commits", "Open"…): usan clases, rutas e iconos.
/// </summary>
internal static class GiteaSelectors
{
    // --- Login: /user/login ---
    public const string LoginPath = "/user/login";
    public const string TwoFactorPath = "/user/two_factor";
    public const string LoginUserInput = "form[action$='/user/login'] #user_name";
    public const string LoginPasswordInput = "form[action$='/user/login'] #password";
    public const string LoginSubmit = "form[action$='/user/login'] button[type='submit']";
    public const string FlashError = ".flash-error, .ui.negative.message";

    // --- Repos del usuario: /{user}?tab=repositories&sort=alphabetically&page=N ---
    // Orden alfabético para que la paginación sea estable mientras se recorre.
    public static string UserReposPath(string user, int page) =>
        $"/{Uri.EscapeDataString(user)}?tab=repositories&sort=alphabetically&page={page}";

    /// <summary>Contador de la pestaña "Repositories" (total esperado, para detectar omisiones).</summary>
    public static string UserReposCounter(string user) =>
        $"a.item[href$='/{CssString(user)}?tab=repositories'] .ui.label";

    public const string RepoListItemLink = ".flex-divided-list .item .item-title a.name";

    // --- Home del repo: /{owner}/{repo} ---
    /// <summary>
    /// "/owner/repo" con cada segmento escapado. Hoy Gitea solo admite [\w.-] en esos nombres, pero así un
    /// nombre con ?, # o % nunca cambiaría la ruta ni añadiría una query.
    /// </summary>
    public static string RepoPath(string repoKey) => "/" + EscapeSegments(repoKey);

    public const string EmptyRepoMarker = ".empty-repo-guide";
    public const string Description = ".repo-description";
    public const string RootFileEntry = ".repo-file-cell.name a.entry-name";
    /// <summary>
    /// Se compara sin distinguir mayúsculas ("README.MD" cuenta), igual que Gitea al mostrar el README.
    /// Solo cuenta el archivo en la raíz y con extensión .md (readme.txt no).
    /// </summary>
    public const string ReadmeFileName = "README.md";

    /// <summary>Contador "N Branches" de la cabecera del repo (total esperado de ramas).</summary>
    public const string BranchesCounter = "a.item[href$='/branches'] b";

    // --- Ramas: /{owner}/{repo}/branches?page=N ---
    // Las ramas borradas recientemente aparecen como <span class="branch-name"> (con "Restaurar"):
    // solo los enlaces son ramas vivas. La rama por defecto se repite en cada página.
    public static string BranchesPath(string repoKey, int page) => $"{RepoPath(repoKey)}/branches?page={page}";

    public const string BranchNameLink = "a.branch-name";

    // --- Una rama: /{owner}/{repo}/src/branch/{rama} ---
    public static string BranchPath(string repoKey, string branch) =>
        $"{RepoPath(repoKey)}/src/branch/{EscapeSegments(branch)}";

    /// <summary>Enlace "<b>N</b> Commits" de la rama mostrada (icono de historial, href .../commits/branch/{rama}).</summary>
    public const string BranchCommitsLink = "a.item[href*='/commits/branch/']:has(svg.octicon-history)";

    // --- PRs: /{owner}/{repo}/pulls ---
    public static string PullsPath(string repoKey) => $"{RepoPath(repoKey)}/pulls";

    /// <summary>
    /// Menú con las dos pestañas "N Open" y "N Closed". Gitea puede pintarlo dos veces en la página: se usa
    /// el primero. No se usa el href: con sesión iniciada la pestaña activa enlaza a state=all y los filtros
    /// (etiquetas, hitos…) llevan state=open (verificado en Gitea 28.2 con login).
    /// </summary>
    public const string PullStateMenu = ".small-menu-items";

    public const string PullStateTab = "a.item";

    /// <summary>Escapa cada segmento y conserva las barras (las ramas pueden tener "/").</summary>
    private static string EscapeSegments(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string CssString(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
