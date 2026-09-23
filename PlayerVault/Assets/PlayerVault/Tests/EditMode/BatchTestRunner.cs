#if UNITY_EDITOR
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Runs the EditMode suite synchronously from the command line and prints a summary.
    /// </summary>
    /// <remarks>
    /// Development tool, not part of the package. Unity's <c>-runTests</c> switch and the
    /// async <c>TestRunnerApi</c> need the batchmode editor to keep running after the command
    /// returns, and on some machines they hang. This asks the Test Framework to run the suite
    /// synchronously inside one <c>-executeMethod</c> call instead. The tests run on the main
    /// thread, which the <see cref="VaultBehaviour"/> tests need to create GameObjects.
    /// </remarks>
    public static class BatchTestRunner
    {
        class Callbacks : ICallbacks
        {
            public readonly StringBuilder Failures = new StringBuilder();
            public int Passed, Failed, Skipped;

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) { }
            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;

                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        Passed++;
                        break;

                    case TestStatus.Skipped:
                    case TestStatus.Inconclusive:
                        Skipped++;
                        break;

                    default:
                        Failed++;
                        Failures.AppendLine($"FAIL  {result.FullName}")
                                .AppendLine($"      {result.Message}")
                                .AppendLine(Indent(result.StackTrace));
                        break;
                }
            }

            static string Indent(string text) =>
                string.IsNullOrEmpty(text) ? string.Empty : "      " + text.Replace("\n", "\n      ");
        }

        public static void RunEditMode()
        {
            var callbacks = new Callbacks();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);

            // The tests block on tasks. With Unity's context installed, a continuation posted
            // back to this thread would wait for the block to end, which it never does.
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            try
            {
                api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode })
                {
                    runSynchronously = true
                });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }

            Debug.Log($"##TESTS## passed={callbacks.Passed} failed={callbacks.Failed} skipped={callbacks.Skipped}");
            if (callbacks.Failed > 0) Debug.Log("##FAILURES##\n" + callbacks.Failures);

            EditorApplication.Exit(callbacks.Failed > 0 || callbacks.Passed == 0 ? 1 : 0);
        }
    }
}
#endif
