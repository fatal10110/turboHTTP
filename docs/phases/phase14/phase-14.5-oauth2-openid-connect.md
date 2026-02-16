# Phase 14.5: OAuth 2.0 / OpenID Connect

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Auth`, `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 6 new, 2 modified

---

## Step 1: Define OAuth/OIDC Models and PKCE Helpers

**Files:**
- `Runtime/Auth/OAuthConfig.cs` (new)
- `Runtime/Auth/OAuthToken.cs` (new)
- `Runtime/Auth/PkceUtility.cs` (new)

### Technical Spec

`OAuthConfig` required fields:

1. `ClientId`.
2. `AuthorizationEndpoint`.
3. `TokenEndpoint`.
4. `RedirectUri`.
5. `Scopes`.
6. `UsePkce` default `true`.
7. `UseOidcDiscovery` optional.

`OAuthToken` required fields:

1. `AccessToken`.
2. `RefreshToken` optional.
3. `TokenType` (`Bearer` expected).
4. `ExpiresAtUtc`.
5. `IdToken` optional.
6. `Scope`.

PKCE requirements:

1. `code_verifier` length 43-128 chars from RFC 7636 allowed charset.
2. `code_challenge_method` fixed to `S256`.
3. `code_challenge = Base64Url(SHA256(code_verifier))`.
4. Deterministic validation helpers for tests.

Validation rules:

1. Reject non-HTTPS authorization/token endpoints except localhost development override.
2. Reject empty scope list by default unless explicitly allowed.
3. Validate redirect URI absolute and scheme-safe.

### Implementation Constraints

1. Do not persist client secrets or tokens in plain text by default.
2. Treat token strings as sensitive in logs and exceptions.
3. Use UTC and configurable clock abstraction for expiry logic.

---

## Step 2: Implement OAuth Client Flows

**Files:**
- `Runtime/Auth/OAuthClient.cs` (new)
- `Runtime/Core/UHttpClient.cs` (modify)

### Technical Spec

Core operations:

```csharp
Task<OAuthAuthorizationRequest> CreateAuthorizationRequestAsync(OAuthConfig config, CancellationToken ct);
Task<OAuthToken> ExchangeCodeAsync(OAuthCodeExchangeRequest request, CancellationToken ct);
Task<OAuthToken> RefreshTokenAsync(OAuthRefreshRequest request, CancellationToken ct);
Task<OpenIdProviderMetadata> DiscoverAsync(Uri discoveryEndpoint, CancellationToken ct);
```

Authorization code flow:

1. Generate `state` and optional OIDC `nonce`.
2. Generate PKCE verifier/challenge pair.
3. Build authorization URI with encoded parameters.
4. Validate callback `state`.
5. Exchange code at token endpoint.
6. Parse token response and compute `ExpiresAtUtc`.

Refresh flow:

1. Trigger refresh when `Now >= ExpiresAtUtc - RefreshSkew`.
2. Use single-flight guard per logical token key to avoid refresh storms.
3. On success, atomically replace stored token.
4. On `invalid_grant`, clear stored refresh token and surface reauth-required error.

OIDC behavior:

1. If discovery enabled, fetch `.well-known/openid-configuration`.
2. Prefer discovered endpoints over static config when both present and valid.
3. Validate `id_token` presence only when `openid` scope requested.

### Implementation Constraints

1. HTTP calls must propagate cancellation token at every step.
2. Token parse must tolerate provider extensions while enforcing required core fields.
3. Refresh path must serialize writes to token store.
4. Browser/UI handoff remains host-application responsibility.

---

## Step 3: Add Token Store Boundary and Auth Attachment Integration

**Files:**
- `Runtime/Auth/ITokenStore.cs` (new)
- `Runtime/Core/UHttpClientOptions.cs` (modify)

### Technical Spec

Token store contract:

```csharp
public interface ITokenStore
{
    Task<OAuthToken> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, OAuthToken token, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}
```

Auth attachment behavior:

1. Optional auth middleware/interceptor loads token from store.
2. If near expiry, run refresh single-flight before request send.
3. Attach `Authorization: Bearer <token>` only for configured audience/origin match.
4. Never attach token to cross-origin redirect hops unless policy allows.

### Implementation Constraints

1. In-memory store default; persistent store opt-in only.
2. Token store keying must include provider + client id + audience scope.
3. Store operations must be bounded-time and cancellation-safe.

---

## Step 4: Add Deterministic OAuth/OIDC Tests

**File:** `Tests/Runtime/Auth/OAuthClientTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `Pkce_GeneratesValidChallenge` | fixed verifier input | expected `S256` challenge |
| `AuthCodeExchange_ParsesToken` | mock token endpoint success | valid `OAuthToken` with UTC expiry |
| `Refresh_SingleFlight` | parallel requests near expiry | one network refresh call |
| `Refresh_InvalidGrant_ClearsToken` | token endpoint returns `invalid_grant` | stored token removed, reauth required |
| `StateMismatch_Fails` | callback with wrong `state` | deterministic validation failure |
| `Discovery_OverridesEndpoints` | valid OIDC discovery doc | discovered endpoints used |
| `SensitiveData_NotLogged` | failure path with tokens | logs/events redact token values |

---

## Verification Criteria

1. Authorization code + PKCE flow is standards-compliant and deterministic.
2. Refresh handling is race-safe and resilient to transient provider failures.
3. Token storage and attachment enforce strict scope/origin boundaries.
4. Sensitive token material is protected across diagnostics and error surfaces.
