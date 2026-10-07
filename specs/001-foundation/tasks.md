# Tasks — 001 Foundation

Legend: **[P]** parallelisable · each task names its proving test.

- [ ] **T001-01** Create solution `Tcp.sln` with `Tcp.Api`, `Tcp.Domain`, `Tcp.Infrastructure`, three test projects; `Directory.Build.props` (net10.0, nullable, warnings as errors, analyzers). *Test*: `dotnet build` in CI.
- [ ] **T001-02** `/healthz` + `/healthz/ready` endpoints. *Test*: integration test returns 200 / 503 when DB unreachable.
- [ ] **T001-03** [P] JSON console logging + correlation ID middleware (`X-Correlation-Id` in/out). *Test*: unit test of middleware sets header.
- [ ] **T001-04** [P] `RequestLogMiddleware` + ring buffer + `Redactor`. *Test*: unit tests — Authorization header, `client_secret` form field, `assertion` field, `access_token` JSON property all masked; buffer capped at N.
- [ ] **T001-05** Authentication/authorization skeleton: fallback policy authenticated; anonymous allow-list. *Test*: integration test — unknown route → 401, `/healthz` → 200.
- [ ] **T001-06** `TcpDbContext` with empty initial migration; auto-migrate on startup behind `Database:MigrateOnStartup`. *Test*: Testcontainers integration test applies migrations.
- [ ] **T001-07** Key Vault configuration provider wired with `DefaultAzureCredential` (skipped when `KeyVault:Uri` empty for local dev; local uses `dotnet user-secrets`). *Test*: unit test of options binding.
- [ ] **T001-08** Dockerfile (multi-stage, chiseled, non-root, port 8080). *Test*: CI `docker build` + container smoke test hitting `/healthz`.
- [ ] **T001-09** [P] Bicep modules (log-analytics, acr, key-vault, sql, cae, container-app) + `main.bicep` + `azure.yaml`. *Test*: `az bicep build` + `azd provision --preview` in CI.
- [ ] **T001-10** `postprovision` hook: SQL MI user, KV secret seeding, RSA key generation (idempotent). *Test*: run twice — no changes on second run.
- [ ] **T001-11** GitHub Actions `ci.yml` (build, test with SQL Server service via Testcontainers, contract tests, gitleaks). *Test*: green pipeline on PR.
- [ ] **T001-12** GitHub Actions `deploy.yml` (OIDC federated credential, `azd deploy`). *Test*: merge to main deploys; `/healthz` 200 post-deploy step.
- [ ] **T001-13** `GET /admin/api/requests` (admin Basic auth) returning the ring buffer, filter by `prefix`, `status`. *Test*: integration test.
- [ ] **T001-14** README "Run locally" (`docker compose` with SQL Server 2022 container + `dotnet run`). *Test*: manual checklist.
