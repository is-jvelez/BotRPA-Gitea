using System.Collections.ObjectModel;

namespace Bots.Domain;

/// <summary>
/// Estado de un repositorio leído de la fuente. Inmutable: las propiedades no tienen setter,
/// así que una expresión <c>with</c> no puede saltarse las validaciones del constructor.
/// </summary>
public sealed record RepoSnapshot
{
    public RepoSnapshot(
        string externalKey,
        string title,
        string? description,
        bool hasReadme,
        int pullRequestCount,
        IReadOnlyList<BranchSnapshot> branches,
        DateTimeOffset capturedAtUtc)
    {
        ValidateExternalKey(externalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegative(pullRequestCount);
        ArgumentNullException.ThrowIfNull(branches);
        UtcGuard.EnsureUtc(capturedAtUtc, nameof(capturedAtUtc));

        var copy = branches.ToArray();
        if (copy.Any(b => b is null))
        {
            throw new ArgumentException("La lista de ramas contiene elementos nulos.", nameof(branches));
        }

        // Los nombres de rama en git distinguen mayúsculas: comparación ordinal.
        var duplicated = copy.GroupBy(b => b.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new ArgumentException($"Rama duplicada '{duplicated.Key}' en el repo '{externalKey}'.", nameof(branches));
        }

        ExternalKey = externalKey;
        Title = title;
        Description = description;
        HasReadme = hasReadme;
        PullRequestCount = pullRequestCount;
        Branches = new ReadOnlyCollection<BranchSnapshot>(copy);
        CapturedAtUtc = capturedAtUtc;
    }

    /// <summary>Clave estable del repo en la fuente: <c>owner/nombre</c>.</summary>
    public string ExternalKey { get; }

    public string Title { get; }

    public string? Description { get; }

    public bool HasReadme { get; }

    /// <summary>PRs abiertos + cerrados (los mergeados cuentan como cerrados).</summary>
    public int PullRequestCount { get; }

    /// <summary>Ramas con su conteo de commits. Vacía para un repo sin commits.</summary>
    public IReadOnlyList<BranchSnapshot> Branches { get; }

    public int BranchCount => Branches.Count;

    public DateTimeOffset CapturedAtUtc { get; }

    private static void ValidateExternalKey(string externalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKey);

        var parts = externalKey.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"La clave '{externalKey}' no tiene el formato 'owner/nombre'.", nameof(externalKey));
        }
    }
}
