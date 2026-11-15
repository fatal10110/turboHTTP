using System;
using System.IO;
using System.Threading;
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
        private CancellationTokenSource _cts;

        void Start()
        {
            _client = new UHttpClient();
            _downloader = new FileDownloader(_client);
            _cts = new CancellationTokenSource();
            DownloadFile();
        }

        async void DownloadFile()
        {
            // Resume sends Range + If-Range, which needs a strong validator (ETag or Last-Modified)
            // and "Accept-Ranges: bytes" from the server (RFC 9110 section 13.1.5). httpbin's /bytes
            // sends neither, so every retry would restart from zero; this 1 MiB file sends both.
            var url = "https://proof.ovh.net/files/1Mb.dat";
            var savePath = Path.Combine(Application.persistentDataPath, "downloaded.bin");

            var options = new DownloadOptions
            {
                EnableResume = true,
                Progress = new Progress<DownloadProgress>(OnProgress)
            };

            try
            {
                var result = await _downloader.DownloadFileAsync(url, savePath, options, _cts.Token);

                Debug.Log($"Downloaded {result.FileSize} bytes in {result.ElapsedTime.TotalSeconds:F1}s");
                Debug.Log($"File saved to: {result.FilePath}");
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Download failed: {ex.HttpError.Type} - {ex.Message}");
            }
            catch (IOException ex)
            {
                Debug.LogError($"File error: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Destroyed while the download was running; the partial file stays for a later resume.
            }
        }

        void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _client?.Dispose();
        }

        private void OnProgress(DownloadProgress progress)
        {
            Debug.Log($"Downloading: {progress.Percentage:F1}% ({progress.BytesDownloaded}/{progress.TotalBytes} bytes), {progress.SpeedBytesPerSecond / 1024:F0} KB/s");
        }
    }
}
