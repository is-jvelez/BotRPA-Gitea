using Bots.Infrastructure.Gitea;
using Microsoft.Playwright;

namespace Bots.Infrastructure.Resilience;

/// <summary>
/// Errores de Playwright/Gitea que se consideran transitorios (se reintentan): timeouts de Playwright,
/// errores de red del navegador (net::ERR_*), 5xx/429 de Gitea y sesión expirada. NO se reintentan:
/// credenciales inválidas, 2FA, selector no encontrado, datos inconsistentes, 404 ni configuración.
/// </summary>
public static class GiteaTransientErrors
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        // Playwright para .NET señala sus timeouts con System.TimeoutException.
        TimeoutException => true,
        GiteaHttpException http => http.IsTransient,
        GiteaSessionExpiredException => true,
        PlaywrightException playwright => playwright.Message.Contains("net::ERR_", StringComparison.Ordinal),
        _ => false,
    };
}
