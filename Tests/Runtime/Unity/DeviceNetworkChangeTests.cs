#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// #99 device rows: network change (Wi-Fi to cellular), airplane mode and back to Wi-Fi, with the same
    /// clients (and so the same pooled connections) throughout. A person changes the network when the screen
    /// shows the instruction and the console logs <c>TURBOHTTP_NETWORK_STEP</c>; each phase starts when the route to a
    /// public destination changes (its source address; Application.internetReachability does not follow changes). After a switch the request on the
    /// old pool must succeed (stale connections are not reused into a hang); with no network it must fail
    /// fast as a <see cref="UHttpException"/>. Run by name on a device: <c>--filter DeviceNetworkChangeTests
    /// --integration</c>.
    /// </summary>
    [Category("Integration")]
    [Category("ExternalNetwork")]
    public sealed class DeviceNetworkChangeTests
    {
        private const string Url = "https://www.google.com/";
        private static readonly TimeSpan RequestBound = TimeSpan.FromSeconds(30);
        private const float StepBoundSeconds = 600f;
        private const float SettleSeconds = 3f;

        [UnityTest, Explicit("a person switches Wi-Fi, cellular and airplane mode during the run (#99)")]
        [Category("DeviceNetwork")]
        [Timeout(1800000)] // UnityTest's default 180 s cannot wait for three manual network changes
        public IEnumerator SameClients_AcrossWifiCellularAirplaneAndBack_RecoverAndFailFast()
        {
            RawSocketTransport.EnsureRegistered();
            // Application.internetReachability does not follow a change while the player runs (iOS 27,
            // Unity 2021.3: it stayed ReachableViaLocalAreaNetwork with Wi-Fi off), so the steps follow the
            // route itself: the source address iOS picks for a public destination.
            string wifi = RouteSource();
            Debug.Log("TURBOHTTP_NETWORK_ROUTE start source=" + (wifi ?? "none") + " reachability=" + Application.internetReachability);
            Assert.IsNotNull(wifi, "no route at the start; start the run on Wi-Fi");
            Assert.IsTrue(IsPrivateLan(wifi), "start the run on Wi-Fi (source " + wifi + " is not a LAN address)");

            var banner = new GameObject("network-step-banner").AddComponent<StepBanner>();
            StepBanner.Text = "Network test running: keep Wi-Fi on";
            var clients = new[]
            {
                (Name: "SslStream", Client: new UHttpClient(new UHttpClientOptions { TlsBackend = TlsBackend.SslStream })),
                (Name: "BouncyCastle", Client: new UHttpClient(new UHttpClientOptions { TlsBackend = TlsBackend.BouncyCastle }))
            };
            try
            {
                yield return Phase("wifi", clients, expectSuccess: true);

                yield return Step(1, "turn Wi-Fi off (cellular data stays on)", src => src != null && src != wifi);
                yield return Phase("cellular", clients, expectSuccess: true);

                yield return Step(2, "turn Airplane Mode on", src => src == null);
                yield return Phase("airplane", clients, expectSuccess: false);

                yield return Step(3, "turn Airplane Mode off and Wi-Fi on", src => src == wifi);
                yield return Phase("wifi-again", clients, expectSuccess: true);

                Debug.Log("TURBOHTTP_NETWORK_DONE");
                StepBanner.Text = "Network test done";
            }
            finally
            {
                UnityEngine.Object.Destroy(banner.gameObject);
                foreach (var c in clients)
                    c.Client.Dispose();
            }
        }

        /// <summary>
        /// #99 NAT64 row (RFC 6147/6146; Apple's IPv6-only review network): run while the device is on a
        /// DNS64/NAT64 network (macOS Internet Sharing, "Create NAT64 Network"). Requires DNS64 to be present
        /// (ipv4only.arpa resolves to IPv6 only, RFC 7050), then a DNS name and an IPv4 literal must connect on
        /// both backends; the literal needs the RFC 7050 prefix synthesis (#167).
        /// </summary>
        [UnityTest, Explicit("needs an IPv6-only DNS64/NAT64 network (#99)")]
        [Category("DeviceNetwork")]
        [Timeout(600000)]
        public IEnumerator Nat64Network_DnsNameAndIpv4Literal_ConnectOnBothBackends()
        {
            RawSocketTransport.EnsureRegistered();
            IPAddress[] resolved = null;
            var probe = Task.Run(async () => resolved = await Dns.GetHostAddressesAsync("ipv4only.arpa").ConfigureAwait(false));
            yield return new WaitUntil(() => probe.IsCompleted);
            Assert.IsFalse(probe.IsFaulted, "ipv4only.arpa did not resolve: " + probe.Exception?.GetBaseException());
            var addresses = string.Join(",", Array.ConvertAll(resolved, a => a.ToString()));
            Debug.Log("TURBOHTTP_NAT64_DNS64 ipv4only.arpa=" + addresses);
            Assert.IsTrue(Array.TrueForAll(resolved, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6),
                "not an IPv6-only DNS64 network: ipv4only.arpa resolved to " + addresses);

            foreach (var backend in new[] { TlsBackend.SslStream, TlsBackend.BouncyCastle })
            {
                using var client = new UHttpClient(new UHttpClientOptions { TlsBackend = backend });
                foreach (var url in new[] { Url, "https://1.1.1.1/" })
                {
                    var outcome = new Outcome();
                    var task = Task.Run(() => SendAsync(client, url, outcome));
                    float deadline = Time.realtimeSinceStartup + (float)RequestBound.TotalSeconds + 10f;
                    yield return new WaitUntil(() => task.IsCompleted || Time.realtimeSinceStartup > deadline);
                    Assert.IsTrue(task.IsCompleted, backend + " " + url + ": the request did not end within its bound");
                    Debug.Log("TURBOHTTP_NAT64_RESULT backend=" + backend + " url=" + url + " status=" + (int)outcome.Status
                        + " error=" + (outcome.Failure == null ? "none" : Describe(outcome.Failure)) + " ms=" + outcome.Milliseconds);
                    Assert.IsNull(outcome.Failure, backend + " " + url + ": " + outcome.Failure);
                    Assert.AreNotEqual((HttpStatusCode)0, outcome.Status);
                }
            }
        }

        private static IEnumerator Step(int n, string instruction, Func<string, bool> reached)
        {
            Debug.Log("TURBOHTTP_NETWORK_STEP " + n + " " + instruction);
            StepBanner.Text = "STEP " + n + ": " + instruction;
            float deadline = Time.realtimeSinceStartup + StepBoundSeconds;
            string source = RouteSource(), last = source;
            while (!reached(source) && Time.realtimeSinceStartup <= deadline)
            {
                float next = Time.realtimeSinceStartup + 0.5f;
                yield return new WaitUntil(() => Time.realtimeSinceStartup > next);
                source = RouteSource();
                if (source != last)
                    Debug.Log("TURBOHTTP_NETWORK_ROUTE source " + (last ?? "none") + " -> " + (source ?? "none"));
                last = source;
            }

            Assert.IsTrue(reached(source), "step " + n + " (" + instruction + ") never happened; source " + (source ?? "none"));
            Debug.Log("TURBOHTTP_NETWORK_ROUTE step=" + n + " source=" + (source ?? "none")
                + " unity-reachability=" + Application.internetReachability);

            StepBanner.Text = "Step " + n + " seen: sending requests, wait";
            // Let the new route settle before the requests.
            float settle = Time.realtimeSinceStartup + SettleSeconds;
            yield return new WaitUntil(() => Time.realtimeSinceStartup > settle);
        }

        // The source address of the route to a public resolver, IPv4 first, then IPv6 (a cellular network may
        // be IPv6-only); null when neither has a route. A UDP connect sends nothing.
        private static string RouteSource()
        {
            foreach (var destination in new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:4860:4860::8888") })
            {
                try
                {
                    using var socket = new System.Net.Sockets.Socket(destination.AddressFamily,
                        System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                    socket.Connect(destination, 53);
                    return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
                }
                catch (System.Net.Sockets.SocketException)
                {
                }
            }

            return null;
        }

        private static bool IsPrivateLan(string address) =>
            address.StartsWith("192.168.", StringComparison.Ordinal) || address.StartsWith("10.", StringComparison.Ordinal)
            || (address.StartsWith("172.", StringComparison.Ordinal) && int.TryParse(address.Split('.')[1], out var b) && b >= 16 && b <= 31);

        // Sends one GET per client; on success the first attempt may fail once (a connection the switch
        // killed), and the second must succeed. Without a network every attempt must fail as UHttpException
        // inside the request bound.
        private static IEnumerator Phase(string name, (string Name, UHttpClient Client)[] clients, bool expectSuccess)
        {
            foreach (var c in clients)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    var outcome = new Outcome();
                    var task = Task.Run(() => SendAsync(c.Client, Url, outcome));
                    float deadline = Time.realtimeSinceStartup + (float)RequestBound.TotalSeconds + 10f;
                    yield return new WaitUntil(() => task.IsCompleted || Time.realtimeSinceStartup > deadline);
                    Assert.IsTrue(task.IsCompleted, name + "/" + c.Name + ": the request did not end within its bound");

                    Debug.Log("TURBOHTTP_NETWORK_RESULT phase=" + name + " client=" + c.Name + " attempt=" + attempt
                        + " reachability=" + Application.internetReachability + " status=" + (int)outcome.Status
                        + " error=" + (outcome.Failure == null ? "none" : Describe(outcome.Failure))
                        + " ms=" + outcome.Milliseconds);

                    if (!expectSuccess)
                    {
                        Assert.IsInstanceOf<UHttpException>(outcome.Failure,
                            name + "/" + c.Name + ": without a network the request must fail as UHttpException");
                        break;
                    }

                    if (outcome.Failure == null)
                    {
                        Assert.AreEqual(HttpStatusCode.OK, outcome.Status, name + "/" + c.Name);
                        break;
                    }

                    Assert.AreEqual(1, attempt, name + "/" + c.Name + ": failed twice: " + outcome.Failure);
                }
            }
        }

        private static async Task SendAsync(UHttpClient client, string url, Outcome outcome)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await client.Get(url).WithTimeout(RequestBound).SendBufferedAsync()
                    .ConfigureAwait(false);
                outcome.Status = response.StatusCode;
            }
            catch (Exception ex)
            {
                outcome.Failure = ex;
            }

            outcome.Milliseconds = clock.ElapsedMilliseconds;
        }

        // The cue for the person running the test: the current instruction in large text over the player
        // (Handheld.Vibrate is not in standalone player builds, and phones may have vibration off).
        private sealed class StepBanner : MonoBehaviour
        {
            internal static volatile string Text = "";
            private GUIStyle _style;

            private void OnGUI()
            {
                _style ??= new GUIStyle(GUI.skin.label) { fontSize = Screen.height / 18, wordWrap = true };
                GUI.Label(new Rect(Screen.width * 0.05f, Screen.height * 0.3f, Screen.width * 0.9f, Screen.height * 0.4f), Text, _style);
            }
        }

        private static string Describe(Exception ex) =>
            ex is UHttpException http ? "UHttpException:" + http.HttpError.Type : ex.GetType().Name;

        private sealed class Outcome
        {
            public HttpStatusCode Status;
            public Exception Failure;
            public long Milliseconds;
        }
    }
}
#endif
