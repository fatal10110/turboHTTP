using System;
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
        private UHttpClient _client;

        void Start()
        {
            _client = new UHttpClient();
            RunExamples();
        }

        async void RunExamples()
        {
            await Example1_SimpleGet();
            await Example2_GetWithHeaders();
            await Example3_Post();
            await Example4_Put();
            await Example5_Delete();
            await Example6_ErrorHandling();
        }

        async Task Example1_SimpleGet()
        {
            Debug.Log("=== Example 1: Simple GET ===");

            using var response = await _client.Get("https://httpbin.org/get").SendBufferedAsync();

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
                .SendBufferedAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example3_Post()
        {
            Debug.Log("=== Example 3: POST ===");

            var data = new { name = "John Doe", age = 30 };

            using var response = await _client
                .Post("https://httpbin.org/post")
                .WithJsonBody(data)
                .SendBufferedAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example4_Put()
        {
            Debug.Log("=== Example 4: PUT ===");

            var data = new { id = 1, title = "Updated Title" };

            using var response = await _client
                .Put("https://httpbin.org/put")
                .WithJsonBody(data)
                .SendBufferedAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example5_Delete()
        {
            Debug.Log("=== Example 5: DELETE ===");

            using var response = await _client
                .Delete("https://httpbin.org/delete")
                .SendBufferedAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example6_ErrorHandling()
        {
            Debug.Log("=== Example 6: Error Handling ===");

            try
            {
                using var response = await _client
                    .Get("https://httpbin.org/status/404")
                    .SendBufferedAsync();

                response.EnsureSuccessStatusCode();
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Error: {ex.HttpError.Type} - {ex.Message}");
            }
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
