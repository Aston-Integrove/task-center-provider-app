# Feature 004 — Task Provider SPI (MVP scope)

**Status**: Ready · **Priority**: P1 · **Depends on**: 001, 002, 003
**Contracts**: `contracts/TaskProviderV2.json` (authoritative, SAP) · `contracts/spi-mvp.openapi.json` (generated subset we implement)
**SAP rules**: TC-PULL-01…08, TC-ID-02, TC-ARCH-01…04, §6–8 of `docs/sap-task-center-digest.md`

## Summary
Implement the SAP Task Center Service Provider Interface v2 **MVP scope** so Task Center can (a) pull task definitions and tasks with a technical user, and (b) show descriptions and execute responses (approve/reject) and actions (claim/release/increase priority) on behalf of the logged-in user.

## Endpoints in scope

| # | Method & path (relative to SPI base) | Auth | Scope |
|---|---|---|---|
| E1 | `GET /capabilities` | tech or user | POC |
| E2 | `GET /taskDefinitions?$top&$skip&languages` | tech or user | POC |
| E3 | `GET /taskDefinitions/{taskDefinitionUrn}?languages` | tech or user | POC |
| E4 | `GET /tasks?modifiedAfter&lastId&$top&languages` | tech or user | POC |
| E5 | `GET /tasks/{taskUrn}?languages` | tech or user | POC |
| E6 | `GET /tasks/{taskUrn}/description` (`Accept-Language`) | user | MVP |
| E7 | `POST /tasks/{taskUrn}/response?languages` | user | MVP |
| E8 | `POST /tasks/{taskUrn}/action?languages` | user | MVP |

SPI base: `/task-provider/v2` **and** `/api/task-provider/v2` (R-03). All other SPI paths return `501` with SAP `Error` body `tcp.spi.notImplemented`.

## User stories

### US-004-1 Capabilities (P1)
**Acceptance**: `GET /capabilities` ⇒ `200 {"value":[{"name":"tasks.pull","value":"true"},{"name":"taskDefinitions.pull","value":"true"},{"name":"tasks.push","value":"false"},{"name":"substitutions","value":"false"},{"name":"user.existence","value":"false"},{"name":"global.operations","value":"false"}]}` — values are strings.

### US-004-2 Pull task definitions (P1)
**Acceptance**
1. Given 3 definitions, `GET /taskDefinitions?$top=2&$skip=0&languages=en-US` returns 2 ordered by `urn` (ordinal); `$skip=2` returns 1; `$skip=4` returns `{"value":[]}`.
2. `$top` > 1000 or < 1, `$skip` < 0, missing/invalid `languages` ⇒ `400` SAP `Error` (`tcp.spi.invalidParameter`, `target` = parameter name).
3. Every definition contains `urn, applicationId, applicationInstanceId, tenantId, localId, name[]` and, when defined, `possibleResponses[]`, `possibleActions[]`, `customAttributes[]`, `capabilities[]` (`tasks.description=true`), `taskDetailsSettings` (`webUISettings.uiType="Default"`, `mobileUISettings.uiType="Default"`).
4. `GET /taskDefinitions/{urn}` returns the definition or `404` (`tcp.spi.taskDefinitionNotFound`).

### US-004-3 Pull tasks — keyset scrolling (P1)
**Acceptance**
1. Given tasks A(t1,"…a"), B(t2,"…b"), C(t2,"…c"), D(t3,"…d") (t1<t2<t3), `GET /tasks?modifiedAfter=t2&lastId=…b&$top=10` returns `[C, D]`.
2. Without `lastId`, `modifiedAfter=t2` returns `[B, C, D]` (≥).
3. `$top=1` repeated with the last returned `(modifiedAt, urn)` visits every task exactly once — even with 2 500 tasks sharing one `modifiedAt` (property test).
4. No more data ⇒ `{"value":[]}`.
5. Results include tasks in **all** statuses (incl. COMPLETED/CANCELED tombstones) — Task Center needs final states.
6. `modifiedAfter` must match `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$`; otherwise `400`. `$top` default 100, max 1000.
7. Performance: 1000 tasks with 5 custom attributes and 3 recipients returned in < 2 s on Azure SQL Basic (budget 30 s).
8. Tasks older than the INITIAL horizon are still returned if `modifiedAt ≥ modifiedAfter` — the provider does not filter by age.

### US-004-4 Task payload rules (P1)
**Acceptance**
1. All user fields contain Global User IDs only; `recipientUsers` includes `processor` when set.
2. `recipientUsers` contains only active, non-deleted users; `recipientGroups` contains SCIM group `displayName`s.
3. `subject[]`, `name[]` honour `languages`: return translations for requested languages that exist; exactly one item has `isDefault:true` (the provider default language if present in the result, otherwise the first returned); if none of the requested languages exist, return the default-language text with `isDefault:true`.
4. Timestamps formatted `yyyy-MM-ddTHH:mm:ss.fffZ`; `null` for absent optional timestamps.
5. `uiLink` = `{PublicBaseUrl}/app/tasks/{urlencoded urn}`.
6. `validResponseCodes`: `null` for open tasks (all definition responses valid), `[]` for COMPLETED/CANCELED.
7. `validActionCodes`: computed — `claim` iff `processor == null` and status READY; `release` iff `processor != null`; `increasePriority` iff `priority != VERY_HIGH`; `[]` when final.
8. Custom attribute values serialised as strings matching their definition type (digest §6). Invalid stored values are omitted and logged.
9. `operationErrors` omitted unless present.

### US-004-5 Single task (P1)
**Acceptance**: `GET /tasks/{urn}` returns the task (any status) or `404 tcp.spi.taskNotFound`. URN accepted raw (`urn:sap.odm.bpm.task:…`) and percent-encoded.

### US-004-6 Task description (P1, MVP)
**Acceptance**
1. With a user token whose user is entitled (FR-SPI-AUTH), `GET /tasks/{urn}/description` with `Accept-Language: de-DE` returns `200 text/html; charset=utf-8` (or `text/plain` if the stored description is plain) and `Content-Language` of the actual language used (fallback default).
2. Not entitled ⇒ `403 tcp.spi.notAuthorized`; unknown ⇒ `404`; technical token ⇒ `403 tcp.auth.userContextRequired`.
3. HTML is sanitised on write (admin) — no scripts, inline event handlers or iframes.

### US-004-7 Respond to a task (P1, MVP)
**Acceptance**
1. Given open task T (status READY, user U in recipients, no processor), when U POSTs `/tasks/T/response?languages=en-US` `{"code":"approve","comment":"ok"}`, then `200` with full Task: `status=COMPLETED`, `completedAt=now`, `completedBy=U`, `modifiedAt=now`, `modifiedBy=U`, `processor=U`, `validResponseCodes=[]`, `validActionCodes=[]`.
2. `reject` with definition `commentRequired=REQUIRED` and no/blank comment ⇒ `400 tcp.spi.commentRequired` with localised message (Accept-Language).
3. `reasonRequired=REQUIRED` and missing/unknown `reasonCode` ⇒ `400 tcp.spi.reasonRequired` / `tcp.spi.invalidReason`.
4. Unknown response code ⇒ `400 tcp.spi.invalidOperation`.
5. Task already COMPLETED/CANCELED ⇒ `409 tcp.spi.taskFinal`.
6. Task reserved by another user (processor ≠ U) ⇒ `403 tcp.spi.reservedByOther`.
7. U not entitled ⇒ `403 tcp.spi.notAuthorized`.
8. Every response attempt (success or failure) is written to `OperationLog`.
9. *(P3 flag `Spi:AsyncResponses`)*: return `202` without body; a background worker completes it after `Spi:AsyncDelaySeconds` and, for codes in `Spi:SimulateFailureCodes`, writes `operationErrors` instead — enables "Failed Tasks" testing.

### US-004-8 Execute an action (P1, MVP)
**Acceptance**
1. `claim` on an unclaimed READY task ⇒ `200`, `status=RESERVED`, `processor=U`, `validActionCodes=["release", …]`.
2. `release` by the processor ⇒ `200`, `status=READY`, `processor=null`.
3. `release` by a non-processor ⇒ `403 tcp.spi.reservedByOther`.
4. `increasePriority` raises LOW→MEDIUM→HIGH→VERY_HIGH; at VERY_HIGH ⇒ `409 tcp.spi.actionNotValid`.
5. Action codes not in definition ⇒ `400 tcp.spi.invalidOperation`. Final task ⇒ `409 tcp.spi.taskFinal`.
6. Every action bumps `modifiedAt` so the change also flows through the next DELTA pull.

## Functional requirements
- FR-SPI-01 URN format `urn:sap.odm.bpm.task:{ApplicationId}:{ApplicationInstanceId}:{TenantId}:{LocalId}` and `urn:sap.odm.bpm.taskdefinition:…`; parts from configuration; `LocalId` ≤ 64 chars, `[A-Za-z0-9_.-]` only; total ≤ 300.
- FR-SPI-02 Task `LocalId` for admin-created tasks: `{DefinitionLocalId}-{yyyyMMdd}-{seq:D6}` (e.g. `PR_APPROVAL-20261007-000042`) — also used in subject for uniqueness.
- FR-SPI-03 Every mutation sets `ModifiedAt = UtcNow truncated to ms`; if the new value ≤ the previous `ModifiedAt` of that task, use previous + 1 ms (monotonic per task).
- FR-SPI-AUTH Entitlement: user U may read description / act if `U ∈ recipientUsers` **or** U is member of a group in `recipientGroups`; and (`processor == null` **or** `processor == U`). Inactive/deleted users are never entitled.
- FR-SPI-04 SAP `Error` body for all SPI 4xx/5xx: `{"error":{"code":"tcp.<area>.<reason>","message":"<localised>","target":null,"details":[]}}`. Messages localised for `en-US` and `de-DE` via resource files; fallback `en-US`.
- FR-SPI-05 Unsupported optional endpoints of `TaskProviderV2.json` (`/bulkOperation`, `/details`, attachments, comments, `/configuration/push`) ⇒ `501 tcp.spi.notImplemented`.
- FR-SPI-06 Responses use `application/json; charset=utf-8`; properties camelCase exactly as SAP schema; omit nulls **except** where SAP examples use explicit `null` (`processor`, `completedAt`, `dueAt`, `recipientGroups`) — serializer configured to write these explicitly.
- FR-SPI-07 Seed data: definitions in `seed/task-definitions.json` loaded idempotently at startup (upsert by URN; definition changes bump nothing on tasks).
- FR-SPI-08 Request/response logged via request log (spec 001).

## Seed task definitions (MVP)

| Local ID | Name (en-US / de-DE) | Responses | Actions | Custom attributes |
|---|---|---|---|---|
| `PR_APPROVAL` | Approve Purchase Requisition / Bestellanforderung genehmigen | `approve` (POSITIVE, comment OPTIONAL; `tasks.bulk.operations` **false** — `/bulkOperation` not implemented), `reject` (NEGATIVE, comment REQUIRED, reason OPTIONAL: `budget`, `duplicate`, `other`) | `claim`, `release`, `increasePriority` | `amount` FLOAT rank 100, `currency` STRING 90, `requester` STRING 80, `costCenter` STRING 70, `neededBy` DATE 60 |
| `LEAVE_APPROVAL` | Approve Leave Request / Urlaubsantrag genehmigen | `approve`, `reject` (comment OPTIONAL) | `claim`, `release` | `leaveType` STRING, `fromDate` DATE, `toDate` DATE, `days` INTEGER |
| `INVOICE_EXCEPTION` | Resolve Invoice Exception / Rechnungsausnahme klären | `accept` (POSITIVE), `return` (NEGATIVE, reason REQUIRED: `price`, `quantity`) | `claim`, `release`, `increasePriority` | `vendor` STRING, `invoiceNo` STRING, `variance` FLOAT, `postingDate` DATE |

All definitions: `capabilities: [{name:"tasks.description", value:true}]`, `taskDetailsSettings.webUISettings.uiType = "Default"`.

## Non-functional
- NFR-004-01 `/tasks` page of 1000 < 2 s p95 (SAP budget 30 s).
- NFR-004-02 E6–E8 p95 < 800 ms (SAP budget 1400 ms) measured from BTP region.
- NFR-004-03 Contract tests validate every E1–E8 response against `TaskProviderV2.json` schemas.

## Out of scope
Bulk operations (unless flag work is pulled in), push, attachments, comments, substitution, users endpoint, global operations, IFrame details, `/details`.
