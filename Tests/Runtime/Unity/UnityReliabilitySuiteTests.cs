using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityReliabilitySuiteTests
    {
        [UnityTest]
        public System.Collections.IEnumerator DispatcherFlood_CompletesWithoutDeadlock()
        {
            var _ = MainThreadDispatcher.Instance;

            var tasks = new Task[128];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { }));
            }

            var all = Task.WhenAll(tasks);
            yield return new WaitUntil(() => all.IsCompleted);

            Assert.IsTrue(all.IsCompleted);
        }
    }
}
