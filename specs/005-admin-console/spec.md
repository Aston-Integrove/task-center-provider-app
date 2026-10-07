# Feature 005 — Admin Console, Test-Data API and "Open in App" Page

**Status**: Ready · **Priority**: P1 · **Depends on**: 003, 004 · **Contract**: `contracts/admin.openapi.yaml` (to be generated from code via `Microsoft.AspNetCore.OpenApi`, reviewed in PR)

## Summary
Testers need to create and change tasks in the "provider system" and watch what SAP sends. This feature provides a protected admin REST API, a single-page admin UI (static HTML + vanilla JS, no build step), and the task page that `uiLink` opens.

## User stories

### US-005-1 Create tasks for synced users (P1)
As a tester, I create a task of a chosen definition, assigned to SCIM-synced users and/or groups, with subject, priority, due date, custom attributes and an HTML description.

**Acceptance**
1. `POST /admin/api/tasks` with `{definitionLocalId, recipients:{users:[globalUserId|email], groups:[displayName]}, subject:{en-US, de-DE?}, priority?, dueAt?, customAttributes:{code:value}, description:{en-US:{contentType, body}}}` ⇒ `201` with the SPI representation.
2. Recipients given by e-mail are resolved to Global User IDs via SCIM store; unknown/inactive users or unknown groups ⇒ `400` listing the offenders.
3. Subject is auto-suffixed with the business number if not unique (e.g. "Approve PR 4711 – Laptop (PR_APPROVAL-20261007-000042)").
4. Custom attribute values are validated against the definition type ⇒ `400` on mismatch.
5. Description HTML sanitised (allow-list: `p, br, b, strong, i, em, ul, ol, li, table, thead, tbody, tr, th, td, a[href], h1–h4, span[style]`).
6. New task: `status=READY`, `createdAt=modifiedAt=now`, `createdBy=null` (system) unless `createdBy` given.

### US-005-2 Change tasks provider-side (P1)
**Acceptance**
1. `PATCH /admin/api/tasks/{urn}` can change subject, priority, dueAt, recipients, custom attributes, description ⇒ `modifiedAt` bumped, `modifiedBy=null`.
2. `POST /admin/api/tasks/{urn}/complete` (simulate completion in the provider UI by a given user) ⇒ COMPLETED.
3. `POST /admin/api/tasks/{urn}/cancel` ⇒ tombstone CANCELED.
4. `POST /admin/api/tasks/{urn}/deactivate` ⇒ INACTIVE; `/reactivate` ⇒ READY.
5. `DELETE` is **not** offered (GDPR rule TC-GDPR).
6. Changes appear in Task Center after the next DELTA pull (≤ 60 s) — verified in 006.

### US-005-3 Bulk generator (P2)
**Acceptance**: `POST /admin/api/tasks/generate {count, definitionLocalId?, recipients, sameTimestamp?:bool}` creates N tasks (≤ 10 000) with random realistic data; `sameTimestamp=true` gives them an identical `modifiedAt` to exercise `lastId` paging.

### US-005-4 Inspect (P1)
**Acceptance**
1. `GET /admin/api/tasks?status&definition&user&search&top&skip` lists tasks.
2. `GET /admin/api/users` and `/admin/api/groups` list SCIM data incl. Global User ID and flags (missing Global User ID highlighted).
3. `GET /admin/api/requests` (spec 001) shown as a live table (auto-refresh 5 s) with expandable redacted bodies.
4. `GET /admin/api/operations` lists `OperationLog`.
5. Diagnostics panel: "Get technical token" (calls own token endpoint with `tc-tech`), "Simulate pull" (calls SPI with that token and shows the JSON), "Decode assertion" (spec 002 T002-13).

### US-005-5 Admin UI (P1)
**Acceptance**: `GET /admin` serves `wwwroot/admin/index.html` (Basic auth) with tabs *Tasks · New task · Users · Groups · Requests · Operations · Diagnostics*; works in current Edge/Chrome; no external CDN (CSP `default-src 'self'`).

### US-005-6 "Open in App" task page (P1)
**Acceptance**
1. `GET /app/tasks/{urn}` renders subject, status, attributes, description, recipients (display names), operation history.
2. Prototype auth: Admin Basic auth (`App:Auth=Basic`). *(P2)* `App:Auth=IasOidc`: OIDC login against IAS (auth code + PKCE, cookie session); the logged-in user's `user_uuid` must be entitled; otherwise 403 page.
3. Page offers the same responses/actions as the definition (for users entitled under OIDC mode), posting to an internal endpoint that reuses the SPI domain logic — proving provider-side completion flows back to Task Center.

## Non-functional
- Admin UI total payload < 150 KB; no build tooling.
- All admin/app endpoints require auth; CSRF: admin API accepts only `application/json` with Basic auth header (no cookies) in Basic mode; OIDC mode uses antiforgery tokens.
