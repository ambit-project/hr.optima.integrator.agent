# Security

The integrator runs on a client's machine next to payroll data, on behalf of a web application. A leaked token or a
compromised application must not be able to read or change more than the integrator's operations need.

## The boundary is SQL Server's rights

The service account `NT SERVICE\AmbitOptimaIntegrator` gets exactly what `db/grants` lists, and nothing it could use to
grant itself more:

| Database | Rights |
| --- | --- |
| `ambit-optima-integrator` | `SELECT`, `INSERT`, `UPDATE`, `DELETE` on schema `integrator`. No DDL: migrations run as the installer. |
| Optima configuration | `SELECT` on three columns of `CDN.Bazy` and three of `CDN.Operatorzy`. |
| Each selected company | `EXECUTE` on `CDN.DodajPracownika`, `CDN.ImportDataDodatki`, `CDN.ImportDataDodatkiHist`; `SELECT` and `UPDATE` column by column on the tables listed in `db/grants/company.sql`. |
| Any other company | none: no database user. |

- An administrator picks the companies in the installer. Nothing is selected on a first install, and clearing a company
  drops the service's user there. A company added to Optima later gets no access until the installer runs again.
- The service refuses nothing by itself when its account is `sysadmin` or `db_owner`, but the UI shows it as a problem.
- There is no channel for free SQL: the HR application sends operation kinds and data, and the desktop UI sends command
  names. All SQL is fixed in the code, with values passed as parameters.

## Writes are switched on locally

`Writes:Enabled` is `false` by default and lives in the local settings file. The installer does not switch it on, and
the HR application cannot. With writes off, the integrator does not offer the write kinds and refuses `employee.add`
over IPC.

With writes on, any signed-in local user can reach `employee.add` through the pipe. Restrict the pipe before switching
writes on outside a test machine.

## Personal data

- Logs, call records and results sent to the HR application carry ids, statuses and numeric codes. SQL Server and Optima
  messages are never stored or sent, because they can contain a PESEL; only their `OPT` codes are kept.
- `integrator.employee_call.masked_parameters` records which values were given, never the values.
- `EmployeeAddRequest` and `EmployeesWork` print only their type name, so logging one by mistake leaks nothing.

## The token

- The token is stored in `appsettings.Production.json` under `AmbitApi:Key`. The file has no inherited rights and opens
  only to `SYSTEM`, administrators and the service account (read only). The rights are set before the token is written.
- The installer passes the token to `Setup` in a temporary file deleted right after, never on a command line, which any
  local user can read. The token never appears in the installer's log or the program's output.
- The token travels only over https; plain http is accepted for a server on the same machine.

## Reporting a vulnerability

Please report vulnerabilities privately to the maintainers rather than in a public issue.
