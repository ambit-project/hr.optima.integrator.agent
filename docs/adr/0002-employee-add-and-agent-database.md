# ADR 0002: Adding an employee, and the `agent-ambit` database

Status: proposed (2026-11-06). When accepted, it replaces the parts of ADR 0001 named under Consequences.

## Context

Ambit has to add a worker to the Comarch ERP Optima databases of a client's companies, possibly around 20 of them.
ADR 0001 kept named write procedures, but said we do not write them ourselves and stored results in SQLite.

What was found on a real Optima instance (`CDN_Firma_Demo`, `CDN_Ambit`):

- Optima ships `CDN.DodajPracownika`. One call writes `PracKod`, `PracEtaty`, `SchematPlatnosci` and `Pracidx`, and a
  trigger writes `PodmiotyView`. The records open, list and save in Optima.
- It does not accept citizenship, identity documents or the end of employment, and it hard-codes sex `M` and
  citizenship `POLSKIE`/`PL`. An empty sex survives a real save in Optima.
- It opens no transaction, and its `RAISERROR` has no `RETURN`, so without `TRY` it carries on after an error.
- Its dates work only as `YYYYMMDD`: it sets `DATEFORMAT DMY` and compares a date string with `0`.
- Its parameters silently truncate: a 35-character first name became 30 characters.
- `@Warnings` and error messages can carry a PESEL, and error 2628 can carry the truncated value.
- An invalid PESEL is cleared and the employee is added anyway, with a warning.
- `CDN.Firma` row `Fir_Numer = 1101` holds the company's tax number.

## Decision

- The installer, run by an administrator, creates `agent-ambit` with its tables and procedures, and may be run again.
  The Worker's account gets only `EXECUTE` on the `app` schema.
- `app.employee_add` adds one employee: to the company named by `@database_name`, or to every active company in
  Optima's own list (`CDN.Bazy` in the configuration database), read on each call. Each company has its own
  transaction and its own status, so one failure does not stop the others.
- The Worker offers this as the IPC command `employee.add`, refused unless `Writes:Enabled` is set in local
  configuration. An IPC request may now carry typed values, and still has no field for SQL text.
- Each company call runs `CDN.DodajPracownika`, then a fixed `UPDATE` of the columns it cannot set, in one transaction.
- On a company database the Worker's account has `EXECUTE` on that procedure and column-level rights only. The
  installer grants them to the companies an administrator selects (ADR 0004), and the Worker cannot grant itself
  anything. A listed company without those rights is reported as `NoAccess`.
- Before writing, the procedure compares the company's tax number with Optima's list, when the list has one.
- A value too long for Optima rejects the whole request and names the fields. Nothing is truncated.
- Fields Ambit does not have stay `NULL` or empty. With no address, Optima's own default country stays.
- Ambit gives each worker an acronym (`PRA_Kod`), and `PRA_ImportRowId` is the Ambit worker id. A company that already
  has either is left alone. A match on the acronym alone is reported, since it may be a different person.
- Logs and procedure calls live in `agent-ambit`. The call row is committed before the company transaction, so a
  rollback cannot erase it. It stores error numbers and `OPT` codes, never message text, and masks personal data.
  Retention is 30 days.
- A call left `Started` when the Worker stopped becomes `Unknown` at startup and is not repeated automatically.

## Consequences

- This replaces three points of ADR 0001: local storage of logs and results in SQLite, "we do not write procedures
  ourselves" as far as our own wrapper and its `UPDATE` go, and the local list of databases, which becomes Optima's
  list limited by SQL rights. The other rules of ADR 0001 stay.
- A new company gets no access until the installer runs again.
- With writes on, any signed-in local user can reach `employee.add`, because the pipe admits them all. Restrict the
  pipe to administrators before switching writes on anywhere other than a test machine.
- A call cut off by a client timeout stays `Started`, so repeating it does nothing until the Worker restarts.
- Ambit needs an acronym field and a rule for assigning it (`ambit.es`, `.instructions/200-optima-integration.md`,
  question 12).
- Writing to `CDN.PracEtaty` directly depends on Optima's schema, without Comarch's support. The column list has to
  be checked after each Optima update, and a Comarch partner should still confirm it.
- The agent works on employee data, which ADR 0001 wanted out of reach of a leaked token. The boundary is the narrow
  SQL rights above, plus a switch that stays local and off by default.
- Database scripts are plain idempotent T-SQL, because a client's machine has no Liquibase.

## Not yet verified

- A payroll run for an employee added this way, and the meaning of `PRE_UBZTyuId = 99999`.
- Whether an Optima update drops the `GRANT`s.
- Updating an existing employee is out of scope: Optima may add a history row for some changes.
