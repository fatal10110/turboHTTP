# Phase 16.4: GraphQL Client

**Depends on:** Phase 5 (Content Handlers / JSON)
**Assembly:** `TurboHTTP.GraphQL`, `TurboHTTP.Tests.Runtime`
**Files:** 4 new, 0 modified

---

## Step 1: Implement GraphQL Query Builder

**Files:**
- `Runtime/GraphQL/GraphQLRequest.cs` (new)
- `Runtime/GraphQL/GraphQLQueryBuilder.cs` (new)

Required behavior:

1. Define `GraphQLRequest` data type containing:
   - `Query` (string) — the GraphQL query or mutation string.
   - `OperationName` (string, optional) — operation name for multi-operation documents.
   - `Variables` (Dictionary<string, object>, optional) — query variables.
2. Implement `GraphQLQueryBuilder` with fluent API:
   - `Query(string query)` — set the query string.
   - `Mutation(string mutation)` — set a mutation string (alias for `Query` with semantic clarity).
   - `WithOperationName(string name)` — set operation name.
   - `WithVariable(string key, object value)` — add a single variable.
   - `WithVariables(Dictionary<string, object> variables)` — add multiple variables.
   - `Build()` — produce a `GraphQLRequest`.
3. Validate query string is non-null and non-empty on `Build()`.
4. Serialize `GraphQLRequest` to JSON payload matching the GraphQL-over-HTTP specification:
   ```json
   {
     "query": "...",
     "operationName": "...",
     "variables": { ... }
   }
   ```
5. Omit `operationName` and `variables` fields from JSON when null/empty (minimize payload size).

Implementation constraints:

1. Use `TurboHTTP.JSON.JsonSerializer` for serialization — do not add a separate JSON dependency.
2. Builder must be reusable — `Build()` must not mutate builder state.
3. Variable values must support primitives, strings, nested objects, and arrays.
4. Query strings must not be validated for GraphQL syntax in this phase (server-side validation is authoritative).
5. Keep the builder allocation-light — avoid unnecessary intermediate collections.

---

## Step 2: Implement GraphQL Response Handling

**File:** `Runtime/GraphQL/GraphQLResponse.cs` (new)

Required behavior:

1. Define `GraphQLResponse<T>` for typed data extraction:
   - `Data` (T) — deserialized `data` field from response.
   - `Errors` (List<GraphQLError>) — list of errors from `errors` field.
   - `Extensions` (Dictionary<string, object>) — optional `extensions` field.
   - `HasErrors` (bool) — convenience property (`Errors != null && Errors.Count > 0`).
2. Define `GraphQLError` with standard fields:
   - `Message` (string) — error description.
   - `Locations` (List<GraphQLErrorLocation>) — source locations (line, column).
   - `Path` (List<object>) — field path to error.
   - `Extensions` (Dictionary<string, object>) — error-specific extensions.
3. Define `GraphQLErrorLocation` with `Line` and `Column` properties.
4. Add extension methods on `UHttpResponse` for GraphQL response parsing:
   - `AsGraphQL<T>()` — parse response body as `GraphQLResponse<T>`, throw on HTTP error.
   - `TryAsGraphQL<T>(out GraphQLResponse<T>)` — parse without throwing on HTTP error.
5. Support non-generic `GraphQLResponse` (untyped `Data` as `Dictionary<string, object>`) for dynamic queries.

Implementation constraints:

1. Response parsing must handle partial responses (data + errors both present, per GraphQL spec).
2. Deserialization must use `TurboHTTP.JSON.JsonSerializer` for consistency with the rest of the library.
3. `AsGraphQL<T>` must throw `UHttpException` on non-2xx status codes before attempting JSON parse.
4. JSON parse failures must produce clear error messages indicating GraphQL response format issue.
5. `Extensions` fields must be optional and default to null (not empty collections).
6. Error types must be in `TurboHTTP.GraphQL` namespace.

---

## Step 3: Add GraphQL Client Extensions and Assembly Definition

**File:** `Runtime/GraphQL/TurboHTTP.GraphQL.asmdef` (new — assembly definition)

Required behavior:

1. Configure assembly definition:
   - References: `TurboHTTP.Core`, `TurboHTTP.JSON`.
   - `autoReferenced: false`.
   - `noEngineReferences: true`.
2. Add extension methods on `UHttpClient` for GraphQL operations:
   - `PostGraphQLAsync<T>(string endpoint, GraphQLRequest request, CancellationToken ct)` — send GraphQL query as POST and return typed `GraphQLResponse<T>`.
   - `PostGraphQLAsync(string endpoint, GraphQLRequest request, CancellationToken ct)` — untyped variant.
3. Add builder extension on `UHttpRequestBuilder`:
   - `WithGraphQLBody(GraphQLRequest request)` — set JSON body from GraphQL request and add `Content-Type: application/json`.
4. Set `Content-Type: application/json` and `Accept: application/graphql-response+json, application/json` headers automatically on GraphQL requests.
5. Reuse existing `UHttpClient.Post()` pipeline — GraphQL requests must flow through all configured middlewares.

Implementation constraints:

1. Follow existing builder extension pattern from `TurboHTTP.JSON.JsonRequestBuilderExtensions`.
2. GraphQL client extensions must not bypass the middleware pipeline.
3. Do not add direct HTTP transport calls — always go through `UHttpClient` API.
4. `Accept` header should prefer `application/graphql-response+json` but accept `application/json` as fallback.

---

## Step 4: Add GraphQL Client Tests

**File:** `Tests/Runtime/GraphQL/GraphQLClientTests.cs` (new)

Required behavior:

1. Validate `GraphQLQueryBuilder` produces correct JSON payload with query only.
2. Validate builder with operation name and variables.
3. Validate omission of null/empty optional fields in serialized payload.
4. Validate `GraphQLResponse<T>` deserialization with data field.
5. Validate error-only response parsing (no data, only errors).
6. Validate partial response (data + errors both present).
7. Validate `HasErrors` property correctness.
8. Validate `AsGraphQL<T>` throws on non-2xx HTTP status.
9. Validate `TryAsGraphQL<T>` returns false on non-2xx without throwing.
10. Validate `PostGraphQLAsync` sends correct headers and body via `MockTransport`.
11. Validate `WithGraphQLBody` builder extension sets correct content type.
12. Validate middleware pipeline integration (auth, logging, retry compose with GraphQL requests).
13. Validate builder reusability (multiple `Build()` calls produce independent requests).

---

## Verification Criteria

1. GraphQL queries and mutations serialize to spec-compliant JSON payloads.
2. Typed and untyped response parsing correctly handles data, errors, and mixed responses.
3. Client extensions integrate with existing middleware pipeline without bypass.
4. Headers follow GraphQL-over-HTTP specification (`application/json` content type, correct accept headers).
5. Builder is reusable and allocation-efficient for repeated query construction.
6. Error surfaces are clear and consistent with TurboHTTP error taxonomy.
