# What is written to Optima

The integrator writes through Comarch's own procedures wherever one exists, and adds a fixed, column-limited `UPDATE`
only for values a procedure cannot take. Optima's schema is Comarch's and may change with an update, so every
statement below has to be checked after an Optima update.

## Companies

- The list of companies is Optima's own, `CDN.Bazy` in the configuration database, read on every call. Inactive
  companies are skipped.
- Before writing, the integrator compares the tax number in the company database (`CDN.Firma`, `Fir_Numer = 1101`) with
  the list's, when the list has one. A mismatch fails that company with 51501.
- `optimaId` values (`TWP_TwpId` and others) mean something only within one company.

## Employees

One person and one company is one transaction:

1. The person is looked up in `CDN.PracKod` by acronym (`PRA_Kod`) or by the application's employee id
   (`PRA_ImportRowId`, upper-case text). If found, nothing is written (`AlreadyExists`); a match on the acronym alone is
   reported as `MATCHED_BY_ACRONYM_ONLY`, since it may be a different person.
2. `CDN.DodajPracownika` adds the employee: `PracKod`, `PracEtaty` and the rest of what the procedure writes. Dates are
   passed as `YYYYMMDD` text, the only form it reads correctly. The source is `AMBIT`.
3. An `UPDATE` of `CDN.PracEtaty` sets what the procedure has no parameter for: sex (left empty), citizenship, the
   address country code, the identity card or passport, its expiry and the end of employment.

Known behaviour of `CDN.DodajPracownika` the integrator works around:

- It truncates text silently, so the integrator refuses a value longer than Optima's column (51014) and names every
  such field.
- It does not stop after its own error. The integrator runs it with `XACT_ABORT` in its own transaction and rolls back
  on any error.
- It clears an invalid PESEL and adds the employee anyway, with a warning. The integrator checks the PESEL's format
  first (51012).
- Countries are names in Optima and ISO codes in the application; `OptimaEmployeeValues` maps the ones it knows.
  Without an address, Optima's default country (Polska / PL) stays.

A row in `integrator.employee_call` is committed before the company's transaction, so a rollback cannot erase it. A
call left `Started` when the service stopped becomes `Unknown` at the next start, and is never repeated automatically.

## Deductions

One deduction is a row of `CDN.Dodatki` (the employee's component) and a row of `CDN.DodatkiHist` (its value and
validity), written by `CDN.ImportDataDodatki` and `CDN.ImportDataDodatkiHist` in one transaction:

- Both rows carry one import key, a version 8 UUID derived from the deduction id and the month
  (`DeductionItem.OptimaKey`). The key is locked and checked first, so the same month exported again, by a retry or a
  new operation, is reported as `AlreadyExists` and written once.
- The employee is found by `PRA_ImportRowId`, else by acronym (reported as `MATCHED_BY_ACRONYM_ONLY`).
- The component must be an active deduction (`TWP_AlgPotracenie = 1`, not inactive or withdrawn), so an allowance
  mapped by mistake is refused.
- The value is valid from the first to the last day of the month, for the period 1900-01-01 to 2999-12-31, payment as
  the employee's, no operator, source `AMBIT`, under the component's name.

A deduction already exported is not changed by a later export: correct it in Optima, or remove it there and export
again. Each export adds one `CDN.Dodatki` row per deduction and month.

## Payroll components

`CDN.TypWyplata`, without inactive or withdrawn rows: id, name, short name, `isDeduction` (`TWP_AlgPotracenie = 1`),
source kind, standard id, default amount, PKZP and seizure flags, and Comarch's import ids.

## Not verified yet

- A payroll run for an employee added by the integrator.
- A payroll run with a deduction written by the integrator rather than by hand with the same procedures.
- Whether an Optima update keeps the column-level `GRANT`s.
