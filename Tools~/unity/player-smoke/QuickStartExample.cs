// README.md "Quick Start", copied verbatim (keep it in sync). Compiled into Assembly-CSharp by
// Tools~/unity/build-player.sh to prove the README compiles from a plain Assets/ script (#7 AC1);
// the component is never added to the smoke scene, so it sends nothing.
using System;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

[Serializable]
public class MyData
{
    public int id;
    public string name;
}

public class Example : MonoBehaviour
{
    private UHttpClient _client;

    private void Awake()
    {
        var options = new UHttpClientOptions
        {
            BaseUrl = "https://api.example.com"
        };
        _client = new UHttpClient(options);
    }

    private async void Start()
    {
        try
        {
            // GET request; dispose the response when you are done with it.
            using var response = await _client.Get("/data")
                .WithHeader("Accept", "application/json")
                .WithTimeout(TimeSpan.FromSeconds(10))
                .SendBufferedAsync();

            if (response.IsSuccessStatusCode)
            {
                MyData data = response.AsJson<MyData>();
                Debug.Log($"Received: {data.id}");
            }
            else
            {
                // 4xx/5xx are normal responses, not exceptions.
                Debug.LogError($"Error: {response.StatusCode}");
            }
        }
        catch (UHttpException ex)
        {
            // Network, TLS, timeout and protocol failures.
            Debug.LogError($"Request failed: {ex.HttpError.Type}");
        }
    }

    private void OnDestroy()
    {
        _client.Dispose();
    }
}
