# ADR 0006: Adding employees from Ambit

Status: accepted (2026-11-11). Ambit's side is `ambit.es` ADR 0083.

## Context

ADR 0002 lets the Worker add an employee through `app.employee_add`, so far only from the local UI over IPC. Ambit now
sends the people of its workers list as an operation (ADR 0003), so that a client's HR does not import a file.

## Decision

- A new operation kind, `AddEmployees`. The agent offers it to Ambit only when `Writes:Enabled` is set locally, so a
  machine with writes off is never given one, and a configuration switched off in between fails it with
  `writes_disabled` without writing.
- The claim carries no people. The agent reads them with its key from
  `GET /api/optima/agent/operations/{id}/employees` while it holds the operation:
  `{ "databaseName": "CDN_Ambit" | null, "employees": [ ... ] }`, each person in the names of `EmployeeAddRequest`.
  A `404` is final (`employees_unavailable`). An unreachable Ambit fails the attempt with `execution_failed`.
- Each person is one call of `app.employee_add` with the company Ambit named, or every active one for `null`. The
  call's `operation_id` is derived from the operation and the person (`EmployeesWork.CallId`, a version 8 UUID), the
  same on every attempt, so trying a failed operation again repeats only `NoAccess`, `Failed` and `Unknown` companies,
  as ADR 0002's procedure does for a repeated call.
- The log has one entry per person and company, with the person's Ambit id: `employee_added` (`Succeeded`),
  `employee_exists` (`AlreadyExists`), `database_no_access`, `employee_failed` (with the error number), and
  `employee_rejected` when the procedure refused the request as a whole (5xxxx number). Messages never leave the
  machine, since they can carry a PESEL.
- One refused person or one failed company does not stop the others. The operation fails with `employees_failed` when
  anything was refused or failed, and with `no_company_writable` when no company could be written. Otherwise it
  succeeds.

## Consequences

- A person is identified in Optima by Ambit's acronym and `PRA_ImportRowId` = Ambit's worker id (ADR 0002), so a person
  imported earlier from Ambit's `.xls` file, which uses the same acronym, is reported as `employee_exists`.
- Updating a person Optima already has stays out of scope.
