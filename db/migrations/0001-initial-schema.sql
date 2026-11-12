-- The integrator's own tables. Applied once by DbUp, which records it in dbo.SchemaVersions.
CREATE SCHEMA integrator;
GO

-- One row per employee call and company. Holds no names, documents or message text.
CREATE TABLE integrator.employee_call
(
    call_id uniqueidentifier NOT NULL
        CONSTRAINT df_employee_call_call_id DEFAULT (NEWSEQUENTIALID())
        CONSTRAINT pk_employee_call PRIMARY KEY,
    operation_id uniqueidentifier NOT NULL,
    database_name sysname NOT NULL,
    employee_id uniqueidentifier NOT NULL,
    employee_code varchar(20) NOT NULL,
    status varchar(20) NOT NULL
        CONSTRAINT ck_employee_call_status
            CHECK (status IN ('Started', 'Succeeded', 'AlreadyExists', 'NoAccess', 'Failed', 'Unknown')),
    attempt int NOT NULL
        CONSTRAINT df_employee_call_attempt DEFAULT (1),
    masked_parameters nvarchar(max) NOT NULL,
    optima_employee_id int NULL,
    optima_contract_id int NULL,
    warning_codes nvarchar(400) NULL,
    error_number int NULL,
    error_procedure sysname NULL,
    error_line int NULL,
    optima_error_codes nvarchar(400) NULL,
    started_at datetime2(3) NOT NULL
        CONSTRAINT df_employee_call_started_at DEFAULT (SYSUTCDATETIME()),
    finished_at datetime2(3) NULL,
    CONSTRAINT ux_employee_call_operation_database UNIQUE (operation_id, database_name)
);

-- For the startup check of interrupted calls and for the retention purge.
CREATE INDEX ix_employee_call_started_at ON integrator.employee_call (started_at) INCLUDE (status);

-- One row per operation and deduction: what each attempt found. The guard against a duplicate is the import key in
-- Optima, not this table. Holds ids, the period and the amount, no names.
CREATE TABLE integrator.deduction_call
(
    operation_id uniqueidentifier NOT NULL,
    deduction_id uniqueidentifier NOT NULL,
    database_name sysname NOT NULL,
    employee_id uniqueidentifier NOT NULL,
    optima_key uniqueidentifier NOT NULL,
    period_year smallint NOT NULL,
    period_month tinyint NOT NULL,
    optima_component_id int NOT NULL,
    amount decimal(15, 2) NOT NULL,
    status varchar(20) NOT NULL
        CONSTRAINT ck_deduction_call_status
            CHECK (status IN ('Succeeded', 'AlreadyExists', 'NoAccess', 'EmployeeNotFound', 'ComponentNotFound',
                              'Failed')),
    attempt int NOT NULL
        CONSTRAINT df_deduction_call_attempt DEFAULT (1),
    optima_employee_id int NULL,
    optima_addition_id int NULL,
    warning_codes nvarchar(400) NULL,
    error_number int NULL,
    error_procedure sysname NULL,
    error_line int NULL,
    started_at datetime2(3) NOT NULL
        CONSTRAINT df_deduction_call_started_at DEFAULT (SYSUTCDATETIME()),
    finished_at datetime2(3) NULL,
    CONSTRAINT pk_deduction_call PRIMARY KEY (operation_id, deduction_id)
);

CREATE INDEX ix_deduction_call_started_at ON integrator.deduction_call (started_at);

-- The Worker's own log. Messages carry identifiers and codes, never personal data.
CREATE TABLE integrator.log_entry
(
    log_entry_id bigint IDENTITY(1, 1) NOT NULL
        CONSTRAINT pk_log_entry PRIMARY KEY,
    logged_at datetime2(3) NOT NULL
        CONSTRAINT df_log_entry_logged_at DEFAULT (SYSUTCDATETIME()),
    level varchar(12) NOT NULL,
    category nvarchar(200) NOT NULL,
    message nvarchar(4000) NOT NULL,
    exception_type nvarchar(200) NULL
);

CREATE INDEX ix_log_entry_logged_at ON integrator.log_entry (logged_at);
