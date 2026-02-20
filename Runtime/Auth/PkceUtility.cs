using System;
using System.Security.Cryptography;
using System.Text;

namespace TurboHTTP.Auth
{
    public static class PkceUtility
    {
        private const string AllowedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

        public static string GenerateCodeVerifier(int length = 64)
        {
            if (length < 43 || length > 128)
                throw new ArgumentOutOfRangeException(nameof(length), "PKCE code verifier length must be 43-128.");

            var chars = new char[length];
            var random = new byte[Math.Min(256, length * 2)];
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
