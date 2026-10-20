namespace Bots.Infrastructure.Gitea;

/// <summary>Credenciales rechazadas o login no automatizable (2FA). No transitorio: no se reintenta.</summary>
public sealed class GiteaLoginException(string message) : Exception(message);

/// <summary>
/// La página no tiene la estructura esperada (un selector cambió). No transitorio: hay que actualizar
/// <see cref="GiteaSelectors"/>. Ver runbook: "selector cambió".
/// </summary>
public sealed class GiteaPageStructureException(string message) : Exception(message);

/// <summary>
/// Lo leído no cuadra con el contador que muestra Gitea (p. ej. faltan repos o ramas en el listado).
/// Se trata como fallo para no guardar datos incompletos (omisión silenciosa).
/// </summary>
public sealed class GiteaInconsistentDataException(string message) : Exception(message);

/// <summary>Gitea redirigió a otra URL base (típicamente su ROOT_URL). Configuración, no transitorio.</summary>
public sealed class GiteaConfigurationException(string message) : Exception(message);

/// <summary>Respuesta HTTP de error de Gitea. Es transitoria si es 5xx o 429.</summary>
public sealed class GiteaHttpException(int statusCode, string url)
    : Exception($"Gitea respondió {statusCode} en {url}.")
{
    public int StatusCode { get; } = statusCode;

    public bool IsTransient => StatusCode is >= 500 or 429;
}

/// <summary>La sesión expiró (Gitea redirigió al login). Transitorio: se vuelve a iniciar sesión.</summary>
public sealed class GiteaSessionExpiredException(string url)
    : Exception($"La sesión de Gitea expiró: se redirigió al login al abrir {url}.");
