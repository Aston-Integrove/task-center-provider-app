# Feature 002 — Authentication & Token Service

**Status**: Ready · **Priority**: P1 · **Depends on**: 001 · **SAP rules**: TC-DEST-05/06/07, TC-ID-02, TC-ARCH-02/03 · **Contract**: `contracts/oauth.openapi.yaml`

## Summary
A minimal OAuth 2.0 authorization server inside the prototype that issues short-lived JWT access tokens to:
1. **SAP Task Center (technical user)** via the primary destination (`OAuth2ClientCredentials`).
2. **SAP Task Center (business user)** via the `_PP` destination (`OAuth2JWTBearer`): exchanges the user's BTP/IAS JWT for our token whose `sub` is the user's **Global User ID**.
3. **SAP Identity Provisioning** for the SCIM API (`client_credentials`).

All protected APIs validate these tokens.

## User stories

### US-002-1 Technical token (P1)
As SAP Task Center, I obtain a technical token so I can pull tasks and definitions.

**Acceptance**
1. Given client `tc-tech` with correct secret (HTTP Basic *or* form `client_id`/`client_secret`), when it POSTs `grant_type=client_credentials`, then response `200` `{"access_token":"<JWT>","token_type":"Bearer","expires_in":900,"scope":"spi.tech"}` with `Cache-Control: no-store`.
2. Given a wrong secret, then `401 {"error":"invalid_client"}` with `WWW-Authenticate: Basic`.
3. Given `tc-tech` requests `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`, then `400 {"error":"unauthorized_client"}`.
4. Given a request with `scope=spi.user` from `tc-tech`, then `400 {"error":"invalid_scope"}`. Omitted scope ⇒ client's default scope.

### US-002-2 User token via JWT bearer (P1)
As SAP Task Center acting for a logged-in user, I exchange the user's JWT so the provider knows who is acting.

**Acceptance**
1. Given client `tc-pp` authenticated and `assertion=<JWT>` signed by a trusted issuer, unexpired, whose Global User ID claim resolves to an **active** SCIM user, when it POSTs `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`, then `200` with an access token where `sub` = Global User ID, `scope` = `spi.user`, `expires_in` ≤ 600 and never later than the assertion's `exp`.
2. Given an assertion from an issuer not in `OAuth:TrustedAssertionIssuers`, then `400 {"error":"invalid_grant","error_description":"untrusted issuer"}`.
3. Given an expired assertion (clock skew 60 s), then `400 invalid_grant`.
4. Given a valid assertion whose user cannot be resolved, or user is `active=false`, then `400 invalid_grant` ("unknown user") and a warning log with issuer + claim *names* only.
5. Given `Diagnostics:LogAssertionClaims=true`, then the claim names and the resolved user id are logged (never the raw token) — used by spike S-02.

### US-002-3 SCIM client token (P1)
**Acceptance**: Given client `ips-scim`, `client_credentials` returns scope `scim`, `expires_in` 900. Optionally (`Scim:AllowBasic=true`) SCIM endpoints also accept HTTP Basic with `ips-scim`/secret.

### US-002-4 Token validation on APIs (P1)
**Acceptance**
1. SPI pull endpoints require scope `spi.tech` **or** `spi.user`.
2. SPI user endpoints (`/description`, `/response`, `/action`) require scope `spi.user` and a `sub`; a `spi.tech` token gets `403` with SAP `Error` body code `tcp.auth.userContextRequired`.
3. SCIM endpoints require scope `scim`.
4. Tokens with wrong `iss`, wrong `aud`, bad signature, or expired ⇒ `401` + `WWW-Authenticate: Bearer error="invalid_token"`.

### US-002-5 Discovery and keys (P2)
**Acceptance**: `GET /.well-known/jwks.json` returns the public RSA key(s) with `kid`; `GET /.well-known/openid-configuration` returns `issuer`, `token_endpoint`, `jwks_uri`, `grant_types_supported`, `token_endpoint_auth_methods_supported`.

## Functional requirements

**Token endpoint**
- FR-TOK-01 `POST /oauth/token`, `Content-Type: application/x-www-form-urlencoded` only; other content types ⇒ `400 invalid_request`.
- FR-TOK-02 Client authentication: `client_secret_basic` and `client_secret_post`; constant-time compare; client registry from configuration (`OAuth:Clients`) with secrets from Key Vault.
- FR-TOK-03 Client registry fields: `clientId`, `secretName`, `allowedGrants[]`, `allowedScopes[]`, `defaultScopes[]`, `audience`, `tokenLifetimeSeconds`.
- FR-TOK-04 Access tokens: JWT RS256, header `kid`; claims `iss` (=`OAuth:Issuer`), `aud` (client audience, default `tc-provider`), `sub`, `client_id`, `scope` (space-delimited), `iat`, `nbf`, `exp`, `jti`. For user tokens add `email`, `name` (from SCIM store) and `orig_iss` (assertion issuer).
- FR-TOK-05 Errors per RFC 6749 §5.2 (`invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`, `unsupported_grant_type`, `invalid_scope`).
- FR-TOK-06 Rate limit: 60 requests/min per client_id (fixed window, in-memory) ⇒ `429`.
- FR-TOK-07 No refresh tokens.

**JWT-bearer grant (principal propagation)**
- FR-PP-01 Accept `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` with `assertion` (RFC 7523). Tolerate an extra `token_format`, `response_type`, `client_id`, `scope` form field (Destination service may send them).
- FR-PP-02 Validate signature with JWKS of the assertion's `iss`; JWKS location resolved per issuer type: OIDC discovery (`{iss}/.well-known/openid-configuration` → `jwks_uri`); for XSUAA issuers ending `/oauth/token`, use `{base}/token_keys`. Optional explicit override `OAuth:TrustedAssertionIssuers[i].JwksUri`. Cache JWKS 1 h; refresh on unknown `kid`.
- FR-PP-03 Validate `exp`, `nbf` (skew 60 s). Audience check optional per issuer (`ValidateAudience`, `ValidAudiences`) — default off for the prototype, logged.
- FR-PP-04 Resolve Global User ID: iterate `OAuth:UserIdClaims` (default `user_uuid`, then `sub` *only if* it is a GUID that exists in SCIM store); if none, fallback by `email` claim → SCIM user `emails.value` (case-insensitive, unique match only). Result must be an active SCIM user.
- FR-PP-05 Token lifetime = min(600 s, assertion `exp` − now).
- FR-PP-10 *(P2, fallback for R-01)* Accept `grant_type=urn:ietf:params:oauth:grant-type:saml2-bearer`: base64url SAML 2.0 assertion, signature validated against BTP subaccount trust certificate (`btp-saml-trust-cert`), `NotOnOrAfter`, `Audience` = `PublicBaseUrl`, NameID (or attribute named by `Saml:UserIdAttribute`) = Global User ID. Implemented as another `IGrantHandler`.

**Resource protection**
- FR-RES-01 One JWT bearer scheme validating our issuer/audience/keys; authorization policies `spi.tech`, `spi.user`, `spi.any`, `scim`, `admin` (Basic auth handler on `/admin/*`, `/app/*`).
- FR-RES-02 `ICurrentUser` service exposes `GlobalUserId`, `IsTechnical`, `ClientId` to endpoints.

**Keys**
- FR-KEY-01 Signing key loaded from Key Vault secret `token-signing-key` (PEM). `kid` = base64url SHA-256 thumbprint of the public key. Optional `token-signing-key-previous` published in JWKS for rotation.
- FR-KEY-02 Local dev: auto-generated ephemeral key with warning log.

## Non-functional
- NFR-002-01 Token endpoint p95 < 300 ms (excluding first JWKS fetch).
- NFR-002-02 No secret, token or assertion in logs (constitution VI).

## Out of scope
Authorization code flow, refresh tokens, dynamic client registration, mTLS client auth, token introspection/revocation.

## Open items
- `[NEEDS CLARIFICATION]` resolved by S-02: exact issuer and claims of the user JWT passed through the Destination service for Task Center (XSUAA vs IAS).
