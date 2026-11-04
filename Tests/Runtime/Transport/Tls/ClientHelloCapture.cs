using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142: reads a ClientHello off a raw loopback socket and parses the parts the TLS client
    /// tests assert on (RFC 8446 section 4.1.2, RFC 6066 section 3).
    /// </summary>
    internal static class ClientHelloCapture
    {
        public const byte HandshakeRecord = 22;
        public const byte AlertRecord = 21;
        public const byte ClientHelloType = 1;
        public const int ServerNameExtension = 0x0000;
        public const int StatusRequestExtension = 0x0005;
        public const int StatusRequestV2Extension = 0x0011;
        public const int KeyShareExtension = 0x0033;
        private const int MaxRecordFragment = 16384;

        /// <summary>
        /// Reads handshake records until one whole handshake message is buffered; returns it with
        /// its 4-byte header (msg_type, uint24 length).
        /// </summary>
        public static byte[] ReadHandshakeMessage(Stream stream)
        {
            var message = new MemoryStream();
            var header = new byte[5];
            while (true)
            {
                BouncyCastleLoopbackServer.ReadExact(stream, header, 5);
                int length = (header[3] << 8) | header[4];
                if (header[0] != HandshakeRecord || length > MaxRecordFragment + 256)
                    throw new InvalidDataException("Expected a handshake record, got type " + header[0]);

                var fragment = new byte[length];
                BouncyCastleLoopbackServer.ReadExact(stream, fragment, length);
                message.Write(fragment, 0, length);

                var buffered = message.ToArray();
                if (buffered.Length >= 4 &&
                    buffered.Length >= 4 + ((buffered[1] << 16) | (buffered[2] << 8) | buffered[3]))
                    return buffered;
            }
        }

        /// <summary>Sends a fatal handshake_failure alert (TLS 1.2 record framing).</summary>
        public static void WriteHandshakeFailureAlert(Stream stream)
        {
            // TLSPlaintext { alert(21), 0x0303, length 2, fatal(2), handshake_failure(40) }
            var alert = new byte[] { AlertRecord, 0x03, 0x03, 0x00, 0x02, 0x02, 40 };
            stream.Write(alert, 0, alert.Length);
            stream.Flush();
        }

        /// <summary>The legacy_session_id of a ClientHello (a TLS 1.3 ServerHello echoes it).</summary>
        public static byte[] SessionId(byte[] message)
        {
            Assert.AreEqual(ClientHelloType, message[0], "Not a ClientHello.");
            int offset = 4 + 2 + 32;
            var id = new byte[message[offset]];
            Array.Copy(message, offset + 1, id, 0, id.Length);
            return id;
        }

        /// <summary>The extensions of a ClientHello in wire order: type to extension_data.</summary>
        public static List<KeyValuePair<int, byte[]>> Extensions(byte[] message)
        {
            Assert.AreEqual(ClientHelloType, message[0], "Not a ClientHello.");
            int offset = 4 + 2 + 32; // header, legacy_version, random
            offset += 1 + message[offset]; // legacy_session_id
            offset += 2 + ReadUInt16(message, offset); // cipher_suites
            offset += 1 + message[offset]; // legacy_compression_methods

            var result = new List<KeyValuePair<int, byte[]>>();
            if (offset >= message.Length)
                return result;

            int end = offset + 2 + ReadUInt16(message, offset);
            offset += 2;
            while (offset < end)
            {
                int type = ReadUInt16(message, offset);
                int length = ReadUInt16(message, offset + 2);
                var data = new byte[length];
                Array.Copy(message, offset + 4, data, 0, length);
                result.Add(new KeyValuePair<int, byte[]>(type, data));
                offset += 4 + length;
            }

            return result;
        }

        public static byte[] FindExtension(byte[] message, int type)
        {
            foreach (var extension in Extensions(message))
            {
                if (extension.Key == type)
                    return extension.Value;
            }

            return null;
        }

        /// <summary>
        /// The host_name entries of the server_name extension (RFC 6066 section 3), or null when
        /// the ClientHello carries no server_name extension.
        /// </summary>
        public static List<string> ServerNames(byte[] message)
        {
            var data = FindExtension(message, ServerNameExtension);
            if (data == null)
                return null;

            var names = new List<string>();
            int end = 2 + ReadUInt16(data, 0);
            int offset = 2;
            while (offset < end)
            {
                byte nameType = data[offset];
                int length = ReadUInt16(data, offset + 1);
                Assert.AreEqual(0, nameType, "Unexpected server_name NameType.");
                names.Add(Encoding.ASCII.GetString(data, offset + 3, length));
                offset += 3 + length;
            }

            return names;
        }

        /// <summary>The named groups of the client's key_share entries (RFC 8446 section 4.2.8).</summary>
        public static List<int> KeyShareGroups(byte[] message)
        {
            var groups = new List<int>();
            var data = FindExtension(message, KeyShareExtension);
            if (data == null)
                return groups;

            int end = 2 + ReadUInt16(data, 0);
            int offset = 2;
            while (offset < end)
            {
                groups.Add(ReadUInt16(data, offset));
                offset += 4 + ReadUInt16(data, offset + 2);
            }

            return groups;
        }

        public static string Describe(byte[] message)
        {
            var types = new List<string>();
            foreach (var extension in Extensions(message))
                types.Add("0x" + extension.Key.ToString("X4"));
            return "extensions: " + string.Join(", ", types);
        }

        /// <summary>
        /// A BouncyCastle provider with a short handshake bound. The test assembly does not reference
        /// the BouncyCastle glue, so the internal constructor is reached by reflection.
        /// </summary>
        public static ITlsProvider CreateBouncyCastleProvider(int handshakeTimeoutMillis)
        {
            var type = BouncyCastleLoopbackServer.GetProviderOrFail().GetType();
            var ctor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null);
            Assert.IsNotNull(ctor, "BouncyCastleTlsProvider(int handshakeTimeoutMillis) is missing.");
            return (ITlsProvider)ctor.Invoke(new object[] { handshakeTimeoutMillis });
        }

        private static int ReadUInt16(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }
    }
}
