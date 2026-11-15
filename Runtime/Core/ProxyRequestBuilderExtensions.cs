using System;

namespace TurboHTTP.Core
{
    public static class ProxyRequestBuilderExtensions
    {
        /// <summary>
        /// Sends this request through <paramref name="proxySettings"/> (a copy) instead of the client's
        /// proxy. The settings stay fixed across redirect hops; <see cref="ProxySettings.BypassList"/> is
        /// checked against the URI of every dispatch. A null value or a null
        /// <see cref="ProxySettings.Address"/> sends the request directly.
        /// </summary>
        /// <exception cref="ArgumentException">The address is not <c>http://</c>, or credentials are set
        /// without an address (<see cref="ProxySettings.Validate"/>).</exception>
        public static UHttpRequest WithProxy(
            this UHttpRequest request,
            ProxySettings proxySettings)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // Same checks as client-level settings (#828): a non-http:// proxy fails here, at the call
            // site, instead of being contacted in cleartext. The transport checks again, because the
            // metadata key can also be set directly.
            proxySettings?.Validate();

            return request.WithMetadata(
                RequestMetadataKeys.ProxySettings,
                proxySettings?.Clone());
        }

        public static UHttpRequest WithoutProxy(this UHttpRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            return request.WithMetadata(RequestMetadataKeys.ProxyDisabled, true);
        }
    }
}
