using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.JSON.Lite;

namespace TurboHTTP.Tests.JSON
{
    /// <summary>
    /// Fixed-seed mutation tests for the LiteJson reader (RFC 8259 grammar, escapes, numbers and the
    /// nesting cap) (#112). JsonParseException is the only failure the reader may raise.
    /// </summary>
    [TestFixture]
    public class JsonFuzzRegressionTests
    {
        private const int Iterations = 5000;
        private const int MaxLength = 2048;

        private static readonly byte[][] Documents =
        {
            SeededMutator.Ascii("{\"a\":1,\"b\":[true,false,null],\"c\":\"x\\u00e9\\n\",\"d\":-1.5e3}"),
            SeededMutator.Ascii("[0,-0,1E+2,0.1,9007199254740993,-1.0e-10,2147483648]"),
            SeededMutator.Ascii("\"str\\\"\\\\\\/\\b\\f\\n\\r\\t\""),
            SeededMutator.Ascii("{\"nested\":{\"deep\":[1,{\"x\":\"y\"}]}}"),
            SeededMutator.Ascii("\"\\ud83d\\ude00\""),
            SeededMutator.Ascii(new string('[', 63) + new string(']', 63)),
            Encoding.UTF8.GetBytes("{\"name\":\"Jörg €\"}"),
        };

        [Test]
        public void JsonReader_MutatedDocuments_FailOnlyWithJsonParseException()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("json", 112, Iterations, MaxLength, Documents, data =>
            {
                try
                {
                    new JsonReader(Encoding.UTF8.GetString(data)).Parse();
                }
                catch (JsonParseException)
                {
                }

                return Task.CompletedTask;
            }));
        }

        [Test]
        public void JsonReader_NestingPastTheCap_FailsWithJsonParseException()
        {
            // 65 levels against MaxDepth 64: the cap must stop recursion with the typed error.
            Assert.Throws<JsonParseException>(() =>
                new JsonReader(new string('[', 65) + new string(']', 65)).Parse());
        }
    }
}
