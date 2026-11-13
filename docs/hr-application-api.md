# The HR application's API

The integrator is a client of the HR application. The application owns this API and publishes it in its OpenAPI
document. The integrator's client, `HttpAmbitApiClient` in `src/Ambit.Optima.Integrator.Infrastructure`, is written by
hand and follows this page.

## Conventions

- Every request carries the header `X-Optima-Agent-Key` with the integrator's token (`optima_` and 64 lowercase
  hexadecimal characters). A token belongs to one organization; the organization comes from the token, never from the
  request.
- JSON is camelCase. Paths are relative to `AmbitApi:BaseUrl`.
- `401` means an unknown or revoked token. The integrator keeps running, logs it and tries again later.

## Endpoints

### `GET api/optima/agent/connection`

Checks the token and changes nothing. The installer uses it. `204` when the token is accepted, `401` when not.

### `POST api/optima/agent/operations/claim`

Takes the next operation for this organization. It changes the operation's state, so it is never used just to check a
token.

```json
{
  "agentVersion": "0.5.0.0",
  "supportedKinds": ["ReadPayrollComponents", "ReadPayrolls", "AddEmployees", "ExportDeductions", "ExportAllowances"]
}
```

`supportedKinds` always lists the read kinds (`ReadPayrollComponents`, `ReadPayrolls`) and lists the write kinds
(`AddEmployees`, `ExportDeductions`, `ExportAllowances`) only when writes are switched on locally. The answer is `204`
when nothing waits, otherwise `200`:

```json
{ "id": "a0000000-0000-4000-8000-000000000001", "kind": "ReadPayrollComponents", "attempt": 1 }
```

The operation keeps its id when an operator tries it again; `attempt` grows.

The integrator claims every `AmbitApi:PollIntervalMilliseconds` (500 by default) while nothing waits, at once after an
operation, and only after `AmbitApi:RetrySeconds` (60 by default) when the application could not be reached or answered
`401`. An empty claim should therefore be cheap for the application.

### `GET api/optima/agent/operations/{id}/employees`

The people of an `AddEmployees` operation the integrator holds. `404` is final (`employees_unavailable`).

```json
{
  "databaseName": "CDN_Firma",
  "employees": [
    {
      "employeeId": "b0000000-0000-4000-8000-000000000001",
      "employeeCode": "KOWJAN1",
      "lastName": "Kowalski",
      "firstName": "Jan",
      "dateOfBirth": "1990-05-12",
      "pesel": "90051212345",
      "citizenshipCode": "PL",
      "email": null,
      "phone": null,
      "street": null,
      "buildingNumber": null,
      "unitNumber": null,
      "postalCode": null,
      "city": null,
      "addressCountryCode": null,
      "note": null,
      "documentKind": "IdentityCard",
      "documentNumber": "ABC123456",
      "documentValidUntil": "2030-01-31",
      "positionName": null,
      "startsOn": "2026-11-01",
      "endsOn": null
    }
  ]
}
```

`databaseName: null` means every active company. `documentKind` is `IdentityCard`, `Passport`, `ResidenceCard` or
`Other`; only the first two are stored in Optima. Country codes are ISO 3166-1 alpha-2. `employeeCode` is the acronym
the application gives the person (at most 20 characters).

### `GET api/optima/agent/operations/{id}/deductions`

The deductions of an `ExportDeductions` operation. `404` is final (`deductions_unavailable`).

```json
{
  "databaseName": "CDN_Firma",
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

`databaseName` is required, because `optimaId` (a payroll component read by `ReadPayrollComponents`) means something
only in one company. `amount` is positive with at most two decimals; the component makes it a deduction. `currency` may
be left out and must be `PLN`.

### `GET api/optima/agent/operations/{id}/allowances`

The allowances of an `ExportAllowances` operation. `404` is final (`allowances_unavailable`). The same as deductions,
under other names:

```json
{
  "databaseName": "CDN_Firma",
  "year": 2026,
  "month": 11,
  "allowances": [
    {
      "allowanceId": "d0000000-0000-4000-8000-000000000002",
      "employeeId": "b0000000-0000-4000-8000-000000000001",
      "employeeCode": "202611ABCDEFGH",
      "optimaId": 12,
      "amount": 300.00,
      "currency": "PLN"
    }
  ]
}
```

`optimaId` must be an active, not withdrawn allowance of that company; a deduction component, or an inactive one, is
logged as `component_not_found`. The import key is derived from `allowanceId` and the month, like a deduction's.

### `GET api/optima/agent/operations/{id}/payroll-period`

The company and month of a `ReadPayrolls` operation. `404` is final (`payrolls_unavailable`).

```json
{ "databaseName": "CDN_Firma", "year": 2026, "month": 10 }
```

The month is the payroll list's own, the month it is declared in, which can differ from the period the list covers.

### `POST api/optima/agent/operations/{id}/result`

The outcome of one attempt. The integrator sends it again until the application answers, so the application must
ignore a repeated result, and a result of an older attempt. `400`, `404` and `413` are final.

```json
{
  "attempt": 1,
  "succeeded": true,
  "errorCode": null,
  "log": [
    { "at": "2026-11-06T20:00:00+00:00", "code": "database_read", "databaseName": "CDN_Firma", "count": 2 }
  ],
  "payrollComponents": [
    {
      "databaseName": "CDN_Firma",
      "taxNumber": "1234567890",
      "components": [
        {
          "optimaId": 338,
          "name": "Potrącenie za mieszkanie",
          "shortName": "pot.mieszkanie",
          "isDeduction": true,
          "sourceKind": 4,
          "standardId": 0,
          "defaultAmount": 0.00,
          "isPkzp": false,
          "isSeizure": false,
          "isActive": true,
          "isWithdrawn": false,
          "importRowId": "8468F49B-C16E-4529-8197-D94D93DDADD8",
          "importRowId2": "6E5E45D1-E075-4CCC-8209-17FE740CDF41"
        }
      ]
    }
  ]
}
```

A log entry has `at`, `code` and, where they apply, `databaseName`, `errorNumber`, `count`, `employeeId`,
`deductionId`, `allowanceId` and `warningCodes` (for example `MATCHED_BY_ACRONYM_ONLY`, `POSITION_NOT_FOUND`,
`DOCUMENT_NOT_STORED`, Optima's `OPT` codes).

`payrollComponents` is present only for `ReadPayrollComponents`. It holds every component Optima defines, active or
not: `isActive` is false for an inactive one and `isWithdrawn` true for a withdrawn one. Only an active, not withdrawn
component can be exported to.

`payrolls` is present only for a successful `ReadPayrolls`:

```json
"payrolls": {
  "databaseName": "CDN_Firma",
  "taxNumber": "1234567890",
  "year": 2026,
  "month": 10,
  "lists": [
    {
      "optimaId": 41,
      "number": "EtatLP/10/2026",
      "name": "Etat",
      "periodFrom": "2026-10-01",
      "periodTo": "2026-10-31",
      "documentDate": "2026-10-31",
      "isClosed": true,
      "payouts": [
        {
          "optimaId": 901,
          "optimaEmployeeId": 77,
          "employeeId": "b0000000-0000-4000-8000-000000000001",
          "employeeCode": "202611ABCDEFGH",
          "gross": 6500.00,
          "net": 4712.34,
          "isClosed": true,
          "elements": [
            { "optimaId": 5001, "componentOptimaId": 1, "name": "Wynagrodzenie zasadnicze", "amount": 6500.00 }
          ]
        }
      ]
    }
  ]
}
```

- `number` and `name` may be null. The ids are Optima's and mean something only in that company.
- `employeeId` is the application's id when Optima holds it as the employee's import id, otherwise null;
  `employeeCode` is the acronym. `gross` is the payout's gross and `net` what is paid out. An element's `amount` is its
  gross value, and `componentOptimaId` its payroll component (an `optimaId` of `ReadPayrollComponents`), null for some
  of Optima's own elements.
- One operation is one company and one month, which keeps a result below the application's size limit (`413`).

## Codes

| Code | Where | Meaning |
| --- | --- | --- |
| `database_read` | log | A company was read; `count` components, or payouts for `ReadPayrolls`. |
| `database_no_access` | log | The installer gave the integrator no access to this company. |
| `database_failed` | log | Reading a company failed; `errorNumber` (51401 when Optima does not list it, 51501 for another tax number). |
| `no_company_readable` | error | No company could be read. |
| `payrolls_unavailable` | error | The application answered `404` for the payroll period. |
| `employee_added`, `employee_exists` | log | Per person and company. |
| `employee_rejected` | log | The person was refused before any company was touched; `errorNumber` 51xxx. |
| `employee_failed` | log | Writing to a company failed; `errorNumber`. |
| `employees_failed`, `no_company_writable` | error | Some person was refused or failed; no company could be written. |
| `employees_unavailable` | error | The application answered `404` for the people. |
| `deduction_added`, `deduction_exists` | log | Per deduction. |
| `allowance_added`, `allowance_exists` | log | Per allowance. |
| `employee_not_found`, `component_not_found` | log | The employee, or an active component of the item's kind, is not in the company. |
| `deduction_rejected`, `deduction_failed` | log | Refused before writing (51xxx), or writing failed. |
| `allowance_rejected`, `allowance_failed` | log | Refused before writing (51xxx), or writing failed. |
| `deductions_failed`, `deductions_unavailable` | error | Some deduction was not written; the application answered `404`. |
| `allowances_failed`, `allowances_unavailable` | error | Some allowance was not written; the application answered `404`. |
| `writes_disabled` | error | Writes were switched off locally after the claim. |
| `execution_failed`, `unsupported_kind` | error | The attempt failed as a whole; the kind is unknown to this version. |

Rejection numbers (`Core/RejectionCodes.cs`): 51010-51015 for an employee, 51020-51025 for a deduction, 51030-51035 for
an allowance (the same reasons, ten higher), 51401 when no active company matches. 51501 marks a company whose database
holds a tax number other than Optima's list.
