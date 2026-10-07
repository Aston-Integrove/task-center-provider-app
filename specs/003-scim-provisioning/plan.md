# Plan — 003 SCIM 2.0

## Approach
Hand-rolled, small SCIM layer (no heavy SCIM framework) — the subset is small and we need precise control of IPS quirks.

```
Tcp.Api/Endpoints/Scim/
  ScimDiscoveryEndpoints.cs   # ServiceProviderConfig, ResourceTypes, Schemas (static JSON resources)
  ScimUsersEndpoints.cs
  ScimGroupsEndpoints.cs
  ScimResults.cs              # application/scim+json results, ListResponse, Error
Tcp.Domain/Identity/
  ScimUser.cs, ScimGroup.cs, GlobalUserIdPolicy.cs
Tcp.Infrastructure/Scim/
  Filter/ScimFilterParser.cs  # recursive-descent parser → AST
  Filter/ScimFilterToLinq.cs  # AST → Expression<Func<ScimUser,bool>> (whitelisted attributes)
  Patch/ScimPatchApplier.cs   # PatchOp over a JsonNode representation, then re-map to entity
  UserDirectory.cs            # IUserDirectory
  GdprUserEraser.cs           # FR-SCIM-12, calls ITaskRepository (spec 004)
```

## Key decisions
1. **Representation**: endpoints map entity ⇄ `JsonObject` (System.Text.Json nodes). PATCH is applied to the `JsonObject` of the current resource and the result re-validated through the same mapping as PUT — one code path for validation.
2. **Filter grammar** (subset of RFC 7644 §3.4.2.2):
   ```
   filter  := or
   or      := and ("or" and)*
   and     := unary ("and" unary)*
   unary   := "not"? primary
   primary := "(" filter ")" | attrExp
   attrExp := attrPath "pr" | attrPath compOp compValue
   attrPath:= [schemaUrn ":"] name ("." sub)? | name "[" filter "]" ("." sub)?
   compOp  := eq|ne|co|sw|ew|gt|ge|lt|le
   ```
   Only whitelisted attribute paths translate to LINQ; anything else ⇒ `invalidFilter`.
3. **IPS compatibility**: IPS typically (a) searches by `userName eq`, (b) creates with POST, (c) updates with PUT or PATCH, (d) deletes with DELETE, (e) manages membership with PATCH on Groups. Spike S-03 verifies; request log captures bodies.
4. **Group member references**: accept `members[].value` = SCIM user `id`; if `Scim:AcceptGlobalUserIdAsMemberValue=true`, also resolve by Global User ID.
5. **GDPR delete** is one transaction across `idm` and `tc` schemas using `ITaskRepository.AnonymiseUser(globalUserId, now)`.
6. **Collation**: `UserName`, `DisplayName` use `SQL_Latin1_General_CP1_CI_AS`; `GlobalUserId` binary.

## Test plan
- Unit: filter parser (≥ 30 cases incl. precedence, quoted strings with escapes, URN-qualified attributes); patch applier (RFC 7644 examples); Global User ID policy.
- Integration (Testcontainers): full CRUD for Users/Groups; uniqueness; GDPR delete effect on tasks.
- Contract: responses vs `contracts/scim.openapi.yaml`.
- Replay test: recorded IPS request bodies from S-03 saved under `tests/fixtures/ips/*.json` and replayed in CI.
