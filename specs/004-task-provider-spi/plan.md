# Plan — 004 Task Provider SPI (MVP)

## Structure
```
Tcp.Domain/Tasks/
  TaskInstance.cs          # aggregate: Respond(), ExecuteAction(), Cancel(), Touch(clock)
  TaskDefinition.cs        # operation lookup, custom attribute types
  Entitlement.cs           # FR-SPI-AUTH
  OperationRules.cs        # validActionCodes / validResponseCodes, comment/reason checks
  CustomAttributeValidator.cs
  LocalizedTextSelector.cs # languages + isDefault rule
  Urn.cs                   # build/parse/validate
Tcp.Infrastructure/Tasks/
  TaskRepository.cs        # pull query, get by urn, save with RowVersion
  DefinitionSeeder.cs
Tcp.Api/Endpoints/Spi/
  SpiRouteGroup.cs         # maps both bases: /task-provider/v2 and /api/task-provider/v2
  CapabilitiesEndpoint.cs, TaskDefinitionsEndpoints.cs, TasksEndpoints.cs,
  DescriptionEndpoint.cs, OperationEndpoints.cs, NotImplementedEndpoints.cs
  SpiMapper.cs             # domain → SAP DTOs
  SpiErrors.cs             # Error body + localisation (Resources/SpiMessages.resx, .de.resx)
  Dtos/                    # generated from contracts/spi-mvp.openapi.json (NSwag/Kiota models only) or hand-written records
```

## Routing
```csharp
foreach (var basePath in new[] { "/task-provider/v2", "/api/task-provider/v2" })
{
    var spi = app.MapGroup(basePath).RequireAuthorization("spi.any").WithTags("SPI");
    spi.MapGet("/capabilities", CapabilitiesEndpoint.Get);
    spi.MapGet("/taskDefinitions", TaskDefinitionsEndpoints.List);
    spi.MapGet("/taskDefinitions/{**taskDefinitionUrn}", TaskDefinitionsEndpoints.Get);
    spi.MapGet("/tasks", TasksEndpoints.Pull);
    spi.MapGet("/tasks/{**rest}", TasksEndpoints.Dispatch);   // see below
    spi.MapPost("/tasks/{**rest}", OperationEndpoints.Dispatch);
    spi.MapPost("/bulkOperation", NotImplementedEndpoints.Handle);
}
```
`{**rest}` catch-all lets us split `urn…/description|/response|/action|/details|/attachments…` from the right (`LastIndexOf('/')` after the URN prefix), avoiding routing ambiguity with colons and encoded slashes. URN = `Uri.UnescapeDataString(segment)`; validate with `Urn.TryParse`.

## Pull implementation
- Parse/validate parameters → `PullQuery(modifiedAfter, lastId, top, languages)`.
- EF Core LINQ:
  ```csharp
  q = db.Tasks.Where(t => t.ModifiedAt > after || (t.ModifiedAt == after && string.Compare(t.Urn, lastId) > 0))
              .OrderBy(t => t.ModifiedAt).ThenBy(t => t.Urn).Take(top).AsNoTracking();
  ```
  Verify generated SQL in an integration test (log capture) — must use `>`/`=` on the BIN2 column; otherwise use `FromSql`.
- Load children in 4 batched queries; map; serialize.

## Operations
- Load task with `RowVersion`; check entitlement → definition operation → rules → mutate aggregate → save. `DbUpdateConcurrencyException` ⇒ reload once and re-evaluate; still conflicting ⇒ `409 tcp.spi.concurrentUpdate`.
- `OperationLog` written in the same transaction (success) or separate transaction (rejections).
- `IClock` abstraction for deterministic tests; `Touch()` implements FR-SPI-03.

## Serialization
`System.Text.Json` with `JsonSerializerDefaults.Web`; custom `UtcMillisecondConverter` for `DateTime` (`yyyy-MM-ddTHH:mm:ss.fffZ`); DTO properties that must emit explicit `null` are annotated `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`, global default `WhenWritingNull`.

## Contract testing
- `contracts/spi-mvp.openapi.json` is generated from `TaskProviderV2.json` by `tools/make-mvp-contract.py` (paths filtered, components retained).
- Contract tests load the SAP file with `Microsoft.OpenApi.Readers` and validate JSON responses via JSON Schema (`JsonSchema.Net` after converting OAS 3.0 schema → JSON Schema draft 2020-12, handling `nullable`).
- Known SAP contract defect: `IFrameUISettings.urlParams` and `DetailsParameters.path` declare `default: null` without `nullable: true` (strict OAS 3.0 validators fail on the SAP file). The generator marks them nullable in `spi-mvp.openapi.json` and records the deviation in `info.description`; the SAP file stays untouched. Seed definitions are validated against the `TaskDefinition` schema in CI (already passing).
- Golden tests: snapshot of `GET /tasks` for seeded data (Verify.Xunit).

## Performance test
`tests/perf/k6-pull.js`: seed 10 000 tasks via admin bulk generator (spec 005), then loop pages of 1000 with `modifiedAfter/lastId` exactly like Task Center; assert p95 < 2 s and total distinct = 10 000.
