# ADR 0001: What the agent may run on SQL Server

Status: accepted (owner, 2026-11-06).

## Context

The agent runs statements on a client's ERP database on behalf of a SaaS. A leaked token or a compromised
server must not reach payroll data or change it. Inspecting a statement's text alone protects nothing.

## Decision

- Reads: any `SELECT`, checked by a T-SQL parser against an allow-list of constructs.
- Writes: only named procedures. Off by default, switched on locally, and never from the cloud.
- The boundary is the SQL rights of the service account; the parser is a second layer. The agent refuses
  to work when the account is `sysadmin` or `db_owner`.
- The agent runs only on databases from its local list.
- Tasks in v1 come from a catalogue in the panel, picked by an organization's Admin. There is no free-SQL field.
- A task that was running when the agent stopped: a read runs again; a write becomes `Unknown` and is never
  repeated automatically.
- Results: full rows on the API with a limit (starting at 1000 rows and 5 MB) and 30-day retention, the same
  locally in SQLite. The SQLite file is protected by an ACL and not encrypted in v1.
- The UI has no way of sending SQL over IPC: a request carries a command's name only.

## Consequences

This replaces the rule of 2026-10-30 (`ambit.es`, `.instructions/200-optima-integration.md`) that there is no SQL
channel without a Comarch partner, for reads. For writes it still holds that we do not write procedures ourselves.
