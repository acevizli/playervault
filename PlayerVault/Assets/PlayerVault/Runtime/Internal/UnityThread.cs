using System.Threading;
using UnityEngine;

namespace PlayerVault.Internal
{
    /// <summary>Which thread is Unity's main thread.</summary>
    internal static class UnityThread
    {
        static int s_mainThreadId = -1;

        /// <summary>True on the main thread. False when the main thread is not known yet.</summary>
        public static bool IsMain => Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        /// <summary>True only when the main thread is known and this is another thread.</summary>
        public static bool IsOtherThread => s_mainThreadId != -1 && !IsMain;

        /// <summary>Records the calling thread as the main thread. Call from a Unity callback.</summary>
        public static void Capture() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnPlayModeStart() => Capture();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void OnEditorLoad() => Capture();
#endif
    }
}
