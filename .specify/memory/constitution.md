# Constitution — SAP Task Center Third-Party Provider Prototype

Version 1.0 · Ratified 2026-10-07 · Owner: Aston Motsau (Integrove)

This constitution governs every spec, plan, task and line of code in this repository. When a spec, plan or AI-generated change conflicts with it, the constitution wins. Amendments require a version bump and a note in the change log at the bottom.

## Purpose

Prove, at minimal cost and complexity, that a non-SAP system hosted on Azure can act as an SAP Task Center **custom task provider** for Valterra, covering:

1. Authentication that works with SAP BTP destinations (technical user + principal propagation).
2. Global User ID synchronisation from SAP Cloud Identity Services via a minimal **SCIM 2.0** API.
3. The **MVP scope** of the Task Center SPI as defined in `TaskProviderV2.json` (v2.0.0).

## Core principles

### I. Contract first (NON-NEGOTIABLE)
- The SAP SPI contract (`specs/004-task-provider-spi/contracts/TaskProviderV2.json`) is the source of truth for wire format. We never rename, reshape or "improve" SAP fields.
- Every HTTP endpoint we expose has an OpenAPI contract in a `contracts/` folder **before** it is implemented.
- Contract tests validate real responses against the OpenAPI schema in CI.

### II. Spec → Plan → Tasks → Code
- No code without an approved `spec.md` (what/why) and `plan.md` (how).
- `tasks.md` items are small (≤ ½ day), ordered, and each names the test that proves it.
- Specs describe behaviour in testable acceptance criteria (Given/When/Then). Ambiguities are marked `[NEEDS CLARIFICATION]` and resolved before planning.

### III. Test first
- Write the failing test, then the code. Minimum: unit tests for domain rules, integration tests against a real SQL Server container (Testcontainers), contract tests against OpenAPI.
- The SPI pull algorithm (keyset paging on `modifiedAt`, `urn`) MUST have property-style tests covering identical timestamps across page boundaries.

### IV. Global User ID is the only user key
- Every user reference sent to Task Center (`createdBy`, `modifiedBy`, `processor`, `completedBy`, `recipientUsers`, `operationErrors.executedBy`) MUST be the IAS **Global User ID**. Never emails, never local IDs.
- `recipientGroups` MUST be IAS group names.
- The provider's user store is populated **only** through SCIM (or the admin seed path in non-production). No hand-typed user IDs in task data.

### V. Simplicity and minimal cost
- One deployable (.NET 10 LTS Minimal API container), one database (Azure SQL Basic), one environment per stage. No message brokers, no caches, no microservices.
- Target run cost ≤ USD 25/month per environment. Any resource adding > USD 5/month needs an ADR.
- YAGNI: features outside the MVP scope (attachments, comments, substitution, push, bulk) are explicitly out of scope until a new spec is approved.

### VI. Secure by default
- All endpoints require authentication except `/healthz`, `/.well-known/*`, and `/oauth/token` (which authenticates clients itself).
- Secrets and signing keys live in Azure Key Vault; the app uses a system-assigned managed identity for Key Vault and Azure SQL (Entra auth). No secrets in source, config files or pipeline logs.
- Tokens: short-lived (≤ 15 min tech, ≤ 10 min user), RS256-signed, audience-restricted per client.
- Logs never contain tokens, assertions, secrets, or full personal data; user IDs may be logged.

### VII. SAP behavioural fidelity
- GDPR: tasks are never hard-deleted while Task Center may hold them; removal = tombstone (`status = CANCELED`, `modifiedAt` bumped).
- Timestamps are UTC with millisecond precision (`yyyy-MM-ddTHH:mm:ss.fffZ`) and stored truncated to ms so equality comparisons are exact.
- Task subjects are unique and meaningful (include a business number).
- Exactly one `LocalizedText` per text carries `isDefault: true`.
- Performance budgets: a 1000-task page in < 30 s (target < 2 s); synchronous user calls < 1400 ms p95.

### VIII. Observable
- Structured logs (JSON) with correlation ID; every inbound SPI/SCIM call recorded in a ring-buffer request log viewable from the admin console (headers redacted). This is how we learn what Task Center and IPS actually send.

## Technology constraints

| Area | Decision |
|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core Minimal APIs, C# 14 |
| Data | Azure SQL Database Basic (5 DTU), EF Core 10 or Dapper (plan decides), migrations in repo |
| Hosting | Azure Container Apps (Consumption), min replicas 1 during integration testing |
| Registry | Azure Container Registry Basic |
| Secrets | Azure Key Vault + system-assigned managed identity |
| IaC / deploy | Bicep + Azure Developer CLI (`azd up`) |
| CI/CD | GitHub Actions (build, test, contract test, `azd deploy`) |
| Region | South Africa North (default), configurable |

## Quality gates (every PR)

1. Build + all tests green; no new analyzer warnings.
2. OpenAPI contract tests pass for any touched endpoint.
3. `tasks.md` checkbox ticked and linked to the PR.
4. No secrets detected (gitleaks).
5. Spec updated if behaviour changed.

## Governance

- Specs are versioned with the code. A behavioural change without a spec change is a defect.
- AI coding agents (Claude Code, Copilot) must be pointed at this constitution and the relevant `spec.md`/`plan.md` before generating code.

## Change log
- 1.0 (2026-10-07) — initial version.
