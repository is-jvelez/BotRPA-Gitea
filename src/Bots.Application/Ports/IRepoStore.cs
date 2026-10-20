using Bots.Domain;

namespace Bots.Application.Ports;

public interface IRepoStore
{
    /// <summary>
    /// Inserta o actualiza el repo y sus ramas en una sola transacción, y elimina las ramas que
    /// ya no existen en la fuente. Idempotente: ejecutarlo dos veces deja el mismo estado.
    /// </summary>
    Task UpsertAsync(RepoSnapshot snapshot, long jobRunId, CancellationToken ct);
}
