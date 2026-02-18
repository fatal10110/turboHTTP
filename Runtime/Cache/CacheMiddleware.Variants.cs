using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    using TurboHTTP.Cache;

    public sealed partial class CacheMiddleware
    {
        private static bool TryResolveVary(HttpHeaders headers, out string[] varyHeaders, out bool wildcard)
        {
            wildcard = false;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = headers.GetValues("Vary");

            for (int i = 0; i < values.Count; i++)
            {
                var line = values[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(',');
                for (int j = 0; j < parts.Length; j++)
                {
                    var token = parts[j].Trim();
                    if (token.Length == 0)
                        continue;

                    if (token == "*")
                    {
                        wildcard = true;
                        varyHeaders = Array.Empty<string>();
                        return true;
                    }

                    var normalizedToken = token.ToLowerInvariant();
                    if (set.Add(normalizedToken) && set.Count > MaxVaryHeaders)
                    {
                        varyHeaders = Array.Empty<string>();
                        return false;
                    }
                }
            }

            varyHeaders = set.OrderBy(h => h, StringComparer.Ordinal).ToArray();
            return true;
        }

        private static string BuildVaryKey(HttpHeaders requestHeaders, IReadOnlyList<string> varyHeaders)
        {
            if (varyHeaders == null || varyHeaders.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(varyHeaders.Count * 32);
            for (int i = 0; i < varyHeaders.Count; i++)
            {
                var name = varyHeaders[i].ToLowerInvariant();
                var values = requestHeaders.GetValues(name);

                sb.Append(name);
                sb.Append('=');

                if (values.Count == 0)
                {
                    sb.Append(EmptyVaryKeyToken);
                }
                else
                {
                    for (int j = 0; j < values.Count; j++)
                    {
                        if (j > 0)
                            sb.Append(',');

                        AppendVaryValueToken(sb, values[j]);
                    }
                }

                sb.Append(';');
            }

            return sb.ToString();
        }

        private static string BuildSignature(IReadOnlyList<string> varyHeaders)
        {
            if (varyHeaders == null || varyHeaders.Count == 0)
                return string.Empty;

            return string.Join("\n", varyHeaders);
        }

        private static string[] ParseSignature(string signature)
        {
            if (string.IsNullOrEmpty(signature))
                return Array.Empty<string>();

            return signature.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string BuildBaseKey(HttpMethod method, Uri uri)
        {
            return method.ToUpperString() + " " + NormalizeUri(uri);
        }

        private static string BuildStorageKey(string baseKey, string varyKey)
        {
            return baseKey + "|" + (string.IsNullOrEmpty(varyKey) ? EmptyVaryKeyToken : varyKey);
        }

        private string[] GetSignatureSnapshot(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket) || bucket.Signatures.Count == 0)
                    return Array.Empty<string>();

                return bucket.Signatures.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }
        }

        private void RegisterStoredVariant(string baseKey, string signature, string storageKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                {
                    bucket = new VariantBucket();
                    _variantIndex[baseKey] = bucket;
                }

                var normalizedSignature = signature ?? string.Empty;
                if (bucket.SignatureByStorageKey.TryGetValue(storageKey, out var previousSignature))
                    ReleaseSignatureRefUnsafe(bucket, previousSignature);

                bucket.SignatureByStorageKey[storageKey] = normalizedSignature;
                AddSignatureRefUnsafe(bucket, normalizedSignature);
                bucket.StorageKeys.Add(storageKey);
            }
        }

        private void UnregisterStoredVariant(string baseKey, string storageKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                    return;

                if (!bucket.StorageKeys.Remove(storageKey))
                    return;

                if (bucket.SignatureByStorageKey.TryGetValue(storageKey, out var signature))
                {
                    bucket.SignatureByStorageKey.Remove(storageKey);
                    ReleaseSignatureRefUnsafe(bucket, signature);
                }

                if (bucket.StorageKeys.Count == 0)
                    _variantIndex.Remove(baseKey);
            }
        }

        private string[] TakeStorageKeys(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                    return Array.Empty<string>();

                _variantIndex.Remove(baseKey);
                return bucket.StorageKeys.ToArray();
            }
        }
        private static bool IsSensitiveVaryHeader(string headerName)
        {
            for (int i = 0; i < SensitiveVaryHeaders.Length; i++)
            {
                if (string.Equals(SensitiveVaryHeaders[i], headerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        private static void AddSignatureRefUnsafe(VariantBucket bucket, string signature)
        {
            bucket.Signatures.Add(signature);

            if (!bucket.SignatureRefCounts.TryGetValue(signature, out var count))
                count = 0;

            bucket.SignatureRefCounts[signature] = count + 1;
        }

        private static void ReleaseSignatureRefUnsafe(VariantBucket bucket, string signature)
        {
            if (!bucket.SignatureRefCounts.TryGetValue(signature, out var count))
            {
                bucket.Signatures.Remove(signature);
                return;
            }

            if (count <= 1)
            {
                bucket.SignatureRefCounts.Remove(signature);
                bucket.Signatures.Remove(signature);
                return;
            }

            bucket.SignatureRefCounts[signature] = count - 1;
        }

        private static void AppendVaryValueToken(StringBuilder sb, string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim();
            sb.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(value);
        }
    }
}
