# Architecture — Task Center Provider Prototype ("Integrove TP")

## 1. Context

```mermaid
flowchart LR
  subgraph BTP["SAP BTP subaccount (Valterra)"]
    TC[SAP Task Center service]
    DS[Destination service]
    WZ[Work Zone / Task Center UI]
  end
  subgraph CIS["SAP Cloud Identity Services"]
    IAS[Identity Authentication]
    IPS[Identity Provisioning]
  end
  subgraph AZ["Azure (South Africa North)"]
    APP["Container App: tc-provider<br/>.NET 10 Minimal API"]
    SQL[(SQLite file on<br/>Azure Files share)]
    KV[Key Vault]
    LOG[Log Analytics]
  end
  User((Business user)) --> WZ --> TC
  TC -- "1. get token" --> DS
  DS -- "2. POST /oauth/token<br/>client_credentials or jwt-bearer" --> APP
  TC -- "3. SPI calls with Bearer token" --> APP
  IAS -- source --> IPS
  IPS -- "SCIM 2.0 /scim/v2 (OAuth client_credentials)" --> APP
  APP --> SQL
  APP -. managed identity .-> KV
  APP --> LOG
  WZ -. "Open in App (uiLink)" .-> APP
```

One container hosts four logical modules sharing one database:

| Module | Path prefix | Caller | Auth |
|---|---|---|---|
| Token service | `/oauth/token`, `/.well-known/jwks.json`, `/.well-known/openid-configuration` | BTP Destination service, IPS | Client authentication (client_secret_basic / client_secret_post) |
| SCIM 2.0 | `/scim/v2/*` | IPS | Bearer (scope `scim`) — Basic optional fallback |
| Task Provider SPI | `/task-provider/v2/*` **and** `/api/task-provider/v2/*` | Task Center | Bearer: scope `spi.tech` (pull) or `spi.user` (PP) |
| Admin + app UI | `/admin/*`, `/app/*` | Integrove testers, business users (deep link) | Admin Basic auth (prototype); IAS OIDC SSO is a stretch item |

## 2. Solution structure

```
src/
  Tcp.Api/                 # Program.cs, endpoint registration, auth, middleware
    Endpoints/Spi/         # TasksEndpoints, TaskDefinitionsEndpoints, CapabilitiesEndpoints, OperationsEndpoints
    Endpoints/Scim/        # UsersEndpoints, GroupsEndpoints, DiscoveryEndpoints
    Endpoints/OAuth/       # TokenEndpoint, JwksEndpoint
    Endpoints/Admin/       # Admin API + static UI (wwwroot/admin)
  Tcp.Domain/              # Task, TaskDefinition, ScimUser, ScimGroup, rules (pure C#)
  Tcp.Infrastructure/      # EF Core DbContext, migrations, Key Vault key provider, JWKS cache
tests/
  Tcp.UnitTests/
  Tcp.IntegrationTests/    # WebApplicationFactory + temporary SQLite files
  Tcp.ContractTests/       # validates responses against contracts/*.yaml|json
infra/                     # main.bicep, modules/*.bicep, azure.yaml (azd)
specs/                     # this spec pack
```

## 3. Key flows

### 3.1 Technical pull (INITIAL / DELTA)

```mermaid
sequenceDiagram
  participant TC as Task Center
  participant DS as Destination svc
  participant TS as /oauth/token
  participant SPI as /task-provider/v2
  participant DB as SQLite
  TC->>DS: get destination INTEGROVE_TP
  DS->>TS: POST grant_type=client_credentials (client tc-tech)
  TS-->>DS: access_token (scope spi.tech, 15 min)
  DS-->>TC: token
  TC->>SPI: GET /taskDefinitions?$top=1000&$skip=0&languages=en-US
  SPI->>DB: SELECT … ORDER BY urn OFFSET/FETCH
  SPI-->>TC: {value:[…]} … then {value:[]}
  loop pages
    TC->>SPI: GET /tasks?modifiedAfter=T&lastId=U&$top=1000&languages=en-US
    SPI->>DB: keyset query on (ModifiedAt, Urn)
    SPI-->>TC: {value:[tasks…]}  (empty ⇒ stop)
  end
```

### 3.2 Principal propagation (approve from Task Center)

```mermaid
sequenceDiagram
  participant U as Business user
  participant TC as Task Center
  participant DS as Destination svc
  participant TS as /oauth/token
  participant SPI as SPI
  U->>TC: Approve task X
  TC->>DS: get destination INTEGROVE_TP_PP (with user JWT)
  DS->>TS: POST grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer<br/>assertion=<user JWT>, client tc-pp
  TS->>TS: validate JWT (issuer allow-list, JWKS, exp)<br/>resolve Global User ID (user_uuid → SCIM lookup)
  TS-->>DS: access_token (sub = Global User ID, scope spi.user, 10 min)
  DS-->>TC: token
  TC->>SPI: POST /tasks/X/response?languages=en-US {code:"approve"}
  SPI->>SPI: authorise: sub ∈ recipients (or group member) and processor rule
  SPI-->>TC: 200 Task (status COMPLETED)
```

> Fallback if Task Center/Destination service cannot use `OAuth2JWTBearer` for the `_PP` destination: add the SAML 2.0 bearer grant (`urn:ietf:params:oauth:grant-type:saml2-bearer`) to the same token endpoint and switch the destination to `OAuth2SAMLBearerAssertion`. The grant handler is pluggable by design (see spec 002, FR-PP-10).

### 3.3 User provisioning

```mermaid
sequenceDiagram
  participant IPS
  participant TS as /oauth/token
  participant SCIM as /scim/v2
  IPS->>TS: client_credentials (client ips-scim)
  IPS->>SCIM: GET /Users?filter=userName eq "jdoe"
  SCIM-->>IPS: ListResponse (0 hits)
  IPS->>SCIM: POST /Users {userName, emails, name, externalId=<GUID>, sap ext userUuid}
  SCIM-->>IPS: 201 + id
  IPS->>SCIM: POST /Groups / PATCH /Groups/{id} members add
```

## 4. Data model overview

```mermaid
erDiagram
  ScimUser ||--o{ ScimGroupMember : "member of"
  ScimGroup ||--o{ ScimGroupMember : has
  TaskDefinition ||--o{ OperationDefinition : defines
  TaskDefinition ||--o{ CustomAttributeDefinition : defines
  TaskDefinition ||--o{ TaskInstance : types
  TaskInstance ||--o{ TaskRecipientUser : "assigned to"
  TaskInstance ||--o{ TaskRecipientGroup : "assigned to"
  TaskInstance ||--o{ TaskCustomAttribute : has
  TaskInstance ||--o{ TaskOperationError : has
  TaskInstance ||--o{ OperationLog : audit
```

Details: `specs/003-scim-provisioning/data-model.md`, `specs/004-task-provider-spi/data-model.md`. Localised texts are stored as JSON columns (`TEXT` with a `json_valid` check) — simple and adequate for ≤3 languages.

## 5. Azure deployment

| Resource | SKU / setting | Est. USD/month* |
|---|---|---|
| Container Apps environment + app | Consumption, 0.25 vCPU / 0.5 GiB, min replicas 1, max 1 | 3 – 7 (after free grant) |
| Storage account (Azure Files share, SQLite file) | Standard LRS, 5 GiB quota | < 1 |
| Azure Container Registry | Basic | ~5 |
| Key Vault | Standard | < 1 |
| Log Analytics | PerGB2018, 30-day retention, daily cap 0.2 GB | 0 – 2 |
| **Total** | | **≈ 10 – 15** |

\*Estimates at list price; confirm in the Azure Pricing Calculator for South Africa North. Delta pulls every 30 s keep the app warm, so scale-to-zero gives no saving while the destination is enabled. The database is a SQLite file (ADR-011), so the app runs as a single replica.

Ingress: external HTTPS on the default `*.azurecontainerapps.io` FQDN (managed TLS). Custom domain optional. Optional IP restriction on `/scim` and `/task-provider` to SAP BTP/IPS egress ranges (stretch).

## 6. Configuration (environment / Key Vault)

| Key | Example | Notes |
|---|---|---|
| `Provider__ApplicationId` | `integrove` | URN part |
| `Provider__ApplicationInstanceId` | `tcproto` | URN part |
| `Provider__TenantId` | `valterra-dev` | URN part |
| `Provider__PublicBaseUrl` | `https://tc-provider.<env>.southafricanorth.azurecontainerapps.io` | uiLink + issuer |
| `Provider__DefaultLanguage` | `en-US` | `isDefault` text |
| `OAuth__Issuer` | `${PublicBaseUrl}` | |
| `OAuth__Clients__0__ClientId` | `tc-tech` | secret in KV `oauth-client-tc-tech` |
| `OAuth__Clients__1__ClientId` | `tc-pp` | grants: jwt-bearer |
| `OAuth__Clients__2__ClientId` | `ips-scim` | scope scim |
| `OAuth__TrustedAssertionIssuers__0` | `https://<subdomain>.authentication.eu10.hana.ondemand.com/oauth/token` | XSUAA |
| `OAuth__TrustedAssertionIssuers__1` | `https://<tenant>.accounts.ondemand.com` | IAS |
| `OAuth__UserIdClaims` | `user_uuid,sub` | ordered claim lookup |
| `OAuth__SigningKeySecretName` | `token-signing-key` | RSA 2048 PEM in KV |
| `Admin__BasicUser` / KV `admin-password` | | |
| `ConnectionStrings__Sql` | `Server=tcp:…;Authentication=Active Directory Managed Identity;Database=tcp` | |
| `Diagnostics__RequestLogSize` | `500` | ring buffer |
