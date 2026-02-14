# Phase 5.5: Multipart Form Data Builder

**Depends on:** Phase 5.2 (Content Type Constants)
**Assembly:** `TurboHTTP.Files`
**Files:** 1 new

---

## Step 1: Builder Construction and Boundary Rules

**File:** `Runtime/Files/MultipartFormDataBuilder.cs`
**Namespace:** `TurboHTTP.Files`

Create builder constructors:

```csharp
public MultipartFormDataBuilder()
public MultipartFormDataBuilder(string boundary)
public string Boundary { get; }
```

Boundary validation requirements:

1. Boundary length must be 1-70 characters.
2. Boundary must use allowed RFC 2046 characters.
3. Null boundary throws `ArgumentNullException`.
4. Invalid boundary throws `ArgumentException`.

---

## Step 2: Add Parts API

Add fluent methods:

```csharp
public MultipartFormDataBuilder AddField(string name, string value)
public MultipartFormDataBuilder AddFile(string name, string filename, byte[] data, string contentType = ContentTypes.OctetStream)
public MultipartFormDataBuilder AddFileFromDisk(string name, string filePath, string contentType = ContentTypes.OctetStream)
```

Validation requirements:

1. Reject null names/filenames/data with `ArgumentNullException`.
2. Reject CR/LF in names and filenames (`ArgumentException`).
3. Escape quoted-string values in `Content-Disposition`.
4. Use `ContentTypes.OctetStream` default for file content type.

---

## Step 3: Build and Apply Multipart Payload

Core methods:

```csharp
public byte[] Build()
public string GetContentType()
public void ApplyTo(UHttpRequestBuilder builder)
```

Build requirements:

1. Emit each part with `--<boundary>\r\n`.
2. Emit terminal boundary `--<boundary>--\r\n`.
3. Include `Content-Disposition` per part.
4. Include `Content-Type` for file parts.
5. Encode multipart output as UTF-8 where applicable.

Apply requirements:

1. Set request body to `Build()` result.
2. Set `Content-Type` header to `multipart/form-data; boundary=<boundary>`.
3. Throw `ArgumentNullException` for null builder.

---

## Verification Criteria

1. Single-field and multi-field bodies are correctly formatted.
2. File parts include filename and content type metadata.
3. Mixed text + file requests build valid multipart payloads.
4. `GetContentType()` includes boundary.
5. Random boundary constructor produces unique boundaries.
6. Custom boundary constructor is usable for reproducible tests.
7. `ApplyTo()` sets both body and Content-Type on the request builder.
