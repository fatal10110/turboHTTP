## 19b.3: Expose Zero-Allocation Response Bodies

**Goal:** Prevent array allocations on large responses by piping `ReadOnlySequence<byte>` through the public API.

Summary:
1. Refactor `UHttpResponse` to safely expose `ReadOnlySequence<byte>` as the primary body representation.
2. Remove the internal array-flattening logic currently backing `UHttpResponse.Body`.
3. Update JSON serialization extensions to deserialize directly from `ReadOnlySequence<byte>`.
4. Update File download extensions to process and write chunks dynamically from the sequence.
