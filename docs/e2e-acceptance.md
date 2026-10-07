# End-to-end acceptance (spec 006)

Run after the spikes ([evidence/](evidence/README.md)). Fill the *Result* column with `pass` / `fail` plus a link or
filename in `docs/evidence/` (screenshots, request-log excerpts with correlation ids). **No scenario has been run yet.**

## Pre-flight

- [ ] `azd up` done; `GET https://<fqdn>/healthz/ready` returns 200 (database and Key Vault reachable).
- [ ] Destinations `INTEGROVE_TP` and `INTEGROVE_TP_PP` created, S-01 and S-02 completed.
- [ ] IPS provisioning of `tc.proto.u1`, `tc.proto.u2`, group `TC_PROTO_USERS` done (S-03).
- [ ] Admin password: `az keyvault secret show --vault-name <kv> --name admin-password --query value -o tsv`.

## Scenarios

| # | Scenario | How to drive and verify it with this repository | Result |
|---|---|---|---|
| E2E-01 | IPS provisions U1, U2 and group G1 | Admin *Users*: both present, Global User IDs equal the IAS user UUIDs, no highlighted row; *Groups*: G1 has 2 members | |
| E2E-02 | Destination saved | Task Center admin: 3 definitions pulled, INITIAL pull successful; *Requests* shows `GET /taskDefinitions` then `GET /tasks` | |
| E2E-03 | Admin creates a PR task for U1 | *New task* (users = U1); within 60 s U1 sees it under the connector, U2 does not | |
| E2E-04 | Task assigned to group G1 | *New task* (groups = G1); U1 and U2 both see it | |
| E2E-05 | U1 opens the task | Header, custom attributes (Amount, Currency, ...) and description shown; *Requests*: `POST /oauth/token` (jwt-bearer) + `GET .../description` | |
| E2E-06 | U1 switches to German | Subject, definition, description in German (needs `de-DE` in Task Center language settings) | |
| E2E-07 | U1 claims a group task | U2 no longer sees it after the next delta pull; U1 sees *Release* instead of *Claim* | |
| E2E-08 | U1 approves | Task leaves the inbox; admin task detail: `COMPLETED`, `completedBy` = U1 GUID; *Operations*: `RESPONSE approve OK` | |
| E2E-09 | U1 rejects without a comment | Task Center prompts for the required comment; with comment -> completed | |
| E2E-10 | U2 acts on a task reserved by U1 (API replay) | `POST /tasks/{urn}/action` with U2's token -> `403 tcp.spi.reservedByOther`, readable message | |
| E2E-11 | Admin changes priority / subject | *Tasks* -> task detail, or `PATCH /admin/api/tasks/{urn}`; updated in Task Center within 60 s | |
| E2E-12 | Admin cancels a task | *Cancel*; disappears from Task Center within 60 s (tombstone still served by `GET /tasks`) | |
| E2E-13 | U2 deactivated in IAS, IPS sync | U2 gets no new tasks (`400 invalid_grant` for U2's token exchange in *Requests*) | |
| E2E-14 | U2 deleted (SCIM `DELETE`) | Tasks only for U2 -> `CANCELED`; U2 removed from shared tasks; `modifiedAt` bumped (see `GdprAndGroupDeletionTests` for the expected shape) | |
| E2E-15 | "Open in App" | `/app/tasks/{urn}` opens in a new tab (Basic mode: admin credentials; IAS mode: SSO) | |
| E2E-16 | 10 000 generated tasks for U1 | *Bulk generator* count 10000 (also once with identical `modifiedAt`); INITIAL/DELTA completes; count in Task Center export equals *Tasks* total; run `tests/perf/k6-pull.js` | |
| E2E-17 | Performance | Pull page p95 < 2 s (`k6-pull.js`); description/response p95 < 800 ms from *Requests* `durationMs` | |

## Sign-off

- [ ] All scenarios pass; evidence in `docs/evidence/`.
- [ ] Monthly cost <= USD 25 (Azure Cost Management; the budget alert from `infra/modules/budget.bicep` has not fired).
- [ ] [btp-setup-guide.md](btp-setup-guide.md) validated by a second person.
- [ ] Production gaps reviewed: [production-gaps.md](production-gaps.md).
