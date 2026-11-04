using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Bounded wait for the tasks of a loopback test server's <c>Dispose</c> (#349). On IL2CPP a
    /// pending accept or socket read is not always ended by stopping the listener or closing the
    /// socket, and an unbounded wait in <c>Dispose</c>, which usually runs on the Unity main thread
    /// outside any <see cref="AssertAsync"/> bound, stalls the whole run. A task that does not end
    /// within the bound is abandoned on the pool with any later fault observed.
    /// </summary>
    internal static class BoundedShutdown
    {
        /// <summary>
        /// Longest a test server's <c>Dispose</c> waits in total. Far above a normal shutdown, which
        /// ends as soon as the listener stops and the accepted sockets close.
        /// </summary>
        internal static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Waits for every task within one shared <paramref name="bound"/>. Ended tasks have their
        /// fault observed; tasks still running are abandoned (<see cref="AbandonableTask.Observe(Task)"/>)
        /// and reported once on the log. Returns true when every task ended in time. Never throws.
        /// </summary>
        internal static bool WaitAll(IReadOnlyList<Task> tasks, TimeSpan bound, string owner)
        {
            var clock = Stopwatch.StartNew();
            int abandoned = 0;
            for (int i = 0; i < tasks.Count; i++)
            {
                Task task = tasks[i];
                if (task == null)
                    continue;

                TimeSpan remaining = bound - clock.Elapsed;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                // The task's wait handle, as in AssertAsync: no awaiter and no Task.Wait (banned).
                if (task.IsCompleted || ((IAsyncResult)task).AsyncWaitHandle.WaitOne(remaining))
                {
                    _ = task.Exception;
                }
                else
                {
                    AbandonableTask.Observe(task);
                    abandoned++;
                }
            }

            if (abandoned == 0)
                return true;

            UnityEngine.Debug.LogWarning(
                owner + ": " + abandoned + " task(s) did not end within " + bound.TotalSeconds +
                " s of Dispose and were abandoned (#349).");
            return false;
        }

        /// <summary><see cref="WaitAll(IReadOnlyList{Task}, TimeSpan, string)"/> for one task.</summary>
        internal static bool Wait(Task task, TimeSpan bound, string owner)
        {
            return WaitAll(new[] { task }, bound, owner);
        }
    }
}
