# Plan — 001 Foundation

## Technical context
| Item | Choice |
|---|---|
| Language | C# 14 / .NET 10 LTS |
| Web | ASP.NET Core Minimal APIs, `Microsoft.AspNetCore.OpenApi` |
| Data | EF Core 10 + `Microsoft.EntityFrameworkCore.SqlServer`, Azure SQL Basic |
| Identity | `Azure.Identity` (`DefaultAzureCredential`), `Azure.Extensions.AspNetCore.Configuration.Secrets` |
| Tests | xUnit v3, FluentAssertions (or Shouldly), `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.MsSql` |
| Logging | built-in JSON console logger (`AddJsonConsole`), optional OpenTelemetry later |
| IaC | Bicep modules + `azure.yaml` (azd) |
| CI | GitHub Actions, `azure/login@v2` OIDC, `Azure/setup-azd` |

## Repository layout
```
azure.yaml
infra/
  main.bicep                 # params: location, envName, principalId
  main.parameters.json
  modules/
    log-analytics.bicep
    container-registry.bicep
    key-vault.bicep          # RBAC mode
    sql.bicep                # server (Entra-only admin = deploying principal), db Basic
    container-apps-env.bicep
    container-app.bicep      # system MI, ingress external 8080, min 1 / max 2
  hooks/
    postprovision.ps1|sh     # create SQL user for MI, seed KV secrets (random), RSA key
src/Tcp.Api, src/Tcp.Domain, src/Tcp.Infrastructure
tests/Tcp.UnitTests, tests/Tcp.IntegrationTests, tests/Tcp.ContractTests
.github/workflows/ci.yml, deploy.yml
```

## Key design points
1. **SQL access for managed identity**: Bicep cannot create contained DB users. `postprovision` hook runs (as the deploying user, the Entra admin):
   ```sql
   CREATE USER [<container-app-name>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [<container-app-name>];
   ALTER ROLE db_datawriter ADD MEMBER [<container-app-name>];
   ALTER ROLE db_ddladmin  ADD MEMBER [<container-app-name>];
   ```
   using `sqlcmd` (go-sqlcmd) with `--authentication-method ActiveDirectoryDefault`.
2. **Secrets seeding**: hook generates 32-byte random values for `oauth-client-tc-tech`, `oauth-client-tc-pp`, `oauth-client-ips-scim`, `admin-password`; generates RSA-2048 PEM `token-signing-key` (`openssl genpkey`) only if absent. Prints where to read them (never the values).
3. **Request log**: `RequestLogMiddleware` captures into a `ConcurrentQueue`-backed ring buffer (`IRequestLog`), with a `Redactor` for headers/form fields/JSON properties named in a deny list. Enabled by `Diagnostics:RequestLogEnabled`.
4. **Auth skeleton**: register JWT bearer authentication validating *our own* issuer (keys from the signing key provider — spec 002) and authorization policies `spi.tech`, `spi.user`, `scim`, `admin` (Basic). Fallback policy = authenticated.
5. **Ports**: container listens on 8080; `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so `PublicBaseUrl` can also be derived.

## Bicep essentials
- Container App: `configuration.ingress.external=true`, `targetPort=8080`, `transport=auto`, `allowInsecure=false`; `registries` with `identity: 'system'`; env vars reference KV via `secrets[].keyVaultUrl` + `identity: 'system'` *or* app loads KV directly (chosen: app loads KV directly — fewer moving parts).
- AcrPull role assignment for the app MI; Key Vault Secrets User role for the app MI.
- SQL: `minimalTlsVersion: '1.2'`, `administrators.azureADOnlyAuthentication: true`, firewall rule `AllowAllWindowsAzureIps` (0.0.0.0) for Container Apps egress (prototype; tighten later).
- Log Analytics `retentionInDays: 30`, `workspaceCapping.dailyQuotaGb: 0.2`.

## Risks
- `azd` hook needs `sqlcmd` and `openssl` on the developer machine/runner → documented prerequisites; CI image installs them.
