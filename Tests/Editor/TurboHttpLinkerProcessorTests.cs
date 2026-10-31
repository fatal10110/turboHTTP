using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;
using TurboHTTP.Editor;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// The package's link.xml rules reach UnityLinker only through
    /// <see cref="TurboHttpLinkerProcessor"/> (#8), and must name the assemblies the types really
    /// live in on Unity's class libraries (#35).
    /// </summary>
    [TestFixture]
    public class TurboHttpLinkerProcessorTests
    {
        private const string BouncyCastleAssembly = "TurboHTTP.Transport.BouncyCastle";
        private const string BouncyCastleLibAssembly = "TurboHTTP.SecureProtocol";
        private const string IgnoreIfMissing = "ignoreIfMissing";

        private string _packageRoot;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _packageRoot = TurboHttpLinkerProcessor.FindPackageRoot();
            Assert.That(_packageRoot, Is.Not.Null, "package root of " + TurboHttpLinkerProcessor.PackageName);
        }

        [Test]
        public void FindPackageRoot_ReturnsTheTurboHttpPackage()
        {
            var packageJson = File.ReadAllText(Path.Combine(_packageRoot, "package.json"));
            StringAssert.Contains("\"" + TurboHttpLinkerProcessor.PackageName + "\"", packageJson);
            Assert.That(TurboHttpLinkerProcessor.FindLinkXmlFiles(_packageRoot), Is.Not.Empty);
        }

        [TestCase("TurboHTTP.JSON", "TurboHTTP.JSON.JsonSerializer")]
        [TestCase("TurboHTTP.JSON", "TurboHTTP.JSON.JsonExtensions")]
        [TestCase("TurboHTTP.Transport", "TurboHTTP.Transport.TransportModuleInitializer")]
        [TestCase("TurboHTTP.Transport", "TurboHTTP.Transport.RawSocketTransport")]
        [TestCase("TurboHTTP.WebSocket.Transport", "TurboHTTP.WebSocket.Transport.WebSocketTransportModuleInitializer")]
        [TestCase("TurboHTTP.WebSocket.Transport", "TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport")]
        public void MergedLinkXml_PreservesReflectionTarget(string assembly, string typeName)
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            AssertPreservesAll(merged, assembly, typeName);
        }

        [TestCase("System.Net.Security.SslStream")]
        [TestCase("System.Net.Security.SslClientAuthenticationOptions")]
        [TestCase("System.Net.Security.SslApplicationProtocol")]
        public void MergedLinkXml_PreservesSslStreamAlpnTypesUnderSystem(string typeName)
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            AssertPreservesAll(merged, "System", typeName);
            Assert.That(AssemblyNames(merged), Does.Not.Contain("System.Net.Security"),
                "System.Net.Security is a facade on Unity's class libraries");
        }

        [Test]
        public void MergedLinkXml_NamesTheAssembliesTheAlpnTypesLiveInOnThisProfile()
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            // Whatever the class library, the entry must use the assembly the type is implemented
            // in; a facade name alone is a no-op for UnityLinker.
            foreach (var type in new[]
                     {
                         typeof(SslStream), typeof(SslClientAuthenticationOptions), typeof(SslApplicationProtocol),
                     })
            {
                AssertPreservesAll(merged, type.Assembly.GetName().Name, type.FullName);
            }

            AssertPreservesAll(merged, typeof(List<>).Assembly.GetName().Name, "System.Collections.Generic.List`1");
        }

        /// <summary>
        /// Every framework rule names the assembly that implements the type on this class library
        /// (the Editor's unityjit profile places these types exactly like the players' unityaot
        /// profile). A facade name resolves through a type forward but preserves nothing, and an
        /// assembly that does not exist would fail UnityLinker, so both are rejected unless the
        /// entry opts out with ignoreIfMissing="1".
        /// </summary>
        [Test]
        public void PackageLinkXml_FrameworkRulesNameTheImplementingAssembly()
        {
            var packageAssemblies = PackageAssemblyNames();

            foreach (var file in TurboHttpLinkerProcessor.FindLinkXmlFiles(_packageRoot))
            {
                foreach (var assembly in XDocument.Load(file).Descendants("assembly"))
                {
                    var name = (string)assembly.Attribute("fullname");
                    Assert.That(name, Is.Not.Null.And.Not.Empty, file);
                    if (name.StartsWith("TurboHTTP.", StringComparison.Ordinal))
                    {
                        Assert.That(packageAssemblies, Does.Contain(name), file + ": no asmdef named " + name);
                        continue;
                    }

                    if ((string)assembly.Attribute(IgnoreIfMissing) == "1")
                        continue;

                    Assert.DoesNotThrow(() => System.Reflection.Assembly.Load(name), file + ": no assembly " + name);
                    foreach (var type in assembly.Elements("type"))
                    {
                        var typeName = (string)type.Attribute("fullname");
                        var resolved = Type.GetType(typeName + ", " + name, throwOnError: false);
                        Assert.That(resolved, Is.Not.Null, file + ": " + typeName + " does not resolve in " + name);
                        Assert.That(resolved.Assembly.GetName().Name, Is.EqualTo(name),
                            file + ": " + typeName + " is implemented in " + resolved.Assembly.GetName().Name +
                            ", not " + name);
                    }
                }
            }
        }

        [Test]
        public void PackageLinkXml_UsesOpenGenericTypeNamesOnly()
        {
            foreach (var file in TurboHttpLinkerProcessor.FindLinkXmlFiles(_packageRoot))
            {
                foreach (var type in XDocument.Load(file).Descendants("type"))
                {
                    var name = (string)type.Attribute("fullname");
                    Assert.That(name, Is.Not.Null.And.Not.Empty, file);
                    Assert.That(name, Does.Not.Contain("[").And.Not.Contain("<"),
                        file + ": UnityLinker ignores closed generic names: " + name);
                }
            }
        }

        [Test]
        public void PackageLinkXml_TurboHttpTypesExist()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .GroupBy(a => a.GetName().Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var file in TurboHttpLinkerProcessor.FindLinkXmlFiles(_packageRoot))
            {
                foreach (var assembly in XDocument.Load(file).Descendants("assembly"))
                {
                    var assemblyName = (string)assembly.Attribute("fullname");
                    if (!assemblyName.StartsWith("TurboHTTP.", StringComparison.Ordinal))
                        continue;

                    Assert.That(loaded.ContainsKey(assemblyName), Is.True, file + ": assembly not loaded: " + assemblyName);
                    foreach (var type in assembly.Elements("type"))
                    {
                        var typeName = (string)type.Attribute("fullname");
                        Assert.That(loaded[assemblyName].GetType(typeName, throwOnError: false), Is.Not.Null,
                            file + ": no type " + typeName + " in " + assemblyName);
                    }
                }
            }
        }

        [Test]
        public void BouncyCastleRule_LivesInTheBouncyCastleFolder()
        {
            var transportLinkXml = Path.Combine(_packageRoot, "Runtime", "Transport", "link.xml");
            var bouncyCastleLinkXml = Path.Combine(_packageRoot, "Runtime", "Transport", "BouncyCastle", "link.xml");

            Assert.That(AssemblyNames(XDocument.Load(transportLinkXml)), Does.Not.Contain(BouncyCastleAssembly));

            var assembly = XDocument.Load(bouncyCastleLinkXml).Descendants("assembly")
                .Single(a => (string)a.Attribute("fullname") == BouncyCastleAssembly);
            Assert.That((string)assembly.Attribute("preserve"), Is.EqualTo("all"));
            // The vendored library is its own assembly (#143) and is preserved by the same rule file,
            // which stays owned by the glue asmdef so it merges only when BouncyCastle ships.
            var libAssembly = XDocument.Load(bouncyCastleLinkXml).Descendants("assembly")
                .Single(a => (string)a.Attribute("fullname") == BouncyCastleLibAssembly);
            Assert.That((string)libAssembly.Attribute("preserve"), Is.EqualTo("all"));
            Assert.That(TurboHttpLinkerProcessor.FindOwningAssembly(bouncyCastleLinkXml, _packageRoot),
                Is.EqualTo(BouncyCastleAssembly));
            Assert.That(TurboHttpLinkerProcessor.FindOwningAssembly(transportLinkXml, _packageRoot),
                Is.EqualTo("TurboHTTP.Transport"));
        }

        [Test]
        public void MergedLinkXml_IncludesModuleRulesOnlyWhenTheModuleIsInTheBuild()
        {
            var withAll = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);
            Assert.That(AssemblyNames(withAll), Does.Contain(BouncyCastleAssembly));
            Assert.That(AssemblyNames(withAll), Does.Contain(BouncyCastleLibAssembly));

            var withoutOptional = TurboHttpLinkerProcessor.BuildMergedLinkXml(
                _packageRoot, name => name != BouncyCastleAssembly && name != "TurboHTTP.JSON");
            var names = AssemblyNames(withoutOptional);
            Assert.That(names, Does.Not.Contain(BouncyCastleAssembly));
            Assert.That(names, Does.Not.Contain(BouncyCastleLibAssembly));
            Assert.That(names, Does.Not.Contain("TurboHTTP.JSON"));
            AssertPreservesAll(withoutOptional, "TurboHTTP.Transport", "TurboHTTP.Transport.RawSocketTransport");
            AssertPreservesAll(withoutOptional, "System", "System.Net.Security.SslStream");
        }

        [Test]
        public void MergedLinkXml_MarksAssembliesNotConfirmedInTheBuildIgnoreIfMissing()
        {
            var inputDirectory = Path.Combine(Path.GetTempPath(), "TurboHttpLinkerTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(inputDirectory);
            try
            {
                foreach (var name in new[] { "TurboHTTP.Core", "TurboHTTP.Transport", "System" })
                    File.WriteAllBytes(Path.Combine(inputDirectory, name + ".dll"), Array.Empty<byte>());

                var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(
                    _packageRoot, TurboHttpLinkerProcessor.CreateAssemblyProbe(inputDirectory));

                Assert.That(AssemblyNames(merged), Does.Not.Contain(BouncyCastleAssembly));
                Assert.That(AssemblyNames(merged), Does.Not.Contain("TurboHTTP.JSON"));
                Assert.That(IgnoreIfMissingOf(merged, "TurboHTTP.Transport"), Is.Null);
                Assert.That(IgnoreIfMissingOf(merged, "System"), Is.Null);
                Assert.That(IgnoreIfMissingOf(merged, "mscorlib"), Is.EqualTo("1"));
            }
            finally
            {
                Directory.Delete(inputDirectory, recursive: true);
            }
        }

        [Test]
        public void MergedLinkXml_WithUnknownLinkerInput_KeepsEveryRuleAndMarksAllIgnoreIfMissing()
        {
            var unknown = Path.Combine(Path.GetTempPath(), "TurboHttpLinkerTest_" + Guid.NewGuid().ToString("N"));
            var probe = TurboHttpLinkerProcessor.CreateAssemblyProbe(unknown);
            Assert.That(probe("TurboHTTP.Core"), Is.Null);

            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, probe);

            Assert.That(AssemblyNames(merged), Does.Contain(BouncyCastleAssembly));
            foreach (var assembly in merged.Descendants("assembly"))
                Assert.That((string)assembly.Attribute(IgnoreIfMissing), Is.EqualTo("1"), (string)assembly.Attribute("fullname"));
        }

        [Test]
        public void MergedLinkXml_HasOneElementPerAssembly()
        {
            var names = AssemblyNames(TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true));

            Assert.That(names, Is.Unique);
            Assert.That(names, Does.Contain("mscorlib"));
        }

        private static void AssertPreservesAll(XDocument linkXml, string assemblyName, string typeName)
        {
            var preserved = linkXml.Descendants("assembly")
                .Where(a => (string)a.Attribute("fullname") == assemblyName)
                .SelectMany(a => a.Elements("type"))
                .Any(t => (string)t.Attribute("fullname") == typeName && (string)t.Attribute("preserve") == "all");
            Assert.That(preserved, Is.True, "no preserve=\"all\" rule for " + typeName + " in assembly " + assemblyName);
        }

        private static string IgnoreIfMissingOf(XDocument linkXml, string assemblyName)
        {
            return (string)linkXml.Descendants("assembly")
                .Single(a => (string)a.Attribute("fullname") == assemblyName)
                .Attribute(IgnoreIfMissing);
        }

        private static List<string> AssemblyNames(XDocument linkXml)
        {
            return linkXml.Descendants("assembly").Select(a => (string)a.Attribute("fullname")).ToList();
        }

        private HashSet<string> PackageAssemblyNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var namePattern = new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"");
            foreach (var asmdef in Directory.GetFiles(Path.Combine(_packageRoot, "Runtime"), "*.asmdef", SearchOption.AllDirectories))
            {
                var match = namePattern.Match(File.ReadAllText(asmdef));
                if (match.Success)
                    names.Add(match.Groups[1].Value);
            }

            return names;
        }
    }
}
