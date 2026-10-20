-- V3: claves de idempotencia para las escrituras de control.
-- Inmutable una vez aplicada: cualquier cambio va en una versión nueva.
--
-- Polly reintenta una operación completa si falla con un error transitorio. Si lo que se perdió fue
-- la respuesta de un COMMIT que sí se aplicó, el reintento repetiría el efecto. La app genera la clave
-- una vez por operación lógica (fuera del reintento) y así el reintento reconoce su propia escritura.
-- Las filas anteriores a V3 quedan con NULL.

-- Clave del TryStartAsync que abrió o retomó el intento actual: un reintento con la misma clave
-- adopta la fila en vez de tratarla como huérfana (y gastar un intento).
ALTER TABLE bots.job_runs ADD start_token UNIQUEIDENTIFIER NULL;
GO

-- Clave del AddErrorAsync que insertó el error: un reintento no duplica la fila. Las filas de
-- auditoría que escribe la retoma de un intento van en la misma transacción y no la necesitan (NULL).
ALTER TABLE bots.job_run_errors ADD idempotency_key UNIQUEIDENTIFIER NULL;
GO

CREATE UNIQUE INDEX UX_job_run_errors_idempotency_key
    ON bots.job_run_errors (idempotency_key)
    WHERE idempotency_key IS NOT NULL;
GO
