using System;
using System.IO;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Files;
using UnityEngine;

namespace TurboHTTP.Samples.FileDownload
{
    /// <summary>
    /// Example of downloading files with progress tracking and resume support.
    /// </summary>
    public class FileDownloadExample : MonoBehaviour
    {
        private UHttpClient _client;
        private FileDownloader _downloader;

        void Start()
        {
            _client = new UHttpClient();
            _downloader = new FileDownloader(_client);
            DownloadFile();
        }

        async void DownloadFile()
        {
            var url = "https://httpbin.org/bytes/1048576";
            var savePath = Path.Combine(Application.persistentDataPath, "downloaded.bin");

            var options = new DownloadOptions
            {
                EnableResume = true,
                Progress = new Progress<DownloadProgress>(OnProgress)
            };

            try
            {
                var result = await _downloader.DownloadFileAsync(url, savePath, options);

                Debug.Log($"Downloaded {result.FileSize} bytes in {result.ElapsedTime.TotalSeconds:F1}s");
                Debug.Log($"File saved to: {result.FilePath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex);
            }
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }

        private void OnProgress(DownloadProgress progress)
        {
            Debug.Log($"Downloading: {progress.Percentage:F1}% ({progress.BytesDownloaded}/{progress.TotalBytes} bytes), {progress.SpeedBytesPerSecond / 1024:F0} KB/s");
        }
    }
}
