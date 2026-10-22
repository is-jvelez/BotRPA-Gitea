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
    /// Sandbox de Chromium (Playwright lo desactiva por defecto). Activo: si una página lograra explotar el
    /// navegador, queda aislada del proceso del bot. Solo se desactiva si el entorno no lo permite (Chromium
    /// falla al arrancar con "No usable sandbox"); ver runbook.
    /// </summary>
    public bool ChromiumSandbox { get; set; } = true;

    /// <summary>
    /// Carpeta donde guardar el HTML de la página cuando un selector no encuentra lo esperado (para
    /// diagnosticar un cambio de Gitea). Vacío = no se guarda. El HTML puede contener datos de la sesión:
    /// usar una carpeta local, nunca versionada.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }

    /// <summary>
    /// Depuración visual: dibuja un cursor en la página y mueve el mouse paso a paso hasta cada elemento que
    /// el bot lee o pulsa. Con pantalla (ejecución local) abre el navegador en ventana, ignora
    /// <see cref="Headless"/> y frena cada acción para seguirla a ojo; sin pantalla (Docker) sigue en headless
    /// y graba un video por página en <see cref="VideoDirectory"/>. Solo para depurar: nunca en producción.
    /// </summary>
    public bool VisualDebug { get; set; }

    /// <summary>
    /// Carpeta de los videos de <see cref="VisualDebug"/> cuando no hay pantalla (una subcarpeta por corrida).
    /// Los videos muestran los datos de los repos: usar una carpeta local, nunca versionada.
    /// </summary>
    public string VideoDirectory { get; set; } = "videos";
}
