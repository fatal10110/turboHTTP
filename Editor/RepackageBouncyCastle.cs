using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Editor tool to repackage BouncyCastle source code with custom namespace.
    /// This prevents conflicts with other Unity plugins that use standard BouncyCastle.
    /// </summary>
    public class RepackageBouncyCastle
    {
        private const string SourceDir = "Assets/TurboHTTP/ThirdParty/BouncyCastle-Source";
        private const string TargetDir = "Assets/TurboHTTP/Runtime/Transport/BouncyCastle/Lib";

        [MenuItem("Tools/TurboHTTP/Repackage BouncyCastle")]
        public static void Repackage()
        {
            if (!Directory.Exists(SourceDir))
            {
                EditorUtility.DisplayDialog(
                    "BouncyCastle Source Not Found",
                    $"Please download BouncyCastle source (v2.2.1+) from GitHub and extract to:\n\n{SourceDir}",
                    "OK");
                return;
            }

            int fileCount = 0;
            var files = Directory.GetFiles(SourceDir, "*.cs", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var content = File.ReadAllText(file);

                // Replace namespace declarations
                content = Regex.Replace(content,
                    @"namespace Org\.BouncyCastle",
                    "namespace TurboHTTP.SecureProtocol.Org.BouncyCastle");

                // Replace using statements
                content = Regex.Replace(content,
                    @"using Org\.BouncyCastle",
                    "using TurboHTTP.SecureProtocol.Org.BouncyCastle");

                // Save to target directory maintaining relative path structure
                var relativePath = file.Substring(SourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var targetPath = Path.Combine(TargetDir, relativePath);
                var targetDirectory = Path.GetDirectoryName(targetPath);

                if (!Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                File.WriteAllText(targetPath, content);
                fileCount++;
            }

            AssetDatabase.Refresh();
            UnityEngine.Debug.Log($"BouncyCastle repackaged successfully! Processed {fileCount} files.");
            EditorUtility.DisplayDialog(
                "Repackage Complete",
                $"Successfully repackaged {fileCount} BouncyCastle source files.\n\nNamespace changed to:\nTurboHTTP.SecureProtocol.Org.BouncyCastle.*",
                "OK");
        }
    }
}
