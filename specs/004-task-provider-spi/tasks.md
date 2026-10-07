# Tasks — 004 Task Provider SPI (MVP)

### Phase A — model & persistence
- [x] **T004-01** `Urn` value object (build/parse/validate, max lengths). *Test*: unit.
- [x] **T004-02** EF entities + migration for `tc` schema incl. BIN2 collation, clustered pull index, rowversion. *Test*: integration — ordering of `urn:…:A` vs `urn:…:a` vs `urn:…:_` is ordinal.
- [x] **T004-03** [P] `LocalizedTextSelector` (languages filter, exactly-one isDefault, fallback). *Test*: unit table.
- [x] **T004-04** [P] `CustomAttributeValidator` (7 types, SAP regexes). *Test*: unit table.
- [x] **T004-05** [P] `OperationRules` (valid codes, comment/reason requirements) + `Entitlement`. *Test*: unit table incl. group membership + processor rules.
- [x] **T004-06** `TaskInstance` aggregate (`Respond`, `ExecuteAction` for claim/release/increasePriority, `Cancel`, monotonic `Touch`). *Test*: unit.
- [x] **T004-07** `DefinitionSeeder` + `seed/task-definitions.json` (3 definitions, en-US + de-DE). *Test*: integration idempotent upsert.

### Phase B — pull endpoints (POC scope)
- [x] **T004-08** SPI route groups for both base paths + auth policies + `SpiErrors` (resx en/de). *Test*: integration — both prefixes reachable; 401/403 shapes.
- [x] **T004-09** `GET /capabilities`. *Test*: contract + exact payload.
- [x] **T004-10** `GET /taskDefinitions` (+ param validation) and `GET /taskDefinitions/{urn}`. *Test*: integration acceptance 004-2; contract.
- [x] **T004-11** `TaskRepository.Pull` + batched child loading. *Test*: integration acceptance 004-3.1–3.6; SQL shape check.
- [x] **T004-12** Property test: random tasks with heavy timestamp collisions; paging with random `$top` visits each exactly once. *Test*: FsCheck / custom generator.
- [x] **T004-13** `SpiMapper` (recipients ∩ active users, valid codes, uiLink, explicit nulls, ms timestamps). *Test*: golden snapshot + contract.
- [x] **T004-14** `GET /tasks/{urn}` (raw + encoded URN). *Test*: integration.

### Phase C — user-context endpoints (MVP scope)
- [x] **T004-15** `GET /tasks/{urn}/description` with Accept-Language + Content-Language. *Test*: integration acceptance 004-6.
- [x] **T004-16** `POST /tasks/{urn}/response`. *Test*: integration acceptance 004-7.1–7.8.
- [x] **T004-17** `POST /tasks/{urn}/action`. *Test*: integration acceptance 004-8.
- [x] **T004-18** Concurrency handling (rowversion retry → 409). *Test*: integration with two parallel responses.
- [x] **T004-19** `501` for unsupported SPI paths. *Test*: integration.

### Phase D — quality
- [x] **T004-20** Contract test harness over `TaskProviderV2.json` for E1–E8. *Test*: CI job.
- [~] **T004-21** k6 pull performance test (10 000 tasks). *Test*: p95 < 2 s.
  - _Status_: `tests/perf/k6-pull.js` is written (10 000 tasks, exactly-once + p95 check) but needs k6 and a deployed instance; the in-process `SpiPerformanceTests` (5 000 rich tasks, every page < 2 s) runs in CI._
- [x] **T004-22** *(P3)* `Spi:AsyncResponses` 202 mode + simulated failures → `operationErrors`. *Test*: integration.
