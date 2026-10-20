namespace Bots.Domain;

/// <summary>Rama de un repositorio con su número de commits en el momento de la captura.</summary>
public sealed record BranchSnapshot
{
    public BranchSnapshot(string name, int commitCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(commitCount);

        Name = name;
        CommitCount = commitCount;
    }

    public string Name { get; }

    public int CommitCount { get; }
}
