# Delivery Plan — Spec-Driven Build

## 1. How we work (GitHub Spec Kit)

```
constitution ──► spec.md ──► plan.md ──► tasks.md ──► implement ──► verify against spec
   (rules)        (what/why)   (how)      (ordered,     (TDD, one     (acceptance
                                           testable)     task/PR)      scenarios)
```

1. **Bootstrap** the repo once:
   ```bash
   uvx --from git+https://github.com/github/spec-kit.git specify init tc-provider-prototype --ai claude
   # then copy this spec pack over the generated .specify/ and specs/ folders
   ```
2. For each feature folder `specs/00N-*`:
   - Review `spec.md`; resolve any `[NEEDS CLARIFICATION]` (`/clarify`).
   - Review `plan.md` (already drafted; regenerate with `/plan` if the spec changes).
   - Work `tasks.md` top-down (`/implement` or manually). Tasks marked **[P]** can run in parallel.
   - Each task is done when its named test passes and the PR passes the constitution's quality gates.
3. A feature is **Done** when every acceptance scenario in its `spec.md` passes (automated where marked).

Prompting an AI agent: *"Read `.specify/memory/constitution.md`, `specs/004-task-provider-spi/spec.md`, `plan.md` and `data-model.md`. Implement task T004-07 test-first. Do not change the contract files."*

## 2. Feature map and dependencies

```mermaid
flowchart LR
  F001[001 Foundation<br/>repo, CI, infra, DB] --> F002[002 Auth / token service]
  F001 --> F003[003 SCIM 2.0]
  F002 --> F003
  F002 --> F004[004 SPI MVP]
  F003 --> F004
  F004 --> F005[005 Admin console + app page]
  F003 --> F005
  F005 --> F006[006 E2E integration with BTP]
  F002 -. spikes S-01..S-03 early .-> F006
```

## 3. Timeline (1 developer + AI assistant, ~4 weeks)

| Week | Milestone | Features / tasks | Exit criteria |
|---|---|---|---|
| 1 | **M1 – Walking skeleton on Azure** | 001 complete; 002 client-credentials + JWKS; SPI stubs returning `{value:[]}`; request log | `azd up` works; spikes S-01 (path) and S-02 (PP token) executed against BTP |
| 2 | **M2 – Users flow** | 002 jwt-bearer grant; 003 SCIM Users + Groups | IPS provisions test users/groups; Global User ID stored for 100 % of users (S-03) |
| 3 | **M3 – Tasks in Task Center (POC scope)** | 004 pull endpoints + capabilities; 005 admin create/modify/cancel | Tasks visible in Task Center for the right users; DELTA pull picks up edits/cancels within 60 s |
| 4 | **M4 – MVP scope** | 004 description, response, action; 005 deep-link page; 006 test pass + perf | Approve/Reject/Claim/Release from Task Center; perf budgets met; demo |

Buffer: SAML-bearer fallback (2 days) if S-02 fails.

## 4. Definition of Done (prototype)

- All acceptance scenarios in specs 002–006 pass; E2E checklist in `specs/006-e2e-integration/spec.md` signed off.
- Monthly Azure cost forecast ≤ USD 25.
- `docs/btp-setup-guide.md` reproduced by someone other than the author.
- Known gaps documented for a production follow-up (HA, custom domain, IP restrictions, SAML, push via SAP, attachments/comments).
