# Risks, Assumptions and Open Questions

Each item is closed by a **spike** (S-xx) in `specs/006-e2e-integration/tasks.md` or a decision. Do the spikes in week 1 — they de-risk everything else.

## Risks

| ID | Risk | Impact | Likelihood | Mitigation / spike |
|---|---|---|---|---|
| R-01 | Task Center's `_PP` flow may only be validated with `OAuth2SAMLBearerAssertion` (guide calls SAML "standard"; alternatives "can be requested"). `OAuth2JWTBearer` might not be accepted, or the user JWT passed by Task Center may be an XSUAA token whose claims differ from IAS. | No in-Task-Center approvals | Medium | **S-02**: deploy token service early with assertion logging; test `_PP` with `OAuth2JWTBearer` using the Destination service "Check connection"/find-destination API with a user token. Fallback: implement `saml2-bearer` grant (spec 002 FR-PP-10, ~2 days). |
| R-02 | The Global User ID claim name in the incoming assertion is unknown (`user_uuid` expected for IAS and IAS-federated XSUAA tokens). | Wrong/unknown user | Medium | Ordered claim list `OAuth__UserIdClaims`; fallback lookup by e-mail against SCIM store; log claim names (not values) on S-02. |
| R-03 | Which base path Task Center appends to the destination URL (`/task-provider/v2` vs `/api/task-provider/v2`). | 404 on pulls | Low | Serve both prefixes; request log shows actual path (**S-01**). |
| R-04 | IPS generic SCIM target behaviour (attribute names, PATCH vs PUT, filter syntax, how Global User ID is transferred). | Users without Global User ID | Medium | **S-03**: run IPS "Read/Provision" in test mode against the request log; adjust transformation (see `docs/btp-setup-guide.md` §4). |
| R-05 | URN contains `:`; some proxies/clients percent-encode path segments. | 404 on single-task calls | Low | Route `{**taskUrn}` catch-all + `Uri.UnescapeDataString`; tests with encoded and raw URNs. |
| R-06 | Container Apps cold start if min replicas = 0 → token call timeouts. | Failed pulls | Low | min replicas 1 during testing. |
| R-07 | Timestamp precision mismatch (SQL `datetime2(7)` vs ms in API) causing lost/duplicate tasks in paging. | Data loss in cache | Medium | Store `datetime2(3)`, truncate on write, property tests (constitution III). |
| R-08 | Valterra network / BTP region egress restrictions to Azure. | Connectivity | Low | Public HTTPS; optional IP allow-list later. |

## Assumptions

- A-01 Valterra has a BTP subaccount with SAP Task Center + SAP Build Work Zone, and IAS is the trusted IdP (ref: guide p.23, p.27).
- A-02 An IPS tenant is available (bundled with Cloud Identity Services) with admin access.
- A-03 Test users exist in IAS with e-mail and display name.
- A-04 Only one language (`en-US`) is configured initially; `de-DE` sample texts exist to test the translation logic.
- A-05 Prototype data is synthetic; no real Valterra business data.

## Open questions (for Valterra / SAP)

| ID | Question | Owner | Needed by |
|---|---|---|---|
| Q-01 | BTP region and subaccount subdomain (for XSUAA issuer allow-list)? | Valterra BTP admin | Week 1 |
| Q-02 | IAS tenant URL and whether Task Center users authenticate via IAS directly or IAS → corporate IdP (Entra ID)? | Valterra IAM | Week 1 |
| Q-03 | Destination naming convention — proposed `INTEGROVE_TP` / `INTEGROVE_TP_PP` (≤ 16 chars). | Valterra BTP admin | Week 1 |
| Q-04 | Is an SAP Customer Influence request already open for JWT-bearer PP? | Integrove / SAP | If S-02 fails |
| Q-05 | Which group(s) in IAS should be provisioned for `recipientGroups` testing? | Valterra IAM | Week 2 |
