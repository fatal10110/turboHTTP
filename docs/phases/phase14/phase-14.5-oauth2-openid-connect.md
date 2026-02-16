# Phase 14.5: OAuth 2.0 / OpenID Connect

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Auth`, `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 4 new, 2 modified

---

## Step 1: Define OAuth/OIDC Models and PKCE Helpers

**Files:**
- `Runtime/Auth/OAuthConfig.cs` (new)
- `Runtime/Auth/OAuthToken.cs` (new)
- `Runtime/Auth/PkceUtility.cs` (new)

Required behavior:

1. Support authorization-code + PKCE configuration fields.
2. Track token expiry, refresh token, and provider metadata.
3. Generate RFC-compliant PKCE verifier/challenge pairs.
4. Expose provider-agnostic config surface.

Implementation constraints:

1. Treat secrets/tokens as sensitive in diagnostics.
2. Use UTC timestamps for expiry/refresh calculations.
3. Validate mandatory fields before starting authorization flow.

---

## Step 2: Implement OAuth Client Flows

**Files:**
- `Runtime/Auth/OAuthClient.cs` (new)
- `Runtime/Core/UHttpClient.cs` (modify)

Required behavior:

1. Implement authorization-code exchange and refresh-token flow.
2. Support OpenID discovery and token endpoint metadata when available.
3. Add deterministic token-expiry checks with configurable skew margin.
4. Provide optional hook to attach bearer tokens to outgoing requests.

Implementation constraints:

1. Keep auth flow cancellation-safe.
2. Ensure refresh racing is single-flight (no duplicate refresh storms).
3. Preserve host app control over UI/browser interaction layer.

---

## Step 3: Add Token Storage Boundary and Tests

**File:** `Runtime/Core/UHttpClientOptions.cs` (modify)

Required behavior:

1. Provide pluggable secure token store interface.
2. Default to in-memory token storage with explicit opt-in persistence.
3. Add deterministic tests for PKCE, refresh, expiry, and invalid_grant cases.

---

## Verification Criteria

1. OAuth/OIDC auth-code + PKCE flow succeeds against compliant providers.
2. Refresh logic is resilient and race-safe under concurrent requests.
3. Token data is not leaked in logs/events by default.
4. Auth integration remains optional and does not affect non-auth flows.
