using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Cache
{
    public sealed partial class CacheInterceptor
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

        // Index cap when the storage's entry limit is unknown (custom ICacheStorage).
        internal const int DefaultVariantIndexCapacity = 4096;

        /// <summary>Number of base keys in the variant index. For tests.</summary>
        internal int VariantIndexCount
        {
            get
            {
                lock (_indexLock)
                {
                    return _variantIndex.Count;
                }
            }
        }

        /// <summary>Variant index bound. Test hook: set before the first request.</summary>
        internal int VariantIndexCapacity
        {
            get
            {
                lock (_indexLock)
                {
                    return _variantIndexCapacity;
                }
            }
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Must be greater than 0.");

                lock (_indexLock)
                {
                    _variantIndexCapacity = value;
                }
            }
        }

        private string[] GetSignatureSnapshot(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket) || bucket.Signatures.Count == 0)
                    return Array.Empty<string>();

                TouchVariantBucketUnsafe(bucket);
                return bucket.Signatures.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }
        }

        private void RegisterStoredVariant(string baseKey, string signature, string storageKey)
        {
            string evictedBaseKey = null;
            string[] evictedStorageKeys = null;
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                {
                    bucket = new VariantBucket();
                    bucket.LruNode = _variantLru.AddLast(baseKey);
                    _variantIndex[baseKey] = bucket;
                }
                else
                {
                    TouchVariantBucketUnsafe(bucket);
                }

                var normalizedSignature = signature ?? string.Empty;
                if (bucket.SignatureByStorageKey.TryGetValue(storageKey, out var previousSignature))
                    ReleaseSignatureRefUnsafe(bucket, previousSignature);

                bucket.SignatureByStorageKey[storageKey] = normalizedSignature;
                AddSignatureRefUnsafe(bucket, normalizedSignature);
                bucket.StorageKeys.Add(storageKey);

                // Each register adds at most one bucket, so one eviction keeps the bound. The
                // just-registered bucket is the most recent one and never the victim.
                if (_variantIndex.Count > _variantIndexCapacity)
                    evictedStorageKeys = EvictLeastRecentBucketUnsafe(out evictedBaseKey);
            }

            if (evictedStorageKeys != null)
                QueueEvictedVariantRemoval(evictedBaseKey, evictedStorageKeys);
        }

        private void TouchVariantBucketUnsafe(VariantBucket bucket)
        {
            var node = bucket.LruNode;
            if (node == null || ReferenceEquals(node, _variantLru.Last))
                return;

            _variantLru.Remove(node);
            _variantLru.AddLast(node);
        }

        private void RemoveVariantBucketUnsafe(string baseKey, VariantBucket bucket)
        {
            _variantIndex.Remove(baseKey);
            if (bucket.LruNode != null)
            {
                _variantLru.Remove(bucket.LruNode);
                bucket.LruNode = null;
            }
        }

        // Drops the least recently used bucket. Returns the storage keys that only the index can
        // reach (Vary variants), or null. The no-Vary key is looked up and invalidated without the
        // index, so it stays in storage.
        private string[] EvictLeastRecentBucketUnsafe(out string baseKey)
        {
            var node = _variantLru.First;
            baseKey = node.Value;
            var bucket = _variantIndex[baseKey];
            RemoveVariantBucketUnsafe(baseKey, bucket);

            var unvariedKey = BuildStorageKey(baseKey, string.Empty);
            List<string> varied = null;
            foreach (var key in bucket.StorageKeys)
            {
                if (string.Equals(key, unvariedKey, StringComparison.Ordinal))
                    continue;

                (varied ??= new List<string>()).Add(key);
            }

            return varied?.ToArray();
        }

        // A Vary entry the index no longer tracks would be invisible to unsafe-method invalidation
        // (RFC 9111 section 4.4) and could be served again once its signature is re-registered, so
        // it is removed from storage. Runs as a mutation of its base key, after any in-flight store.
        private void QueueEvictedVariantRemoval(string baseKey, string[] storageKeys)
        {
            QueueBackgroundWork(
                QueueMutationAsync(
                    baseKey,
                    static (owner, key, token, state) =>
                        owner.RemoveEvictedVariantsCoreAsync(key, (string[])state, token),
                    _backgroundWorkToken,
                    storageKeys),
                "variant index eviction");
        }

        private async Task RemoveEvictedVariantsCoreAsync(
            string baseKey,
            string[] storageKeys,
            CancellationToken cancellationToken)
        {
            // QueueMutationAsync starts mutations inline under _pendingMutationLock; leave it first.
            await new ThreadPoolHop();

            for (int i = 0; i < storageKeys.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStorageKeyRegistered(baseKey, storageKeys[i]))
                    continue;

                await _policy.Storage.RemoveAsync(storageKeys[i], cancellationToken).ConfigureAwait(false);
            }
        }

        private bool IsStorageKeyRegistered(string baseKey, string storageKey)
        {
            lock (_indexLock)
            {
                return _variantIndex.TryGetValue(baseKey, out var bucket)
                       && bucket.StorageKeys.Contains(storageKey);
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
                    RemoveVariantBucketUnsafe(baseKey, bucket);
            }
        }

        private string[] TakeStorageKeys(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                    return Array.Empty<string>();

                RemoveVariantBucketUnsafe(baseKey, bucket);
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

        private static bool TryResolveSensitiveVaryFlags(
            string[] varyHeaders,
            out bool hasCookieVary,
            out bool hasAuthorizationVary)
        {
            hasCookieVary = false;
            hasAuthorizationVary = false;

            if (varyHeaders == null || varyHeaders.Length == 0)
                return false;

            bool containsSensitive = false;
            for (int i = 0; i < varyHeaders.Length; i++)
            {
                var header = varyHeaders[i];
                if (!IsSensitiveVaryHeader(header))
                    continue;

                containsSensitive = true;

                if (!hasCookieVary && string.Equals(header, "cookie", StringComparison.OrdinalIgnoreCase))
                    hasCookieVary = true;

                if (!hasAuthorizationVary && string.Equals(header, "authorization", StringComparison.OrdinalIgnoreCase))
                    hasAuthorizationVary = true;
            }

            return containsSensitive;
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
