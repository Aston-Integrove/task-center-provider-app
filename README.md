# Integrove TP — SAP Task Center Third-Party Provider Prototype

Spec pack for a low-cost Azure prototype that plugs a non-SAP task source into **SAP Task Center** for Valterra. Built with **spec-driven development** (GitHub Spec Kit layout).

**Prototype goals**
1. Authentication that works with SAP BTP destinations — technical user (`OAuth2ClientCredentials`) and principal propagation (`OAuth2JWTBearer`, SAML fallback).
2. Minimal **SCIM 2.0** API so SAP Identity Provisioning can sync IAS users/groups with their **Global User ID**.
3. **MVP scope** of the Task Center SPI (`TaskProviderV2.json`): pull definitions/tasks, description, response, action.

**Stack**: .NET 10 LTS Minimal API · Azure Container Apps · Azure SQL Basic · Key Vault + managed identity · Bicep + azd · GitHub Actions. Estimated run cost ≈ USD 15–20/month.

## Reading order

| # | Document | Purpose |
|---|---|---|
| 1 | `.specify/memory/constitution.md` | Non-negotiable rules for humans and AI agents |
| 2 | `docs/sap-task-center-digest.md` | What SAP requires, with rule IDs and guide page refs |
| 3 | `docs/architecture.md` | Components, flows, Azure resources, cost, config |
| 4 | `docs/decisions.md` | ADR-001…010 |
| 5 | `docs/risks-and-open-questions.md` | Risks, spikes, questions for Valterra |
| 6 | `docs/delivery-plan.md` | How to run the SDD workflow, milestones, 4-week plan |
| 7 | `specs/001…006/*` | Feature specs → plans → tasks → contracts |
| 8 | `docs/btp-setup-guide.md` | BTP destinations, IPS, verification steps |

## Feature specs

| Feature | Spec | Contract |
|---|---|---|
| 001 Foundation (repo, infra, CI/CD, request log) | `specs/001-foundation/` | — |
| 002 Auth & token service | `specs/002-auth-token-service/` | `contracts/oauth.openapi.yaml` |
| 003 SCIM 2.0 Users + Groups | `specs/003-scim-provisioning/` | `contracts/scim.openapi.yaml` |
| 004 Task Provider SPI (MVP) | `specs/004-task-provider-spi/` | `contracts/TaskProviderV2.json` (SAP), `contracts/spi-mvp.openapi.json` (subset) |
| 005 Admin console + Open-in-App page | `specs/005-admin-console/` | generated from code |
| 006 E2E integration (spikes, acceptance) | `specs/006-e2e-integration/` | — |

## Quick start (once implemented)

```bash
azd auth login
azd env new dev --location southafricanorth
azd up                                  # provision + deploy (~10–15 min)
azd env get-values | grep APP_FQDN      # base URL for BTP destinations
```
Then follow `docs/btp-setup-guide.md`.

## Run locally

Prerequisites: .NET 10 SDK, Docker.

```bash
docker compose up -d                       # SQL Server 2022 on localhost:1433
dotnet run --project src/Tcp.Api           # Development env: auto-migrates, admin / dev-admin
curl http://localhost:5000/healthz         # {"status":"Healthy"}  (port is printed on startup)
curl -u admin:dev-admin "http://localhost:5000/admin/api/requests?prefix=/task-provider"
```

Tests:

```bash
dotnet test tests/Tcp.UnitTests
dotnet test tests/Tcp.IntegrationTests     # starts its own SQL Server container (Testcontainers)
dotnet test tests/Tcp.ContractTests
```

Local secrets: use `dotnet user-secrets` (project `Tcp.Api`) — never commit them. Key Vault is only used when `KeyVault__Uri` is set.

## Deploy

Prerequisites: Azure CLI, `azd`, PowerShell 7 (`pwsh`), go-sqlcmd (`winget install sqlcmd`).

`azd up` runs `infra/hooks/preprovision.ps1` (resolves the deploying principal), provisions `infra/main.bicep`, runs `infra/hooks/postprovision.ps1` (SQL user for the managed identity, Key Vault secrets — idempotent, values never printed) and deploys the container.

### GitHub Actions (OIDC)

`ci.yml` builds and tests every PR; `deploy.yml` runs `azd deploy` on merges to `main`. Configure once:

1. Create an Entra app registration with a federated credential for `repo:Aston-Integrove/task-center-provider-app:environment:dev` and grant it *Contributor* + *User Access Administrator* on the subscription (or the resource group).
2. Add repository (or `dev` environment) **variables**: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME`, `AZURE_LOCATION`. No secrets are needed.

## Tools
- `tools/make-mvp-contract.py` — regenerates `spi-mvp.openapi.json` from the SAP file.

## Sources
- *SAP Task Center – Third-Party Adoption Guide*, v1.1, 2024-07-10 (in the parent folder).
- `TaskProviderV2.json` — SAP Task Center Service Provider Interface v2.0.0.
- RFC 6749, RFC 7523, RFC 7522, RFC 7643, RFC 7644.
