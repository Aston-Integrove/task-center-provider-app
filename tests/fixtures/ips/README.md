# IPS replay fixtures (spike S-03)

Each `*.json` file is a sequence of SCIM requests replayed against the API by
`tests/Tcp.IntegrationTests/IpsReplayTests.cs`.

```jsonc
{
  "description": "...",
  "steps": [
    { "name": "create user", "method": "POST", "path": "/scim/v2/Users",
      "body": { ... },                       // strings may contain {{placeholders}}
      "capture": { "userId": "id" },         // remember top-level fields of the response
      "expect": { "status": 201, "body": { "active": true, "meta.resourceType": "User" } } }
  ]
}
```

Placeholders: `{{rand}}` (12 hex chars, unique per run), `{{guid1}}`, `{{guid2}}`, `{{guid3}}` (fresh GUIDs per run)
and anything captured by an earlier step. `expect.body` keys are dotted paths (`Resources.0.id`, `members.length`).

## Status

`01`-`03` are **synthetic**: written from the documented behaviour of SAP Identity Provisioning's SCIM target
system (search by `userName`, create, `PUT`/`PATCH` updates, group membership via `PATCH`). Replace or extend them
with real captures once spike S-03 runs against Valterra's IPS:

1. Keep the request log enabled (`Diagnostics:RequestLogEnabled=true`, the default) and run the IPS job.
2. `GET /admin/api/requests/export?prefix=/scim` (admin Basic auth) returns the redacted, replayable steps.
3. Review the output for personal data, replace real values with placeholders, save it here as `NN-name.json`.
4. Tighten `expect` and finalise `Scim:GlobalUserIdSources` based on what IPS actually sends.
