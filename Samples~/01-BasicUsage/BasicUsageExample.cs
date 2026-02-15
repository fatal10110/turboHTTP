using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Samples.BasicUsage
{
    /// <summary>
    /// Basic usage examples for TurboHTTP.
    /// Demonstrates GET, POST, PUT, DELETE requests.
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

            var response = await _client.Get("https://httpbin.org/get").SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }

        async Task Example2_GetWithHeaders()
        {
            Debug.Log("=== Example 2: GET with Headers ===");

            var response = await _client
                .Get("https://httpbin.org/headers")
                .WithHeader("X-Custom-Header", "my-value")
                .WithHeader("Accept", "application/json")
                .SendAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example3_Post()
        {
            Debug.Log("=== Example 3: POST ===");

            var data = new { name = "John Doe", age = 30 };

            var response = await _client
                .Post("https://httpbin.org/post")
                .WithJsonBody(data)
                .SendAsync();

            Debug.Log(response.GetBodyAsString());
        }

        async Task Example4_Put()
        {
            Debug.Log("=== Example 4: PUT ===");

            var data = new { id = 1, title = "Updated Title" };

            var response = await _client
                .Put("https://httpbin.org/put")
                .WithJsonBody(data)
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example5_Delete()
        {
            Debug.Log("=== Example 5: DELETE ===");

            var response = await _client
                .Delete("https://httpbin.org/delete")
                .SendAsync();

            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example6_ErrorHandling()
        {
            Debug.Log("=== Example 6: Error Handling ===");

            try
            {
                var response = await _client
                    .Get("https://httpbin.org/status/404")
                    .SendAsync();

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
