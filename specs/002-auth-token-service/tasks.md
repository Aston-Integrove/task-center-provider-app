# Tasks — 002 Authentication & Token Service

- [x] **T002-01** `ISigningKeyProvider` (Key Vault PEM, ephemeral dev key, `kid` thumbprint, previous key). *Test*: unit — kid stable for same key; JWKS contains both keys.
- [x] **T002-02** [P] `GET /.well-known/jwks.json` + `openid-configuration`. *Test*: contract test against `oauth.openapi.yaml`.
- [x] **T002-03** `ConfigClientStore` + client authentication (basic + post, constant-time). *Test*: unit — both methods, wrong secret, unknown client, both methods supplied ⇒ `invalid_request`.
- [x] **T002-04** `TokenEndpoint` skeleton with RFC 6749 errors + `Cache-Control: no-store`. *Test*: integration — unsupported grant, non-form content type.
- [x] **T002-05** `ClientCredentialsGrantHandler` + `AccessTokenFactory`. *Test*: integration — token claims (`iss`,`aud`,`scope`,`exp`), `invalid_scope`, `unauthorized_client`.
- [x] **T002-06** JWT bearer authentication for resources + policies (`spi.tech`, `spi.user`, `spi.any`, `scim`). *Test*: integration — expired / wrong aud / tampered token ⇒ 401; wrong scope ⇒ 403.
- [x] **T002-07** Rate limiter on `/oauth/token`. *Test*: 61st request in a minute ⇒ 429.
- [x] **T002-08** `IAssertionIssuerRegistry` with OIDC + XSUAA JWKS resolution and cache. *Test*: integration with `TestIssuer`; key rotation (unknown kid triggers refresh).
- [x] **T002-09** `IGlobalUserResolver` (claim order, nested claim path, email fallback, active check). *Test*: unit table-driven cases.
- [x] **T002-10** `JwtBearerGrantHandler`. *Test*: integration — acceptance 002-2.1 … 2.5.
- [x] **T002-11** `ICurrentUser` + SAP `Error` body for `403 tcp.auth.userContextRequired`. *Test*: integration.
- [x] **T002-12** Admin Basic auth handler. *Test*: integration — `/admin/api/requests` 401 without creds.
- [x] **T002-13** Spike support: `Diagnostics:LogAssertionClaims`; admin endpoint `POST /admin/api/diagnostics/decode-assertion` (admin only, returns header + claim names/values *except* signature) to inspect a pasted JWT. *Test*: unit.
- [x] **T002-14** *(P2 / fallback)* `Saml2BearerGrantHandler` with `SignedXml` validation, audience, NotOnOrAfter, NameID/attribute mapping. *Test*: integration with a self-signed test assertion.
