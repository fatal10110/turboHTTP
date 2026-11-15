using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

namespace TurboHTTP.Samples.BasicUsage
{
    /// <summary>
    /// Basic usage examples for TurboHTTP.
    /// Demonstrates GET, POST, PUT, DELETE requests.
    /// Note: `SendBufferedAsync()` returns a `ValueTask<UHttpResponse>`; dispose every response (`using var`). 
    /// These samples use `Task` for simplicity, but in hot paths, 
    /// consider returning `ValueTask` or using `UniTask`.
    /// </summary>
    public class BasicUsageExample : MonoBehaviour
    {
        // WithJsonBody uses Unity's JsonUtility by default: [Serializable] classes with public
        // fields (anonymous types are not supported).
        [Serializable]
        public class PersonBody
        {
            public string name;
            public int age;
        }

        [Serializable]
        public class TitleBody
        {
            public int id;
            public string title;
        }

        private UHttpClient _client;
        private CancellationTokenSource _cts;

        void Start()
        {
            _client = new UHttpClient();
            _cts = new CancellationTokenSource();
            RunExamples();
        }

        // async void is only the Unity entry point: everything that can throw is caught here.
        async void RunExamples()
        {
            try
            {
                await Example1_SimpleGet();
                await Example2_GetWithHeaders();
                await Example3_Post();
                await Example4_Put();
                await Example5_Delete();
                await Example6_ErrorHandling();
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Request failed: {ex.HttpError.Type} - {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Destroyed while a request was in flight.
            }
            finally
            {
                // The entry point owns disposal, so no continuation can touch a disposed client.
                _cts?.Dispose();
                _cts = null;
                _client?.Dispose();
                _client = null;
            }
        }

        async Task Example1_SimpleGet()
        {
            Debug.Log("=== Example 1: Simple GET ===");

            using var response = await _client.Get("https://httpbin.org/get").SendBufferedAsync(_cts.Token);

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }

        async Task Example2_GetWithHeaders()
        {
            Debug.Log("=== Example 2: GET with Headers ===");

            using var response = await _client
                .Get("https://httpbin.org/headers")
                .WithHeader("X-Custom-Header", "my-value")
                .WithHeader("Accept", "application/json")
                .SendBufferedAsync(_cts.Token);

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example3_Post()
        {
            Debug.Log("=== Example 3: POST ===");

            var data = new PersonBody { name = "John Doe", age = 30 };

            using var response = await _client
                .Post("https://httpbin.org/post")
                .WithJsonBody(data)
                .SendBufferedAsync(_cts.Token);

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example4_Put()
        {
            Debug.Log("=== Example 4: PUT ===");

            var data = new TitleBody { id = 1, title = "Updated Title" };

            using var response = await _client
                .Put("https://httpbin.org/put")
                .WithJsonBody(data)
                .SendBufferedAsync(_cts.Token);

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example5_Delete()
        {
            Debug.Log("=== Example 5: DELETE ===");

            using var response = await _client
                .Delete("https://httpbin.org/delete")
                .SendBufferedAsync(_cts.Token);

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example6_ErrorHandling()
        {
            Debug.Log("=== Example 6: Error Handling ===");

            try
            {
                using var response = await _client
                    .Get("https://httpbin.org/status/404")
                    .SendBufferedAsync(_cts.Token);

                response.EnsureSuccessStatusCode();
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Error: {ex.HttpError.Type} - {ex.Message}");
            }
        }

        void OnDestroy()
        {
            // Only cancel here: RunExamples disposes the client once the in-flight request has unwound.
            _cts?.Cancel();
        }
    }
}
