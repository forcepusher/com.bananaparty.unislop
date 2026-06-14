using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;

namespace UniSlop.MCP
{
    [InitializeOnLoad]
    public class McpServer
    {
        private const int UnityApiPort = 5108; // Internal port for Bun -> Unity
        private const int McpServerPort = 5107; // Port LLM connects to (referenced in README)

        private static Process _serverProcess;
        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static bool _isShuttingDown;
        private const string ProcessIdPrefKey = "UniSlop_McpServerPid";

        public enum ServerStatus { Disabled, Starting, Running, Error }
        public static ServerStatus Status { get; private set; } = ServerStatus.Disabled;
        public static bool HasBeenAccessed { get; private set; } = false;
        public static bool IsListening { get; private set; } = false;
        public static string LastError { get; private set; } = "";
        public static event Action StatusChanged;

        static McpServer()
        {
            EditorApplication.update += UpdateLoop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            ScheduleStart();
        }

        static void ScheduleStart()
        {
            EditorApplication.delayCall += () =>
            {
                if (_isShuttingDown) return;
                StartServer();
            };
        }

        private static void OnBeforeAssemblyReload()
        {
            _isShuttingDown = true;
            EditorApplication.update -= UpdateLoop;
            Cleanup(notify: false);
        }

        private static void OnEditorQuitting()
        {
            _isShuttingDown = true;
            Cleanup(notify: false);
        }

        private static void UpdateLoop()
        {
            if (_isShuttingDown) return;
            if (Status != ServerStatus.Running && Status != ServerStatus.Starting) return;

            try
            {
                if (_serverProcess == null || _serverProcess.HasExited)
                {
                    LastError = "MCP server process exited unexpectedly.";
                    SetStatus(ServerStatus.Error);
                }
            }
            catch (InvalidOperationException)
            {
                // Process handle disposed during shutdown.
            }
        }

        public static void StartServer()
        {
            if (_isShuttingDown) return;

            _isShuttingDown = false;
            Cleanup(notify: false);
            FreePorts(McpServerPort, UnityApiPort);

            try
            {
                string packagePath = GetPackagePath();
                string bunPath = GetBunPath(packagePath);

                if (bunPath == null || !File.Exists(bunPath))
                {
                    string msg = $"[UniSlop] Bun runtime not found at {bunPath}. Please ensure it's in the Editor/Bun folder.";
                    UnityEngine.Debug.LogError(msg);
                    LastError = msg;
                    SetStatus(ServerStatus.Error);
                    return;
                }

                string serverScript = Path.Combine(packagePath, "Editor", "Server", "index.ts");

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = bunPath,
                    Arguments = $"run {serverScript}",
                    WorkingDirectory = Path.GetDirectoryName(serverScript),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                _serverProcess = Process.Start(psi);
                if (_serverProcess != null)
                {
                    EditorPrefs.SetInt(ProcessIdPrefKey, _serverProcess.Id);
                }

                SetStatus(ServerStatus.Starting);

                Task.Run(() => ReadStream(_serverProcess?.StandardOutput, "STDOUT", token), token);
                Task.Run(() => ReadStream(_serverProcess?.StandardError, "STDERR", token), token);

                StartUnityApiListener(token);
            }
            catch (Exception e)
            {
                string msg = $"[UniSlop] Failed to start MCP server: {e.Message}";
                UnityEngine.Debug.LogError(msg);
                LastError = msg;
                SetStatus(ServerStatus.Error);
            }
        }

        public static void StopServer()
        {
            Cleanup(notify: true);
        }

        static void Cleanup(bool notify)
        {
            int savedPid = EditorPrefs.GetInt(ProcessIdPrefKey, -1);
            EditorPrefs.DeleteKey(ProcessIdPrefKey);

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            StopListener();

            if (_serverProcess != null)
            {
                int pid = _serverProcess.Id;
                try
                {
                    if (!_serverProcess.HasExited) _serverProcess.Kill();
                }
                catch { }
                try { _serverProcess.Dispose(); } catch { }
                _serverProcess = null;
                KillProcess(pid);
            }
            else if (savedPid > 0)
            {
                KillProcess(savedPid);
            }

            if (!_isShuttingDown)
                FreePorts(McpServerPort, UnityApiPort);

            HasBeenAccessed = false;
            IsListening = false;
            SetStatus(ServerStatus.Disabled, notify);
        }

        static void StopListener()
        {
            HttpListener listener = _listener;
            _listener = null;
            if (listener == null) return;

            try { listener.Stop(); } catch { }
            try { listener.Abort(); } catch { }
            try { listener.Close(); } catch { }
        }

        static void SetStatus(ServerStatus status, bool notify = true)
        {
            Status = status;
            if (notify && !_isShuttingDown)
                StatusChanged?.Invoke();
        }

        private static void KillProcess(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return;

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {pid} /F /T",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                else
                {
                    Process.GetProcessById(pid).Kill();
                }
            }
            catch { }
        }

        private static void FreePorts(params int[] ports)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return;

            try
            {
                var netstat = Process.Start(new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (netstat == null) return;

                string output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit();

                var portSet = new HashSet<int>(ports);
                int unityPid = Process.GetCurrentProcess().Id;

                foreach (string line in output.Split('\n'))
                {
                    if (!line.Contains("LISTENING")) continue;

                    string trimmed = line.Trim();
                    bool matchesPort = false;
                    foreach (int port in portSet)
                    {
                        if (trimmed.Contains($":{port} ")) { matchesPort = true; break; }
                    }
                    if (!matchesPort) continue;

                    string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (!int.TryParse(parts[parts.Length - 1], out int pid)) continue;
                    if (pid <= 0 || pid == unityPid) continue;

                    KillProcess(pid);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[UniSlop] Could not free ports: {e.Message}");
            }
        }

        private static void StartUnityApiListener(CancellationToken token)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{UnityApiPort}/");
            _listener.Start();

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        HttpListenerContext context = await _listener.GetContextAsync();
                        if (token.IsCancellationRequested) break;

                        string requestBody = await ReadRequestBody(context.Request);
                        string response = HandleApiRequest(requestBody);

                        byte[] buffer = Encoding.UTF8.GetBytes(response);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = buffer.Length;
                        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        context.Response.Close();
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        if (token.IsCancellationRequested) break;
                        UnityEngine.Debug.LogWarning($"[UniSlop] API Listener error: {e.Message}");
                    }
                }
            }, token);
        }

        private static async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private static string HandleApiRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return McpUnityBridge.Error("Empty request body");

            McpRequest request;
            try
            {
                request = JsonUtility.FromJson<McpRequest>(body);
            }
            catch (Exception e)
            {
                return McpUnityBridge.Error($"Invalid JSON: {e.Message}");
            }

            if (request == null || string.IsNullOrEmpty(request.command))
                return McpUnityBridge.Error("Missing command");

            if (request.command == "agent_connected")
                MarkAgentConnected();

            if (request.command == "compile" && !body.Contains("\"wait\""))
                request.wait = true;

            return McpUnityBridge.Handle(request);
        }

        private static void MarkAgentConnected()
        {
            if (_isShuttingDown) return;
            EditorApplication.delayCall += () =>
            {
                if (_isShuttingDown) return;
                HasBeenAccessed = true;
                SetStatus(ServerStatus.Running);
            };
        }

        private static void MarkListening()
        {
            if (_isShuttingDown) return;
            EditorApplication.delayCall += () =>
            {
                if (_isShuttingDown) return;
                IsListening = true;
                StatusChanged?.Invoke();
            };
        }

        private static string GetPackagePath()
        {
            string[] guids = AssetDatabase.FindAssets("t:Script MCPManager");
            if (guids.Length > 0)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                int firstSlash = assetPath.IndexOf('/');
                if (firstSlash != -1)
                {
                    int secondSlash = assetPath.IndexOf('/', firstSlash + 1);
                    string relativePackagePath = secondSlash == -1 ? assetPath : assetPath.Substring(0, secondSlash);
                    return Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, relativePackagePath));
                }
            }
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", "com.bananaparty.unislop");
        }

        private static string GetBunPath(string packagePath)
        {
            string osFolder = "";
            if (Application.platform == RuntimePlatform.WindowsEditor) osFolder = "bun-windows-x64";
            else if (Application.platform == RuntimePlatform.OSXEditor) osFolder = "bun-darwin-aarch64";
            else if (Application.platform == RuntimePlatform.LinuxEditor) osFolder = "bun-linux-x64";

            if (string.IsNullOrEmpty(osFolder)) return null;

            string bunDir = Path.Combine(packagePath, "Editor", "Bun", osFolder);
            string exeName = "bun";
            if (Application.platform == RuntimePlatform.WindowsEditor) exeName += ".exe";

            return Path.Combine(bunDir, exeName);
        }

        public static string GetServerUrl() => $"http://localhost:{McpServerPort}/mcp";

        private static void ReadStream(StreamReader reader, string prefix, CancellationToken token)
        {
            if (reader == null) return;

            try
            {
                while (!token.IsCancellationRequested && !reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (line == null || token.IsCancellationRequested) break;

                    UnityEngine.Debug.Log($"[UniSlop {prefix}] {line}");
                    if (line.Contains("MCP Server running"))
                        MarkListening();
                    if (line.Contains("Agent connected"))
                        MarkAgentConnected();
                    if (prefix == "STDERR" && (line.Contains("Fatal error") || line.Contains("Cannot find module") || line.Contains("Unhandled exception") || line.Contains("EADDRINUSE")))
                    {
                        string captured = line;
                        EditorApplication.delayCall += () =>
                        {
                            if (_isShuttingDown) return;
                            LastError = captured;
                            SetStatus(ServerStatus.Error);
                        };
                    }
                }
            }
            catch (Exception)
            {
                // Stream closed during shutdown.
            }
        }
    }
}
