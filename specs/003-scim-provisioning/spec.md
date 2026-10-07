# Feature 003 — SCIM 2.0 Provisioning (Users + Groups)

**Status**: Ready · **Priority**: P1 · **Depends on**: 001, 002 · **SAP rules**: TC-ID-01…06 · **Contract**: `contracts/scim.openapi.yaml` · **Standards**: RFC 7643, RFC 7644

## Summary
A minimal SCIM 2.0 service provider that SAP Cloud Identity Services – Identity Provisioning (IPS) can target to push IAS users and groups into the prototype, so that every user carries the IAS **Global User ID** used in all Task Center payloads.

## User stories

### US-003-1 Provision a user (P1)
As IPS, I create a user so tasks can be assigned to them by Global User ID.

**Acceptance**
1. Given a valid `scim` token, when IPS `POST /scim/v2/Users` with `userName`, `emails`, `name`, `active`, `externalId` and/or SAP extension `userUuid`, then `201` with the stored resource, `id`, `meta.resourceType=User`, `meta.created`, `meta.lastModified`, `meta.location`, and `Location` header.
2. Given the payload, then `GlobalUserId` is derived per FR-SCIM-07 and is visible in the admin console.
3. Given a user with the same `userName` already exists, then `409` with SCIM error `scimType: uniqueness`.
4. Given a payload with unknown attributes or extensions, then they are ignored (not an error) and preserved in `RawJson` for diagnostics.
5. Given no Global User ID can be derived, then `400` `scimType: invalidValue` with detail "Global User ID missing" **unless** `Scim:RequireGlobalUserId=false` (then the user is stored and flagged).

### US-003-2 Find / read users (P1)
**Acceptance**
1. `GET /scim/v2/Users?filter=userName eq "jdoe"` returns a `ListResponse` with `totalResults`, `startIndex`, `itemsPerPage`, `Resources`.
2. Filters supported: `eq`, `ne`, `co`, `sw`, `pr`, combined with `and`/`or`, on `userName`, `externalId`, `id`, `emails.value`, `emails[type eq "work"].value`, `displayName`, `active`, and `urn:ietf:params:scim:schemas:extension:sap:2.0:User:userUuid`. Attribute names case-insensitive; string comparison case-insensitive except `id`/`externalId`.
3. Paging via `startIndex` (1-based) and `count` (default 100, max 1000).
4. `GET /scim/v2/Users/{id}` returns `200` or `404`.
5. Unsupported filter ⇒ `400` `scimType: invalidFilter`.
6. `attributes` / `excludedAttributes` query params are accepted; `excludedAttributes=members` is honoured on Groups (others may be ignored).

### US-003-3 Update / deactivate / delete users (P1)
**Acceptance**
1. `PUT /scim/v2/Users/{id}` replaces mutable attributes; `200`.
2. `PATCH /scim/v2/Users/{id}` with `PatchOp` operations `add|replace|remove` (path optional, e.g. `active`, `emails[type eq "work"].value`, `name.familyName`) ⇒ `200` with resource (or `204` when `Scim:PatchReturnsNoContent`).
3. Setting `active=false` ⇒ user cannot obtain user tokens (spec 002) and is excluded from new task assignment; existing tasks unchanged.
4. `DELETE /scim/v2/Users/{id}` ⇒ `204`. GDPR handling (FR-SCIM-12): user is soft-deleted and anonymised; open tasks where they are the only recipient are **tombstoned** (`CANCELED`); they are removed from other tasks' recipient lists; `processor`/`createdBy` references replaced by `null`; all touched tasks get a new `modifiedAt`.
5. Changing a user's Global User ID via PUT/PATCH is rejected with `400 mutability` once tasks reference it.

### US-003-4 Groups (P1)
**Acceptance**
1. `POST /scim/v2/Groups` with `displayName`, optional `externalId`, `members[{value}]` ⇒ `201`.
2. `PATCH /scim/v2/Groups/{id}` supports `add`/`remove` on `members` (incl. `members[value eq "<id>"]` path) and `replace` on `displayName`.
3. `GET /Groups?filter=displayName eq "X"`; `excludedAttributes=members` omits members.
4. `PUT` and `DELETE` supported; deleting a group removes it from task recipient groups with `modifiedAt` bump.
5. Member `value` refers to our SCIM user `id`; unknown member ⇒ `400 invalidValue`.

### US-003-5 Discovery (P1)
**Acceptance**: `GET /ServiceProviderConfig`, `/ResourceTypes`, `/Schemas` return RFC 7643-compliant documents declaring: patch=true, bulk=false, filter=true (maxResults 1000), changePassword=false, sort=false, etag=false, authenticationSchemes=[oauthbearertoken, httpbasic (if enabled)].

## Functional requirements
- FR-SCIM-01 Base path `/scim/v2`; media type `application/scim+json` (also accept `application/json`).
- FR-SCIM-02 Schemas: core User `urn:ietf:params:scim:schemas:core:2.0:User`, Group `…:core:2.0:Group`, Enterprise extension (accepted, stored raw), SAP extension `urn:ietf:params:scim:schemas:extension:sap:2.0:User` (read `userUuid`, `userId`).
- FR-SCIM-03 Errors: `{ "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"], "status": "400", "scimType": "...", "detail": "..." }`.
- FR-SCIM-04 `id` is server-assigned (GUID). When `Scim:UseGlobalUserIdAsId=true` the `id` equals the Global User ID (simplifies IPS mapping) — default `false`.
- FR-SCIM-05 Stored attributes: `userName` (unique, case-insensitive), `externalId`, `displayName`, `name.givenName`, `name.familyName`, `emails[]` (value, type, primary), `active`, SAP `userUuid`, `RawJson`.
- FR-SCIM-06 `meta.version` not supported (no ETags).
- FR-SCIM-07 **Global User ID derivation** (configurable order `Scim:GlobalUserIdSources`, default): (1) SAP extension `userUuid`; (2) `externalId` if it is a GUID; (3) none. Stored normalised lower-case GUID string.
- FR-SCIM-08 Group `displayName` unique (case-insensitive) — it is the value used in `recipientGroups`.
- FR-SCIM-09 `IUserDirectory` service for other features: `FindByGlobalUserId`, `FindByEmail`, `IsMemberOfAnyGroup(userId, groupNames)`, `ListActive`.
- FR-SCIM-10 Every SCIM write is audited (`ScimAudit` table: op, resource, id, client_id, timestamp).
- FR-SCIM-11 Authentication: bearer token with scope `scim`; optional Basic (`Scim:AllowBasic`).
- FR-SCIM-12 GDPR delete behaviour as in US-003-3 #4, executed in one transaction.

## Non-functional
- NFR-003-01 1 000 users provisioned by IPS in < 5 min on Azure SQL Basic.
- NFR-003-02 Single SCIM operation p95 < 500 ms.

## Out of scope
Bulk endpoint, sorting, ETags, `/Me`, password management, nested groups, enterprise manager references.
