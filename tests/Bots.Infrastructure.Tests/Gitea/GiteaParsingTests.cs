using Bots.Infrastructure.Gitea;
using Bots.Infrastructure.Resilience;

namespace Bots.Infrastructure.Tests.Gitea;

/// <summary>Tests sin navegador: lectura de números y clasificación de errores.</summary>
public sealed class GiteaParsingTests
{
    [Theory]
    [InlineData("6", 6)]
    [InlineData(" 1,234 ", 1234)]
    [InlineData("1.234", 1234)]
    [InlineData("\n  2 Closed\n", 2)]
    [InlineData("0 Abierta", 0)]
    public void Counts_are_read_regardless_of_locale(string text, int expected) =>
        Assert.Equal(expected, PlaywrightRepoSource.ParseCount(text));

    [Theory]
    [InlineData("")]
    [InlineData("Commits")]
    public void Text_without_a_number_is_a_page_structure_error(string text) =>
        Assert.Throws<GiteaPageStructureException>(() => PlaywrightRepoSource.ParseCount(text));

    [Fact]
    public void Repo_paths_are_unchanged_for_valid_Gitea_names()
    {
        Assert.Equal("/ana/mi-repo.v2_x", GiteaSelectors.RepoPath("ana/mi-repo.v2_x"));
        Assert.Equal("/ana/mi-repo/branches?page=2", GiteaSelectors.BranchesPath("ana/mi-repo", 2));
        Assert.Equal("/ana/mi-repo/pulls", GiteaSelectors.PullsPath("ana/mi-repo"));
        Assert.Equal("/ana/mi-repo/src/branch/feature/login", GiteaSelectors.BranchPath("ana/mi-repo", "feature/login"));
    }

    [Fact]
    public void Special_characters_in_owner_or_repo_cannot_alter_the_path_or_add_a_query()
    {
        Assert.Equal("/ana/a%3Fb%3D1%23x", GiteaSelectors.RepoPath("ana/a?b=1#x"));
        Assert.Equal("/ana/100%25/pulls", GiteaSelectors.PullsPath("ana/100%"));
        Assert.Equal("/ana/a%20b/src/branch/r%23s", GiteaSelectors.BranchPath("ana/a b", "r#s"));
    }

    [Fact]
    public void Transient_and_non_transient_errors_are_classified()
    {
        Assert.True(GiteaTransientErrors.IsTransient(new TimeoutException()));
        Assert.True(GiteaTransientErrors.IsTransient(new GiteaHttpException(503, "/x")));
        Assert.True(GiteaTransientErrors.IsTransient(new GiteaSessionExpiredException("/x")));

        Assert.False(GiteaTransientErrors.IsTransient(new GiteaHttpException(404, "/x")));
        Assert.False(GiteaTransientErrors.IsTransient(new GiteaLoginException("credenciales")));
        Assert.False(GiteaTransientErrors.IsTransient(new GiteaPageStructureException("selector")));
        Assert.False(GiteaTransientErrors.IsTransient(new GiteaInconsistentDataException("faltan repos")));
        Assert.False(GiteaTransientErrors.IsTransient(new GiteaConfigurationException("ROOT_URL")));
    }
}
