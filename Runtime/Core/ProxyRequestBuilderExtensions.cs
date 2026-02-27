using System;

namespace TurboHTTP.Core
{
    public static class ProxyRequestBuilderExtensions
    {
        public static UHttpRequest WithProxy(
            this UHttpRequest request,
            ProxySettings proxySettings)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

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
