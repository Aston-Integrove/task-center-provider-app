# Evidence pack

Spec 006 is run in Valterra's SAP landscape; this folder is where its results go. Nothing here has been filled in yet:
the templates below say exactly what to capture and with which tool of this repository, so the spikes take hours, not days.

| File | Task | Question it answers |
|---|---|---|
| [S-01.md](S-01.md) | T006-03 | Which path, parameters and headers does Task Center really send? |
| [S-02.md](S-02.md) | T006-04 | Does the `_PP` destination reach us with a user assertion, which issuer/claims, JWT or SAML? |
| [S-03.md](S-03.md) | T006-06 | What does IPS send to a generic SCIM target and where is the Global User ID? |
| [../e2e-acceptance.md](../e2e-acceptance.md) | T006-07..11 | The 17 end-to-end scenarios with a result column |

## Rules for evidence

1. **Redact before committing.** The request log already masks `Authorization`, `client_secret`, `assertion` and token fields,
   but check names, e-mail addresses and tenant URLs of real people before a file is committed. Use placeholders.
2. **Raw captures are fixtures.** Whatever IPS sends goes to `tests/fixtures/ips/` (see the README there) so CI replays it forever.
3. **A result is a screenshot or log excerpt plus the correlation id** (`X-Correlation-Id`, visible in the Requests tab and in
   Log Analytics), so any claim can be traced to the exact requests.
4. **If the outcome contradicts a spec, change the spec** (and its ADR) in the same pull request.

## Tools you will use

| Need | Tool |
|---|---|
| What did SAP send? | Admin console, tab *Requests*; or `GET /admin/api/requests?prefix=/task-provider` |
| Save IPS traffic as fixtures | `GET /admin/api/requests/export?prefix=/scim` |
| Inspect a user JWT from BTP | Admin console, tab *Diagnostics* -> *Decode assertion*; log claim names with `Diagnostics__LogAssertionClaims=true` |
| Get the token Task Center would get | Admin console, *Diagnostics* -> *Get technical token* |
| Replay a Task Center pull | *Diagnostics* -> *Simulate pull*, or `tests/perf/k6-pull.js` |
| Create tasks / 10 000 tasks | Admin console, *New task* / *Bulk generator* |
| Cost | Azure Cost Management; budget alert is deployed by `infra/modules/budget.bicep` |
