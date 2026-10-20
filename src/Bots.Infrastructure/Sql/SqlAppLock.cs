using Dapper;
using Microsoft.Data.SqlClient;

namespace Bots.Infrastructure.Sql;

/// <summary>Lock entre instancias con <c>sp_getapplock</c> de sesión (vive mientras la conexión siga abierta).</summary>
internal static class SqlAppLock
{
    private const string AcquireSql = """
        DECLARE @result INT;
        EXEC @result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
        SELECT @result;
        """;

    private const string ReleaseSql = """
        DECLARE @result INT;
        EXEC @result = sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session';
        SELECT @result;
        """;

    public static string ResourceFor(string jobName) => $"bots:job:{jobName}";

    /// <summary>Intenta tomar el lock sin esperar. <c>false</c> si lo tiene otra sesión.</summary>
    public static async Task<bool> TryAcquireAsync(SqlConnection connection, string resource, CancellationToken ct)
    {
        var result = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(AcquireSql, new { Resource = resource }, cancellationToken: ct));

        // 0/1 = concedido; -1 = timeout (lo tiene otra sesión). El resto (-2 cancelado, -3 deadlock,
        // -999 parámetros) es un error, no "lock ocupado".
        return result switch
        {
            >= 0 => true,
            -1 => false,
            _ => throw new InvalidOperationException($"sp_getapplock devolvió {result} para '{resource}'."),
        };
    }

    public static async Task ReleaseAsync(SqlConnection connection, string resource, CancellationToken ct)
    {
        var result = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(ReleaseSql, new { Resource = resource }, cancellationToken: ct));
        if (result < 0)
        {
            throw new InvalidOperationException($"sp_releaseapplock devolvió {result} para '{resource}'.");
        }
    }
}
