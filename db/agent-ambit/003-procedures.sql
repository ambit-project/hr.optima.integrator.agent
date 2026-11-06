/*
Everything in `app` can be called by the Worker. Safe to run again.

How the rights work, because the code below depends on it:
- The Worker's account has only EXECUTE on `app`. It reaches the `agent` tables through ownership chaining.
- Chaining stops at the database boundary. So anything on a company database runs as `[company].sys.sp_executesql`,
  in that database and with the account's own rights there, which 005-firm.sql grants column by column.
- `agent.optima_databases` is a synonym for Optima's list of companies (CDN.Bazy), not a table of ours.

Procedures in this file:
- app.log_write, app.log_purge                 the Worker's logs and their retention
- app.employee_call_mark_interrupted           closes calls cut off by a stopped Worker
- app.employee_add                             the only write to Optima
- app.payroll_components_read                  reads deductions and allowances for Ambit
*/
USE [$(AgentDatabase)];
GO

-- Keeps the OPT codes of an Optima message and drops the rest, which can hold a PESEL.
-- The message is split on '#'; a part counts as a code only when it is OPT, digits and capitals, and nothing else.
CREATE OR ALTER FUNCTION agent.optima_codes (@text nvarchar(4000))
RETURNS TABLE
AS
RETURN
    SELECT STRING_AGG(CONVERT(nvarchar(400), part.value), N',') AS codes
    FROM STRING_SPLIT(@text, N'#') AS part
    WHERE part.value LIKE N'OPT[0-9]%'
      AND part.value NOT LIKE N'%[^A-Z0-9]%';
GO

-- One line of the Worker's own log.
CREATE OR ALTER PROCEDURE app.log_write
    @level varchar(12),
    @category nvarchar(200),
    @message nvarchar(4000),
    @exception_type nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;

    INSERT agent.log_entry (level, category, message, exception_type)
    VALUES (@level, @category, @message, @exception_type);
END;
GO

-- Removes old logs and finished calls. A call still Started is kept, because its outcome is not known yet.
CREATE OR ALTER PROCEDURE app.log_purge
    @retention_days int = 30
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;

    IF @retention_days < 1
        THROW 51002, N'Retention must be at least one day.', 1;

    DECLARE @before datetime2(3) = DATEADD(DAY, -@retention_days, SYSUTCDATETIME());

    DELETE agent.log_entry WHERE logged_at < @before;
    DELETE agent.employee_call WHERE started_at < @before AND status <> 'Started';
END;
GO

-- Run by the Worker at startup: a call still Started was cut off, and may or may not have been written.
CREATE OR ALTER PROCEDURE app.employee_call_mark_interrupted
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;

    UPDATE agent.employee_call
    SET status = 'Unknown', finished_at = SYSUTCDATETIME()
    WHERE status = 'Started';
END;
GO

/*
Adds one employee to @database_name, or to every active company in Optima's list when it is NULL. Each company is
handled on its own, and one that already has this acronym or employee id is left alone. Returns one row per company.

String parameters are deliberately wider than Optima's columns: a narrower parameter would truncate without an
error, and the length check below could never see it. Text reaches Optima only as sp_executesql parameters.

The order of work:
1. Check the request once. A bad request is refused with THROW, before any company is touched.
2. List the companies and prepare the values, which are the same for every company.
3. For each company, in name order:
   a. Claim its row in agent.employee_call and commit, so a later rollback cannot erase the record.
   b. Stop if the account has no access, if the tax number differs, or if the employee is already there.
   c. Add the employee in one transaction on the company database.
   d. Record the outcome. An error here is caught and stays this company's, and the loop goes on.
4. Return one row per company, read from agent.employee_call.

Status of a company's row (operation_id + database_name):
- Started        the work is under way, or the Worker stopped during it (see app.employee_call_mark_interrupted)
- Succeeded      added
- AlreadyExists  was there before; left alone
- NoAccess       the installer gave the account no access to this company
- Failed         an error, kept as numbers and OPT codes only
- Unknown        cut off, so it may or may not have been written
Calling again with the same operation_id repeats only NoAccess, Failed and Unknown, and leaves the others as they are.
*/
CREATE OR ALTER PROCEDURE app.employee_add
    @operation_id uniqueidentifier,
    @employee_id uniqueidentifier,
    @employee_code nvarchar(4000),
    @last_name nvarchar(4000),
    @first_name nvarchar(4000),
    @database_name sysname = NULL,
    @center_id int = 3,
    @operator_id int = NULL,
    @date_of_birth date = NULL,
    @pesel nvarchar(4000) = NULL,
    @citizenship_code char(2) = NULL,
    @email nvarchar(4000) = NULL,
    @phone nvarchar(4000) = NULL,
    @street nvarchar(4000) = NULL,
    @building_number nvarchar(4000) = NULL,
    @unit_number nvarchar(4000) = NULL,
    @postal_code nvarchar(4000) = NULL,
    @city nvarchar(4000) = NULL,
    @address_country_code char(2) = NULL,
    @note nvarchar(max) = NULL,
    @document_kind varchar(20) = NULL,
    @document_number nvarchar(4000) = NULL,
    @document_valid_until date = NULL,
    @position_name nvarchar(4000) = NULL,
    @starts_on date = NULL,
    @ends_on date = NULL
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;

    -- Each company commits on its own, which a caller's transaction would undo.
    IF @@TRANCOUNT > 0
        THROW 51001, N'app.employee_add cannot run inside a transaction.', 1;

    -- Step 1: check the request.
    -- A missing value becomes an empty string, as CDN.DodajPracownika gets it. Only the position stays NULL,
    -- because NULL there means "do not look one up".
    SELECT
        @employee_code = ISNULL(TRIM(@employee_code), N''),
        @last_name = ISNULL(TRIM(@last_name), N''),
        @first_name = ISNULL(TRIM(@first_name), N''),
        @pesel = ISNULL(TRIM(@pesel), N''),
        @email = ISNULL(TRIM(@email), N''),
        @phone = ISNULL(TRIM(@phone), N''),
        @street = ISNULL(TRIM(@street), N''),
        @building_number = ISNULL(TRIM(@building_number), N''),
        @unit_number = ISNULL(TRIM(@unit_number), N''),
        @postal_code = ISNULL(TRIM(@postal_code), N''),
        @city = ISNULL(TRIM(@city), N''),
        @note = ISNULL(TRIM(@note), N''),
        @document_number = ISNULL(TRIM(@document_number), N''),
        @position_name = NULLIF(TRIM(@position_name), N'');

    IF @operation_id IS NULL OR @employee_id IS NULL
        THROW 51010, N'operation_id and employee_id are required.', 1;
    IF @employee_code = N'' OR @last_name = N'' OR @first_name = N''
        THROW 51011, N'employee_code, last_name and first_name are required.', 1;
    IF @pesel <> N'' AND (LEN(@pesel) <> 11 OR @pesel LIKE N'%[^0-9]%')
        THROW 51012, N'pesel must have 11 digits.', 1;
    IF @document_kind IS NOT NULL
       AND @document_kind NOT IN ('IdentityCard', 'Passport', 'ResidenceCard', 'Other')
        THROW 51013, N'document_kind is not known.', 1;

    -- Each limit is the size of Optima's column. All fields over the limit are named at once, and nothing is truncated.
    -- Documents other than an identity card or passport are not stored (DOCUMENT_NOT_STORED below), hence 4000.
    DECLARE @too_long nvarchar(1000) =
    (
        SELECT STRING_AGG(v.field, N', ')
        FROM (VALUES
            (N'employee_code', LEN(@employee_code), 20),
            (N'last_name', LEN(@last_name), 40),
            (N'first_name', LEN(@first_name), 30),
            (N'email', LEN(@email), 127),
            (N'phone', LEN(@phone), 20),
            (N'street', LEN(@street), 40),
            (N'building_number', LEN(@building_number), 10),
            (N'unit_number', LEN(@unit_number), 10),
            (N'postal_code', LEN(@postal_code), 10),
            (N'city', LEN(@city), 40),
            (N'note', LEN(@note), 1024),
            (N'document_number', LEN(@document_number),
                CASE @document_kind WHEN 'Passport' THEN 9 WHEN 'IdentityCard' THEN 12 ELSE 4000 END)
        ) AS v (field, length, max_length)
        WHERE v.length > v.max_length
    );
    IF @too_long IS NOT NULL
    BEGIN
        DECLARE @too_long_message nvarchar(1100) = N'Too long for Optima: ' + @too_long + N'.';
        THROW 51014, @too_long_message, 1;
    END;

    -- Step 2: the companies this call covers, each with the tax number from Optima's list, without dashes.
    -- NULLIF turns "no number on the list" into NULL, which later means "nothing to compare".
    DECLARE @firms TABLE (database_name sysname NOT NULL PRIMARY KEY, tax_id nvarchar(40) NULL);

    INSERT @firms (database_name, tax_id)
    SELECT
        CONVERT(sysname, TRIM(Baz_NazwaBazy)),
        NULLIF(MAX(ISNULL(REPLACE(TRIM(Baz_NIP), '-', ''), '')), '')
    FROM agent.optima_databases
    WHERE ISNULL(Baz_Nieaktywna, 0) = 0
      AND TRIM(Baz_NazwaBazy) <> ''
      AND (@database_name IS NULL OR TRIM(Baz_NazwaBazy) = @database_name)
    GROUP BY CONVERT(sysname, TRIM(Baz_NazwaBazy));

    IF NOT EXISTS (SELECT 1 FROM @firms)
        THROW 51401, N'No active Optima company database matches this request.', 1;

    -- Values for Optima: never read back from its row, so the account needs no SELECT on those columns.
    DECLARE
        @import_row_id nvarchar(36) = CONVERT(nvarchar(36), @employee_id),
        @birth nvarchar(10) = CONVERT(nvarchar(10), @date_of_birth, 112),
        @hired nvarchar(10) = CONVERT(nvarchar(10), @starts_on, 112),
        @citizenship_name nvarchar(30) = N'',
        @has_address bit = IIF(@street + @building_number + @postal_code + @city <> N'', 1, 0),
        @address_country_name nvarchar(40) = N'Polska',
        @identity_card nvarchar(12) = IIF(@document_kind = 'IdentityCard', @document_number, N''),
        @passport nvarchar(9) = IIF(@document_kind = 'Passport', @document_number, N'');

    -- Optima keeps countries as text and Ambit as ISO codes, so agent.country translates. An unknown code gives ''.
    SELECT @citizenship_name = citizenship_name FROM agent.country WHERE country_code = @citizenship_code;

    -- With no address, or no country given for it, the address country stays Polska / PL.
    IF @has_address = 1 AND @address_country_code IS NOT NULL
        SET @address_country_name = ISNULL(
            (SELECT country_name FROM agent.country WHERE country_code = @address_country_code), N'');
    ELSE
        SET @address_country_code = 'PL';

    DECLARE @citizenship_code_text nvarchar(2) = ISNULL(CONVERT(nvarchar(2), @citizenship_code), N'');

    -- Which values were given, never what they were. Code and dates of employment identify the record.
    DECLARE @masked nvarchar(max) =
    (
        SELECT
            @employee_code AS employee_code,
            @starts_on AS starts_on,
            @ends_on AS ends_on,
            @citizenship_code AS citizenship_code,
            @document_kind AS document_kind,
            @position_name AS position_name,
            IIF(@pesel <> N'', 1, 0) AS has_pesel,
            IIF(@date_of_birth IS NOT NULL, 1, 0) AS has_date_of_birth,
            IIF(@document_number <> N'', 1, 0) AS has_document_number,
            IIF(@email <> N'', 1, 0) AS has_email,
            IIF(@phone <> N'', 1, 0) AS has_phone,
            @has_address AS has_address,
            IIF(@note <> N'', 1, 0) AS has_note
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    /*
    What runs on the company database, in one transaction (step 3c):
    1. Comarch's CDN.DodajPracownika adds the employee. PRA_ImportRowId holds Ambit's employee id and PRA_Kod the acronym.
    2. The UPDATE sets the columns that procedure has no parameter for.
    3. The last SELECT returns the employee's row id, because the procedure returns only the contract's.
    */
    DECLARE @add_sql nvarchar(max) = N'
EXEC CDN.DodajPracownika
    @OnlyNew = 1, @ConfigDBName = N'''', @ServerName = N'''',
    @PRA_ImportRowId = @import_row_id, @PRA_Kod = @employee_code, @PRA_Archiwalny = 0,
    @PRE_Nazwisko = @last_name, @PRE_Imie1 = @first_name, @PRE_Imie2 = N'''', @PRE_ImieOjca = N'''', @PRE_ImieMatki = N'''',
    @PRE_DataUr = @birth, @PRE_MiejsceUr = N'''', @PRE_NazwRodowe = N'''', @PRE_NazwRodoweM = N'''',
    @PRE_Pesel = @pesel, @PRE_NipKraj = N'''', @PRE_NipE = N'''', @Status = N'''',
    @PRE_MLDKraj = @address_country_name, @PRE_MLDWojewodztwo = N'''', @PRE_MLDPowiat = N'''', @PRE_MLDGmina = N'''',
    @PRE_MLDUlica = @street, @PRE_MLDNrDomu = @building_number, @PRE_MLDNrLokalu = @unit_number,
    @PRE_MLDMiasto = @city, @PRE_MLDKodPocztowy = @postal_code, @PRE_MLDPoczta = N'''', @PRE_MLDAdres2 = N'''',
    @PRE_HDKTelefon1 = @phone, @PRE_ZatrudnionyOd = @hired,
    @PRE_PODMnoznikKoszty = 1, @PRE_PODMnoznikUlga = 1,
    @Urz_Akronim = NULL, @Urz_ImportRowId = NULL, @Fpl_Nazwa = NULL, @Fpl_ImportRowId = NULL,
    @Kat_KodSzczegol = NULL, @Kat_ImportRowId = NULL, @PRE_KategoriaOpis = N'''',
    @BNa_Numer = NULL, @BNa_ImportRowId = NULL, @PRE_RachunekNr = N'''', @PRE_IBAN = 0,
    @PRE_HDKUwagi = @note, @PRE_NieRozliczac = 0, @PRE_RodzajDokumentu = 0,
    @PRE_KontoKsiegowe = N'''', @PRE_KontoKsiegowe2 = N'''', @PRE_ETAEtatL = NULL, @PRE_ETAEtatM = NULL,
    @Zak_Symbol = NULL, @Zak_ImportRowId = NULL, @ZrdId = N''AMBIT'', @PRE_HDKEmail = @email,
    @CntId = @center_id, @StanowiskoId = @position_id, @OpeId = @operator_id,
    @SlowId = @contract_id OUTPUT, @Warnings = @warnings OUTPUT;

UPDATE CDN.PracEtaty
SET PRE_Plec = N'''',
    PRE_Obywatelstwo = @citizenship_name,
    PRE_ObywatelKodKraju = @citizenship_code_text,
    PRE_MLDKodKraju = @address_country_code,
    PRE_DowOsobisty = @identity_card,
    PRE_Paszport = @passport,
    PRE_DowWaznyDo = @document_valid_until,
    PRE_ZatrudnionyDo = @ends_on
WHERE PRE_PreId = @contract_id;

SELECT @employee_row_id = PRE_PraId FROM CDN.PracEtaty WHERE PRE_PreId = @contract_id;';

    -- These sizes are Optima's. They cannot truncate anything, because step 1 has already refused longer values.
    DECLARE @add_parameters nvarchar(max) = N'
@import_row_id nvarchar(36), @employee_code nvarchar(20), @last_name nvarchar(40), @first_name nvarchar(30),
@birth nvarchar(10), @pesel nvarchar(11), @address_country_name nvarchar(40), @street nvarchar(40),
@building_number nvarchar(10), @unit_number nvarchar(10), @city nvarchar(40), @postal_code nvarchar(10),
@phone nvarchar(20), @hired nvarchar(10), @note nvarchar(1024), @email nvarchar(127),
@center_id int, @position_id int, @operator_id int,
@citizenship_name nvarchar(30), @citizenship_code_text nvarchar(2), @address_country_code char(2),
@identity_card nvarchar(12), @passport nvarchar(9), @document_valid_until date, @ends_on date,
@contract_id int OUTPUT, @warnings nvarchar(1000) OUTPUT, @employee_row_id int OUTPUT';

    -- Step 3: one pass per company. Each pass takes the next name after @current, and ends when there is none.
    DECLARE @current sysname = N'';

    WHILE 1 = 1
    BEGIN
        DECLARE @firm sysname = NULL, @tax_id nvarchar(40) = NULL;

        SELECT TOP (1) @firm = database_name, @tax_id = tax_id
        FROM @firms
        WHERE database_name > @current
        ORDER BY database_name;

        IF @firm IS NULL BREAK;
        SET @current = @firm;

        -- Step 3a: claim the row. Committed on its own, so that the company's rollback cannot erase it.
        -- The locks make two simultaneous calls with one operation_id take turns, so only one of them proceeds.
        -- @proceed stays 0 for Started, Succeeded and AlreadyExists: that company is skipped, and step 4 reports its row.
        DECLARE @previous varchar(20) = NULL, @proceed bit = 0;

        BEGIN TRANSACTION;

        SELECT @previous = status
        FROM agent.employee_call WITH (UPDLOCK, HOLDLOCK)
        WHERE operation_id = @operation_id AND database_name = @firm;

        IF @previous IS NULL
        BEGIN
            INSERT agent.employee_call
                (operation_id, database_name, employee_id, employee_code, status, masked_parameters)
            VALUES
                (@operation_id, @firm, @employee_id, CONVERT(varchar(20), @employee_code), 'Started', @masked);
            SET @proceed = 1;
        END;
        ELSE IF @previous IN ('Failed', 'Unknown', 'NoAccess')
        BEGIN
            -- Only an explicit new request gets here, and the check below stops a second employee.
            UPDATE agent.employee_call
            SET status = 'Started', attempt = attempt + 1, started_at = SYSUTCDATETIME(), finished_at = NULL,
                error_number = NULL, error_procedure = NULL, error_line = NULL, optima_error_codes = NULL,
                warning_codes = NULL
            WHERE operation_id = @operation_id AND database_name = @firm;
            SET @proceed = 1;
        END;

        COMMIT TRANSACTION;

        IF @proceed = 0 CONTINUE;

        BEGIN TRY
            -- Step 3b: the checks. `EXEC @execute` runs the procedure named in the variable, here
            -- [company].sys.sp_executesql, so the SQL passed to it runs on that company's database.
            DECLARE
                @execute nvarchar(300) = QUOTENAME(@firm) + N'.sys.sp_executesql',
                @actual_tax_id nvarchar(400) = NULL,
                @existing_id int = NULL,
                @existing_row_id nvarchar(36) = NULL,
                @position_id int = NULL,
                @contract_id int = NULL,
                @employee_row_id int = NULL,
                @warnings nvarchar(1000) = NULL,
                @extra_warnings nvarchar(100) = NULL;

            -- The account has a user only where an administrator gave it one, so this is the real boundary.
            IF ISNULL(HAS_DBACCESS(@firm), 0) = 0
            BEGIN
                UPDATE agent.employee_call
                SET status = 'NoAccess', finished_at = SYSUTCDATETIME()
                WHERE operation_id = @operation_id AND database_name = @firm;
                CONTINUE;
            END;

            EXEC @execute
                N'SELECT @value = REPLACE(CONVERT(nvarchar(400), Fir_Wartosc), N''-'', N'''') FROM CDN.Firma WHERE Fir_Numer = 1101;',
                N'@value nvarchar(400) OUTPUT',
                @value = @actual_tax_id OUTPUT;

            -- Guards against a database that is not the company the list says it is. CDN.Firma row 1101 is its tax number.
            -- Optima's list has no tax number for some companies, and then there is nothing to compare.
            IF @tax_id IS NOT NULL AND ISNULL(@actual_tax_id, N'') <> @tax_id
                THROW 51501, N'The tax number in this database differs from Optima''s list.', 1;

            -- The acronym Ambit gave this person decides; the employee id also finds one renamed in Optima.
            -- When both match different rows, the ORDER BY prefers the row with the employee id.
            -- This check is what stops a repeated call from adding a second employee.
            EXEC @execute
                N'SELECT TOP (1) @value = PRA_PraId, @row_id = PRA_ImportRowId FROM CDN.PracKod WHERE PRA_Kod = @code OR PRA_ImportRowId = @import_row_id ORDER BY CASE WHEN PRA_ImportRowId = @import_row_id THEN 0 ELSE 1 END;',
                N'@code nvarchar(20), @import_row_id nvarchar(36), @value int OUTPUT, @row_id nvarchar(36) OUTPUT',
                @code = @employee_code, @import_row_id = @import_row_id,
                @value = @existing_id OUTPUT, @row_id = @existing_row_id OUTPUT;

            IF @existing_id IS NOT NULL
            BEGIN
                -- Same acronym from another source: possibly a different person, so it has to be visible.
                UPDATE agent.employee_call
                SET status = 'AlreadyExists',
                    optima_employee_id = @existing_id,
                    warning_codes = IIF(ISNULL(@existing_row_id, N'') = @import_row_id, NULL, N'MATCHED_BY_ACRONYM_ONLY'),
                    finished_at = SYSUTCDATETIME()
                WHERE operation_id = @operation_id AND database_name = @firm;
                CONTINUE;
            END;

            -- A position or document Optima cannot take does not stop the employee: it becomes a warning on the row.
            IF @position_name IS NOT NULL
            BEGIN
                EXEC @execute
                    N'SELECT TOP (1) @value = DKM_DkmId FROM CDN.DaneKadMod WHERE DKM_Rodzaj = 1 AND DKM_Nazwa = @name ORDER BY DKM_DkmId;',
                    N'@name nvarchar(4000), @value int OUTPUT',
                    @name = @position_name, @value = @position_id OUTPUT;

                IF @position_id IS NULL
                    SET @extra_warnings = N'POSITION_NOT_FOUND';
            END;

            IF @document_number <> N'' AND @document_kind IN ('ResidenceCard', 'Other')
                SET @extra_warnings = CONCAT_WS(N',', @extra_warnings, N'DOCUMENT_NOT_STORED');

            -- Step 3c: the write. @add_sql is described where it is declared.
            BEGIN TRANSACTION;

            EXEC @execute @add_sql, @add_parameters,
                @import_row_id = @import_row_id, @employee_code = @employee_code, @last_name = @last_name,
                @first_name = @first_name, @birth = @birth, @pesel = @pesel,
                @address_country_name = @address_country_name, @street = @street,
                @building_number = @building_number, @unit_number = @unit_number, @city = @city,
                @postal_code = @postal_code, @phone = @phone, @hired = @hired, @note = @note, @email = @email,
                @center_id = @center_id, @position_id = @position_id, @operator_id = @operator_id,
                @citizenship_name = @citizenship_name, @citizenship_code_text = @citizenship_code_text,
                @address_country_code = @address_country_code, @identity_card = @identity_card,
                @passport = @passport, @document_valid_until = @document_valid_until, @ends_on = @ends_on,
                @contract_id = @contract_id OUTPUT, @warnings = @warnings OUTPUT,
                @employee_row_id = @employee_row_id OUTPUT;

            COMMIT TRANSACTION;

            -- Step 3d: the outcome. Warnings from Optima are reduced to their OPT codes, as errors are below.
            UPDATE agent.employee_call
            SET status = 'Succeeded',
                optima_employee_id = @employee_row_id,
                optima_contract_id = @contract_id,
                warning_codes = NULLIF(CONCAT_WS(N',', (SELECT codes FROM agent.optima_codes(@warnings)), @extra_warnings), N''),
                finished_at = SYSUTCDATETIME()
            WHERE operation_id = @operation_id AND database_name = @firm;
        END TRY
        BEGIN CATCH
            -- The message text is read only for its OPT codes and never stored, because it can hold personal data.
            -- Nothing is rethrown, so the loop goes on to the next company.
            DECLARE
                @error_number int = ERROR_NUMBER(),
                @error_procedure sysname = ERROR_PROCEDURE(),
                @error_line int = ERROR_LINE(),
                @error_message nvarchar(4000) = ERROR_MESSAGE();

            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

            UPDATE agent.employee_call
            SET status = 'Failed',
                error_number = @error_number,
                error_procedure = @error_procedure,
                error_line = @error_line,
                optima_error_codes = (SELECT codes FROM agent.optima_codes(@error_message)),
                finished_at = SYSUTCDATETIME()
            WHERE operation_id = @operation_id AND database_name = @firm;
        END CATCH;
    END;

    -- Step 4: the result, read from the record, so it also covers companies skipped in step 3a.
    SELECT
        database_name, status, attempt, optima_employee_id, optima_contract_id, warning_codes,
        error_number, error_procedure, optima_error_codes
    FROM agent.employee_call
    WHERE operation_id = @operation_id
      AND database_name IN (SELECT database_name FROM @firms)
    ORDER BY database_name;
END;
GO

/*
Reads the active payroll components of every active company: deductions and allowances. Only reads, so it keeps no call
record. One result set per kind of fact: first what happened on each company, then the components. A company's failure
stays that company's.

A company's status is NoAccess, Read or Failed. Its components come back only with Read.
*/
-- The earlier name of this procedure, removed when an existing installation is upgraded.
DROP PROCEDURE IF EXISTS app.deduction_definitions_read;
GO
CREATE OR ALTER PROCEDURE app.payroll_components_read
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @firms TABLE (
        database_name sysname NOT NULL PRIMARY KEY,
        status varchar(20) NULL,
        tax_number nvarchar(400) NULL,
        error_number int NULL
    );

    INSERT @firms (database_name)
    SELECT DISTINCT CONVERT(sysname, TRIM(Baz_NazwaBazy))
    FROM agent.optima_databases
    WHERE ISNULL(Baz_Nieaktywna, 0) = 0 AND TRIM(Baz_NazwaBazy) <> '';

    -- A temporary table, because rows from `INSERT ... EXEC` cannot say which company they came from. database_name
    -- stays NULL until the company's read has finished, so NULL marks the rows of the company being read right now.
    CREATE TABLE #components (
        optima_id int NOT NULL,
        name nvarchar(40) NOT NULL,
        short_name nvarchar(40) NULL,
        is_deduction bit NOT NULL,
        source_kind int NOT NULL,
        standard_id smallint NULL,
        default_amount decimal(15, 2) NULL,
        is_pkzp bit NOT NULL,
        is_seizure bit NOT NULL,
        import_row_id varchar(36) NULL,
        import_row_id2 varchar(36) NULL,
        database_name sysname NULL
    );

    -- One pass per company, in name order. Each pass ends by taking the next name, and NULL means there is none.
    DECLARE @firm sysname = (SELECT MIN(database_name) FROM @firms);

    WHILE @firm IS NOT NULL
    BEGIN
        -- The account has a user only where an administrator gave it one, so this is the real boundary.
        IF ISNULL(HAS_DBACCESS(@firm), 0) = 0
            UPDATE @firms SET status = 'NoAccess' WHERE database_name = @firm;
        ELSE
        BEGIN TRY
            DECLARE
                @execute nvarchar(300) = QUOTENAME(@firm) + N'.sys.sp_executesql',
                @tax_number nvarchar(400) = NULL;

            EXEC @execute
                N'SELECT @value = REPLACE(CONVERT(nvarchar(400), Fir_Wartosc), N''-'', N'''') FROM CDN.Firma WHERE Fir_Numer = 1101;',
                N'@value nvarchar(400) OUTPUT',
                @value = @tax_number OUTPUT;

            -- Active definitions only, deductions and allowances alike. TWP_AlgPotracenie = 1 marks a deduction.
            -- TWP_TwpId differs between companies, so optima_id means something only together with database_name.
            INSERT #components (
                optima_id, name, short_name, is_deduction, source_kind, standard_id, default_amount, is_pkzp,
                is_seizure, import_row_id, import_row_id2
            )
            EXEC @execute
                N'SELECT
                      TWP_TwpId, CONVERT(nvarchar(40), TWP_Nazwa), TWP_Skrot,
                      CONVERT(bit, CASE WHEN TWP_AlgPotracenie = 1 THEN 1 ELSE 0 END),
                      TWP_RodzajZrodla, TWP_Standardowy, TWP_AlgKwota,
                      CONVERT(bit, CASE WHEN ISNULL(TWP_PKZP, 0) <> 0 THEN 1 ELSE 0 END),
                      CONVERT(bit, CASE WHEN ISNULL(TWP_ZajecieWynagr, 0) <> 0 THEN 1 ELSE 0 END),
                      TWP_ImportRowId, TWP_ImportRowId2
                  FROM CDN.TypWyplata
                  WHERE ISNULL(TWP_Nieaktywny, 0) = 0
                    AND ISNULL(TWP_Wycofany, 0) = 0;';

            UPDATE #components SET database_name = @firm WHERE database_name IS NULL;
            UPDATE @firms SET status = 'Read', tax_number = @tax_number WHERE database_name = @firm;
        END TRY
        BEGIN CATCH
            -- Drops whatever this company's read left behind, so a failed company never looks partly read.
            DELETE #components WHERE database_name IS NULL;
            UPDATE @firms SET status = 'Failed', error_number = ERROR_NUMBER() WHERE database_name = @firm;
        END CATCH;

        SET @firm = (SELECT MIN(database_name) FROM @firms WHERE database_name > @firm);
    END;

    SELECT database_name, status, tax_number, error_number FROM @firms ORDER BY database_name;

    SELECT
        database_name, optima_id, name, short_name, is_deduction, source_kind, standard_id, default_amount, is_pkzp,
        is_seizure, import_row_id, import_row_id2
    FROM #components
    ORDER BY database_name, name;
END;
GO
