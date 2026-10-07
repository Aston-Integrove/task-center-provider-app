# Feature 001 — Platform Foundation

**Status**: Ready · **Priority**: P1 · **Depends on**: — · **Constitution**: V, VI, VIII

## Summary
A deployable, observable, secured-by-default .NET 10 service on Azure Container Apps with an Azure SQL database, Key Vault, CI/CD and a request log — the walking skeleton every other feature builds on.

## User stories

### US-001-1 One-command environment (P1)
As a developer, I can run `azd up` and get a working environment (Container App, SQL, Key Vault, ACR, Log Analytics) so that the prototype is reproducible.

**Acceptance**
1. Given a fresh Azure subscription and `azd env new dev`, when I run `azd up`, then all resources are created in the chosen region and `GET https://<fqdn>/healthz` returns `200 {"status":"Healthy"}` within 15 minutes.
2. Given the deployment, when I inspect the Container App, then it uses a system-assigned managed identity with *Key Vault Secrets User* on the vault and is an Entra user in the SQL database with `db_datareader`, `db_datawriter`, `db_ddladmin`.
3. Given the deployment, then no secret appears in Bicep parameters, `azd` env files committed to git, or pipeline logs.

### US-001-2 CI/CD (P1)
As a developer, every push to a PR runs build + tests; merges to `main` deploy to `dev`.

**Acceptance**
1. Given a PR, when CI runs, then it executes `dotnet build`, unit, integration (Testcontainers SQL Server) and contract tests and fails on any failure.
2. Given a merge to `main`, then GitHub Actions authenticates with OIDC (no stored Azure secret) and runs `azd deploy`.

### US-001-3 Database migrations (P1)
**Acceptance**
1. Given a new image starts, when the DB schema is behind, then EF Core migrations are applied at startup (prototype simplification) and logged.
2. Given migrations fail, then the container reports unhealthy and does not serve traffic.

### US-001-4 Observability and request log (P1)
As an integrator, I can see every inbound request to `/oauth`, `/scim`, `/task-provider` (method, path, query, status, duration, caller client_id/sub, redacted headers, request/response body truncated to 8 KB with secrets/tokens masked) so I can learn what SAP actually sends.

**Acceptance**
1. Given any SPI call, then a structured JSON log line with `correlationId`, `route`, `status`, `durationMs`, `clientId` is written to stdout (→ Log Analytics).
2. Given the request log is enabled, when I call `GET /admin/api/requests?prefix=/task-provider`, then I get the last N (default 500, in-memory ring buffer) entries newest first.
3. `Authorization` headers, `client_secret`, `assertion`, `access_token` values are always replaced by `***`.

### US-001-5 Secure defaults (P1)
**Acceptance**
1. Any endpoint not explicitly anonymous returns `401` without a valid credential.
2. Anonymous allow-list: `/healthz`, `/.well-known/*`, `/oauth/token`.
3. HTTPS only (Container Apps `allowInsecure=false`); HSTS header set.

## Functional requirements
- FR-001-01 Single container image built from `src/Tcp.Api/Dockerfile` (chiseled `mcr.microsoft.com/dotnet/aspnet:10.0` base, non-root).
- FR-001-02 Configuration via environment variables + Key Vault configuration provider (`AddAzureKeyVault` with `DefaultAzureCredential`).
- FR-001-03 `GET /healthz` (liveness, anonymous) and `GET /healthz/ready` (DB + Key Vault reachable).
- FR-001-04 Problem responses for our non-SAP endpoints follow RFC 9457; SPI endpoints use SAP `Error` body (spec 004).
- FR-001-05 Global exception handler never leaks stack traces.

## Non-functional
- NFR-001-01 Cold start < 5 s; idle memory < 200 MB.
- NFR-001-02 Monthly cost ≤ USD 25 (see architecture §5).

## Out of scope
Custom domain, private networking, multi-region, staging slots.
