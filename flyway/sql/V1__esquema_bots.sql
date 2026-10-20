-- V1: esquema `bots` y tablas de datos de repositorios.
-- Inmutable una vez aplicada: cualquier cambio va en una versión nueva.
-- Fechas en UTC (DATETIME2, sufijo _utc).

-- El rol lo crea sql-init. Sin él el GRANT final no tendría destinatario: se falla aquí
-- con un mensaje claro en vez de omitirlo.
IF DATABASE_PRINCIPAL_ID('bots_app_role') IS NULL
    THROW 50001, 'Falta el rol bots_app_role en BotsDb. Ejecuta sql-init antes de las migraciones.', 1;
GO

-- CREATE SCHEMA debe ir solo en su lote: se ejecuta vía EXEC.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'bots')
    EXEC('CREATE SCHEMA bots');
GO

CREATE TABLE bots.repos
(
    id              BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_repos PRIMARY KEY,
    -- owner/nombre. Gitea no distingue mayúsculas en nombres de owner/repo: la collation
    -- por defecto (CI) es la adecuada para el UNIQUE.
    external_key    NVARCHAR(256)  NOT NULL CONSTRAINT UQ_repos_external_key UNIQUE,
    title           NVARCHAR(256)  NOT NULL,
    description     NVARCHAR(2048) NULL,
    has_readme      BIT            NOT NULL,
    pr_count        INT            NOT NULL CONSTRAINT CK_repos_pr_count CHECK (pr_count >= 0),
    captured_at_utc DATETIME2(3)   NOT NULL,
    -- Última corrida que actualizó el repo. Sin FK: bots.job_runs se crea en V2.
    last_job_run_id BIGINT         NULL,
    created_at_utc  DATETIME2(3)   NOT NULL CONSTRAINT DF_repos_created_at_utc DEFAULT SYSUTCDATETIME(),
    updated_at_utc  DATETIME2(3)   NOT NULL CONSTRAINT DF_repos_updated_at_utc DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE bots.repo_branches
(
    id              BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_repo_branches PRIMARY KEY,
    repo_id         BIGINT        NOT NULL
        CONSTRAINT FK_repo_branches_repos REFERENCES bots.repos (id) ON DELETE CASCADE,
    -- Los nombres de rama en git distinguen mayúsculas ('Main' y 'main' son ramas distintas):
    -- collation binaria, igual que la comparación ordinal del Domain.
    branch_name     NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
    commit_count    INT           NOT NULL CONSTRAINT CK_repo_branches_commit_count CHECK (commit_count >= 0),
    captured_at_utc DATETIME2(3)  NOT NULL,
    CONSTRAINT UQ_repo_branches_repo_branch UNIQUE (repo_id, branch_name)
);
GO

-- Permisos de la app a nivel de esquema: cubren las tablas de V2 y las futuras.
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::bots TO bots_app_role;
GO
