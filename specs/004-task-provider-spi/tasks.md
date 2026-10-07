# Tasks — 004 Task Provider SPI (MVP)

### Phase A — model & persistence
- [ ] **T004-01** `Urn` value object (build/parse/validate, max lengths). *Test*: unit.
- [ ] **T004-02** EF entities + migration for `tc` schema incl. BIN2 collation, clustered pull index, rowversion. *Test*: integration — ordering of `urn:…:A` vs `urn:…:a` vs `urn:…:_` is ordinal.
- [ ] **T004-03** [P] `LocalizedTextSelector` (languages filter, exactly-one isDefault, fallback). *Test*: unit table.
- [ ] **T004-04** [P] `CustomAttributeValidator` (7 types, SAP regexes). *Test*: unit table.
- [ ] **T004-05** [P] `OperationRules` (valid codes, comment/reason requirements) + `Entitlement`. *Test*: unit table incl. group membership + processor rules.
- [ ] **T004-06** `TaskInstance` aggregate (`Respond`, `ExecuteAction` for claim/release/increasePriority, `Cancel`, monotonic `Touch`). *Test*: unit.
- [ ] **T004-07** `DefinitionSeeder` + `seed/task-definitions.json` (3 definitions, en-US + de-DE). *Test*: integration idempotent upsert.

### Phase B — pull endpoints (POC scope)
- [ ] **T004-08** SPI route groups for both base paths + auth policies + `SpiErrors` (resx en/de). *Test*: integration — both prefixes reachable; 401/403 shapes.
- [ ] **T004-09** `GET /capabilities`. *Test*: contract + exact payload.
- [ ] **T004-10** `GET /taskDefinitions` (+ param validation) and `GET /taskDefinitions/{urn}`. *Test*: integration acceptance 004-2; contract.
- [ ] **T004-11** `TaskRepository.Pull` + batched child loading. *Test*: integration acceptance 004-3.1–3.6; SQL shape check.
- [ ] **T004-12** Property test: random tasks with heavy timestamp collisions; paging with random `$top` visits each exactly once. *Test*: FsCheck / custom generator.
- [ ] **T004-13** `SpiMapper` (recipients ∩ active users, valid codes, uiLink, explicit nulls, ms timestamps). *Test*: golden snapshot + contract.
- [ ] **T004-14** `GET /tasks/{urn}` (raw + encoded URN). *Test*: integration.

### Phase C — user-context endpoints (MVP scope)
- [ ] **T004-15** `GET /tasks/{urn}/description` with Accept-Language + Content-Language. *Test*: integration acceptance 004-6.
- [ ] **T004-16** `POST /tasks/{urn}/response`. *Test*: integration acceptance 004-7.1–7.8.
- [ ] **T004-17** `POST /tasks/{urn}/action`. *Test*: integration acceptance 004-8.
- [ ] **T004-18** Concurrency handling (rowversion retry → 409). *Test*: integration with two parallel responses.
- [ ] **T004-19** `501` for unsupported SPI paths. *Test*: integration.

### Phase D — quality
- [ ] **T004-20** Contract test harness over `TaskProviderV2.json` for E1–E8. *Test*: CI job.
- [ ] **T004-21** k6 pull performance test (10 000 tasks). *Test*: p95 < 2 s.
- [ ] **T004-22** *(P3)* `Spi:AsyncResponses` 202 mode + simulated failures → `operationErrors`. *Test*: integration.
