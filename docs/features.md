# What the agent does, and what is not done yet

State on 2026-11-11: agent `main` at `1999c47`, Ambit (`ambit.es`) `main` at `8f4ab526`. Ambit's open work is task
`.instructions/201-optima-payroll-components-deductions.md` in `ambit.es`.

The goal (`ambit.es`, `.instructions/200-optima-integration.md`) has two parts:

1. An employee hired in Ambit is exported to an Optima company.
2. The month's deductions from pay are exported to Optima.

The second depends on a third: Ambit's deduction types have to be mapped to the payroll components each Optima company
defines.

## Summary

| Function | Agent | Ambit | Usable end to end |
| --- | --- | --- | --- |
| Install, service, agent database, rights per company | done | - | yes |
| Connection to Ambit: key, claiming operations, reporting results | done | done | yes |
| Adding Ambit's employees to Optima (`AddEmployees`) | done (ADR 0006), only with `Writes:Enabled` | done (ADR 0083) | yes, not yet run against a live Ambit |
| Reading Optima's deductions and allowances and sending them to Ambit | done (`ReadPayrollComponents`) | done as `ReadDeductionDefinitions` | no, the names differ |
| Mapping Ambit's deduction types to Optima's components | - | not started (task 201) | no |
| Writing an employee's deductions to Optima | not started, checked once by hand | not started (task 201) | no |

## Done in the agent

### Installation and service

- One installer (`installer/`, ADR 0004, ADR 0005) installs the Windows service `AmbitOptimaAgent` and the desktop UI.
- It creates the `agent-ambit` database with its tables and procedures and may be run again.
- It grants the service's account narrow rights on the Optima companies an administrator selects: `EXECUTE` on the
  procedures used, column-level rights only. A company added later gets no access until the installer runs again.
- It takes Ambit's address and the agent key (`/AMBITURL`, `/TOKENFILE`) and checks the connection.
- Logs and procedure calls are kept in `agent-ambit` for 30 days, with codes and ids only, no personal data.

### Connection to Ambit (ADR 0003)

- The agent asks Ambit for work (`POST /api/optima/agent/operations/claim`) and reports the result
  (`POST .../operations/{id}/result`), with the `X-Optima-Agent-Key` header. It opens no port.
- An operation runs once. Only an operator in Ambit starts another attempt, under the same id.
- Off by default (`AmbitApi:Enabled`).
- Kinds: `ReadPayrollComponents` always, `AddEmployees` only when `Writes:Enabled` is set locally.

### Adding employees (`employee.add` locally, `AddEmployees` from Ambit; ADR 0002, ADR 0006)

- `app.employee_add` calls Comarch's `CDN.DodajPracownika`, then sets the columns that procedure cannot (citizenship,
  identity document, end of employment, sex), in one transaction per company.
- One company, or every active company on Optima's list, each with its own status.
- From Ambit: the claim carries no people. The agent reads them from
  `GET /api/optima/agent/operations/{id}/employees` while it holds the operation, at most 500 per operation.
- Each person's call is keyed by the operation and the person, so trying a failed operation again repeats only the
  companies that failed. The log has one entry per person and company (`employee_added`, `employee_exists`,
  `employee_rejected`, `employee_failed`, `database_no_access`).
- An employee whose acronym (`PRA_Kod`) or Ambit id (`PRA_ImportRowId`) is already there is left alone. Too long a
  value rejects that person, nothing is truncated. The company's tax number is checked against Optima's list.
- Locally the same is reachable over IPC (`employee.add`), also only with `Writes:Enabled`.
- Records added this way open, list and save in Optima. A payroll run for such an employee has not been checked.
  Updating an existing employee is out of scope.

### Reading deductions and allowances (`ReadPayrollComponents`)

- `app.payroll_components_read` reads `CDN.TypWyplata` of every active company on Optima's list, without inactive or
  withdrawn rows, about 300 per company.
- Each component carries `optimaId`, name, short name, `isDeduction` (`TWP_AlgPotracenie = 1`), source kind, default
  amount, PKZP and seizure flags and Optima's import ids. Companies are named by database and tax number.
- `optimaId` means something only within one company, so the list is per company.

## Not done

### Sending the list of deductions and allowances to Ambit

- Built on both sides, but they do not match. Ambit asks for `ReadDeductionDefinitions` and expects
  `deductionDefinitions` without `isDeduction`, so it never gives this agent the operation. Until it follows ADR 0003,
  Ambit's "Send to Optima" dialog has no list of companies and offers only "every active company".
- To do in Ambit: task 201, topic 1.

### Mapping our deductions to Optima's

- Not started. An Ambit deduction (`DeductionConsent`) has a `TypeCode`; nothing links a type to an Optima component.
- To do in Ambit (task 201, topic 2): per Optima company, our deduction type → `optimaId` (only components with
  `isDeduction`), a picker with search, and a check before export that every type used in the month is mapped.

### Writing deductions to Optima

- Not started in the agent: no procedure, no operation kind.
- Checked once by hand: one deduction written with Comarch's `CDN.ImportDataDodatki` and `CDN.ImportDataDodatkiHist`
  entered a payroll run as -500.00 without changing the ZUS or health bases (2026-11-06, `CDN_AmbitProd`).
- To do: Ambit proposes the operation (task 201, topic 3, e.g. `ExportDeductions`, with the items read like the
  employees of ADR 0006). Then the agent: an `app.` procedure built on those Comarch procedures, keyed so that a retry
  skips what an earlier attempt wrote, offered only with `Writes:Enabled`.
