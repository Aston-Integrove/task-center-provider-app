# Feature 006 — End-to-End Integration with SAP BTP (Spikes + Acceptance)

**Status**: Ready · **Priority**: P1 · **Depends on**: all · **Guide**: `docs/btp-setup-guide.md`

## Summary
Prove the whole chain in Valterra's BTP landscape: IAS → IPS → SCIM → our user store; Task Center → destinations → our token service → SPI; business user approves in Task Center.

## Spikes (week 1–2, time-boxed)

| ID | Question | Method | Time-box | Exit |
|---|---|---|---|---|
| **S-01** | Which path does Task Center call (`/task-provider/v2/...` vs `/api/...` vs bare)? Which headers/params (`languages`, `$top`) does it send? | Deploy skeleton with SPI stubs returning `{value:[]}`; create `INTEGROVE_TP`; read request log | 0.5 d | Destination URL and SPI base confirmed; ADR updated |
| **S-02** | Does the `_PP` destination with `OAuth2JWTBearer` reach our token endpoint with a user assertion, which issuer/claims, and does Task Center use it for description/response? | (a) Destination service REST "find destination" with `X-user-token` from a test app; (b) open a task in Task Center; inspect token endpoint logs (`LogAssertionClaims`) | 1 d | Trusted issuer + `UserIdClaims` configured, or decision to implement SAML fallback (T002-14) |
| **S-03** | What exactly does IPS send to a generic SCIM target and where is the Global User ID? | IPS Read job then Provision job for 2 users + 1 group; capture bodies | 1 d | Transformation finalised; fixtures committed; 100 % users have Global User ID |

## End-to-end acceptance scenarios

| # | Scenario | Expected |
|---|---|---|
| E2E-01 | IPS provisions users U1, U2 and group G1 (U1, U2) | Admin console shows both with Global User IDs equal to IAS user UUIDs |
| E2E-02 | Destination saved | Task Center admin: definitions pulled (3), INITIAL pull successful |
| E2E-03 | Admin creates PR task for U1 | Within 60 s, U1 sees it in Task Center under *Integrove*; U2 does not |
| E2E-04 | Task assigned to group G1 | U1 and U2 both see it |
| E2E-05 | U1 opens task | Header, custom attributes (Amount, Currency…) and HTML description shown |
| E2E-06 | U1 switches language to German (if `de-DE` configured) | Subject/definition/description in German |
| E2E-07 | U1 claims group task | U2 no longer sees it after delta pull; U1 sees *Release* instead of *Claim* |
| E2E-08 | U1 approves | Task leaves inbox; provider shows COMPLETED, `completedBy`=U1 GUID; OperationLog OK |
| E2E-09 | U1 rejects without comment | Task Center prompts for comment (REQUIRED); with comment ⇒ completed |
| E2E-10 | U2 tries to act on a task reserved by U1 (via API replay) | 403 with readable message |
| E2E-11 | Admin changes priority / subject | Updated in Task Center within 60 s |
| E2E-12 | Admin cancels task | Disappears from Task Center within 60 s |
| E2E-13 | U2 deactivated in IAS → IPS sync | U2 gets no new tasks; PP token for U2 rejected |
| E2E-14 | U2 deleted (SCIM DELETE) | Tasks only for U2 → CANCELED; U2 removed from shared tasks |
| E2E-15 | "Open in App" | `/app/tasks/{urn}` opens in new tab |
| E2E-16 | 10 000 generated tasks for U1 | INITIAL/DELTA completes, no missing/duplicate tasks (count check in TC admin export vs provider) |
| E2E-17 | Performance | Pull page p95 < 2 s; description/response p95 < 800 ms (from request log durations) |

## Exit / sign-off
All E2E scenarios pass (screenshots + request-log excerpts in `docs/evidence/`), monthly cost ≤ USD 25, setup guide validated by a second person.
