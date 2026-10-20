using Bots.Domain;

namespace Bots.Application.Ports;

/// <summary>
/// Fuente de repositorios (Gitea vía RPA). Se separa en listado + detalle para que el fallo
/// de un repo no aborte el resto: un fallo en <see cref="ListRepoKeysAsync"/> es fatal para la
/// corrida; un fallo en <see cref="GetRepoAsync"/> afecta solo a ese repo.
/// </summary>
public interface IRepoSource
{
    /// <summary>Claves <c>owner/nombre</c> de todos los repos del usuario logueado (todas las páginas).</summary>
    Task<IReadOnlyList<string>> ListRepoKeysAsync(CancellationToken ct);

    /// <summary>Detalle completo de un repo.</summary>
    Task<RepoSnapshot> GetRepoAsync(string externalKey, CancellationToken ct);
}
