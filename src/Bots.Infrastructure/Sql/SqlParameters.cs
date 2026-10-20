using System.Data;
using Dapper;

namespace Bots.Infrastructure.Sql;

/// <summary>
/// Las columnas de fecha son DATETIME2 en UTC. Dapper enviaría un <see cref="DateTime"/> como el tipo
/// antiguo DATETIME (precisión de 3,33 ms) y un <see cref="DateTimeOffset"/> como DATETIMEOFFSET (que
/// obliga a convertir la columna e impide usar el índice), así que se declara DATETIME2 explícitamente.
/// </summary>
internal static class SqlParameters
{
    public static DynamicParameters AddUtc(this DynamicParameters parameters, string name, DateTimeOffset value)
    {
        parameters.Add(name, value.UtcDateTime, DbType.DateTime2);
        return parameters;
    }

    /// <summary>Lee un DATETIME2 guardado en UTC.</summary>
    public static DateTimeOffset FromUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
