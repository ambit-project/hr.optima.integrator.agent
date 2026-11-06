-- Schemas, tables and seed data. Safe to run again.
USE [$(AgentDatabase)];
GO
SET NOCOUNT, XACT_ABORT ON;

IF SCHEMA_ID(N'agent') IS NULL EXEC (N'CREATE SCHEMA agent;');
IF SCHEMA_ID(N'app') IS NULL EXEC (N'CREATE SCHEMA app;');
GO

-- Optima's own list of company databases, read on every call.
IF OBJECT_ID(N'agent.optima_databases') IS NOT NULL DROP SYNONYM agent.optima_databases;
CREATE SYNONYM agent.optima_databases FOR [$(ConfigDatabase)].CDN.Bazy;
GO

-- PRE_Obywatelstwo and PRE_MLDKraj are text in Optima, while Ambit holds ISO country codes.
IF OBJECT_ID(N'agent.country') IS NULL
CREATE TABLE agent.country
(
    country_code char(2) NOT NULL
        CONSTRAINT pk_agent_country PRIMARY KEY,
    country_name nvarchar(40) NOT NULL,
    citizenship_name nvarchar(30) NOT NULL
);
GO

INSERT agent.country (country_code, country_name, citizenship_name)
SELECT v.country_code, v.country_name, v.citizenship_name
FROM (VALUES ('PL', N'Polska', N'POLSKIE'), ('UA', N'Ukraina', N'UKRAIŃSKIE'))
    AS v (country_code, country_name, citizenship_name)
WHERE NOT EXISTS (SELECT 1 FROM agent.country AS c WHERE c.country_code = v.country_code);
GO

-- One row per operation and company. Holds no names, documents or message text.
IF OBJECT_ID(N'agent.employee_call') IS NULL
CREATE TABLE agent.employee_call
(
    call_id uniqueidentifier NOT NULL
        CONSTRAINT df_agent_employee_call_call_id DEFAULT (NEWSEQUENTIALID())
        CONSTRAINT pk_agent_employee_call PRIMARY KEY,
    operation_id uniqueidentifier NOT NULL,
    database_name sysname NOT NULL,
    employee_id uniqueidentifier NOT NULL,
    employee_code varchar(20) NOT NULL,
    status varchar(20) NOT NULL
        CONSTRAINT ck_agent_employee_call_status
            CHECK (status IN ('Started', 'Succeeded', 'AlreadyExists', 'NoAccess', 'Failed', 'Unknown')),
    attempt int NOT NULL
        CONSTRAINT df_agent_employee_call_attempt DEFAULT (1),
    masked_parameters nvarchar(max) NOT NULL,
    optima_employee_id int NULL,
    optima_contract_id int NULL,
    warning_codes nvarchar(400) NULL,
    error_number int NULL,
    error_procedure sysname NULL,
    error_line int NULL,
    optima_error_codes nvarchar(400) NULL,
    started_at datetime2(3) NOT NULL
        CONSTRAINT df_agent_employee_call_started_at DEFAULT (SYSUTCDATETIME()),
    finished_at datetime2(3) NULL,
    CONSTRAINT ux_agent_employee_call_operation_database UNIQUE (operation_id, database_name)
);
GO

-- For the Worker's startup check of interrupted calls, and for the retention purge.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_agent_employee_call_started_at'
               AND object_id = OBJECT_ID(N'agent.employee_call'))
    CREATE INDEX ix_agent_employee_call_started_at ON agent.employee_call (started_at) INCLUDE (status);
GO

-- The Worker's own log. Callers must not put personal data in the message.
IF OBJECT_ID(N'agent.log_entry') IS NULL
CREATE TABLE agent.log_entry
(
    log_entry_id bigint IDENTITY(1, 1) NOT NULL
        CONSTRAINT pk_agent_log_entry PRIMARY KEY,
    logged_at datetime2(3) NOT NULL
        CONSTRAINT df_agent_log_entry_logged_at DEFAULT (SYSUTCDATETIME()),
    level varchar(12) NOT NULL,
    category nvarchar(200) NOT NULL,
    message nvarchar(4000) NOT NULL,
    exception_type nvarchar(200) NULL
);
GO

-- For the retention purge.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_agent_log_entry_logged_at'
               AND object_id = OBJECT_ID(N'agent.log_entry'))
    CREATE INDEX ix_agent_log_entry_logged_at ON agent.log_entry (logged_at);
GO
