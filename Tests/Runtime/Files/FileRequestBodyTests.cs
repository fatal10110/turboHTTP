using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Files;

namespace TurboHTTP.Tests.Files
{
    [TestFixture]
    public class FileRequestBodyTests
    {
        [Test]
        public async Task WithFileBody_ConfiguresFileRequestBody_AndReadsFileContents()
        {
            var path = CreateTempFile("file-body");
            try
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/upload"))
                    .WithFileBody(path, 8192);

                Assert.IsInstanceOf<FileRequestBody>(request.Content);
                var fileBody = (FileRequestBody)request.Content;
                Assert.AreEqual(path, fileBody.Path);
                Assert.AreEqual(8192, fileBody.BufferSize);
                Assert.AreEqual(RequestBodyReplayability.Replayable, fileBody.Replayability);
                Assert.IsFalse(fileBody.TryGetBufferedData(out _));
                Assert.AreEqual("file-body", await ReadAllAsync(fileBody.OpenReadSessionAsync(CancellationToken.None)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public async Task Clone_FileBody_CreatesNewWrapperOverSamePath()
        {
            var path = CreateTempFile("clone-file-body");
            try
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/upload"))
                    .WithFileBody(path);

                var clone = request.Clone();

                Assert.IsInstanceOf<FileRequestBody>(clone.Content);
                Assert.AreNotSame(request.Content, clone.Content);
                Assert.AreEqual("clone-file-body", await ReadAllAsync(clone.Content.OpenReadSessionAsync(CancellationToken.None)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void WithFileBody_NullRequest_ThrowsArgumentNullException()
        {
            var ex = Assert.Throws<ArgumentNullException>(() =>
                FileRequestBuilderExtensions.WithFileBody(null, "ignored"));
            Assert.AreEqual("request", ex.ParamName);
        }

        [Test]
        public void FileRequestBody_Length_IsCachedAtConstruction()
        {
            var path = CreateTempFile("abc");
            try
            {
                var body = new FileRequestBody(path);

                File.AppendAllText(path, "def");

                Assert.AreEqual(3, body.Length);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string CreateTempFile(string contents)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }

        private static async Task<string> ReadAllAsync(ValueTask<RequestBodyReadSession> pendingSession)
        {
            return await ReadAllAsync(await pendingSession.ConfigureAwait(false));
        }

        private static async Task<string> ReadAllAsync(RequestBodyReadSession session)
        {
            using (session)
            using (var stream = new MemoryStream())
            {
                var buffer = new byte[16];
                while (true)
                {
                    int read = await session.ReadAsync(buffer, CancellationToken.None);
                    if (read == 0)
                        break;

                    await stream.WriteAsync(buffer, 0, read);
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
