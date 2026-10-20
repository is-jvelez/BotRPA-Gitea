using Bots.Infrastructure.Sql;
using Microsoft.Data.SqlClient;

namespace Bots.Infrastructure.Tests;

public sealed class SqlConnectionSettingsTests
{
    [Fact]
    public void Password_with_separators_cannot_inject_options()
    {
        var settings = new SqlConnectionSettings
        {
            Server = "sql,1433",
            User = "bots_app",
            Password = "a;TrustServerCertificate=True;b",
        };

        var parsed = new SqlConnectionStringBuilder(settings.BuildConnectionString());

        Assert.Equal("a;TrustServerCertificate=True;b", parsed.Password);
        Assert.False(parsed.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.Equal("BotsDb", parsed.InitialCatalog);
    }
}
