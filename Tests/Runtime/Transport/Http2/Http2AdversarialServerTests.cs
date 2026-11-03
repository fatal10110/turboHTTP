using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Tests.Transport.Http2.Adversarial;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// The scripted adversarial HTTP/2 server scenarios (#111) as NUnit tests for the host-editor run.
    /// The same scenarios run in PR CI as rows of the h2-interop job (Tools~/h2-interop/run.sh --only adversarial).
    /// </summary>
    [TestFixture]
    public class Http2AdversarialServerTests
    {
        private static IEnumerable<string> ScenarioNames => Http2AdversarialScenarios.Names;

        [TestCaseSource(nameof(ScenarioNames))]
        public void Scenario(string name)
        {
            AssertAsync.Run(async () =>
            {
                // Watchdog only: every scenario step waits on client frames, never on the clock.
                using var watchdog = new CancellationTokenSource(30000);
                await Http2AdversarialScenarios.Get(name)(watchdog.Token);
            });
        }
    }
}
