# TurboHTTP - Complete HTTP Client for Unity

A production-grade, modular HTTP client for Unity with advanced features designed for games and applications requiring reliable network communication.

## Features

- **Modular Architecture:** Use only the modules you need
- **Cross-Platform:** Works on Editor, Standalone, iOS, Android (WebGL in v1.x)
- **Advanced Retry Logic:** Intelligent retries with idempotency awareness
- **HTTP Caching:** ETag-based caching for optimized bandwidth
- **Timeline Tracing:** Detailed observability for every request
- **File Downloads:** Resume support and integrity verification
- **Unity Integration:** Native support for Texture2D, AudioClip, and more
- **Testing Tools:** Record/replay mode for deterministic testing
- **Editor Monitor:** Inspect HTTP traffic directly in Unity Editor

## Quick Start

```csharp
using TurboHTTP.Core;

var client = new UHttpClient();
var response = await client.Get("https://api.example.com/data").SendAsync();

if (response.IsSuccessStatusCode)
{
    var text = System.Text.Encoding.UTF8.GetString(response.Body);
    Debug.Log(text);
}
```

## Documentation

See the [Documentation](Documentation~/QuickStart.md) folder for:
- Quick Start Guide
- Module Documentation
- API Reference
- Platform Notes

## Requirements

- Unity 2021.3 LTS or higher
- .NET Standard 2.1

## Support

For support, please contact: support@yourcompany.com

## License

Proprietary - Unity Asset Store License
