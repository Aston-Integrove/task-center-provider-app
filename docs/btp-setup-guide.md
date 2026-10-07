# Configuration Guide — SAP BTP, IAS/IPS and the Azure Provider

This is the provider-side documentation SAP requires (guide p.34–35: "the task provider must document how these destinations should be configured"). Values in `<…>` come from `azd env get-values` after deployment or from Valterra admins.

## 0. Prerequisites checklist

- [ ] BTP subaccount with **SAP Task Center** subscription + instance and **SAP Build Work Zone** site with the Task Center app (SAP Help → Task Center → Initial Setup).
- [ ] Subaccount trusts **IAS** (OIDC) and Task Center users log on via IAS.
- [ ] `Task_Center_global_settings` destination exists with the language vector (start with `en-US`).
- [ ] IPS tenant reachable with an admin user.
- [ ] Prototype deployed (`azd up`) and `/healthz` returns 200.
- [ ] Secrets created in Key Vault: `oauth-client-tc-tech`, `oauth-client-tc-pp`, `oauth-client-ips-scim`, `admin-password`, `token-signing-key` (azd post-provision hook generates them).

## 1. Provider identifiers

| Item | Value |
|---|---|
| Public base URL | `https://<app-fqdn>` |
| Token endpoint | `https://<app-fqdn>/oauth/token` |
| JWKS | `https://<app-fqdn>/.well-known/jwks.json` |
| SPI base | `https://<app-fqdn>/task-provider/v2` (alias `/api/task-provider/v2`) |
| SCIM base | `https://<app-fqdn>/scim/v2` |
| URN triple | `integrove:tcproto:valterra-dev` (`applicationId:applicationInstanceId:tenantId`) |

## 2. Primary destination (technical user) — `INTEGROVE_TP`

BTP cockpit → subaccount → Connectivity → Destinations → New.

| Field | Value |
|---|---|
| Name | `INTEGROVE_TP` (must be < 16 chars) |
| Type | HTTP |
| URL | `https://<app-fqdn>` — **see note** |
| Proxy Type | Internet |
| Authentication | OAuth2ClientCredentials |
| Client ID | `tc-tech` |
| Client Secret | value of KV secret `oauth-client-tc-tech` |
| Token Service URL Type | Dedicated |
| Token Service URL | `https://<app-fqdn>/oauth/token` |

Additional properties:

| Property | Value |
|---|---|
| `tc.enabled` | `true` |
| `tc.provider_type` | `Custom` |
| `tc.ui.group` | `Integrove` |
| `tc.ui.label` | `Integrove TP (prototype)` |
| `scope` (optional) | `spi.tech` |

> **URL note (spike S-01)**: start with the bare host. The request log in the admin console shows the exact path Task Center calls. If Task Center does **not** append `/task-provider/v2`, change URL to `https://<app-fqdn>/task-provider/v2`. Both work on the server side.

Use **Check Connection** — expect a 401/404 from the root, which proves reachability; token retrieval is verified in step 5.

## 3. Principal-propagation destination — `INTEGROVE_TP_PP`

| Field | Value |
|---|---|
| Name | `INTEGROVE_TP_PP` |
| Type | HTTP |
| URL | same as primary |
| Proxy Type | Internet |
| Authentication | **OAuth2JWTBearer** |
| Client ID | `tc-pp` |
| Client Secret | KV secret `oauth-client-tc-pp` |
| Token Service URL Type | Dedicated |
| Token Service URL | `https://<app-fqdn>/oauth/token` |

Additional properties: **none of `tc.*`** (must not have `tc.enabled`). Optional `scope=spi.user`.

Token service trust: add the issuer of the user JWT that Task Center hands to the Destination service to `OAuth__TrustedAssertionIssuers` (XSUAA: `https://<subdomain>.authentication.<region>.hana.ondemand.com/oauth/token`; IAS: `https://<tenant>.accounts.ondemand.com`). The JWKS is discovered automatically (`/token_keys` for XSUAA, OIDC discovery for IAS).

**Fallback (if S-02 fails)** — switch Authentication to `OAuth2SAMLBearerAssertion`:

| Field | Value |
|---|---|
| Audience | `https://<app-fqdn>` |
| Client Key | `tc-pp` |
| Token Service URL | `https://<app-fqdn>/oauth/token` |
| Token Service User / Password | `tc-pp` / secret |
| `nameIdFormat` (additional) | `urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified` |
| `userIdSource` (additional) | `user_uuid` (so the SAML NameID carries the Global User ID) |

and upload the subaccount's SAML signing certificate (Destinations → Download Trust) to Key Vault secret `btp-saml-trust-cert`. Requires spec 002 FR-PP-10 to be implemented.

## 4. Identity Provisioning (IAS → provider)

### 4.1 Source system
- Type: **Identity Authentication**, pointing to the Valterra IAS tenant (create via IPS "Source Systems" → Identity Authentication; the IAS tenant itself is usually pre-wired).
- Properties (examples): `ias.user.filter` to limit to a test group, e.g. `groups.display eq "TC_PROTO_USERS"`; `ias.group.filter` = `displayName eq "TC_PROTO_APPROVERS"`.

### 4.2 Target system
- Type: generic **SCIM** target ("SCIM System" in the IPS system-type list — verify in your tenant).
- Properties:

| Property | Value |
|---|---|
| `Type` | HTTP |
| `URL` | `https://<app-fqdn>/scim/v2` |
| `ProxyType` | Internet |
| `Authentication` | OAuth2ClientCredentials (or BasicAuthentication if OAuth is not offered) |
| `OAuth2TokenServiceURL` | `https://<app-fqdn>/oauth/token` |
| `User` | `ips-scim` |
| `Password` | KV secret `oauth-client-ips-scim` |
| `scim.support.patch.operation` (if offered) | `true` |
| `ips.trace.failed.entity.content` | `true` (during prototype) |

### 4.3 Transformation — carry the Global User ID
Start from the default target transformation IPS generates and make sure the IAS user UUID lands in a field our SCIM API reads (spec 003 FR-SCIM-07). Illustrative fragment:

```json
{
  "user": {
    "mappings": [
      { "sourcePath": "$.userName", "targetPath": "$.userName" },
      { "sourcePath": "$.emails", "targetPath": "$.emails", "preserveArrayWithSingleElement": true },
      { "sourcePath": "$.name.givenName", "targetPath": "$.name.givenName", "optional": true },
      { "sourcePath": "$.name.familyName", "targetPath": "$.name.familyName", "optional": true },
      { "sourcePath": "$.displayName", "targetPath": "$.displayName", "optional": true },
      { "sourcePath": "$.active", "targetPath": "$.active", "optional": true },
      { "sourcePath": "$.id", "targetPath": "$.externalId" },
      { "sourcePath": "$['urn:ietf:params:scim:schemas:extension:sap:2.0:User']['userUuid']",
        "targetPath": "$['urn:ietf:params:scim:schemas:extension:sap:2.0:User']['userUuid']",
        "optional": true }
    ]
  },
  "group": {
    "mappings": [
      { "sourcePath": "$.displayName", "targetPath": "$.displayName" },
      { "sourcePath": "$.id", "targetPath": "$.externalId" },
      { "sourcePath": "$.members", "targetPath": "$.members", "preserveArrayWithSingleElement": true, "optional": true }
    ]
  }
}
```

The exact IAS source attribute holding the Global User ID is confirmed in spike S-03 (the request log shows every attribute IPS sends). Run a **Read** job first, then **Provision**. Check the admin console → Users: every user must show a non-empty *Global User ID*.

## 5. Verification sequence

1. Admin console → *Diagnostics* → token test: `POST /oauth/token` as `tc-tech` returns a JWT.
2. IPS provisioning job succeeds; users and groups visible in admin console.
3. Admin console → create 3 tasks assigned to a provisioned user.
4. Save `INTEGROVE_TP`; within ~1–2 min Task Center Administration → Connectors shows task definition + INITIAL pull **succeeded**.
5. Log in to Work Zone as the assigned user → tasks visible under the *Integrove* tab.
6. Open a task → description renders (PP flow works) → **Approve** → task disappears; admin console shows status COMPLETED with `completedBy` = user's Global User ID.
