using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

#pragma warning disable IDE0051 // Remove unused private members — called by Unity via attribute

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Platform policy and concurrency profile for managed decoder routing.
    /// </summary>
    public sealed class DecoderRoutingPolicy
    {
        public bool EnableManagedImageDecode { get; set; }
        public bool EnableManagedAudioDecode { get; set; }
        public int MaxImageDecodeConcurrency { get; set; } = 2;
        public int MaxAudioDecodeConcurrency { get; set; } = 2;
        public string PolicyName { get; set; } = "fallback";

        public DecoderRoutingPolicy Clone()
        {
            return new DecoderRoutingPolicy
            {
                EnableManagedImageDecode = EnableManagedImageDecode,
                EnableManagedAudioDecode = EnableManagedAudioDecode,
                MaxImageDecodeConcurrency = MaxImageDecodeConcurrency,
                MaxAudioDecodeConcurrency = MaxAudioDecodeConcurrency,
                PolicyName = PolicyName
            };
        }
    }

    /// <summary>
    /// Builder used by <see cref="DecoderRegistry.Initialize"/> for pre-bootstrap registration.
    /// </summary>
    public sealed class DecoderRegistryBuilder
    {
        private readonly List<IImageDecoder> _imageDecoders = new List<IImageDecoder>();
        private readonly List<IAudioDecoder> _audioDecoders = new List<IAudioDecoder>();

        internal IReadOnlyList<IImageDecoder> ImageDecoders => _imageDecoders;
        internal IReadOnlyList<IAudioDecoder> AudioDecoders => _audioDecoders;

        public DecoderRegistryBuilder AddImageDecoder(IImageDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));
            _imageDecoders.Add(decoder);
            return this;
        }

        public DecoderRegistryBuilder AddAudioDecoder(IAudioDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));
            _audioDecoders.Add(decoder);
            return this;
        }
    }

    /// <summary>
    /// Managed decoder registration and policy-driven resolution.
    /// </summary>
    public static class DecoderRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<IImageDecoder> ImageDecoders = new List<IImageDecoder>();
        private static readonly List<IAudioDecoder> AudioDecoders = new List<IAudioDecoder>();
        private static readonly HashSet<string> SessionWarnings = new HashSet<string>(StringComparer.Ordinal);

        private static bool _bootstrapped;
        private static bool _registrationSealed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (Gate)
            {
                ImageDecoders.Clear();
                AudioDecoders.Clear();
                SessionWarnings.Clear();
                _bootstrapped = false;
                _registrationSealed = false;
            }
        }

        /// <summary>
        /// Initializes registry defaults and optional custom registrations.
        /// </summary>
        /// <remarks>
        /// Call this early at app startup if you need to register custom decoders.
        /// Once initialized, registration is sealed for deterministic runtime behavior.
        /// </remarks>
        public static void Initialize(Action<DecoderRegistryBuilder> configure = null)
        {
            lock (Gate)
            {
                if (_bootstrapped)
                    return;

                var builder = new DecoderRegistryBuilder();
                AddDefaultDecoders_NoLock(builder);

                for (var i = 0; i < ImageDecoders.Count; i++)
                {
                    builder.AddImageDecoder(ImageDecoders[i]);
                }

                for (var i = 0; i < AudioDecoders.Count; i++)
                {
                    builder.AddAudioDecoder(AudioDecoders[i]);
                }
                
                // Initialization is startup-only; keep configure under lock to avoid
                // duplicate configure callback invocation in concurrent bootstrap races.
                configure?.Invoke(builder);

                ImageDecoders.Clear();
                AudioDecoders.Clear();

                for (var i = 0; i < builder.ImageDecoders.Count; i++)
                {
                    var decoder = builder.ImageDecoders[i];
                    if (decoder != null)
                        ImageDecoders.Add(decoder);
                }

                for (var i = 0; i < builder.AudioDecoders.Count; i++)
                {
                    var decoder = builder.AudioDecoders[i];
                    if (decoder != null)
                        AudioDecoders.Add(decoder);
                }

                _bootstrapped = true;
                _registrationSealed = true;
            }
        }

        public static void BootstrapDefaults()
        {
            Initialize();
        }

        public static void RegisterImageDecoder(IImageDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));

            lock (Gate)
            {
                ThrowIfSealed();
                ImageDecoders.Add(decoder);
            }
        }

        public static void RegisterAudioDecoder(IAudioDecoder decoder)
        {
            if (decoder == null) throw new ArgumentNullException(nameof(decoder));

            lock (Gate)
            {
                ThrowIfSealed();
                AudioDecoders.Add(decoder);
            }
        }

        public static DecoderRoutingPolicy GetRoutingPolicy()
        {
            RuntimePlatform platform;
            try
            {
                platform = Application.platform;
            }
            catch
            {
                return new DecoderRoutingPolicy
                {
                    EnableManagedImageDecode = false,
                    EnableManagedAudioDecode = false,
                    MaxImageDecodeConcurrency = 1,
                    MaxAudioDecodeConcurrency = 1,
                    PolicyName = "fallback"
                };
            }

            switch (platform)
            {
                case RuntimePlatform.WebGLPlayer:
                    return new DecoderRoutingPolicy
                    {
                        EnableManagedImageDecode = false,
                        EnableManagedAudioDecode = false,
                        MaxImageDecodeConcurrency = 1,
                        MaxAudioDecodeConcurrency = 1,
                        PolicyName = "webgl-fallback"
                    };

                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.Android:
                    return new DecoderRoutingPolicy
                    {
                        EnableManagedImageDecode = true,
                        EnableManagedAudioDecode = true,
                        MaxImageDecodeConcurrency = 2,
                        MaxAudioDecodeConcurrency = 2,
                        PolicyName = "mobile-il2cpp"
                    };

                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return new DecoderRoutingPolicy
                    {
                        EnableManagedImageDecode = true,
                        EnableManagedAudioDecode = true,
                        MaxImageDecodeConcurrency = 4,
                        MaxAudioDecodeConcurrency = 4,
                        PolicyName = "editor-standalone"
                    };

                default:
                    return new DecoderRoutingPolicy
                    {
                        EnableManagedImageDecode = false,
                        EnableManagedAudioDecode = false,
                        MaxImageDecodeConcurrency = 1,
                        MaxAudioDecodeConcurrency = 1,
                        PolicyName = "unknown-fallback"
                    };
            }
        }

        public static bool TryResolveImageDecoder(
            string contentType,
            string fileNameOrPath,
            out IImageDecoder decoder,
            out string reason)
        {
            Initialize();

            var policy = GetRoutingPolicy();
            if (!policy.EnableManagedImageDecode)
            {
                WarnPolicyDisabledOnce(
                    "image-policy-disabled-" + policy.PolicyName,
                    "Managed image decoding is policy-disabled for platform profile '" +
                    policy.PolicyName +
                    "'. Falling back to Texture2D.LoadImage.");

                decoder = null;
                reason = "policy-disabled";
                return false;
            }

            var extension = NormalizeExtension(fileNameOrPath);
            lock (Gate)
            {
                for (var i = 0; i < ImageDecoders.Count; i++)
                {
                    var candidate = ImageDecoders[i];
                    if (candidate != null && candidate.CanDecode(contentType, extension))
                    {
                        decoder = candidate;
                        reason = null;
                        return true;
                    }
                }
            }

            decoder = null;
            reason = "no-decoder";
            return false;
        }

        public static bool TryResolveAudioDecoder(
            string contentType,
            string fileNameOrPath,
            out IAudioDecoder decoder,
            out string reason)
        {
            Initialize();

            var policy = GetRoutingPolicy();
            if (!policy.EnableManagedAudioDecode)
            {
                WarnPolicyDisabledOnce(
                    "audio-policy-disabled-" + policy.PolicyName,
                    "Managed audio decoding is policy-disabled for platform profile '" +
                    policy.PolicyName +
                    "'. Falling back to Unity decode path.");

                decoder = null;
                reason = "policy-disabled";
                return false;
            }

            var extension = NormalizeExtension(fileNameOrPath);
            lock (Gate)
            {
                for (var i = 0; i < AudioDecoders.Count; i++)
                {
                    var candidate = AudioDecoders[i];
                    if (candidate != null && candidate.CanDecode(contentType, extension))
                    {
                        decoder = candidate;
                        reason = null;
                        return true;
                    }
                }
            }

            decoder = null;
            reason = "no-decoder";
            return false;
        }

        public static async Task WarmupImageDecodersAsync(CancellationToken cancellationToken = default)
        {
            Initialize();

            IImageDecoder[] snapshot;
            lock (Gate)
            {
                snapshot = ImageDecoders.ToArray();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoder = snapshot[i];
                if (decoder == null)
                    continue;

                try
                {
                    await decoder.WarmupAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WarnPolicyDisabledOnce(
                        "image-warmup-" + decoder.Id,
                        "Image decoder warmup failed for '" + decoder.Id + "': " + ex.Message);
                }
            }
        }

        public static async Task WarmupAudioDecodersAsync(CancellationToken cancellationToken = default)
        {
            Initialize();

            IAudioDecoder[] snapshot;
            lock (Gate)
            {
                snapshot = AudioDecoders.ToArray();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoder = snapshot[i];
                if (decoder == null)
                    continue;

                try
                {
                    await decoder.WarmupAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WarnPolicyDisabledOnce(
                        "audio-warmup-" + decoder.Id,
                        "Audio decoder warmup failed for '" + decoder.Id + "': " + ex.Message);
                }
            }
        }

        private static string NormalizeExtension(string fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
                return string.Empty;

            try
            {
                return Path.GetExtension(fileNameOrPath) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void ThrowIfSealed()
        {
            if (_registrationSealed)
            {
                throw new InvalidOperationException(
                    "Decoder registration is sealed after bootstrap. " +
                    "Use DecoderRegistry.Initialize(builder => ...) at startup for custom decoders.");
            }
        }

        private static void WarnPolicyDisabledOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!SessionWarnings.Add(key))
                    return;
            }

            Debug.LogWarning("[TurboHTTP] " + message);
        }

        private static void AddDefaultDecoders_NoLock(DecoderRegistryBuilder builder)
        {
            builder.AddImageDecoder(new StbImageSharpDecoder());

            builder.AddAudioDecoder(new WavPcmDecoder());
            builder.AddAudioDecoder(new AiffPcmDecoder());
            builder.AddAudioDecoder(new NVorbisDecoder());
            builder.AddAudioDecoder(new NLayerMp3Decoder());
        }
    }
}
