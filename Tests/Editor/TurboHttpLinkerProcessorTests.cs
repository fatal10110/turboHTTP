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

        // Non-TurboHTTP assembly names a package link.xml may use. "System" and "mscorlib" are the
        // implementation assemblies of Unity's Mono/IL2CPP class libraries; the other two are
        // .NET Standard facades kept for profiles that implement the types there (#35).
        private static readonly HashSet<string> AllowedFrameworkAssemblies = new HashSet<string>(StringComparer.Ordinal)
        {
            "mscorlib",
            "System",
            "System.Net.Security",
            "System.Security.Cryptography.Algorithms",
        };

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
        [TestCase("TurboHTTP.Observability", "TurboHTTP.Observability.MonitorInterceptor")]
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
        public void MergedLinkXml_PreservesSslStreamAlpnTypesUnderSystemAndFacade(string typeName)
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            AssertPreservesAll(merged, "System", typeName);
            AssertPreservesAll(merged, "System.Net.Security", typeName);
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

        [Test]
        public void PackageLinkXml_NamesOnlyKnownAssemblies()
        {
            var packageAssemblies = PackageAssemblyNames();

            foreach (var file in TurboHttpLinkerProcessor.FindLinkXmlFiles(_packageRoot))
            {
                foreach (var assembly in XDocument.Load(file).Descendants("assembly"))
                {
                    var name = (string)assembly.Attribute("fullname");
                    Assert.That(name, Is.Not.Null.And.Not.Empty, file);
                    if (name.StartsWith("TurboHTTP.", StringComparison.Ordinal))
                        Assert.That(packageAssemblies, Does.Contain(name), file + ": no asmdef named " + name);
                    else
                        Assert.That(AllowedFrameworkAssemblies, Does.Contain(name), file + ": unknown assembly " + name);
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

            var withoutOptional = TurboHttpLinkerProcessor.BuildMergedLinkXml(
                _packageRoot, name => name != BouncyCastleAssembly && name != "TurboHTTP.JSON");
            var names = AssemblyNames(withoutOptional);
            Assert.That(names, Does.Not.Contain(BouncyCastleAssembly));
            Assert.That(names, Does.Not.Contain("TurboHTTP.JSON"));
            AssertPreservesAll(withoutOptional, "TurboHTTP.Transport", "TurboHTTP.Transport.RawSocketTransport");
            AssertPreservesAll(withoutOptional, "System", "System.Net.Security.SslStream");
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
