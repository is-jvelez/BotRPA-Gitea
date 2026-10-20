using System.Globalization;
using System.Text.RegularExpressions;
using Bots.Infrastructure.Resilience;
using Bots.Infrastructure.Sql;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Bots.Infrastructure.Tests.SqlServerFixture))]

namespace Bots.Infrastructure.Tests;

/// <summary>
/// Un SQL Server (Testcontainers) para todo el ensamblado. Reproduce lo mínimo de sql-init (BotsDb,
/// bots_app_role y un usuario de app sin DDL), aplica las migraciones V*.sql reales y conecta los
/// almacenes con el usuario de la app, de modo que los tests también validan los permisos.
/// El sql-init completo del compose lo prueba el job flyway-validate del CI.
/// </summary>
public sealed partial class SqlServerFixture : IAsyncLifetime
{
    // Mismo tag que .env (MSSQL_TAG) para probar contra la versión real.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";
    private const string AppUser = "bots_app_test";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    /// <summary>sa sobre BotsDb: solo para preparar datos y comprobar resultados.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Usuario de la app (miembro de bots_app_role).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var master = new SqlConnectionStringBuilder(_container.GetConnectionString());
        AdminConnectionString = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "BotsDb" }.ConnectionString;

        var appPassword = $"Tst-{Guid.NewGuid():N}";
        AppConnectionString = new SqlConnectionStringBuilder(AdminConnectionString)
        {
            UserID = AppUser,
            Password = appPassword,
        }.ConnectionString;

        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("CREATE DATABASE BotsDb;");
            await connection.ExecuteAsync(
                $"CREATE LOGIN [{AppUser}] WITH PASSWORD = N'{appPassword}', DEFAULT_DATABASE = BotsDb, CHECK_POLICY = ON;");
        }

        await using (var connection = new SqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("CREATE ROLE bots_app_role;");
            await connection.ExecuteAsync($"CREATE USER [{AppUser}] FOR LOGIN [{AppUser}];");
            await connection.ExecuteAsync($"ALTER ROLE bots_app_role ADD MEMBER [{AppUser}];");

            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "flyway"), "V*__*.sql")
                         .OrderBy(f => int.Parse(MigrationVersion().Match(Path.GetFileName(f)).Groups[1].Value, CultureInfo.InvariantCulture)))
            {
                foreach (var batch in GoSeparator().Split(await File.ReadAllTextAsync(file)).Where(b => !string.IsNullOrWhiteSpace(b)))
                {
                    await connection.ExecuteAsync(batch);
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public SqlConnectionFactory AppConnections() => new(AppConnectionString);

    public static SqlResilience Resilience(ResilienceOptions? options = null) =>
        new(options ?? new ResilienceOptions(), TimeProvider.System, NullLogger<SqlResilience>.Instance);

    public SqlJobRunStore JobRunStore(TimeProvider? time = null, int maxAttempts = 3) =>
        new(AppConnections(), Resilience(), new SqlJobRunStoreOptions { MaxAttempts = maxAttempts },
            time ?? TimeProvider.System, NullLogger<SqlJobRunStore>.Instance);

    public SqlRepoStore RepoStore(TimeProvider? time = null) =>
        new(AppConnections(), Resilience(), time ?? TimeProvider.System);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        return (await connection.QueryAsync<T>(sql, parameters)).ToList();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    [GeneratedRegex(@"^V(\d+)__")]
    private static partial Regex MigrationVersion();
}
