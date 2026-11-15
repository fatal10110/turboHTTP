using System;
using System.Collections.Generic;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Settings for <see cref="AuthInterceptor"/>: the authorization scheme, the origins the token
    /// may be sent to, and whether an <c>Authorization</c> header already on the request is replaced.
    /// </summary>
    /// <remarks>
    /// <see cref="AuthInterceptor"/> copies these values when it is constructed; changing the options
    /// afterwards does not affect an existing interceptor.
    /// </remarks>
    public sealed class AuthInterceptorOptions
    {
        /// <summary>
        /// Authorization scheme written before the token (for example <c>"Bearer"</c> or
        /// <c>"Basic"</c>). <c>null</c> or empty writes the token alone. Default: <c>"Bearer"</c>.
        /// </summary>
        public string Scheme { get; set; } = "Bearer";

        /// <summary>
        /// Origins (scheme, host and port, such as <c>https://api.example.com</c>) the token may be
        /// sent to. A request to any other origin passes through without an <c>Authorization</c>
        /// header from the interceptor, and the token provider is not asked for a token.
        /// When the list is empty (the default) the token is attached for every origin the client
        /// is used with, so use a dedicated client per authenticated origin or fill this list.
        /// </summary>
        /// <remarks>
        /// Each entry must be an absolute <c>http</c> or <c>https</c> URI without user info, path,
        /// query or fragment (a trailing <c>/</c> is allowed). The port is compared as well, so
        /// <c>https://api.example.com</c> (port 443) does not match <c>https://api.example.com:8443</c>
        /// or <c>http://api.example.com</c>.
        /// </remarks>
        public IList<Uri> AllowedOrigins { get; } = new List<Uri>();

        /// <summary>
        /// When <c>false</c> (the default), a request that already carries an <c>Authorization</c>
        /// header, for example one set with <c>WithBearerToken</c>, is sent unchanged and the token
        /// provider is not asked for a token. When <c>true</c>, the interceptor replaces that header
        /// with its own credential for the origins it authenticates.
        /// </summary>
        public bool OverrideExistingAuthorization { get; set; }
    }
}
