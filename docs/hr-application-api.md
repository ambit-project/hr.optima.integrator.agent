# The HR application's API

The integrator is a client of the HR application. The application owns this API and publishes its OpenAPI document,
generated from its own code. The integrator's client is to be generated from that document (Kiota) into
`src/Ambit.Optima.Integrator.Infrastructure`. Until the document exists, `HttpAmbitApiClient` is written by hand and
follows this page.

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
{ "agentVersion": "0.4.0.0", "supportedKinds": ["ReadPayrollComponents", "AddEmployees", "ExportDeductions"] }
```

`supportedKinds` lists the write kinds only when writes are switched on locally. The answer is `204` when nothing
waits, otherwise `200`:

```json
{ "id": "a0000000-0000-4000-8000-000000000001", "kind": "ReadPayrollComponents", "attempt": 1 }
```

The operation keeps its id when an operator tries it again; `attempt` grows.

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

### `POST api/optima/agent/operations/{id}/result`

The outcome of one attempt. The integrator sends it again until the application answers, so the application must
ignore a repeated result. `400`, `404` and `413` are final.

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
          "importRowId": "8468F49B-C16E-4529-8197-D94D93DDADD8",
          "importRowId2": "6E5E45D1-E075-4CCC-8209-17FE740CDF41"
        }
      ]
    }
  ]
}
```

A log entry has `at`, `code` and, where they apply, `databaseName`, `errorNumber`, `count`, `employeeId`,
`deductionId` and `warningCodes` (for example `MATCHED_BY_ACRONYM_ONLY`, `POSITION_NOT_FOUND`, `DOCUMENT_NOT_STORED`,
Optima's `OPT` codes). `payrollComponents` is present only for `ReadPayrollComponents`.

## Codes

| Code | Where | Meaning |
| --- | --- | --- |
| `database_read` | log | A company was read; `count` components. |
| `database_no_access` | log | The installer gave the integrator no access to this company. |
| `database_failed` | log | Reading a company failed; `errorNumber`. |
| `no_company_readable` | error | No company could be read. |
| `employee_added`, `employee_exists` | log | Per person and company. |
| `employee_rejected` | log | The person was refused before any company was touched; `errorNumber` 51xxx. |
| `employee_failed` | log | Writing to a company failed; `errorNumber`. |
| `employees_failed`, `no_company_writable` | error | Some person was refused or failed; no company could be written. |
| `employees_unavailable` | error | The application answered `404` for the people. |
| `deduction_added`, `deduction_exists` | log | Per deduction. |
| `employee_not_found`, `component_not_found` | log | The employee or an active deduction component is not in the company. |
| `deduction_rejected`, `deduction_failed` | log | Refused before writing (51xxx), or writing failed. |
| `deductions_failed`, `deductions_unavailable` | error | Some deduction was not written; the application answered `404`. |
| `writes_disabled` | error | Writes were switched off locally after the claim. |
| `execution_failed`, `unsupported_kind` | error | The attempt failed as a whole; the kind is unknown to this version. |

Rejection numbers (`Core/RejectionCodes.cs`): 51010-51015 for an employee, 51020-51025 for a deduction, 51401 when no
active company matches. 51501 marks a company whose database holds a tax number other than Optima's list.
