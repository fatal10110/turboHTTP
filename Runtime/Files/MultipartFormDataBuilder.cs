using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Files
{
    /// <summary>
    /// Fluent builder for multipart/form-data request bodies (file uploads).
    /// <para><b>Non-ASCII filenames:</b> Uses UTF-8 in the filename= parameter.
    /// Strict RFC 7578 compliance requires filename*= (RFC 5987) for non-ASCII
    /// characters. This is planned for a future release.</para>
    /// </summary>
    public class MultipartFormDataBuilder
    {
        private readonly List<IPart> _parts = new List<IPart>();
        private readonly string _boundary;

        public MultipartFormDataBuilder()
        {
            _boundary = "----TurboHTTP" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Create a builder with a specific boundary (useful for testing and reproducibility).
        /// Boundary must be 1-70 characters and contain only RFC 2046 bchars.
        /// </summary>
        /// <exception cref="ArgumentNullException">Boundary is null.</exception>
        /// <exception cref="ArgumentException">Boundary is empty, too long, or contains invalid characters.</exception>
        public MultipartFormDataBuilder(string boundary)
        {
            if (boundary == null) throw new ArgumentNullException(nameof(boundary));
            if (boundary.Length == 0 || boundary.Length > 70)
                throw new ArgumentException("Boundary must be 1-70 characters.", nameof(boundary));
            foreach (var c in boundary)
            {
                if (!IsValidBoundaryChar(c))
                    throw new ArgumentException(
                        $"Boundary contains invalid character '{c}'. Only alphanumeric, hyphens, and RFC 2046 bchars are allowed.",
                        nameof(boundary));
            }
            _boundary = boundary;
        }

        /// <summary>
        /// The boundary string used to separate parts.
        /// </summary>
        public string Boundary => _boundary;

        /// <summary>
        /// Add a text field.
        /// </summary>
        /// <exception cref="ArgumentException">Name contains CR or LF characters.</exception>
        public MultipartFormDataBuilder AddField(string name, string value)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            ValidateNoCrLf(name, nameof(name));
            _parts.Add(new TextPart(name, value ?? string.Empty));
            return this;
        }

        /// <summary>
        /// Add a file from a byte array.
        /// </summary>
        /// <exception cref="ArgumentException">Name or filename contains CR or LF characters.</exception>
        public MultipartFormDataBuilder AddFile(
            string name, string filename, byte[] data,
            string contentType = ContentTypes.OctetStream)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            if (data == null) throw new ArgumentNullException(nameof(data));
            ValidateNoCrLf(name, nameof(name));
            ValidateNoCrLf(filename, nameof(filename));
            _parts.Add(new FilePart(name, filename, data, contentType ?? ContentTypes.OctetStream));
            return this;
        }

        /// <summary>
        /// Add a file read from disk.
        /// <para><b>Memory Note:</b> Reads the entire file into memory via File.ReadAllBytes().
        /// For very large files on mobile, consider using AddFile() with a pre-loaded byte array
        /// and monitoring available memory.</para>
        /// </summary>
        public MultipartFormDataBuilder AddFileFromDisk(
            string name, string filePath,
            string contentType = ContentTypes.OctetStream)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            var data = File.ReadAllBytes(filePath);
            var filename = Path.GetFileName(filePath);
            return AddFile(name, filename, data, contentType);
        }

        /// <summary>
        /// Build the multipart/form-data body as a byte array.
        /// </summary>
        public byte[] Build()
        {
            using (var stream = new MemoryStream())
            {
                foreach (var part in _parts)
                {
                    WriteBytes(stream, $"--{_boundary}\r\n");
                    part.WriteTo(stream);
                }

                WriteBytes(stream, $"--{_boundary}--\r\n");
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Get the Content-Type header value including boundary parameter.
        /// </summary>
        public string GetContentType()
        {
            // Unquoted boundary maximizes compatibility with older proxies/servers.
            // Boundary chars are already validated to RFC 2046 bchars.
            return $"multipart/form-data; boundary={_boundary}";
        }

        /// <summary>
        /// Apply this multipart data to a request builder (sets body + Content-Type).
        /// </summary>
        public void ApplyTo(UHttpRequestBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            builder.WithBody(Build()).WithHeader("Content-Type", GetContentType());
        }

        /// <summary>
        /// Escape a string for use inside a quoted-string in Content-Disposition.
        /// Escapes backslash and double-quote per RFC 2616 Section 2.2.
        /// </summary>
        private static string EscapeQuotedString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Validate that a value does not contain CR or LF (header injection prevention).
        /// </summary>
        private static void ValidateNoCrLf(string value, string paramName)
        {
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new ArgumentException(
                    "Value must not contain CR or LF characters.", paramName);
        }

        /// <summary>
        /// RFC 2046 bchars: DIGIT / ALPHA / ' ( ) + _ , - . / : = ?
        /// Intentionally excludes space for stricter interoperability and simpler parsing.
        /// </summary>
        private static bool IsValidBoundaryChar(char c)
        {
            if (c >= 'A' && c <= 'Z') return true;
            if (c >= 'a' && c <= 'z') return true;
            if (c >= '0' && c <= '9') return true;
            return c == '\'' || c == '(' || c == ')' || c == '+' || c == '_' ||
                   c == ',' || c == '-' || c == '.' || c == '/' || c == ':' ||
                   c == '=' || c == '?';
        }

        private static void WriteBytes(MemoryStream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private interface IPart
        {
            void WriteTo(MemoryStream stream);
        }

        private sealed class TextPart : IPart
        {
            private readonly string _name;
            private readonly string _value;

            public TextPart(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public void WriteTo(MemoryStream stream)
            {
                var header = Encoding.UTF8.GetBytes(
                    $"Content-Disposition: form-data; name=\"{EscapeQuotedString(_name)}\"\r\n\r\n");
                stream.Write(header, 0, header.Length);

                var value = Encoding.UTF8.GetBytes(_value + "\r\n");
                stream.Write(value, 0, value.Length);
            }
        }

        private sealed class FilePart : IPart
        {
            private readonly string _name;
            private readonly string _filename;
            private readonly byte[] _data;
            private readonly string _contentType;

            public FilePart(string name, string filename, byte[] data, string contentType)
            {
                _name = name;
                _filename = filename;
                _data = data;
                _contentType = contentType;
            }

            public void WriteTo(MemoryStream stream)
            {
                var header = Encoding.UTF8.GetBytes(
                    $"Content-Disposition: form-data; name=\"{EscapeQuotedString(_name)}\"; filename=\"{EscapeQuotedString(_filename)}\"\r\n" +
                    $"Content-Type: {_contentType}\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(_data, 0, _data.Length);

                var crlf = Encoding.UTF8.GetBytes("\r\n");
                stream.Write(crlf, 0, crlf.Length);
            }
        }
    }
}
