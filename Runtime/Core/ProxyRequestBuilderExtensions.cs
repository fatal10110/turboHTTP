using System;

namespace TurboHTTP.Core
{
    public static class ProxyRequestBuilderExtensions
    {
        public static UHttpRequestBuilder WithProxy(
            this UHttpRequestBuilder builder,
            ProxySettings proxySettings)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            return builder.WithMetadata(
                RequestMetadataKeys.ProxySettings,
                proxySettings?.Clone());
        }

        public static UHttpRequestBuilder WithoutProxy(this UHttpRequestBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            return builder.WithMetadata(RequestMetadataKeys.ProxyDisabled, true);
        }
    }
}
