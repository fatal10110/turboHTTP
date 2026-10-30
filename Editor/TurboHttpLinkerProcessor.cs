using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Passes the package's <c>Runtime/**/link.xml</c> files to UnityLinker. UnityLinker does not
    /// read <c>link.xml</c> files inside UPM packages on its own, so without this processor the
    /// reflection targets they preserve are stripped under Medium/High managed stripping.
    /// </summary>
    /// <remarks>
    /// Each <c>link.xml</c> belongs to the assembly of the nearest <c>.asmdef</c> above it and is
    /// merged only when that assembly is part of the build, so module rules (for example the
    /// whole-assembly BouncyCastle preserve) apply only when the module ships.
    /// </remarks>
    internal sealed class TurboHttpLinkerProcessor : IUnityLinkerProcessor
    {
        internal const string PackageName = "com.turbohttp.complete";
        private const string LinkXmlFileName = "link.xml";
        private const string OutputFileName = "TurboHTTP.link.xml";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var packageRoot = FindPackageRoot();
            if (packageRoot == null)
            {
                throw new BuildFailedException(
                    "[TurboHTTP] Could not locate the " + PackageName + " package folder, so its link.xml " +
                    "rules cannot be passed to UnityLinker and stripped reflection targets would fail at runtime.");
            }

            var inputDirectory = data?.inputDirectory;
            Func<string, bool> isAssemblyInBuild;
            if (string.IsNullOrEmpty(inputDirectory) || !Directory.Exists(inputDirectory))
                isAssemblyInBuild = _ => true;
            else
                isAssemblyInBuild = name => File.Exists(Path.Combine(inputDirectory, name + ".dll"));

            var merged = BuildMergedLinkXml(packageRoot, isAssemblyInBuild);

            var outputDirectory = Path.Combine(Path.GetFullPath("Temp"), "TurboHTTP");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, OutputFileName);
            merged.Save(outputPath);
            return outputPath;
        }

        /// <summary>
        /// Absolute path of the package root (the folder holding <c>package.json</c> and
        /// <c>Runtime/</c>), or null when it cannot be found.
        /// </summary>
        internal static string FindPackageRoot()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(TurboHttpLinkerProcessor).Assembly);
            if (packageInfo != null && IsPackageRoot(packageInfo.resolvedPath))
                return Path.GetFullPath(packageInfo.resolvedPath);

            // Copy embedded under Assets/: Editor/TurboHTTP.Editor.asmdef sits one level below the root.
            var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(
                typeof(TurboHttpLinkerProcessor).Assembly.GetName().Name);
            if (string.IsNullOrEmpty(asmdefPath))
                return null;

            var editorDirectory = Path.GetDirectoryName(Path.GetFullPath(asmdefPath));
            var root = editorDirectory == null ? null : Path.GetDirectoryName(editorDirectory);
            return IsPackageRoot(root) ? root : null;
        }

        /// <summary>Every <c>link.xml</c> under <c>Runtime/</c>, in ordinal path order.</summary>
        internal static List<string> FindLinkXmlFiles(string packageRoot)
        {
            var runtimeDirectory = Path.Combine(packageRoot, "Runtime");
            var files = new List<string>(Directory.GetFiles(runtimeDirectory, LinkXmlFileName, SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// Name of the assembly defined by the nearest <c>.asmdef</c> in the folder of
        /// <paramref name="linkXmlPath"/> or one of its parents below <paramref name="packageRoot"/>;
        /// null when there is none.
        /// </summary>
        internal static string FindOwningAssembly(string linkXmlPath, string packageRoot)
        {
            var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directory = Path.GetDirectoryName(Path.GetFullPath(linkXmlPath));
            while (directory != null && directory.Length > root.Length
                   && directory.StartsWith(root, StringComparison.Ordinal))
            {
                var asmdefs = Directory.GetFiles(directory, "*.asmdef", SearchOption.TopDirectoryOnly);
                if (asmdefs.Length > 0)
                {
                    Array.Sort(asmdefs, StringComparer.Ordinal);
                    var definition = JsonUtility.FromJson<AssemblyDefinitionName>(File.ReadAllText(asmdefs[0]));
                    return string.IsNullOrEmpty(definition?.name) ? null : definition.name;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        /// <summary>
        /// Merges the package's <c>link.xml</c> files into one document, one <c>assembly</c>
        /// element per assembly name. A file whose owning assembly is not in the build
        /// (<paramref name="isAssemblyInBuild"/> returns false) is skipped.
        /// </summary>
        internal static XDocument BuildMergedLinkXml(string packageRoot, Func<string, bool> isAssemblyInBuild)
        {
            if (packageRoot == null)
                throw new ArgumentNullException(nameof(packageRoot));
            if (isAssemblyInBuild == null)
                throw new ArgumentNullException(nameof(isAssemblyInBuild));

            var linker = new XElement("linker");
            var assemblies = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var sources = new List<string>();

            foreach (var file in FindLinkXmlFiles(packageRoot))
            {
                var owner = FindOwningAssembly(file, packageRoot);
                if (owner != null && !isAssemblyInBuild(owner))
                    continue;

                var root = XDocument.Load(file).Root;
                if (root == null || root.Name.LocalName != "linker")
                    throw new InvalidDataException("[TurboHTTP] " + file + " has no <linker> root element.");

                sources.Add(file.Substring(packageRoot.Length).TrimStart('/', '\\').Replace('\\', '/'));

                foreach (var element in root.Elements())
                {
                    var fullname = element.Name.LocalName == "assembly"
                        ? (string)element.Attribute("fullname")
                        : null;
                    if (fullname == null)
                    {
                        linker.Add(new XElement(element));
                        continue;
                    }

                    if (assemblies.TryGetValue(fullname, out var existing))
                    {
                        foreach (var attribute in element.Attributes())
                        {
                            if (existing.Attribute(attribute.Name) == null)
                                existing.SetAttributeValue(attribute.Name, attribute.Value);
                        }

                        existing.Add(new XElement(element).Nodes());
                        continue;
                    }

                    var copy = new XElement(element);
                    assemblies.Add(fullname, copy);
                    linker.Add(copy);
                }
            }

            linker.AddFirst(new XComment(
                " Generated by TurboHTTP.Editor.TurboHttpLinkerProcessor from: " + string.Join(", ", sources) + " "));
            return new XDocument(linker);
        }

        private static bool IsPackageRoot(string path)
        {
            return !string.IsNullOrEmpty(path)
                   && File.Exists(Path.Combine(path, "package.json"))
                   && Directory.Exists(Path.Combine(path, "Runtime"));
        }

        [Serializable]
        private sealed class AssemblyDefinitionName
        {
#pragma warning disable CS0649 // Assigned by JsonUtility.
            public string name;
#pragma warning restore CS0649
        }
    }
}
