using Microsoft.Data.SqlClient;

namespace Bots.Infrastructure.Sql;

/// <summary>
/// Datos de conexión de la app (se enlazan desde <c>Sql</c> en el Worker). La cadena se construye con
/// <see cref="SqlConnectionStringBuilder"/>: una contraseña con ';' o '=' no puede romperla ni inyectar opciones.
/// </summary>
public sealed class SqlConnectionSettings
{
    /// <summary>Servidor y puerto, p. ej. "sql,1433" en Docker o "localhost,1433" fuera.</summary>
    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = "BotsDb";

    /// <summary>Usuario de la app (APP_DB_USER): miembro de bots_app_role, sin DDL.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Contraseña (APP_DB_PASSWORD). Solo desde variables de entorno o user-secrets.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Solo para el SQL Server local de la PoC (certificado autofirmado del contenedor). En Azure debe ser
    /// <c>false</c>: el certificado se valida.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString() =>
        new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            UserID = User,
            Password = Password,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "bots-sync-repos",
        }.ConnectionString;
}
