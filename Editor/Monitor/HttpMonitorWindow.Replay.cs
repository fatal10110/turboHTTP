using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    public partial class HttpMonitorWindow
    {
        private static readonly HashSet<string> ReplaySkippedHeaders = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Host",
            "Content-Length",
            "Transfer-Encoding",
            "Connection",
            "Proxy-Connection"
        };

        private async void ReplayEvent(HttpMonitorEvent evt)
        {
            try
            {
                await ReplayEventAsync(evt);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TurboHTTP] Replay failed with unhandled exception: {ex}");
            }
        }

        private async Task ReplayEventAsync(HttpMonitorEvent evt)
        {
            if (!TryBuildReplayRequest(evt, out var request, out var warning, out var error))
            {
                Debug.LogError($"[TurboHTTP] Replay failed: {error}");
                return;
            }

            if (!string.IsNullOrEmpty(warning)
                && !EditorUtility.DisplayDialog(
                    "Replay HTTP Request",
                    warning + "\n\nContinue with replay?",
                    "Replay",
                    "Cancel"))
            {
                return;
            }

            try
            {
                using var client = new UHttpClient();
                var response = await client.SendAsync(request);
                Debug.Log(
                    $"[TurboHTTP] Replay completed: {(int)response.StatusCode} {response.StatusCode} " +
                    $"for {request.Method} {request.Uri}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TurboHTTP] Replay failed: {ex.Message}");
            }
        }

        private static bool TryBuildReplayRequest(
            HttpMonitorEvent evt,
            out UHttpRequest request,
            out string warning,
            out string error)
        {
            request = null;
            warning = string.Empty;
            error = string.Empty;

            if (evt == null)
            {
                error = "No monitor event is selected.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(evt.Url)
                || !Uri.TryCreate(evt.Url, UriKind.Absolute, out var uri))
            {
                error = "Captured URL is missing or invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(evt.Method)
                || !Enum.TryParse(evt.Method, ignoreCase: true, out HttpMethod method))
            {
                error = $"Unsupported HTTP method '{evt.Method ?? string.Empty}'.";
                return false;
            }

            var headers = new HttpHeaders();
            bool skippedTransportHeaders = false;
            if (evt.RequestHeaders != null)
            {
                foreach (var pair in evt.RequestHeaders)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    if (ReplaySkippedHeaders.Contains(pair.Key))
                    {
                        skippedTransportHeaders = true;
                        continue;
                    }

                    headers.Set(pair.Key, pair.Value ?? string.Empty);
                }
            }

            var body = evt.RequestBody.IsEmpty ? null : evt.RequestBody.ToArray();
            request = new UHttpRequest(method, uri, headers, body);

            warning = BuildReplayWarning(evt, skippedTransportHeaders, body == null || body.Length == 0);
            return true;
        }

        private static string BuildReplayWarning(
            HttpMonitorEvent evt,
            bool skippedTransportHeaders,
            bool bodyIsEmpty)
        {
            var warning = new StringBuilder();

            if (evt.Method == "POST" || evt.Method == "PATCH" || evt.Method == "DELETE")
            {
                AppendReplayWarning(
                    warning,
                    $"{evt.Method} is not idempotent. Replaying may cause side effects (duplicate creation, data modification).");
            }

            if (evt.IsRequestBodyTruncated)
            {
                AppendReplayWarning(
                    warning,
                    "Captured request body is truncated. Replay will send only captured preview bytes.");
            }

            if (evt.IsRequestBodyBinary)
            {
                AppendReplayWarning(
                    warning,
                    "Captured request body is binary. Replay sends captured bytes exactly as stored.");
            }

            if (skippedTransportHeaders)
            {
                AppendReplayWarning(
                    warning,
                    "Transport-managed headers (for example Host/Content-Length) are omitted and regenerated.");
            }

            if (bodyIsEmpty && evt.OriginalRequestBodySize > 0)
            {
                AppendReplayWarning(
                    warning,
                    "Captured request body is empty even though original request had a body.");
            }

            return warning.ToString();
        }

        private static void AppendReplayWarning(StringBuilder builder, string line)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append("- ");
            builder.Append(line);
        }
    }
}
