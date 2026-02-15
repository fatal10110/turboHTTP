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
        [SerializeField] private UnityEngine.UI.Slider progressBar;
        [SerializeField] private UnityEngine.UI.Text statusText;

        private FileDownloader _downloader;

        void Start()
        {
            _downloader = new FileDownloader();
        }

        public async void DownloadImage()
        {
            var url = "https://via.placeholder.com/2000";
            var savePath = Path.Combine(Application.persistentDataPath, "downloaded_image.png");

            var options = new DownloadOptions
            {
                EnableResume = true,
                Progress = new Progress<DownloadProgress>(OnProgress)
            };

            try
            {
                if (statusText != null) statusText.text = "Downloading...";

                var result = await _downloader.DownloadFileAsync(url, savePath, options);

                if (statusText != null) statusText.text = $"Downloaded {result.FileSize} bytes in {result.ElapsedTime.TotalSeconds:F1}s";
                Debug.Log($"File saved to: {result.FilePath}");
            }
            catch (System.Exception ex)
            {
                if (statusText != null) statusText.text = $"Error: {ex.Message}";
                Debug.LogError(ex);
            }
        }

        private void OnProgress(DownloadProgress progress)
        {
            if (progressBar != null) progressBar.value = progress.Percentage / 100f;
            if (statusText != null) statusText.text = $"Downloading: {progress.Percentage:F1}% ({progress.BytesDownloaded}/{progress.TotalBytes} bytes)";
            Debug.Log($"Speed: {progress.SpeedBytesPerSecond / 1024:F0} KB/s");
        }
    }
}
