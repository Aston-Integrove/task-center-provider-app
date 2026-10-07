# Data Model — 004 Task Provider SPI

Schema `tc`. Timestamps `datetime2(3)` UTC. URN columns `varchar(300) COLLATE Latin1_General_100_BIN2` (ordinal ordering = "lexicographically greater").

## `tc.TaskDefinition`
| Column | Type | Notes |
|---|---|---|
| `Urn` | `varchar(300)` BIN2 PK | |
| `LocalId` | `varchar(64)` | unique |
| `NameJson` | `nvarchar(max)` | `[{languageCode,text}]` — default flag computed at read |
| `ResponsesJson` | `nvarchar(max)` | `ResponseDefinition[]` as SAP shape (names as LocalizedText lists) |
| `ActionsJson` | `nvarchar(max)` | `ActionDefinition[]` |
| `CustomAttributesJson` | `nvarchar(max)` | `CustomAttributeDefinition[]` |
| `CapabilitiesJson` | `nvarchar(max)` | |
| `TaskDetailsSettingsJson` | `nvarchar(max)` null | |
| `ModifiedAt` | `datetime2(3)` | |

Rationale: definitions are small, read-mostly, and returned whole — JSON columns keep the schema tiny.

## `tc.TaskInstance`
| Column | Type | Notes |
|---|---|---|
| `Urn` | `varchar(300)` BIN2 PK (nonclustered) | |
| `LocalId` | `varchar(64)` | unique |
| `DefinitionUrn` | `varchar(300)` BIN2 FK | |
| `Status` | `varchar(16)` | enum check constraint |
| `Priority` | `varchar(16)` | default `MEDIUM` |
| `SubjectJson` | `nvarchar(max)` | LocalizedText list |
| `DescriptionJson` | `nvarchar(max)` null | `[{languageCode, contentType, body}]` |
| `CreatedAt` / `CreatedBy` | `datetime2(3)` / `varchar(64)` null | Global User ID |
| `ModifiedAt` / `ModifiedBy` | `datetime2(3)` / `varchar(64)` null | |
| `Processor` | `varchar(64)` null | Global User ID |
| `DueAt` | `datetime2(3)` null | |
| `CompletedAt` / `CompletedBy` | `datetime2(3)` null / `varchar(64)` null | |
| `RowVersion` | `rowversion` | optimistic concurrency for actions/responses |

**Indexes**
- `IX_TaskInstance_Pull` **clustered** on `(ModifiedAt, Urn)` — the pull query is a pure range scan.
- `IX_TaskInstance_Processor` on `Processor` (filtered `WHERE Processor IS NOT NULL`).

## `tc.TaskRecipientUser`
`TaskUrn` (FK, BIN2) + `GlobalUserId varchar(64)` — PK both; index on `GlobalUserId`.

## `tc.TaskRecipientGroup`
`TaskUrn` + `GroupName nvarchar(256)` — PK both.

## `tc.TaskCustomAttribute`
`TaskUrn` + `Code varchar(64)` PK, `Value nvarchar(255)`.

## `tc.TaskOperationError`
`Id bigint identity`, `TaskUrn`, `ExecutedAt`, `Code varchar(64)`, `Message nvarchar(2000)`, `ExecutedBy varchar(64)`; latest per `(TaskUrn, ExecutedBy)` returned.

## `tc.OperationLog` (audit)
`Id bigint identity`, `TaskUrn`, `Kind` (`RESPONSE|ACTION`), `Code`, `Comment nvarchar(2000)`, `ReasonCode`, `UserId`, `At`, `Outcome` (`OK|REJECTED`), `ErrorCode`.

## `tc.LocalIdSequence`
`DefinitionLocalId varchar(64)`, `Day date`, `Next int` — for FR-SPI-02 (or use a SQL `SEQUENCE`).

## Pull query (EF Core / SQL)

```sql
SELECT TOP (@top) t.*
FROM tc.TaskInstance t
WHERE (t.ModifiedAt = @modifiedAfter AND t.Urn > @lastId)
   OR (t.ModifiedAt > @modifiedAfter)
ORDER BY t.ModifiedAt ASC, t.Urn ASC;
-- when lastId is null:  WHERE t.ModifiedAt >= @modifiedAfter
```
Child rows (recipients, groups, custom attributes, operation errors) are loaded with 4 set-based queries `WHERE TaskUrn IN (@page urns)` — never N+1.

## Domain ⇄ SPI mapping (Task)
| SPI property | Source |
|---|---|
| `urn`, `localId`, `definitionId` | `Urn`, `LocalId`, `DefinitionUrn` |
| `applicationId`, `applicationInstanceId`, `tenantId` | configuration |
| `status`, `priority` | columns |
| `subject` | `SubjectJson` filtered by `languages` + default rule |
| `createdAt/By`, `modifiedAt/By`, `processor`, `dueAt`, `completedAt/By` | columns |
| `uiLink` | computed |
| `customAttributes` | `TaskCustomAttribute` validated against definition |
| `recipientUsers` | `TaskRecipientUser` ∩ active SCIM users ∪ {processor} |
| `recipientGroups` | `TaskRecipientGroup` (null when empty) |
| `validResponseCodes`, `validActionCodes` | computed (spec US-004-4 #6–7) |
| `operationErrors` | latest per user from `TaskOperationError` (omitted when none) |
