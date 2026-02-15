using System;
using System.Threading.Tasks;
using TurboHTTP.Core;
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

        void Start()
        {
            var options = new UHttpClientOptions
            {
                BaseUrl = "https://jsonplaceholder.typicode.com"
            };
            _client = new UHttpClient(options);

            RunExamples();
        }

        async void RunExamples()
        {
            await GetAllPosts();
            await GetSinglePost();
            await CreatePost();
            await UpdatePost();
            await DeletePost();
        }

        async Task GetAllPosts()
        {
            Debug.Log("=== Get All Posts ===");

            var posts = await _client.GetJsonAsync<Post[]>("/posts");

            Debug.Log($"Fetched {posts.Length} posts");
            if (posts.Length > 0)
            {
                Debug.Log($"First post: {posts[0].title}");
            }
        }

        async Task GetSinglePost()
        {
            Debug.Log("=== Get Single Post ===");

            var post = await _client.GetJsonAsync<Post>("/posts/1");

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

            var created = await _client.PostJsonAsync<Post, Post>("/posts", newPost);

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

            var result = await _client.PutJsonAsync<Post, Post>("/posts/1", updatedPost);

            Debug.Log($"Updated post: {result.title}");
        }

        async Task DeletePost()
        {
            Debug.Log("=== Delete Post ===");

            var response = await _client.Delete("/posts/1").SendAsync();

            Debug.Log($"Delete status: {response.StatusCode}");
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
