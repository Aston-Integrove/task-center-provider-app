# Plan — 005 Admin Console

## Structure
```
Tcp.Api/Endpoints/Admin/
  AdminTasksEndpoints.cs      # CRUD-ish + generate, uses TaskInstance aggregate (spec 004)
  AdminIdentityEndpoints.cs   # users, groups (read-only views over SCIM store)
  AdminDiagnosticsEndpoints.cs
Tcp.Api/Endpoints/App/
  AppTaskPage.cs              # server-rendered HTML (Razor Slices or raw string builder + HtmlEncoder)
Tcp.Api/wwwroot/admin/
  index.html, admin.js, admin.css
Tcp.Infrastructure/Html/
  HtmlSanitizer               # Ganss.Xss (HtmlSanitizer NuGet) with allow-list
```

## Decisions
- **No SPA framework**: vanilla JS `fetch` + `<template>` elements; Basic credentials prompted by the browser.
- **Reuse domain**: admin mutations go through `TaskInstance` methods so `Touch()` semantics (monotonic `modifiedAt`) are identical to SPI operations.
- **Random data**: `Bogus` NuGet for the generator (subjects like "Approve PR 4711 – 3× Dell Latitude 7450 for Mining Ops").
- **Recipient resolution**: `IUserDirectory.FindByEmail` / `FindByGlobalUserId`.
- **OIDC (P2)**: `Microsoft.AspNetCore.Authentication.OpenIdConnect` with IAS application (client id/secret in KV), scope `openid email profile`, map `user_uuid` claim.

## Tests
- Integration: create → appears in `/tasks` pull with correct fields; patch bumps modifiedAt; cancel tombstones; generator with sameTimestamp produces collisions; sanitizer strips `<script>`.
- UI smoke: Playwright test (Chromium preinstalled in CI image) loads `/admin`, creates a task, sees it in list.
