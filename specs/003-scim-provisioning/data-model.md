# Data Model — 003 SCIM

All tables in schema `idm`. Timestamps `datetime2(3)` UTC.

## `idm.ScimUser`
| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` PK | SCIM `id` |
| `UserName` | `nvarchar(256)` | unique (CI collation) |
| `ExternalId` | `nvarchar(256)` null | |
| `GlobalUserId` | `varchar(64)` null | **unique filtered index** (`WHERE GlobalUserId IS NOT NULL`); lower-case GUID |
| `DisplayName` | `nvarchar(256)` null | |
| `GivenName` | `nvarchar(128)` null | |
| `FamilyName` | `nvarchar(128)` null | |
| `PrimaryEmail` | `nvarchar(320)` null | index; denormalised from emails |
| `EmailsJson` | `nvarchar(max)` | `ISJSON` check |
| `Active` | `bit` | default 1 |
| `IsDeleted` | `bit` | soft delete (GDPR) |
| `RawJson` | `nvarchar(max)` | last payload, redacted on delete |
| `Created`, `LastModified` | `datetime2(3)` | |

## `idm.ScimGroup`
| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` PK | |
| `DisplayName` | `nvarchar(256)` | unique CI; = `recipientGroups` value |
| `ExternalId` | `nvarchar(256)` null | |
| `Created`, `LastModified` | `datetime2(3)` | |

## `idm.ScimGroupMember`
| Column | Type | Notes |
|---|---|---|
| `GroupId` | FK → ScimGroup | PK part |
| `UserId` | FK → ScimUser | PK part; index on `UserId` |

## `idm.ScimAudit`
`Id bigint identity`, `At datetime2(3)`, `ClientId varchar(64)`, `Operation varchar(16)`, `ResourceType varchar(16)`, `ResourceId uniqueidentifier`, `Summary nvarchar(400)`.

## Mapping SCIM ⇄ columns
| SCIM path | Column |
|---|---|
| `id` | `Id` |
| `userName` | `UserName` |
| `externalId` | `ExternalId` |
| `displayName` | `DisplayName` |
| `name.givenName` / `name.familyName` | `GivenName` / `FamilyName` |
| `emails` | `EmailsJson`, primary → `PrimaryEmail` |
| `active` | `Active` |
| `urn:ietf:params:scim:schemas:extension:sap:2.0:User.userUuid` | → `GlobalUserId` (FR-SCIM-07) |
| `meta.created` / `meta.lastModified` | `Created` / `LastModified` |
