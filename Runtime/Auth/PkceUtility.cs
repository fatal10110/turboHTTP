using System;
using System.Security.Cryptography;
using System.Text;

namespace TurboHTTP.Auth
{
    /// <summary>Proof Key for Code Exchange helpers (RFC 7636). Stateless and thread-safe.</summary>
    public static class PkceUtility
    {
        private const string AllowedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

        /// <summary>Generates a random code verifier from the unreserved characters with a cryptographic generator (RFC 7636 section 4.1).</summary>
        /// <param name="length">Length from 43 to 128.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is outside 43 to 128.</exception>
        public static string GenerateCodeVerifier(int length = 64)
        {
            if (length < 43 || length > 128)
                throw new ArgumentOutOfRangeException(nameof(length), "PKCE code verifier length must be 43-128.");

            var chars = new char[length];
            // Oversample to reduce extra RNG fills caused by rejection sampling.
            var random = new byte[Math.Max(256, length * 4)];
            var alphabetLength = AllowedChars.Length;
            var rejectionThreshold = 256 - (256 % alphabetLength);
            using var rng = RandomNumberGenerator.Create();

            var position = 0;
            while (position < chars.Length)
            {
                rng.GetBytes(random);

                for (int i = 0; i < random.Length && position < chars.Length; i++)
                {
                    var value = random[i];
                    if (value >= rejectionThreshold)
                        continue;

                    chars[position++] = AllowedChars[value % alphabetLength];
                }
            }

            return new string(chars);
        }

        /// <summary>Derives the <c>S256</c> code challenge: base64url of the SHA-256 of the verifier (RFC 7636 section 4.2).</summary>
        /// <exception cref="ArgumentException"><paramref name="codeVerifier"/> is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The verifier is not 43 to 128 characters.</exception>
        public static string CreateS256CodeChallenge(string codeVerifier)
        {
            if (string.IsNullOrEmpty(codeVerifier))
                throw new ArgumentException("Code verifier is required.", nameof(codeVerifier));
            if (codeVerifier.Length < 43 || codeVerifier.Length > 128)
                throw new ArgumentOutOfRangeException(nameof(codeVerifier), "PKCE code verifier length must be 43-128.");

            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
            return Base64UrlEncode(digest);
        }

        /// <summary>Whether <paramref name="codeVerifier"/> is 43 to 128 characters of the unreserved set. Never throws.</summary>
        public static bool ValidateCodeVerifier(string codeVerifier)
        {
            if (string.IsNullOrEmpty(codeVerifier))
                return false;
            if (codeVerifier.Length < 43 || codeVerifier.Length > 128)
                return false;

            for (int i = 0; i < codeVerifier.Length; i++)
            {
                if (AllowedChars.IndexOf(codeVerifier[i]) < 0)
                    return false;
            }

            return true;
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
