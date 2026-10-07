# SAP Task Center — Requirements Digest for a Custom Provider

Extracted from *SAP Task Center – Third-Party Adoption Guide v1.1 (2024-07-10)* and `TaskProviderV2.json` (SPI v2.0.0). Page numbers refer to the guide's footer ("PUBLIC n"). Each rule has an ID that specs reference (e.g. `TC-PULL-02`).

## 1. How Task Center talks to a provider

| ID | Rule | Source |
|---|---|---|
| TC-ARCH-01 | Task Center keeps a **central cache**. Task list, filter, sort are served from cache; only details, description and operations call the provider live. | p.4, p.24 |
| TC-ARCH-02 | Pulls (tasks, task definitions) run in a **technical-user** context (service-to-service). | p.24–25 |
| TC-ARCH-03 | Description, details, actions, responses run in the **logged-in user** context (principal propagation); the provider must know who is acting and authorise it. | p.24, p.31 |
| TC-ARCH-04 | PUSH is supported only for SAP providers → a third-party provider is **pull-only** (`tasks.push=false`). | p.24, p.68 |

## 2. Destinations (connectivity)

| ID | Rule | Source |
|---|---|---|
| TC-DEST-01 | A destination with additional property `tc.enabled=true` is treated as a connector. | p.25 |
| TC-DEST-02 | Primary destination name `<Provider_name>`, **< 16 characters**, used for technical flows. | p.34 |
| TC-DEST-03 | Secondary destination `<Provider_name>_PP` for principal propagation; it MUST NOT have `tc.enabled`. | p.32, p.34 |
| TC-DEST-04 | Add `tc.provider_type=Custom` for third-party providers. Optional: `tc.ui.group` (filter tab, translatable `tc.ui.group.<lang>`), `tc.ui.label`. | p.6, p.31, p.34 |
| TC-DEST-05 | Before every call Task Center asks the Destination service for a token. If the destination cannot return a valid token, the call fails. | p.30 |
| TC-DEST-06 | Technical user: standard mechanisms — client credentials, basic, mTLS. Example uses `OAuth2ClientCredentials` with a dedicated token service URL. | p.31 |
| TC-DEST-07 | Principal propagation: standard flow is SAML (`OAuth2SAMLBearerAssertion`). "As a general rule, if the destination can be configured in a way it returns a valid security token identifying the current user at task provider side, PP will work." Alternatives may need an SAP Customer Influence request. | p.31–32 |
| TC-DEST-08 | Central multi-tenant endpoints: pass tenant/company via `URL.headers.<name>` / `URL.queries.<name>` additional properties. | p.108 |
| TC-DEST-09 | The provider must document how to configure both destinations; the customer configures and validates them. | p.34 |

## 3. Identity prerequisites

| ID | Rule | Source |
|---|---|---|
| TC-ID-01 | SAP Cloud Identity Services – Identity Authentication (IAS) is a hard prerequisite; common user base between BTP and provider. | p.27 |
| TC-ID-02 | Every user reference in SPI payloads is the IAS **Global User ID**. | p.27, p.72, p.108 |
| TC-ID-03 | User details (display name, e-mail) come from IAS, not from the provider. | p.27 |
| TC-ID-04 | Recommended sync: SAP Cloud Identity Services – Identity Provisioning (IPS) from IAS to the provider (or IAS API). | p.27 |
| TC-ID-05 | SSO between provider UI and IAS is recommended for "Open in App" navigation. | p.27 |
| TC-ID-06 | `recipientGroups` are IAS group names; members of the group may process the task. | p.74, p.108 |

## 4. Scope levels

| Scope | Endpoints | User capability |
|---|---|---|
| **POC** | `GET /tasks`, `GET /tasks/{urn}`, `GET /taskDefinitions`, `GET /taskDefinitions/{urn}` (+ `GET /capabilities` optional) — technical user only | See tasks and custom attributes; act via `uiLink` deep link |
| **MVP** (this prototype) | POC + `GET /tasks/{urn}/description`, `POST /tasks/{urn}/response`, `POST /tasks/{urn}/action` (PP) | Approve / reject / act inside Task Center |
| Advanced | `/bulkOperation`, comments, attachments, substitution, users, global operations | Out of scope |

Source: p.28–30.

## 5. Pull algorithm

| ID | Rule | Source |
|---|---|---|
| TC-PULL-01 | Definitions are pulled first, then an INITIAL task pull of the last **3 months**. | p.25, p.26 |
| TC-PULL-02 | `GET /tasks?modifiedAfter=…&lastId=…&$top=…&languages=…` must behave like: `SELECT TOP $top * FROM TASKS WHERE (modifiedAt = :modifiedAfter AND urn > :lastId) OR (modifiedAt > :modifiedAfter) ORDER BY modifiedAt ASC, urn ASC`. `lastId` is the task **URN**; comparison is lexicographic (ordinal). When `lastId` is absent, `modifiedAt >= modifiedAfter`. | p.25, p.32, p.68–69 |
| TC-PULL-03 | An **empty `value: []`** means "no more data" and stops the pull. | p.33 |
| TC-PULL-04 | Page size up to 1000. INITIAL: 25 pages, wait 30 s, repeat. DELTA: every 30 s, up to 5 pages. | p.26 |
| TC-PULL-05 | `/taskDefinitions` uses simple `$top`/`$skip` paging; empty list ends. Re-pulled every 24 h. | p.26, p.33 |
| TC-PULL-06 | If a task references an unknown definition or unknown custom attribute, Task Center calls `GET /taskDefinitions/{urn}`. | p.26 |
| TC-PULL-07 | `modifiedAfter` format `yyyy-MM-ddTHH:mm:ss.fffZ` (UTC). `languages` is **required**, comma-separated, e.g. `en-US,de-DE` (max 3 languages configured in `Task_Center_global_settings`). | spec, p.110 |
| TC-PULL-08 | Performance: 1000 tasks in < 30 s; synchronous calls < 1400 ms. | p.34–35 |

## 6. Entities (MVP subset)

**URN pattern** `urn:sap.odm.bpm.<task|taskdefinition>:<applicationId>:<applicationInstanceId>:<tenantId>:<localId>`; the triple `applicationId:applicationInstanceId:tenantId` maps 1:1 to the `tc.enabled` destination. All URN parts are also returned as separate fields. Max lengths: urn 300, parts 64. (p.53, p.71, p.108)

**Task (required)**: `urn, applicationId, applicationInstanceId, tenantId, localId, definitionId, status, createdAt, modifiedAt, subject[]`.
Optional used by us: `createdBy, modifiedBy, processor, dueAt, completedAt, completedBy, priority (default MEDIUM), uiLink, customAttributes[] (≤30), recipientUsers[], recipientGroups[], validResponseCodes, validActionCodes, operationErrors`.

- Status enum: `READY, RESERVED, IN_PROGRESS (deprecated), FOR_RESUBMISSION, INACTIVE, COMPLETED, CANCELED`. (p.72)
- `processor` must be in `recipientUsers`; if set, only the processor sees the task. (p.73)
- `validResponseCodes` / `validActionCodes`: null ⇒ all definition operations valid (pull); `[]` ⇒ none. (p.73–74)
- Custom attribute values are strings validated against the definition type: `STRING` (≤255, truncated), `BOOLEAN` ("true"/"false"), `INTEGER` (int32), `FLOAT`, `DATE` (`yyyy-MM-dd`), `DATETIME` (`…T…​.fffZ`), `TIME` (`HH:mm:ss`). Invalid values are silently dropped by Task Center. (p.74)
- Subject must be unique and meaningful (e.g. "Approve PR 4711 – Laptop for L. Robbins"). (p.33)

**TaskDefinition (required)**: Base fields + `name[]`. Optional: `possibleResponses[]`, `possibleActions[]`, `customAttributes[]` (`code, type, name[], format, rank`), `capabilities[]` (`attachments, attachments.create, comments, comments.create, tasks.description`), `taskDetailsSettings` (`webUISettings`/`mobileUISettings` → `uiType: Default|BuiltIn|DataDriven|IFrame|Form`; third parties: `Default` or `IFrame`). (p.53–55, p.111)

**Operation definition**: `code` (no commas, ≤64), `name[]`, `nature (POSITIVE|NEGATIVE|NEUTRAL)`, `commentRequired`, `reasonRequired` (`REQUIRED|OPTIONAL|UNSUPPORTED`), `possibleReasons[]`, `capabilities[{name:"tasks.bulk.operations"}]`.

**Responses vs actions** (p.42, p.78–82)
- Response = finalising (→ `COMPLETED`/`CANCELED`), may be async (`202`, no body), bulk-capable. `200` returns the full updated Task.
- Action = non-finalising (task stays active), synchronous only, `200` returns the full updated Task.
- Request body: `{ "code": "...", "comment": "...|null", "reasonCode": "...|null" }`; query `languages` required; header `Accept-Language` for error message language.

**Error body** (all 4xx/5xx): `{ "error": { "code": "app.area.reason", "message": "user-facing, localised", "target": null, "details": [] } }`. Messages are shown to business users.

**Capabilities** `GET /capabilities` → `{ "value": [ { "name": "tasks.pull", "value": "true" }, … ] }` (values are **strings**). Defaults: `tasks.pull=true`, `taskDefinitions.pull=true`, others false. (p.35–38)

## 7. Localisation (p.53–54, p.110)
- Every `LocalizedText[]` contains the translations available among the requested `languages`; providers may return fewer.
- Exactly **one** entry has `isDefault: true` (used when the user's language is missing). `'*'` language code is a fallback alternative and is mutually exclusive with `isDefault`.
- Language codes `ll-CC` (e.g. `en-US`). `/description` honours `Accept-Language` and returns `Content-Language`.

## 8. GDPR (p.33–34)
- No sensitive personal data in task payloads.
- Never hard-delete: send a tombstone update with `status: CANCELED`.
- When a person must be forgotten, anonymise affected tasks and bump `modifiedAt` so Task Center picks up the change.

## 9. SPI contract notes specific to `TaskProviderV2.json`
- `servers: /api/task-provider/v2` while the guide's examples use `/task-provider/v2/...`. **[NEEDS CLARIFICATION — resolved by spike S-01]** which suffix Task Center appends to the destination URL. The prototype serves both prefixes.
- Security schemes in the file are AFC-specific (XSUAA client credentials, scope `AFC_API_Access`). For our provider the schemes are replaced by our own token service; schemas/paths are unchanged.
- This spec version has **no** `/users`, `/globalOperations` or substitution endpoints → capabilities `user.existence`, `global.operations`, `substitutions` must be `false`. Claim/release are therefore modelled as task-definition **actions**.
