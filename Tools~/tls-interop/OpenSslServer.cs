using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace TurboHTTP.Tools.TlsInterop
{
    /// <summary>
    /// One OpenSSL 3 <c>s_server</c> process serving exactly one connection (<c>-naccept 1</c>) on an
    /// ephemeral loopback port, adapted from the bc-csharp strip smoke harness (#25). The caller supplies
    /// the identity and protocol flags (<c>-cert</c>, <c>-key</c>, <c>-tls1_3</c>, <c>-ciphersuites</c>,
    /// <c>-cipher</c>, <c>-www</c>, ...); the port is read from the <c>ACCEPT</c> line s_server prints. A
    /// missing or non-3.x OpenSSL throws, it never skips. Dispose kills a process that is still running and
    /// writes the command line, stdout and stderr to <c>&lt;logDir&gt;/&lt;name&gt;.log</c>.
    /// </summary>
    internal sealed class OpenSslServer : IDisposable
    {
        private const int StartTimeoutMillis = 10000;
        private const int ExitTimeoutMillis = 10000;

        private readonly Process _process;
        private readonly string _logPath;
        private readonly StringBuilder _output = new StringBuilder();
        private readonly ManualResetEventSlim _accepted = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _stdoutClosed = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _stderrClosed = new ManualResetEventSlim(false);
        private int _disposed;

        private OpenSslServer(Process process, string logPath)
        {
            _process = process;
            _logPath = logPath;
        }

        /// <summary><c>$TURBO_TLS_INTEROP_OPENSSL</c>, else <c>openssl</c> from PATH.</summary>
        public static string Binary
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("TURBO_TLS_INTEROP_OPENSSL");
                return string.IsNullOrEmpty(configured) ? "openssl" : configured;
            }
        }

        private static readonly Lazy<string> s_version = new Lazy<string>(() =>
        {
            string version = Run("version").Trim();
            if (!version.StartsWith("OpenSSL 3.", StringComparison.Ordinal))
                throw new InvalidOperationException("the TLS interop job needs OpenSSL 3.x; '" + Binary + " version' says: " + version);
            return version;
        });

        /// <summary>"OpenSSL 3.x.y ..."; throws when the binary is missing or not 3.x.</summary>
        public static string Version => s_version.Value;

        public int Port { get; private set; }

        /// <summary>Starts <c>openssl s_server -accept 127.0.0.1:0 -naccept 1 &lt;flags&gt;</c>, waits for ACCEPT.</summary>
        public static OpenSslServer Start(string logDir, string name, params string[] flags)
        {
            _ = Version;
            var info = NewStartInfo();
            foreach (string arg in new[] { "s_server", "-accept", "127.0.0.1:0", "-naccept", "1" })
                info.ArgumentList.Add(arg);
            foreach (string flag in flags)
                info.ArgumentList.Add(flag);

            var process = new Process { StartInfo = info };
            var server = new OpenSslServer(process, Path.Combine(logDir, name + ".log"));
            process.OutputDataReceived += (_, e) => server.OnLine(e.Data, server._stdoutClosed, parseAccept: true);
            process.ErrorDataReceived += (_, e) => server.OnLine(e.Data, server._stderrClosed, parseAccept: false);
            try
            {
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!server._accepted.Wait(StartTimeoutMillis))
                    throw new InvalidOperationException("openssl s_server printed no ACCEPT line within " + StartTimeoutMillis + " ms: " + server.Output);
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        /// <summary>stdout and stderr so far (s_server's connection report and errors).</summary>
        public string Output
        {
            get
            {
                lock (_output)
                    return _output.ToString();
            }
        }

        /// <summary>Waits for the exit after the one connection; returns the full output.</summary>
        public string WaitForExit()
        {
            if (!_process.WaitForExit(ExitTimeoutMillis) || !_stdoutClosed.Wait(ExitTimeoutMillis) || !_stderrClosed.Wait(ExitTimeoutMillis))
                throw new InvalidOperationException("openssl s_server still running " + ExitTimeoutMillis + " ms after its connection: " + Output);
            return Output;
        }

        /// <summary>Runs <c>openssl &lt;args&gt;</c> to completion and returns stdout and stderr; throws on a non-zero exit.</summary>
        public static string Run(params string[] args)
        {
            var info = NewStartInfo();
            foreach (string arg in args)
                info.ArgumentList.Add(arg);
            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("cannot run '" + Binary + "' (install OpenSSL 3 or set TURBO_TLS_INTEROP_OPENSSL)", ex);
            }

            using (process)
            {
                process.StandardInput.Close();
                var stderr = process.StandardError.ReadToEndAsync();
                string stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    throw new InvalidOperationException(Binary + " " + string.Join(" ", args) + " did not exit");
                }

                string all = stdout + stderr.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(Binary + " " + string.Join(" ", args) + " exited " + process.ExitCode + ": " + all);
                return all;
            }
        }

        private static ProcessStartInfo NewStartInfo()
        {
            return new ProcessStartInfo(Binary)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        private void OnLine(string line, ManualResetEventSlim closed, bool parseAccept)
        {
            if (line == null)
            {
                closed.Set();
                return;
            }

            lock (_output)
                _output.AppendLine(line);

            // "ACCEPT 127.0.0.1:41235" (OpenSSL 3 prints the bound address when -accept uses port 0).
            if (parseAccept && !_accepted.IsSet && line.StartsWith("ACCEPT ", StringComparison.Ordinal))
            {
                int colon = line.LastIndexOf(':');
                if (colon > 0 && int.TryParse(line.Substring(colon + 1), out int port) && port > 0)
                {
                    Port = port;
                    _accepted.Set();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                    _process.WaitForExit(ExitTimeoutMillis);
                }
            }
            catch (InvalidOperationException)
            {
                // Never started.
            }

            try
            {
                File.WriteAllText(_logPath, Binary + " " + string.Join(" ", _process.StartInfo.ArgumentList) + "\n" + Output);
            }
            catch (IOException)
            {
                // Logs are diagnostics only.
            }

            _process.Dispose();
            _accepted.Dispose();
        }
    }
}
