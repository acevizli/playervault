using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayerVault.Editor
{
    /// <summary>
    /// Exports the SDK as a .unitypackage for use in a game project.
    /// </summary>
    /// <remarks>
    /// The package contains Runtime, the readme and the sample. The tests and this script are
    /// only used to develop the SDK and are left out.
    /// </remarks>
    public static class PackageExporter
    {
        static readonly string[] SourcePaths =
        {
            "Assets/PlayerVault/Runtime",
            "Assets/PlayerVault/Samples",
            "Assets/PlayerVault/README.md"
        };

        const string OutputName = "PlayerVault.unitypackage";

        [MenuItem("PlayerVault/Export .unitypackage")]
        public static void Export()
        {
            var outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", OutputName));

            AssetDatabase.ExportPackage(
                SourcePaths,
                outputPath,
                ExportPackageOptions.Recurse);

            Debug.Log($"[PlayerVault] Exported to {outputPath}");
        }
    }
}
