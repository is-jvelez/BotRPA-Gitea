using Bots.Domain;

namespace Bots.Application.Tests;

public sealed class DomainInvariantTests
{
    private static readonly DateTimeOffset Utc = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_repo_without_branches_is_valid()
    {
        var snapshot = new RepoSnapshot("ana/vacio", "vacio", null, hasReadme: false, pullRequestCount: 0, [], Utc);

        Assert.Equal(0, snapshot.BranchCount);
    }

    [Fact]
    public void BranchCount_is_derived_from_branches()
    {
        var snapshot = new RepoSnapshot("ana/a", "a", "desc", true, 2, [new("main", 5), new("dev", 7)], Utc);

        Assert.Equal(2, snapshot.BranchCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("sin-barra")]
    [InlineData("ana/")]
    [InlineData("/repo")]
    [InlineData("a/b/c")]
    public void Invalid_external_key_is_rejected(string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new RepoSnapshot(key, "t", null, false, 0, [], Utc));

    [Fact]
    public void Negative_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepoSnapshot("ana/a", "a", null, false, -1, [], Utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchSnapshot("main", -1));
    }

    [Fact]
    public void Duplicated_branches_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new RepoSnapshot("ana/a", "a", null, false, 0, [new("main", 1), new("main", 2)], Utc));

    [Fact]
    public void Branch_names_differing_only_in_case_are_distinct()
    {
        var snapshot = new RepoSnapshot("ana/a", "a", null, false, 0, [new("Main", 1), new("main", 2)], Utc);

        Assert.Equal(2, snapshot.BranchCount);
    }

    [Fact]
    public void Non_utc_dates_are_rejected()
    {
        var local = new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.FromHours(-5));

        Assert.Throws<ArgumentException>(() => new RepoSnapshot("ana/a", "a", null, false, 0, [], local));
        Assert.Throws<ArgumentException>(() => new JobRun(1, "job", local, 1, Utc));
    }

    [Fact]
    public void JobRun_requires_attempt_of_at_least_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobRun(1, "job", Utc, 0, Utc));
}
