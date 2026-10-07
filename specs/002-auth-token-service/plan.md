# Plan — 002 Authentication & Token Service

## Components (in `Tcp.Api/Auth` and `Tcp.Infrastructure/Auth`)

| Type | Responsibility |
|---|---|
| `TokenEndpoint` | Parses form, authenticates client, dispatches to `IGrantHandler` by `grant_type`, shapes RFC 6749 response/errors |
| `IClientStore` / `ConfigClientStore` | Clients from `OAuth:Clients`; secrets via `IConfiguration` (Key Vault provider) |
| `IGrantHandler` | `ClientCredentialsGrantHandler`, `JwtBearerGrantHandler`, *(P2)* `Saml2BearerGrantHandler` |
| `IAssertionIssuerRegistry` | Trusted issuers + JWKS resolution and caching (`Microsoft.IdentityModel.Protocols.ConfigurationManager<JsonWebKeySet>` or custom `HttpClient` cache) |
| `IGlobalUserResolver` | Claim lookup order + SCIM-store fallback (uses `IUserDirectory` from spec 003) |
| `ISigningKeyProvider` | Loads RSA PEM(s) → `RsaSecurityKey` with `kid`; used both to sign and to validate |
| `AccessTokenFactory` | `JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)` |
| `BasicAdminAuthenticationHandler` | Admin Basic auth |

Libraries: `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.IdentityModel.Protocols.OpenIdConnect`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.RateLimiting`. *(P2 SAML)*: `System.Security.Cryptography.Xml` (`SignedXml`) — no third-party SAML stack.

## Request handling

```csharp
app.MapPost("/oauth/token", TokenEndpoint.Handle)
   .AllowAnonymous()
   .DisableAntiforgery()
   .RequireRateLimiting("token");
```

```
Handle(form):
  client = AuthenticateClient(Authorization header | client_id+client_secret)   -> invalid_client (401)
  handler = handlers[grant_type]                                                -> unsupported_grant_type
  if grant_type ∉ client.allowedGrants                                          -> unauthorized_client
  scopes = requested ?? client.defaultScopes; ensure ⊆ client.allowedScopes     -> invalid_scope
  result = handler.Handle(client, form, scopes)                                 -> invalid_grant
  return { access_token, token_type: "Bearer", expires_in, scope }
```

JWT-bearer handler:
```
iss = ReadUnvalidatedIssuer(assertion)        // JsonWebToken(assertion).Issuer
issuerCfg = registry.Find(iss)  ?? invalid_grant("untrusted issuer")
validation = { ValidIssuer = iss, IssuerSigningKeys = await issuerCfg.GetKeysAsync(),
               ValidateAudience = issuerCfg.ValidateAudience, ClockSkew = 60s }
principal = handler.ValidateTokenAsync(assertion, validation) ?? invalid_grant
userId = resolver.Resolve(principal.Claims) ?? invalid_grant("unknown user")
issue token sub=userId, lifetime=min(600, exp-now)
```

## Default client configuration
```json
"OAuth": {
  "Issuer": "https://<fqdn>",
  "Clients": [
    { "ClientId": "tc-tech",  "SecretName": "oauth-client-tc-tech",  "AllowedGrants": ["client_credentials"], "AllowedScopes": ["spi.tech"], "DefaultScopes": ["spi.tech"], "Audience": "tc-provider", "TokenLifetimeSeconds": 900 },
    { "ClientId": "tc-pp",    "SecretName": "oauth-client-tc-pp",    "AllowedGrants": ["urn:ietf:params:oauth:grant-type:jwt-bearer"], "AllowedScopes": ["spi.user"], "DefaultScopes": ["spi.user"], "Audience": "tc-provider", "TokenLifetimeSeconds": 600 },
    { "ClientId": "ips-scim", "SecretName": "oauth-client-ips-scim", "AllowedGrants": ["client_credentials"], "AllowedScopes": ["scim"], "DefaultScopes": ["scim"], "Audience": "tc-provider-scim", "TokenLifetimeSeconds": 900 }
  ],
  "TrustedAssertionIssuers": [
    { "Issuer": "https://<subdomain>.authentication.<region>.hana.ondemand.com/oauth/token", "Type": "Xsuaa" },
    { "Issuer": "https://<tenant>.accounts.ondemand.com", "Type": "Oidc" }
  ],
  "UserIdClaims": [ "user_uuid", "ext_attr.user_uuid", "sub" ]
}
```
(`ext_attr.user_uuid` = dotted path into a nested claim object.)

## Testing strategy
- **Unit**: grant dispatch, scope rules, claim resolution order, lifetime cap, redaction.
- **Integration**: `WebApplicationFactory` with a fake trusted issuer — test spins up an in-memory JWKS endpoint (`TestIssuer` creates RSA key, serves `/.well-known/openid-configuration` + JWKS via a `DelegatingHandler` stub), mints assertions with arbitrary claims.
- **Contract**: responses validated against `contracts/oauth.openapi.yaml`.
- **Manual (S-02)**: BTP Destination service REST API `GET /destination-configuration/v1/destinations/INTEGROVE_TP_PP` with `X-user-token: <user JWT>` → returns `authTokens[0].value` minted by us. Checks the whole PP chain without Task Center.

## Security notes
- Never accept `alg=none`/HS*; allow only RS256/RS384/RS512/ES256 for assertions; only RS256 for our tokens.
- Assertion max size 16 KB; form max size 32 KB.
- `jti` replay cache not required (short lifetimes) — noted for production.
