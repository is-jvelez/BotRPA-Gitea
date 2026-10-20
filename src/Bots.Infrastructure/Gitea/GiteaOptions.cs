namespace Bots.Infrastructure.Gitea;

/// <summary>Opciones de <see cref="PlaywrightRepoSource"/> (se enlazan desde <c>Gitea</c> en el Worker).</summary>
public sealed class GiteaOptions
{
    /// <summary>URL base de Gitea. En Docker: http://host.docker.internal:3000; fuera: http://localhost:3000.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Usuario cuyos repos se procesan (GITEA_USER).</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Contraseña (GITEA_PASSWORD). Solo desde variables de entorno, nunca en appsettings.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Timeout de cada navegación (carga de página).</summary>
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout de cada acción sobre la página (rellenar, hacer clic).</summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tope de páginas por listado paginado (protección contra bucles si la paginación cambia).</summary>
    public int MaxPages { get; set; } = 200;

    /// <summary>Navegador sin ventana. Solo se desactiva para depurar en local.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Carpeta donde guardar el HTML de la página cuando un selector no encuentra lo esperado (para
    /// diagnosticar un cambio de Gitea). Vacío = no se guarda. El HTML puede contener datos de la sesión:
    /// usar una carpeta local, nunca versionada.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }
}
