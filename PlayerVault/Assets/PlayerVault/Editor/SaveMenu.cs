using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayerVault.Editor
{
    /// <summary>
    /// Menu items for finding and clearing saves while developing a game. To reset one player,
    /// use <b>Delete Save</b> on the VaultBehaviour's context menu instead.
    /// </summary>
    public static class SaveMenu
    {
        static string SaveFolder => new JsonFileStorage().RootDirectory;

        [MenuItem("Tools/PlayerVault/Show Save Folder")]
        static void ShowSaveFolder()
        {
            var folder = SaveFolder;
            EditorUtility.RevealInFinder(Directory.Exists(folder) ? folder : Application.persistentDataPath);
        }

        [MenuItem("Tools/PlayerVault/Delete All Saves...")]
        static void DeleteAllSaves()
        {
            var folder = SaveFolder;
            if (!Directory.Exists(folder))
            {
                Debug.Log($"[PlayerVault] There are no saves in {folder}.");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Delete all PlayerVault saves?",
                    $"Every player's balances and claim history in\n{folder}\nwill be deleted. " +
                    "Rewards already claimed can then be claimed again.",
                    "Delete", "Cancel"))
                return;

            Directory.Delete(folder, recursive: true);
            Debug.Log($"[PlayerVault] Deleted every save in {folder}.");
        }

        /// <summary>An open vault would write its state straight back, so only outside play mode.</summary>
        [MenuItem("Tools/PlayerVault/Delete All Saves...", validate = true)]
        static bool CanDeleteAllSaves() => !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
