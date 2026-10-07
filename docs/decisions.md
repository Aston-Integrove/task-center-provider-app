# Architecture Decision Records

Status key: **Accepted** · Proposed · Superseded

## ADR-001 Runtime: .NET 10 LTS Minimal API — Accepted
- **Context**: Team preference is .NET. .NET 8 support ends 10 Nov 2026; .NET 10 LTS is supported to Nov 2028.
- **Decision**: .NET 10, ASP.NET Core Minimal APIs, single project for the API plus Domain/Infrastructure class libraries.
- **Consequences**: Native OpenAPI document generation (`Microsoft.AspNetCore.OpenApi`) for our own endpoints; SAP contract kept as the authoritative file and validated by contract tests.

## ADR-002 Hosting: Azure Container Apps (Consumption) — Accepted
- **Context**: Need public HTTPS, minimal ops and cost; Task Center polls every 30 s.
- **Decision**: One Container App, 0.25 vCPU / 0.5 GiB, min replicas 1 while the destination is enabled (0 otherwise), max 2.
- **Alternatives**: App Service B1 (fixed ~USD 13, fine), Functions Flex (URN routing + cold starts awkward).

## ADR-003 Database: Azure SQL Basic (5 DTU) — Accepted
- **Context**: Pull query is keyset paging on `(ModifiedAt, Urn)`; serverless SQL cannot auto-pause because of 30-s delta pulls, so the free offer (100k vCore-s/month) would be exhausted in days.
- **Decision**: Azure SQL Database Basic, Entra-only auth with managed identity. `Urn` columns use a **binary collation** (`Latin1_General_100_BIN2`) to guarantee ordinal ordering matching "lexicographically greater".
- **Consequences**: ~USD 5/month fixed; 2 GB is ample (≈ 1 M tasks).

## ADR-004 Own minimal OAuth 2.0 token service — Accepted
- **Context**: BTP destinations need a token endpoint for both technical and user flows; we need full control during the prototype and to carry the Global User ID into our token.
- **Decision**: Implement `/oauth/token` in the app supporting `client_credentials` and `urn:ietf:params:oauth:grant-type:jwt-bearer` (RFC 7523). Tokens are RS256 JWTs signed with a key loaded from Key Vault; JWKS published at `/.well-known/jwks.json`. Grant handlers are pluggable so `saml2-bearer` (RFC 7522) can be added.
- **Alternatives**: Entra ID as authorization server (cannot natively accept BTP user JWTs as assertions without complex federation); SAML-only (SAP default but more work to validate assertions).
- **Risk**: R-01 (Task Center support for `OAuth2JWTBearer` on `_PP`).

## ADR-005 SCIM 2.0 subset for IPS — Accepted
- **Decision**: Implement RFC 7643/7644 subset: `/Users`, `/Groups` (CRUD, PATCH, `filter` with `eq`/`and`/`or`, paging), `/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas`. No bulk, no sort, no ETags, no `/Me`.
- **Global User ID**: stored in a dedicated column, resolved from (in order) SAP extension `userUuid`, `externalId`, or the SCIM `id` supplied by IPS transformation. Configurable.

## ADR-006 Persistence style: EF Core 10 — Accepted
- **Decision**: EF Core with code-first migrations; the `/tasks` keyset query hand-written via LINQ (verified SQL) or `FromSql` if needed for plan stability.

## ADR-007 Secrets: Key Vault + managed identity — Accepted
- OAuth client secrets and the admin password are Key Vault secrets, loaded at startup via managed identity (Key Vault configuration provider) and compared in constant time. SQL uses Entra managed-identity auth, so there is no DB password. Signing key = RSA PEM secret (`token-signing-key`), rotated by adding a second key (`kid`) and publishing both in JWKS.

## ADR-008 IaC and delivery: Bicep + azd + GitHub Actions — Accepted
- `azd up` for first provisioning; GitHub Actions with OIDC federated credential (`azure/login`) runs `azd deploy` on `main`.

## ADR-009 Task detail UI: `Default` — Accepted
- Use `uiType: Default` + `/description`. IFrame embedding needs third-party cookies/SSO in an iframe and is deferred. `uiLink` points at `/app/tasks/{urn}` for "Open in App".
- **Amendment (spec 004 implementation):** `TaskProviderV2.json` defines `/description` as *plain text* (`text/plain; charset=utf-8`). Descriptions are authored as sanitised HTML and converted to plain text for the SPI; the HTML is rendered only by the provider's own `/app/tasks/{urn}` page.

## ADR-010 Claim/Release as definition actions — Accepted
- `TaskProviderV2.json` has no global-operations endpoint, so `claim` and `release` are defined as `possibleActions` per definition with `validActionCodes` computed per task (`processor == null` ⇒ `claim`, else `release`).
