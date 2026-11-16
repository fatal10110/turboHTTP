using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

namespace TurboHTTP.Samples.JsonApi
{
    [Serializable]
    public class Post
    {
        public int userId;
        public int id;
        public string title;
        public string body;
    }

    [Serializable]
    public class Comment
    {
        public int postId;
        public int id;
        public string name;
        public string email;
        public string body;
    }

    /// <summary>
    /// Example of working with a REST API using JSON.
    /// Uses JSONPlaceholder (https://jsonplaceholder.typicode.com)
    /// </summary>
    public class JsonApiExample : MonoBehaviour
    {
        private UHttpClient _client;
        private CancellationTokenSource _cts;

        void Start()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://jsonplaceholder.typicode.com"
            };
            _client = new UHttpClient(options);
            _cts = new CancellationTokenSource();

            RunExamples();
        }

        // async void is only the Unity entry point: everything that can throw is caught here.
        async void RunExamples()
        {
            try
            {
                await GetAllPosts();
                await GetSinglePost();
                await CreatePost();
                await UpdatePost();
                await DeletePost();
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

        async Task GetAllPosts()
        {
            Debug.Log("=== Get All Posts ===");

            var posts = await _client.GetJsonAsync<Post[]>("/posts", _cts.Token);

            Debug.Log($"Fetched {posts.Length} posts");
            if (posts.Length > 0)
            {
                Debug.Log($"First post: {posts[0].title}");
            }
        }

        async Task GetSinglePost()
        {
            Debug.Log("=== Get Single Post ===");

            var post = await _client.GetJsonAsync<Post>("/posts/1", _cts.Token);

            Debug.Log($"Post {post.id}: {post.title}");
            Debug.Log($"Body: {post.body}");
        }

        async Task CreatePost()
        {
            Debug.Log("=== Create Post ===");

            var newPost = new Post
            {
                userId = 1,
                title = "My New Post",
                body = "This is the content of my post"
            };

            var created = await _client.PostJsonAsync<Post, Post>("/posts", newPost, _cts.Token);

            Debug.Log($"Created post with ID: {created.id}");
        }

        async Task UpdatePost()
        {
            Debug.Log("=== Update Post ===");

            var updatedPost = new Post
            {
                id = 1,
                userId = 1,
                title = "Updated Title",
                body = "Updated content"
            };

            var result = await _client.PutJsonAsync<Post, Post>("/posts/1", updatedPost, _cts.Token);

            Debug.Log($"Updated post: {result.title}");
        }

        async Task DeletePost()
        {
            Debug.Log("=== Delete Post ===");

            using var response = await _client.Delete("/posts/1").SendBufferedAsync(_cts.Token);

            Debug.Log($"Delete status: {response.StatusCode}");
        }

        void OnDestroy()
        {
            _cts?.Cancel();
        }
    }
}
