namespace TurboHTTP.Core
{
    /// <summary>
    /// Common MIME content type constants.
    /// </summary>
    public static class ContentTypes
    {
        /// <summary><c>application/json</c>.</summary>
        public const string Json = "application/json";
        /// <summary><c>application/xml</c>.</summary>
        public const string Xml = "application/xml";
        /// <summary><c>application/x-www-form-urlencoded</c>.</summary>
        public const string FormUrlEncoded = "application/x-www-form-urlencoded";
        /// <summary><c>multipart/form-data</c>; a body needs a boundary parameter, which this constant does not carry.</summary>
        public const string MultipartFormData = "multipart/form-data";
        /// <summary><c>text/plain</c>.</summary>
        public const string PlainText = "text/plain";
        /// <summary><c>text/html</c>.</summary>
        public const string Html = "text/html";
        /// <summary><c>application/octet-stream</c>.</summary>
        public const string OctetStream = "application/octet-stream";
        /// <summary><c>image/png</c>.</summary>
        public const string Png = "image/png";
        /// <summary><c>image/jpeg</c>.</summary>
        public const string Jpeg = "image/jpeg";
        /// <summary><c>image/gif</c>.</summary>
        public const string Gif = "image/gif";
        /// <summary><c>application/pdf</c>.</summary>
        public const string Pdf = "application/pdf";
        /// <summary><c>application/zip</c>.</summary>
        public const string Zip = "application/zip";
    }
}
