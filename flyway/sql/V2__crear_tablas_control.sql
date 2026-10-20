-- V2: tablas de control de corridas (idempotencia y auditoría).
-- Inmutable una vez aplicada: cualquier cambio va en una versión nueva.

CREATE TABLE bots.job_runs
(
    id                BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_job_runs PRIMARY KEY,
    job_name          NVARCHAR(100)  NOT NULL,
    scheduled_for_utc DATETIME2(3)   NOT NULL,
    -- Se incrementa al retomar la misma ocurrencia tras Failed/Abandoned.
    attempt           INT            NOT NULL CONSTRAINT DF_job_runs_attempt DEFAULT 1
        CONSTRAINT CK_job_runs_attempt CHECK (attempt >= 1),
    started_at_utc    DATETIME2(3)   NOT NULL,
    finished_at_utc   DATETIME2(3)   NULL,
    -- Nombres de Bots.Domain.JobRunStatus.
    status            NVARCHAR(20)   NOT NULL
        CONSTRAINT CK_job_runs_status CHECK (status IN (N'Running', N'Succeeded', N'PartiallyFailed', N'Failed', N'Abandoned')),
    repos_ok          INT            NOT NULL CONSTRAINT DF_job_runs_repos_ok DEFAULT 0
        CONSTRAINT CK_job_runs_repos_ok CHECK (repos_ok >= 0),
    repos_failed      INT            NOT NULL CONSTRAINT DF_job_runs_repos_failed DEFAULT 0
        CONSTRAINT CK_job_runs_repos_failed CHECK (repos_failed >= 0),
    -- SyncReposJob trunca el resumen a 2000 caracteres.
    error_summary     NVARCHAR(2000) NULL,
    -- Anti-duplicado: una sola fila por ocurrencia programada de cada job.
    CONSTRAINT UQ_job_runs_job_scheduled UNIQUE (job_name, scheduled_for_utc)
);
GO

CREATE TABLE bots.job_run_errors
(
    id              BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_job_run_errors PRIMARY KEY,
    job_run_id      BIGINT        NOT NULL
        CONSTRAINT FK_job_run_errors_job_runs REFERENCES bots.job_runs (id),
    -- Intento en que ocurrió: conserva el detalle de cada intento aunque job_runs se reutilice.
    attempt         INT           NOT NULL CONSTRAINT CK_job_run_errors_attempt CHECK (attempt >= 1),
    -- NULL para errores de toda la corrida (listado, 0 repos, fallo fatal).
    repo_key        NVARCHAR(256) NULL,
    error_type      NVARCHAR(200) NOT NULL,
    message         NVARCHAR(MAX) NOT NULL,
    occurred_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF_job_run_errors_occurred_at_utc DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_job_run_errors_job_run_attempt ON bots.job_run_errors (job_run_id, attempt);
GO
