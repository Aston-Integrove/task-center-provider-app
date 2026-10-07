# Tasks — 005 Admin Console

- [x] **T005-01** `POST /admin/api/tasks` incl. recipient resolution, attribute validation, subject uniqueness. *Test*: integration acceptance 005-1.
- [x] **T005-02** [P] HTML sanitizer for descriptions. *Test*: unit — script/onerror/iframe stripped, tables kept.
- [x] **T005-03** `PATCH /admin/api/tasks/{urn}`, `/complete`, `/cancel`, `/deactivate`, `/reactivate`. *Test*: integration acceptance 005-2.
- [x] **T005-04** `GET /admin/api/tasks` with filters. *Test*: integration.
- [x] **T005-05** [P] `GET /admin/api/users`, `/groups`, `/operations`. *Test*: integration.
- [x] **T005-06** Diagnostics endpoints (tech token, simulate pull, decode assertion). *Test*: integration.
- [x] **T005-07** Admin UI (`index.html`, `admin.js`, `admin.css`) — tabs per US-005-5. *Test*: Playwright smoke.
- [x] **T005-08** `/app/tasks/{urn}` page (Basic mode). *Test*: integration renders subject + encoded output (XSS test).
- [x] **T005-09** Bulk generator with `sameTimestamp`. *Test*: integration — 2 500 tasks, single timestamp; pulled exactly once in pages of 1000.
- [x] **T005-10** *(P2)* IAS OIDC mode for `/app` + provider-side respond/act buttons. *Test*: integration with fake OIDC server; manual with IAS.
  - _Status_: automated against an in-memory IAS (discovery, JWKS, token endpoint, PKCE, nonce); the manual run against the real IAS tenant belongs to T006-08._
