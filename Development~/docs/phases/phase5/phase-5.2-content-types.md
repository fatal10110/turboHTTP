# Phase 5.2: Content Type Constants

**Depends on:** Phase 5.1 (UHttpResponse Content Helpers)
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new

---

## Step 1: Create `ContentTypes` Static Class

**File:** `Runtime/Core/ContentTypes.cs`
**Namespace:** `TurboHTTP.Core`

Create a central MIME constants class:

```csharp
public static class ContentTypes
{
    public const string Json = "application/json";
    public const string Xml = "application/xml";
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";
    public const string MultipartFormData = "multipart/form-data";
    public const string PlainText = "text/plain";
    public const string Html = "text/html";
    public const string OctetStream = "application/octet-stream";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Pdf = "application/pdf";
    public const string Zip = "application/zip";
}
```

Usage expectations:

1. Core request builder helpers use these constants where applicable.
2. JSON and Files modules consume constants instead of string literals.
3. Tests assert exact values to prevent accidental regressions.

---

## Verification Criteria

1. All constants compile and are public.
2. No duplicate/contradictory MIME strings in new Phase 5 code.
3. `ContentTypesTests` verifies each constant value.
