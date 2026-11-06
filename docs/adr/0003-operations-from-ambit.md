# ADR 0003: Operations from Ambit, and reading payroll components

Status: proposed (2026-11-06). The owner set the statuses and the retry rule. The other points follow from them.

## Context

The agent has to take work from Ambit, starting with reading the deductions and allowances Optima defines, so that a
person in Ambit can pick one. Later operations will write to payroll data, where a blind repeat could create a duplicate.

## Decision

- An operation has four statuses in Ambit: `New`, `InProgress`, `Success`, `Failed`. Its log is visible in Ambit.
- An operation runs once. The agent never repeats one, and neither does Ambit. Only an operator, by trying a failed
  operation again, starts another attempt. The operation keeps its id, which a writing operation uses as its key in
  Optima, so that a retry can see what an earlier attempt wrote.
- If the agent stops answering, its operation becomes `Failed` with `outcome_unknown` when the lease runs out. A result
  that arrives later still counts, unless an operator has started another attempt by then.
- The agent asks Ambit for work, so it opens no port. It sends a result again until Ambit answers, because Ambit ignores
  a repeated result. A `400` or `404` is final.
- The connection is off by default (`AmbitApi:Enabled`). Without it the agent still serves the local UI.
- A log carries codes and identifiers, never SQL Server's message text, which can hold personal data.
- `app.payroll_components_read` reads every active company on Optima's list. It returns every row of `CDN.TypWyplata` that
  is neither inactive nor withdrawn, as a person in Ambit may pick any of them (owner, 2026-11-06). `TWP_AlgPotracenie = 1`
  makes a component a deduction (`isDeduction`), and anything else an allowance. Nothing marks a component in its name,
  since the name can appear on a worker's payslip.
- A company without access is logged and does not fail the operation, as an administrator may exclude it. A company that
  fails does fail the operation, and the definitions of the other companies are still kept. No company within reach
  fails it too.

## The contract with Ambit

Every request carries the header `X-Optima-Agent-Key`. A key belongs to one organization, and the organization comes from
the key, never from the request. JSON is camelCase.

`POST /api/optima/agent/operations/claim`

```json
{ "agentVersion": "1.0.0.0", "supportedKinds": ["ReadPayrollComponents"] }
```

The answer is `204` when nothing waits, otherwise `200`:

```json
{ "id": "a0000000-0000-4000-8000-000000000001", "kind": "ReadPayrollComponents", "attempt": 1 }
```

`POST /api/optima/agent/operations/{id}/result`

```json
{
  "attempt": 1,
  "succeeded": true,
  "errorCode": null,
  "log": [
    { "at": "2026-11-06T20:00:00+00:00", "code": "database_read", "databaseName": "CDN_AmbitProd", "errorNumber": null, "count": 2 }
  ],
  "payrollComponents": [
    {
      "databaseName": "CDN_AmbitProd",
      "taxNumber": "1234567890",
      "components": [
        {
          "optimaId": 338,
          "name": "Potrącenie za mieszkanie",
          "shortName": "hr.pot.mieszanie",
          "isDeduction": true,
          "sourceKind": 4,
          "standardId": 0,
          "defaultAmount": 0.00,
          "isPkzp": false,
          "isSeizure": false,
          "importRowId": "8468F49B-C16E-4529-8197-D94D93DDADD8",
          "importRowId2": "6E5E45D1-E075-4CCC-8209-17FE740CDF41"
        }
      ]
    }
  ]
}
```

Agent log and error codes: `database_read`, `database_no_access`, `database_failed`, `no_company_readable`,
`execution_failed`, `unsupported_kind`.

## Consequences

- This replaces a point of ADR 0001: a read that was running when the agent stopped does not run again by itself.
- It also conflicts with `ambit.es`, `.instructions/200-optima-integration.md`, point 6, where an expired lease returns a
  task to the queue. That plan has to change when Ambit's side is built.
- `optimaId` means something only within one company. `CDN_AmbitBud` and `CDN_Firma_Demo` give the same component
  different ids, so Ambit keeps a list per company. Renaming a component in Optima keeps its id, so Ambit links by id.
- The draft of Ambit's side on `feat/optima-queue` still uses the earlier names (`ReadDeductionDefinitions`,
  `deductionDefinitions`, `definitions`) and has to follow this contract, with `isDeduction` added.
- A new company's components cannot be read until the installer runs again, because of the column-level `GRANT`.
- The list holds about 300 components per company, many of them technical (benefits, overtime), so Ambit's picker needs
  a search and a split into deductions and allowances.
- `CDN.TypWyplata` is Optima's schema, without Comarch's support, so the column list has to be checked after each update.

## Not yet verified

- Ambit's side of this contract against the running agent.
- An allowance on a payroll run, and writing either from the agent. One deduction written with Comarch's
  `CDN.ImportDataDodatki` and `CDN.ImportDataDodatkiHist` entered a payroll run as -500.00 without changing the bases of
  ZUS or health contributions (2026-11-06, `CDN_AmbitProd`). A payslip shows the definition's current name.
