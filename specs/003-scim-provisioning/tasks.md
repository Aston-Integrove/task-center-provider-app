# Tasks — 003 SCIM 2.0

- [ ] **T003-01** EF entities + migration for `idm` schema (data-model.md). *Test*: integration — unique indexes enforced.
- [ ] **T003-02** [P] SCIM result helpers (`application/scim+json`, ListResponse, Error). *Test*: unit.
- [ ] **T003-03** [P] Discovery endpoints with static JSON. *Test*: contract test.
- [ ] **T003-04** `GlobalUserIdPolicy` (FR-SCIM-07). *Test*: unit — userUuid wins, GUID externalId fallback, non-GUID externalId rejected, normalisation.
- [ ] **T003-05** User mapping entity ⇄ JsonObject incl. SAP extension. *Test*: unit round-trip.
- [ ] **T003-06** `POST /Users`, `GET /Users/{id}`. *Test*: integration — acceptance 003-1.1–1.5.
- [ ] **T003-07** Filter parser + LINQ translation. *Test*: unit grammar table; integration — `userName eq`, `emails.value eq`, `externalId eq`, `and/or`.
- [ ] **T003-08** `GET /Users` list with paging. *Test*: integration — startIndex/count, totalResults.
- [ ] **T003-09** `PUT /Users/{id}`. *Test*: integration incl. Global User ID mutability rule.
- [ ] **T003-10** `ScimPatchApplier` + `PATCH /Users/{id}`. *Test*: unit (RFC examples) + integration (`active=false`).
- [ ] **T003-11** `DELETE /Users/{id}` + `GdprUserEraser` (needs T004-02 repo; stub interface first). *Test*: integration — tasks tombstoned/anonymised with new `modifiedAt`.
- [ ] **T003-12** Groups CRUD + membership PATCH (incl. `members[value eq "x"]` remove). *Test*: integration.
- [ ] **T003-13** `IUserDirectory` implementation. *Test*: integration — group membership lookup.
- [ ] **T003-14** SCIM audit table writes. *Test*: integration.
- [ ] **T003-15** Optional Basic auth for SCIM. *Test*: integration both modes.
- [ ] **T003-16** Spike S-03 support: capture IPS payloads → `tests/fixtures/ips/`; replay test. *Test*: replay suite green.
