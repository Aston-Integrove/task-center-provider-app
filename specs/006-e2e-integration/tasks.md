# Tasks — 006 End-to-End Integration

### Week 1 — de-risk
- [ ] **T006-01** Collect Q-01…Q-05 answers from Valterra (region, subdomain, IAS tenant, destination names, test groups).
- [ ] **T006-02** Create IAS test users `tc.proto.u1`, `tc.proto.u2` and groups `TC_PROTO_USERS`, `TC_PROTO_APPROVERS`.
- [ ] **T006-03** **S-01** path/params spike (needs T001-*, T002-05, SPI stubs). Record in `docs/evidence/S-01.md`.
- [ ] **T006-04** **S-02** PP spike (needs T002-10, T002-13). Record claim names, issuer. Decide JWT vs SAML; update ADR-004.

### Week 2 — users
- [ ] **T006-05** Configure IPS source/target/transformation per setup guide §4.
- [ ] **T006-06** **S-03** IPS spike; commit fixtures; finalise `Scim:GlobalUserIdSources`.
- [ ] **T006-07** Run E2E-01, E2E-13.

### Week 3 — POC scope in Task Center
- [ ] **T006-08** Configure `INTEGROVE_TP` with final URL; verify E2E-02.
- [ ] **T006-09** Run E2E-03, 04, 11, 12, 15.

### Week 4 — MVP scope
- [ ] **T006-10** Configure `INTEGROVE_TP_PP`; run E2E-05 … 10.
- [ ] **T006-11** Run E2E-14 (GDPR) and E2E-16/17 (volume, performance).
- [ ] **T006-12** Cost check in Azure Cost Management; set budget alert at USD 25.
- [ ] **T006-13** Evidence pack + demo; list production gaps (HA, custom domain, IP restrictions, SAML, private endpoints, monitoring alerts, attachments/comments, bulk).
