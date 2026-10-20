using Microsoft.Data.SqlClient;

namespace Bots.Infrastructure.Resilience;

/// <summary>
/// Errores de SqlClient que se consideran transitorios (se reintentan). Todo lo demás —login
/// fallido (18456), permisos (229, 262), sintaxis, violación de UNIQUE (2627, 2601)— NO se
/// reintenta: repetirlo daría el mismo resultado.
/// </summary>
public static class SqlTransientErrors
{
    private static readonly HashSet<int> TransientNumbers =
    [
        -2,     // timeout del cliente
        20,     // la instancia no admite cifrado / conexión rota al negociar
        64,     // conexión cerrada por el servidor
        233,    // no hay proceso en el otro extremo del pipe
        1205,   // víctima de deadlock
        4060,   // no se puede abrir la base (p. ej. aún recuperándose tras un reinicio)
        4221,   // réplica de lectura no disponible
        10053,  // conexión abortada por el host
        10054,  // conexión reiniciada por el host remoto
        10060,  // timeout de red al conectar
        10928,  // Azure SQL: límite de recursos
        10929,  // Azure SQL: límite de recursos
        40143,  // Azure SQL: error al procesar la petición
        40197,  // Azure SQL: error del servicio
        40501,  // Azure SQL: servicio ocupado
        40613,  // Azure SQL: base no disponible temporalmente
        49918,  // Azure SQL: recursos insuficientes
        49919,  // Azure SQL: demasiadas operaciones
        49920,  // Azure SQL: servicio ocupado
    ];

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)),
        TimeoutException => true,
        _ => false,
    };
}
