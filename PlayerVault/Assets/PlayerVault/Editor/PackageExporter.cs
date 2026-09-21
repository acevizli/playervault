using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayerVault.Editor
{
    /// <summary>
    /// Exports the SDK as a .unitypackage for consumption by a game project.
    /// Deliberately exports only Assets/PlayerVault/Runtime — tests, this editor
    /// script and the samples are development artifacts, not part of the SDK a
    /// game imports.
    /// </summary>
    public static class PackageExporter
    {
        const string SourceFolder = "Assets/PlayerVault/Runtime";
        const string OutputName = "PlayerVault.unitypackage";

        [MenuItem("PlayerVault/Export .unitypackage")]
        public static void Export()
        {
            var outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", OutputName));

            AssetDatabase.ExportPackage(
                SourceFolder,
                outputPath,
                ExportPackageOptions.Recurse);

            Debug.Log($"[PlayerVault] Exported to {outputPath}");
        }
    }
}
