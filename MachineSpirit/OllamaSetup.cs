// MachineSpirit/OllamaSetup.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// Ollama one-click auto-setup: detect → start server → pull model → ready.
    /// All operations are non-blocking (coroutine-based).
    /// </summary>
    public static class OllamaSetup
    {
        public enum SetupState { Idle, Checking, Starting, Pulling, Ready, Error }

        private static SetupState _state = SetupState.Idle;
        private static string _statusText = "";
        private static Process _serverProcess;

        public static SetupState State => _state;
        public static string StatusText => _statusText;

        /// <summary>
        /// Find the ollama executable path. Checks PATH first, then common install locations.
        /// </summary>
        private static string FindOllamaPath()
        {
            // 1) Check PATH via 'where' command
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ollama",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // 'where' may return multiple lines; take the first
                    string firstLine = output.Split('\n')[0].Trim();
                    proc.Dispose();
                    return firstLine;
                }
                proc.Dispose();
            }
            catch { /* where command failed — try known paths */ }

            // 2) Check common Windows install locations
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                System.IO.Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"),
                System.IO.Path.Combine(localAppData, "Ollama", "ollama.exe"),
                @"C:\Program Files\Ollama\ollama.exe",
                @"C:\Program Files (x86)\Ollama\ollama.exe",
            };

            foreach (string path in candidates)
            {
                if (System.IO.File.Exists(path)) return path;
            }

            return null;
        }

        /// <summary>
        /// Run the full auto-setup sequence as a coroutine.
        /// </summary>
        public static IEnumerator RunAutoSetup(string model)
        {
            _state = SetupState.Checking;
            _statusText = "Checking Ollama server...";

            // Step 1: Check if server is already running (HTTP — works regardless of PATH)
            bool serverRunning = false;
            var request = UnityWebRequest.Get("http://localhost:11434/api/tags");
            request.timeout = 3;
            yield return request.SendWebRequest();
            serverRunning = request.result == UnityWebRequest.Result.Success;
            request.Dispose();

            // Step 2: If server not running, find executable and start it
            if (!serverRunning)
            {
                _statusText = "Checking Ollama installation...";
                string ollamaPath = FindOllamaPath();

                if (ollamaPath == null)
                {
                    _state = SetupState.Error;
                    _statusText = "Ollama not found. Install from https://ollama.com";
                    yield break;
                }

                _state = SetupState.Starting;
                _statusText = "Starting Ollama server...";

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ollamaPath,
                        Arguments = "serve",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    _serverProcess = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _state = SetupState.Error;
                    _statusText = $"Failed to start Ollama: {ex.Message}";
                    yield break;
                }

                // Wait for server to become ready (up to 15 seconds)
                float deadline = Time.realtimeSinceStartup + 15f;
                bool started = false;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return new WaitForSeconds(1f);
                    var check = UnityWebRequest.Get("http://localhost:11434/api/tags");
                    check.timeout = 2;
                    yield return check.SendWebRequest();
                    started = check.result == UnityWebRequest.Result.Success;
                    check.Dispose();
                    if (started) break;
                }

                if (!started)
                {
                    _state = SetupState.Error;
                    _statusText = "Ollama server failed to start (timeout)";
                    yield break;
                }
            }

            // Step 4: Check if model exists
            _statusText = $"Checking model {model}...";
            bool modelExists = false;

            var tagReq = UnityWebRequest.Get("http://localhost:11434/api/tags");
            tagReq.timeout = 5;
            yield return tagReq.SendWebRequest();

            if (tagReq.result == UnityWebRequest.Result.Success)
            {
                // Response contains model names — simple string check
                string body = tagReq.downloadHandler.text;
                modelExists = body.Contains($"\"{model}\"") || body.Contains($"\"{model}:");
            }
            tagReq.Dispose();

            // Step 5: Pull model if not found
            if (!modelExists)
            {
                _state = SetupState.Pulling;
                _statusText = $"Downloading {model}... 0%";

                Process pullProc = null;
                bool pullDone = false;
                bool pullError = false;
                string errorMsg = "";

                try
                {
                    string pullExe = FindOllamaPath() ?? "ollama";
                    var psi = new ProcessStartInfo
                    {
                        FileName = pullExe,
                        Arguments = $"pull {model}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    pullProc = Process.Start(psi);

                    // Read stderr asynchronously (ollama outputs progress to stderr)
                    pullProc.ErrorDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string line = e.Data;

                        // Parse progress: "pulling abc123... 45% ▕████ ▏ 1.2 GB/2.6 GB"
                        var match = Regex.Match(line, @"(\d+)%");
                        if (match.Success)
                        {
                            _statusText = $"Downloading {model}... {match.Groups[1].Value}%";
                        }
                        else if (line.Contains("success"))
                        {
                            pullDone = true;
                        }
                        else if (line.Contains("error") || line.Contains("Error"))
                        {
                            pullError = true;
                            errorMsg = line;
                        }
                    };
                    pullProc.BeginErrorReadLine();

                    // Also read stdout
                    pullProc.OutputDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string line = e.Data;
                        var match = Regex.Match(line, @"(\d+)%");
                        if (match.Success)
                        {
                            _statusText = $"Downloading {model}... {match.Groups[1].Value}%";
                        }
                        else if (line.Contains("success"))
                        {
                            pullDone = true;
                        }
                    };
                    pullProc.BeginOutputReadLine();
                }
                catch (Exception ex)
                {
                    _state = SetupState.Error;
                    _statusText = $"Failed to pull model: {ex.Message}";
                    yield break;
                }

                // Wait for pull to complete (up to 30 minutes for large models)
                float pullDeadline = Time.realtimeSinceStartup + 1800f;
                while (!pullDone && !pullError && !pullProc.HasExited && Time.realtimeSinceStartup < pullDeadline)
                {
                    yield return new WaitForSeconds(0.5f);
                }

                if (pullProc.HasExited && pullProc.ExitCode != 0 && !pullDone)
                {
                    _state = SetupState.Error;
                    _statusText = $"Model pull failed: {(string.IsNullOrEmpty(errorMsg) ? "unknown error" : errorMsg)}";
                    pullProc.Dispose();
                    yield break;
                }

                if (pullError)
                {
                    _state = SetupState.Error;
                    _statusText = $"Model pull error: {errorMsg}";
                    pullProc.Dispose();
                    yield break;
                }

                pullProc.Dispose();
            }

            // Done!
            _state = SetupState.Ready;
            _statusText = $"Ready! Press F2 to start chatting.";
        }

        /// <summary>Reset state (e.g., when switching provider).</summary>
        public static void Reset()
        {
            _state = SetupState.Idle;
            _statusText = "";
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.58.0: Installed Model Detection
        // ════════════════════════════════════════════════════════════

        private static readonly List<string> _installedModels = new List<string>();
        private static bool _isFetchingModels;

        public static IReadOnlyList<string> InstalledModels => _installedModels;
        public static bool IsFetchingModels => _isFetchingModels;

        /// <summary>
        /// Fetch list of installed Ollama models via /api/tags endpoint.
        /// </summary>
        public static IEnumerator FetchInstalledModels()
        {
            if (_isFetchingModels) yield break;
            _isFetchingModels = true;

            var request = UnityWebRequest.Get("http://localhost:11434/api/tags");
            request.timeout = 5;
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(request.downloadHandler.text);
                    var models = json["models"];
                    _installedModels.Clear();

                    if (models != null)
                    {
                        foreach (var model in models)
                        {
                            // "name" field contains "model:tag" format (e.g., "llama3.2:latest")
                            string name = model["name"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                // Strip ":latest" tag for cleaner display
                                if (name.EndsWith(":latest"))
                                    name = name.Substring(0, name.Length - 7);
                                _installedModels.Add(name);
                            }
                        }
                    }

                    Main.LogDebug($"[MachineSpirit] Found {_installedModels.Count} installed Ollama models");
                }
                catch (System.Exception ex)
                {
                    Main.LogDebug($"[MachineSpirit] Failed to parse Ollama models: {ex.Message}");
                }
            }

            request.Dispose();
            _isFetchingModels = false;
        }
    }
}
