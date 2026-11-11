# ADR 0007: Exporting a month's deductions from Ambit

Status: proposed (2026-11-11). Ambit's side is task 201 in `ambit.es` (`.instructions/201-optima-payroll-components-deductions.md`,
topic 3), which should follow the contract below or change it here first.

## Context

Ambit computes each employee's deductions for a month (`MonthDeductions`) and maps its deduction types to the payroll
components each Optima company defines (ADR 0003 reads them, with `isDeduction`). HR has to see those deductions on the
payroll in Optima without typing them in.

In Optima a deduction given to an employee is a row of `CDN.Dodatki` (the employee's component) with a row of
`CDN.DodatkiHist` (its value and the dates it is valid). Comarch ships `CDN.ImportDataDodatki` and
`CDN.ImportDataDodatkiHist`, which insert them and refuse an import key they already have. On 2026-11-06 a 500.00
deduction written with them entered a real payroll run in `CDN_AmbitProd` as -500.00, with the ZUS and health bases
unchanged.

## Decision

- A new operation kind, `ExportDeductions`: one company and one month. The agent offers it to Ambit only when
  `Writes:Enabled` is set locally, and a configuration switched off in between fails it with `writes_disabled`.
- As in ADR 0006, the claim carries no data. The agent reads it with its key from
  `GET /api/optima/agent/operations/{id}/deductions` while it holds the operation. A `404` is final
  (`deductions_unavailable`); an unreachable Ambit fails the attempt with `execution_failed`.

  ```json
  {
    "databaseName": "CDN_Firma_Demo",
    "year": 2026,
    "month": 11,
    "deductions": [
      {
        "deductionId": "d0000000-0000-4000-8000-000000000001",
        "employeeId": "b0000000-0000-4000-8000-000000000001",
        "employeeCode": "KOWJAN1",
        "optimaId": 338,
        "amount": 500.00,
        "currency": "PLN"
      }
    ]
  }
  ```

  `databaseName` is required, because `optimaId` means something only in one company. `amount` is the month's amount,
  positive, at most two decimals; the component makes it a deduction. `currency` may be left out and must be `PLN`.
  `employeeCode` is the acronym Ambit gave the person (ADR 0006).
- Each deduction is one call of `app.deduction_add`, with one transaction on the company database:
  - the employee is found by `PRA_ImportRowId` = `employeeId`, else by the acronym, which is reported as
    `MATCHED_BY_ACRONYM_ONLY` in `warningCodes`;
  - the component must be an active deduction (`TWP_AlgPotracenie = 1`), so an allowance mapped by mistake is refused;
  - both rows are written in the shape that went through the payroll run: valid from the first to the last day of the
    month, period 1900-01-01 to 2999-12-31, payment as the employee's, no operator, source `AMBIT`, the component's
    name.
- Both rows carry one import key, a version 8 UUID derived from `deductionId` and the month
  (`DeductionItem.OptimaKey`). So the same month exported again, by a retry or by a new operation, finds the rows and
  reports `deduction_exists` instead of writing them twice. A later change of the amount in Ambit is not carried over.
- The log has one entry per deduction, with `deductionId` and `employeeId`: `deduction_added`, `deduction_exists`,
  `employee_not_found`, `component_not_found`, `database_no_access`, `deduction_failed` (with the error number) and
  `deduction_rejected` when the procedure refused the request (51xxx). No message text leaves the machine.
- The operation succeeds only when every deduction was added or was already there. Otherwise it fails with
  `deductions_failed`, and an operator can try it again: what was written is found by its key.
- `agent.deduction_call` keeps one row per operation and deduction, with ids, the month, the component and the amount,
  for 30 days. The guard against a duplicate is the key in Optima, not this table.
- The installer grants `EXECUTE` on the two Comarch procedures and `SELECT` on the key columns of `CDN.Dodatki` and
  `CDN.DodatkiHist`, on each company it selects.

## Consequences

- One deduction makes one row of `CDN.Dodatki` per month, so an employee's list of components grows by one row a month
  for each deduction. One component with a row of history per month would be tidier, but has not been through a payroll
  run.
- Correcting a deduction already exported is out of scope: it has to be changed in Optima, or removed there and
  exported again.
- An existing installation gets the new rights only when the installer runs again.
- `CDN.ImportDataDodatki` and `CDN.ImportDataDodatkiHist` are Comarch's, without a promise that they stay the same, so
  they have to be checked after each Optima update.

## Not yet verified

- A payroll run with a deduction written by `app.deduction_add` rather than by hand. The rows match the verified one
  except for the empty operator.
- Ambit's side of the contract.
- Whether an HR specialist accepts every component marked `TWP_AlgPotracenie = 1` for this, including PKZP and seizures.
