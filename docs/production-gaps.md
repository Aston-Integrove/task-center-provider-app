# From prototype to production: known gaps

What the prototype deliberately does not do (constitution V: minimal cost and complexity), and what to do about each before
real business data flows through it. Items marked **found while building** were not in the original plan.

## Reliability and scale

| Gap | Why it matters | What to do |
|---|---|---|
| Single replica by design (max 2) | Fine for a pull-based prototype, not for an SLA | Zone-redundant Container Apps environment, min 2 replicas, SQL General Purpose with zone redundancy |
| **Found while building:** in-memory state is per replica: request log ring buffer, token rate limiter, JWKS cache, async-response queue | With 2 replicas the request log shows half the traffic; limits are per replica; a queued async response is lost on restart | Move request log to Log Analytics queries, rate limit at the edge (Front Door/APIM), drop or persist `Spi:AsyncResponses` |
| **Found while building:** ASP.NET data-protection keys (antiforgery, IAS session cookie) are not shared between replicas or restarts | In IAS mode a form post can land on a replica that cannot read the token | Persist keys to Blob Storage/Key Vault (`PersistKeysToAzureBlobStorage` + `ProtectKeysWithAzureKeyVault`), or stay at one replica |
| No automated database backup/restore drill | SQL Basic has 7-day PITR only | Restore test, longer retention, geo-redundant backup |
| Migrations run at startup | A failed or slow migration blocks every replica | Run migrations as a deployment step (job) before the new revision |

## Security

| Gap | What to do |
|---|---|
| SQL firewall allows all Azure IPs (`0.0.0.0` rule) for Container Apps egress | VNet-integrated environment + private endpoint for SQL and Key Vault; drop public access |
| Admin console and `/app` use one shared Basic password | Entra ID / IAS SSO with roles; remove Basic outside development |
| No IP restriction on `/scim` and `/task-provider` | Allow-list BTP and IPS egress ranges at the ingress or in front (Front Door/APIM) |
| `jti` replay cache not implemented (tokens are 10-15 min) | Add a short-lived replay cache if tokens could leak |
| Token signing key rotation is manual | Automate: publish new key + `token-signing-key-previous`, then retire (JWKS already serves both) |
| Secrets seeded by `postprovision` once; no rotation | Key Vault rotation policy for client secrets; rotate on a schedule |
| No WAF / DDoS plan, public default `*.azurecontainerapps.io` host | Custom domain + managed certificate, Front Door with WAF |
| SAML bearer fallback has had no interop test against a real BTP subaccount | Validate in S-02 if the destination cannot use JWT bearer |
| Admin task API trusts the admin for recipient selection (no per-tenant isolation) | Real roles and audit of admin actions |

## Observability and operations

| Gap | What to do |
|---|---|
| No alerts | Azure Monitor alerts on `/healthz/ready` failures, 5xx rate, SQL DTU > 80 %, pull gaps (no `GET /tasks` for 5 min), token endpoint 401 spikes |
| No dashboards | Workbook over the JSON console logs (`correlationId`, `route`, `status`, `durationMs`, `clientId`) |
| Budget alert is a notification only | Budget action group to scale down / disable the destination |
| No runbook | Document: rotate secrets, replay a missed pull, handle a stuck task, GDPR request |

## Functional scope deliberately left out

Attachments, comments, bulk operations (`/bulkOperation`; definitions declare `tasks.bulk.operations=false`), push, substitution,
users endpoint, global operations, IFrame task details (`uiType` stays `Default`), `/details`. All answer `501 tcp.spi.notImplemented`.
Each needs a new spec (constitution V).

## Data and compliance

- Synthetic data only so far (assumption A-05). Before real data: data-protection review, retention rules for
  `tc.OperationLog`, `idm.ScimAudit` and the request log, and a tested GDPR erase (the mechanics exist: `DELETE /Users/{id}`
  anonymises users and re-publishes affected tasks; there is no erase for the audit tables' non-personal columns by design).
- Task `description` is plain text over the SPI (SAP contract) and sanitised HTML in the provider UI.
- Time zone: everything is UTC with millisecond precision; keep it that way when adding sources.
