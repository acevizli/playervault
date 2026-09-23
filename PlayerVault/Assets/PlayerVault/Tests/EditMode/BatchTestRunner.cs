#if UNITY_EDITOR
using System;
using System.Text;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using UnityEditor;
using UnityEngine;

namespace PlayerVault.Tests
{
    /// <summary>
    /// Runs the EditMode suite synchronously from the command line and prints a summary.
    /// </summary>
    /// <remarks>
    /// Development tool, not part of the package. Unity's <c>-runTests</c> switch and the
    /// async <c>TestRunnerApi</c> need the batchmode editor to keep running after the command
    /// returns, and on some machines they hang. This runs NUnit directly inside one
    /// <c>-executeMethod</c> call instead. That only works because the tests are plain
    /// <c>[Test]</c> methods with no coroutines or scenes.
    /// </remarks>
    public static class BatchTestRunner
    {
        class Listener : ITestListener
        {
            public readonly StringBuilder Failures = new StringBuilder();
            public int Passed, Failed, Skipped;

            public void TestStarted(ITest test) { }

            public void TestFinished(ITestResult result)
            {
                if (result.Test.IsSuite) return;

                switch (result.ResultState.Status)
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

            public void TestOutput(TestOutput output) { }

            static string Indent(string text) =>
                string.IsNullOrEmpty(text) ? string.Empty : "      " + text.Replace("\n", "\n      ");
        }

        public static void RunEditMode()
        {
            var exitCode = 1;

            try
            {
                var runner = new NUnitTestAssemblyRunner(new DefaultTestAssemblyBuilder());
                runner.Load(typeof(BatchTestRunner).Assembly, new System.Collections.Generic.Dictionary<string, object>());

                var listener = new Listener();
                runner.Run(listener, TestFilter.Empty);

                Debug.Log($"##TESTS## passed={listener.Passed} failed={listener.Failed} skipped={listener.Skipped}");
                if (listener.Failed > 0) Debug.Log("##FAILURES##\n" + listener.Failures);

                exitCode = listener.Failed > 0 ? 1 : 0;
            }
            catch (Exception exception)
            {
                Debug.Log("##TESTS## runner threw: " + exception);
            }

            EditorApplication.Exit(exitCode);
        }
    }
}
#endif
