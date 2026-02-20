using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class MainThreadDispatcherTests
    {
        [UnityTest]
        public IEnumerator ExecuteAsync_FromWorkerThread_RunsOnMainThread()
        {
            var _ = MainThreadDispatcher.Instance;
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var executedThreadId = -1;

            var task = Task.Run(async () =>
            {
                await MainThreadDispatcher.ExecuteAsync(() =>
                {
                    executedThreadId = Thread.CurrentThread.ManagedThreadId;
                });
            });

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.AreEqual(mainThreadId, executedThreadId);
        }

        [UnityTest]
        public IEnumerator ExecuteAsync_WhenActionThrows_PropagatesException()
        {
            var _ = MainThreadDispatcher.Instance;
            var task = Task.Run(async () =>
            {
                await MainThreadDispatcher.ExecuteAsync(() =>
                {
                    throw new InvalidOperationException("boom");
                });
            });

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.Faulted, task.Status);
            var root = task.Exception?.GetBaseException();
            Assert.IsInstanceOf<InvalidOperationException>(root);
            Assert.AreEqual("boom", root?.Message);
        }

        [UnityTest]
        public IEnumerator IsMainThread_ReturnsFalseOnWorkerThread()
        {
            var _ = MainThreadDispatcher.Instance;
            var workerReportedMain = true;

            var task = Task.Run(() =>
            {
                workerReportedMain = MainThreadDispatcher.IsMainThread();
            });

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.IsTrue(MainThreadDispatcher.IsMainThread());
            Assert.IsFalse(workerReportedMain);
        }

        [UnityTest]
        public IEnumerator Execute_FromWorkerThread_ThrowsInvalidOperationException()
        {
            var _ = MainThreadDispatcher.Instance;
            Exception error = null;

            var task = Task.Run(() =>
            {
                try
                {
                    MainThreadDispatcher.Execute(() => { });
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.IsNotNull(error);
            Assert.IsInstanceOf<InvalidOperationException>(error);
            StringAssert.Contains("Use ExecuteAsync", error.Message);
        }
    }
}
