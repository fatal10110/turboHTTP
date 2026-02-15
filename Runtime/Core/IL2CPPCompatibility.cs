using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Validates runtime compatibility with IL2CPP and AOT constraints.
    /// Runs a series of smoke tests on core language features to detect aggressive code stripping.
    /// </summary>
    public static class IL2CPPCompatibility
    {
        /// <summary>
        /// Runs all compatibility checks and returns a pass/fail result.
        /// </summary>
        /// <param name="report">Detailed diagnostics report.</param>
        /// <returns>True if all checks pass; otherwise, false.</returns>
        public static bool Validate(out string report)
        {
            var sb = new StringBuilder();
            bool allPassed = true;

            sb.AppendLine($"IL2CPP Validation Report (Platform: {PlatformInfo.GetPlatformDescription()})");
            sb.AppendLine(new string('-', 40));

            allPassed &= RunCheck("Async / await flow", CheckAsyncAwait, sb);
            allPassed &= RunCheck("Generic Virtual Methods", CheckGenericVirtualMethods, sb);
            allPassed &= RunCheck("Cancellation Tokens", CheckCancellation, sb);
            allPassed &= RunCheck("Reflection (Core)", CheckCoreReflection, sb);

            report = sb.ToString();
            return allPassed;
        }

        private static bool RunCheck(string name, Func<bool> check, StringBuilder report)
        {
            try
            {
                bool result = check();
                report.AppendLine($"[{ (result ? "PASS" : "FAIL") }] {name}");
                return result;
            }
            catch (Exception ex)
            {
                report.AppendLine($"[FAIL] {name}: Unhandled Exception {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static bool CheckAsyncAwait()
        {
            // Exercise a real async state machine on a worker thread to avoid main-thread deadlocks.
            var result = Task.Run(async () =>
            {
                await Task.Yield();
                return 1;
            }).GetAwaiter().GetResult();

            return result == 1;
        }

        private static bool CheckGenericVirtualMethods()
        {
            // Verify AOT generation for shared generic interfaces often used in networking
            IEquatable<int> box = 5;
            return box.Equals(5);
        }

        private static bool CheckCancellation()
        {
            // Verify CancellationTokenSource is not stripped/broken
            using var cts = new CancellationTokenSource();
            if (cts.Token.CanBeCanceled != true) return false;
            
            cts.Cancel();
            return cts.IsCancellationRequested;
        }

        private static bool CheckCoreReflection()
        {
            // Verify we can reflect on a visible type.
            // TurboHTTP Core minimizes reflection, but we need to ensure Type.GetType() works with assembly-qualified names.
            var assemblyQualified = typeof(PlatformInfo).AssemblyQualifiedName;
            if (string.IsNullOrEmpty(assemblyQualified))
                return false;

            var type = Type.GetType(assemblyQualified);
            return type == typeof(PlatformInfo);
        }
    }
}
