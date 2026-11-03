## 19b.2: Pooled Request Objects (Zero-Allocation Builder)

**Goal:** Shift from immutable builders to pooled, mutable request objects returned directly from the client.

Summary:
1. Implement a request pool inside `UHttpClient` (or `HttpTransportFactory`).
2. Add `UHttpClient.CreateRequest(HttpMethod, string)` which returns an `IDisposable` request (rented from the pool).
3. Remove `UHttpRequestBuilder` and the `UHttpRequest` immutable copy constructors (`WithHeaders`, `WithBody`, etc.).
4. Make `UHttpRequest` state mutable for the renter but internally safe (cleared automatically upon return to the pool).
5. Update all extensions (JSON, Auth, Multipart) to operate seamlessly on the mutable leased request.
