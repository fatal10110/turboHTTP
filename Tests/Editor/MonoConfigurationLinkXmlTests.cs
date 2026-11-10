using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NUnit.Framework;
using TurboHTTP.Editor;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// #450: on Mono players, SslStream's first handshake runs ServicePointManager's type initializer,
    /// which creates the configuration host and the system.net/connectionManagement types by reflection.
    /// The package rules must keep them at every Managed Stripping Level, or every SslStream request fails.
    /// </summary>
    [TestFixture]
    public class MonoConfigurationLinkXmlTests
    {
        private const string ConfigurationAssembly = "System.Configuration";
        private const string ConfigurationHost = "System.Configuration.ExeConfigurationHost";
        private const string DefaultConstructor = "System.Void .ctor()";

        private string _packageRoot;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _packageRoot = TurboHttpLinkerProcessor.FindPackageRoot();
            Assert.That(_packageRoot, Is.Not.Null, "package root of " + TurboHttpLinkerProcessor.PackageName);
        }

        [Test]
        public void MergedLinkXml_PreservesTheConfigurationHostWhole()
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            var type = FindType(merged, ConfigurationAssembly, ConfigurationHost);
            Assert.That((string)type.Attribute("preserve"), Is.EqualTo("all"),
                "the host is reached only through its interface, so it keeps every member");
        }

        [TestCase("System.Net.Configuration.NetSectionGroup")]
        [TestCase("System.Net.Configuration.ConnectionManagementSection")]
        [TestCase("System.Net.Configuration.ConnectionManagementElementCollection")]
        public void MergedLinkXml_PreservesTheConstructorTheConfigurationSystemCalls(string typeName)
        {
            var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(_packageRoot, _ => true);

            var signatures = FindType(merged, "System", typeName).Elements("method")
                .Select(m => (string)m.Attribute("signature"));
            Assert.That(signatures, Does.Contain(DefaultConstructor));
        }

        [TestCase(ConfigurationHost, ConfigurationAssembly)]
        [TestCase("System.Net.Configuration.NetSectionGroup", "System")]
        [TestCase("System.Net.Configuration.ConnectionManagementSection", "System")]
        [TestCase("System.Net.Configuration.ConnectionManagementElementCollection", "System")]
        public void ConfigurationType_HasTheParameterlessConstructorTheRuleNames(string typeName, string assemblyName)
        {
            // The Editor runs Mono's full class library, which has the same configuration system as a Mono player.
            var type = Type.GetType(typeName + ", " + assemblyName, throwOnError: false);
            Assert.That(type, Is.Not.Null, typeName + " in " + assemblyName);

            var constructor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            Assert.That(constructor, Is.Not.Null, typeName + " has no parameterless constructor");
        }

        [Test]
        public void MergedLinkXml_WithoutTheConfigurationAssemblyInTheBuild_MarksItIgnoreIfMissing()
        {
            // IL2CPP's class library has no configuration host: its rule must not fail that linker run.
            var inputDirectory = Path.Combine(Path.GetTempPath(), "TurboHttpLinkerTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(inputDirectory);
            try
            {
                foreach (var name in new[] { "TurboHTTP.Core", "TurboHTTP.Transport", "System" })
                    File.WriteAllBytes(Path.Combine(inputDirectory, name + ".dll"), Array.Empty<byte>());

                var merged = TurboHttpLinkerProcessor.BuildMergedLinkXml(
                    _packageRoot, TurboHttpLinkerProcessor.CreateAssemblyProbe(inputDirectory));

                var assembly = merged.Descendants("assembly")
                    .Single(a => (string)a.Attribute("fullname") == ConfigurationAssembly);
                Assert.That((string)assembly.Attribute("ignoreIfMissing"), Is.EqualTo("1"));
            }
            finally
            {
                Directory.Delete(inputDirectory, recursive: true);
            }
        }

        private static XElement FindType(XDocument linkXml, string assemblyName, string typeName)
        {
            var type = linkXml.Descendants("assembly")
                .Where(a => (string)a.Attribute("fullname") == assemblyName)
                .Elements("type")
                .SingleOrDefault(t => (string)t.Attribute("fullname") == typeName);
            Assert.That(type, Is.Not.Null, typeName + " is not preserved under " + assemblyName);
            return type;
        }
    }
}
