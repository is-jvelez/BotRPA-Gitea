using Microsoft.Data.SqlClient;

namespace Bots.Infrastructure.Sql;

/// <summary>Crea conexiones (sin abrir) a BotsDb.</summary>
public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _dedicatedConnectionString;

    public SqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        // La conexión del lock no sale del pool: al cerrarla termina la sesión de SQL Server y,
        // con ella, cualquier applock de sesión que siguiera tomado. Así nunca vuelve al pool
        // una sesión con el lock puesto.
        _dedicatedConnectionString = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    /// <summary>Conexión para operaciones de datos (con pool).</summary>
    public SqlConnection Create() => new(_connectionString);

    /// <summary>Conexión dedicada y de larga vida para el applock de sesión (sin pool).</summary>
    public SqlConnection CreateDedicated() => new(_dedicatedConnectionString);
}
