using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UniSlop.MCP
{
    // Supervises the detached MCP server and exposes an in-process JSON API for it.
    //
    //   Agent ──(MCP, :5107)──▶ mono server (detached process) ──(JSON, dynamic port)──▶ this listener
    //
    // The MCP server is a small C# program (Editor/Server~/Server.cs) compiled by Unity's bundled
    // mcs and run by Unity's bundled mono. It is intentionally NOT killed on domain reload — it owns
    // the agent connection and keeps retrying while Unity reloads. Its PID is stored in SessionState
    // so that after a reload we reattach to the running process instead of spawning a duplicate.
    // The in-process JSON listener is torn down and rebuilt around a reload on a fresh ephemeral port
    // published via UnityApiPortFilePath, which the server re-reads before each call.
    [InitializeOnLoad]
    public class McpServer
    {
        public const int McpServerPort = 5107;  // MCP server (the agent connects here)

        const string PidKey = "unislop.server.pid";
        const string ConnectedKey = "unislop.server.connected";
        const string SessionStartedKey = "unislop.session.started"; // SessionState: set once per editor session
        const string LastPidPrefKey = "UniSlop.LastServerPid";       // EditorPrefs: survives editor restarts/crashes
        const string ProcessMarker = "--unislop-mcp";

        static Process _server;
        static Socket _listenSocket;
        static volatile bool _listenerRunning;
        static volatile bool _isShuttingDown;
        static volatile bool _compileInFlight;

        public enum ServerStatus { Disabled, Starting, Running, Error }
        public static ServerStatus Status { get; private set; } = ServerStatus.Disabled;
        public static bool IsListening { get; private set; }
        public static string LastError { get; private set; } = "";
        public static event Action StatusChanged;

        static McpServer()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            ScheduleStart();
        }

        static void OnAfterAssemblyReload()
        {
            // Runs synchronously as part of the reload sequence, on the main thread, regardless of
            // editor focus. This is the ONLY reliable place to rebind the internal port after a
            // reload: EditorApplication.delayCall (used for the very first launch) does not fire
            // while the editor is unfocused, so relying on it leaves the server offline until the
            // user clicks Unity.
            _isShuttingDown = false;
            StartServer();
        }

        static void ScheduleStart()
        {
            // First launch only: afterAssemblyReload (which handles every reload) does not fire on
            // the initial domain load, so defer the first StartServer to when the editor is ready.
            EditorApplication.delayCall += () =>
            {
                if (_isShuttingDown) return;
                StartServer();
            };
        }

        static void OnBeforeAssemblyReload()
        {
            // Tear down only the in-process listener; leave the MCP server process running.
            _isShuttingDown = true;
            StopListener();
        }

        static void OnEditorQuitting()
        {
            _isShuttingDown = true;
            StopListener();
            KillProcess();
        }

        public static void StartServer()
        {
            _isShuttingDown = false;

            // Bind the internal port (no-op if already listening).
            if (!StartListener())
                return;

            try
            {
                EnsureProcess();

                bool connected = SessionState.GetBool(ConnectedKey, false);
                SetStatus(connected ? ServerStatus.Running : ServerStatus.Starting);
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
            StopListener();
            KillProcess();
            IsListening = false;
            SessionState.SetBool(ConnectedKey, false);
            SetStatus(ServerStatus.Disabled);
        }

        // --- MCP server process lifecycle ------------------------------------------------------

        static void EnsureProcess()
        {
            // SessionState survives domain reloads but is cleared when the editor closes, so an
            // unset flag means this is a fresh editor launch (not a reload).
            bool firstLaunchThisSession = !SessionState.GetBool(SessionStartedKey, false);

            if (firstLaunchThisSession)
            {
                SessionState.SetBool(SessionStartedKey, true);
                StartProcess();
                return;
            }

            // Domain reload: reattach to the process we spawned earlier this session.
            int pid = SessionState.GetInt(PidKey, -1);
            if (pid > 0 && TryReattach(pid))
                return;

            StartProcess();
        }

        // Launches the server, compiling first when the build is missing or stale. The launch and all
        // SessionState bookkeeping stay on the main thread (both are instant), but compilation — cold
        // mono start plus JIT, a couple of seconds — is pushed to a background thread so it never
        // freezes the editor during startup or a domain reload.
        static void StartProcess()
        {
            string exe = ServerExePath;

            if (IsBuildFresh(exe))
            {
                Launch(exe);
                return;
            }

            if (_compileInFlight)
                return;

            _compileInFlight = true;
            string mono = MonoExecutablePath;
            string mcs = CompilerPath;
            string source = ServerSourcePath;

            // Dedicated thread, NOT the shared ThreadPool: compilation must proceed even if the pool
            // is busy, and it must never itself contend for pool threads.
            new Thread(() =>
            {
                string output;
                bool ok;
                try { ok = CompileServerInternal(mono, mcs, source, exe, out output); }
                catch (Exception e) { ok = false; output = e.Message; }
                _compileInFlight = false;

                // If the post is dropped (mid-reload), the next StartServer finds a fresh build and
                // launches synchronously, so no state is lost.
                McpMainThread.Post(() =>
                {
                    if (_isShuttingDown) return;
                    if (!ok) { OnStartFailed("MCP server compilation failed:\n" + output); return; }
                    Launch(exe);
                });
            }) { IsBackground = true, Name = "UniSlop Server Compile" }.Start();
        }

        static void Launch(string exe)
        {
            if (_server != null)
            {
                try { if (!_server.HasExited) return; } catch { }
            }

            EnsureMcpPortFree();

            string mono = MonoExecutablePath;
            if (!File.Exists(mono))
            {
                OnStartFailed($"mono runtime not found at '{mono}'.");
                return;
            }

            // Do NOT redirect stdout/stderr. The server is a detached process that outlives this
            // AppDomain; redirected pipes whose draining tasks die on reload would fill the OS
            // buffer and wedge the server. The server logs to its own file instead.
            var psi = new ProcessStartInfo
            {
                FileName = mono,
                Arguments = $"\"{exe}\" {McpServerPort} \"{UnityApiPortFilePath}\" {ProcessMarker}",
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            _server = Process.Start(psi);
            if (_server == null)
            {
                OnStartFailed("Process.Start returned null for the mono runtime.");
                return;
            }
            WatchProcessExit(_server);

            SessionState.SetInt(PidKey, _server.Id);
            SessionState.SetBool(ConnectedKey, false);
            EditorPrefs.SetInt(LastPidPrefKey, _server.Id);

            UnityEngine.Debug.Log($"[UniSlop] Started MCP server (mono pid {_server.Id}) at http://localhost:{McpServerPort}/mcp");
        }

        static void OnStartFailed(string message)
        {
            string msg = "[UniSlop] Failed to start MCP server: " + message;
            UnityEngine.Debug.LogError(msg);
            LastError = msg;
            SetStatus(ServerStatus.Error);
        }

        static bool IsBuildFresh(string exe)
        {
            return File.Exists(exe)
                && File.Exists(ServerSourcePath)
                && File.GetLastWriteTimeUtc(exe) >= File.GetLastWriteTimeUtc(ServerSourcePath);
        }

        // Free :5107 before every launch. Orphans from crashes, other projects, or stop/start can
        // otherwise win the bind race and the new server exits immediately.
        static void EnsureMcpPortFree()
        {
            var targets = new HashSet<int>();

            int lastPid = EditorPrefs.GetInt(LastPidPrefKey, -1);
            if (lastPid > 0)
                targets.Add(lastPid);

            int sessionPid = SessionState.GetInt(PidKey, -1);
            if (sessionPid > 0)
                targets.Add(sessionPid);

            foreach (int pid in ListMarkedProcessIds())
                targets.Add(pid);

            foreach (int pid in ListListenerPidsOnPort(McpServerPort))
            {
                if (IsLikelyOurServerProcess(pid))
                    targets.Add(pid);
            }

            foreach (int pid in targets)
                KillIfOurServer(pid);

            WaitForPortFree(McpServerPort, 2000);
        }

        static void KillIfOurServer(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return;
            if (!IsLikelyOurServerProcess(pid)) return;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.HasExited) return;
                if (!proc.ProcessName.StartsWith("mono", StringComparison.OrdinalIgnoreCase))
                    return;

                proc.Kill();
                UnityEngine.Debug.Log($"[UniSlop] Killed dangling MCP server (mono pid {pid}).");
            }
            catch { }
        }

        static bool IsLikelyOurServerProcess(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return false;

            if (pid == EditorPrefs.GetInt(LastPidPrefKey, -1)) return true;
            if (pid == SessionState.GetInt(PidKey, -1)) return true;

            string cmd = GetProcessCommandLine(pid);
            if (string.IsNullOrEmpty(cmd)) return false;

            if (cmd.Contains(ProcessMarker)) return true;

            string exeName = Path.GetFileName(ServerExePath);
            if (cmd.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0
                && cmd.IndexOf(McpServerPort.ToString(), StringComparison.Ordinal) >= 0)
                return true;

            return false;
        }

        static void WaitForPortFree(int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ListListenerPidsOnPort(port).Count == 0)
                    return;
                Thread.Sleep(50);
            }
        }

        static List<int> ListListenerPidsOnPort(int port)
        {
            var pids = new List<int>();
            string portToken = ":" + port;

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    string output = RunProcess("netstat", "-ano -p tcp");
                    foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (line.IndexOf(portToken, StringComparison.Ordinal) < 0) continue;

                        string trimmed = line.Trim();
                        int lastSpace = trimmed.LastIndexOf(' ');
                        if (lastSpace < 0 || !int.TryParse(trimmed.Substring(lastSpace + 1).Trim(), out int pid))
                            continue;
                        if (pid > 0)
                            pids.Add(pid);
                    }
                    return pids;
                }

                string lsof = RunProcess("/usr/sbin/lsof", $"-nP -iTCP:{port} -sTCP:LISTEN -t");
                if (string.IsNullOrWhiteSpace(lsof))
                    lsof = RunProcess("lsof", $"-nP -iTCP:{port} -sTCP:LISTEN -t");

                foreach (string line in lsof.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out int pid) && pid > 0)
                        pids.Add(pid);
                }
            }
            catch { }

            return pids;
        }

        static List<int> ListMarkedProcessIds()
        {
            var ids = new List<int>();
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    string output = RunProcess("powershell",
                        "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*" + ProcessMarker + "*' } | Select-Object -ExpandProperty ProcessId\"");
                    foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out int pid) && pid > 0)
                            ids.Add(pid);
                    }
                    return ids;
                }

                string ps = RunProcess("/bin/ps", "-eo pid,args");
                foreach (string line in ps.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains(ProcessMarker)) continue;
                    string trimmed = line.TrimStart();
                    int end = 0;
                    while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
                    if (end > 0 && int.TryParse(trimmed.Substring(0, end), out int pid))
                        ids.Add(pid);
                }
            }
            catch { }

            return ids;
        }

        static string GetProcessCommandLine(int pid)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    return RunProcess("powershell",
                        "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter \\\"ProcessId=" + pid + "\\\").CommandLine\"");
                }

                return RunProcess("/bin/ps", "-p " + pid + " -o args=");
            }
            catch
            {
                return "";
            }
        }

        static string RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null) return "";
                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return stdout;
            }
        }

        static bool TryReattach(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.HasExited || !proc.ProcessName.StartsWith("mono", StringComparison.OrdinalIgnoreCase))
                    return false;

                _server = proc;
                WatchProcessExit(_server);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void WatchProcessExit(Process process)
        {
            try
            {
                int watchedPid = process.Id;
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    McpMainThread.Post(() =>
                    {
                        if (_isShuttingDown) return;
                        if (SessionState.GetInt(PidKey, -1) != watchedPid) return;
                        LastError = ReadServerLogTail()
                            ?? "MCP server process exited unexpectedly.";
                        SessionState.EraseInt(PidKey);
                        SetStatus(ServerStatus.Error);
                    });
                };
            }
            catch { }
        }

        static string ReadServerLogTail()
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "unislop-server.log");
                if (!File.Exists(logPath)) return null;

                string[] lines = File.ReadAllLines(logPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf("failed to bind", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "MCP server could not bind to port " + McpServerPort + " (another process is using it). " + line;
                    break;
                }
            }
            catch { }

            return null;
        }

        static void KillProcess()
        {
            int pid = SessionState.GetInt(PidKey, -1);
            SessionState.EraseInt(PidKey);

            if (_server != null)
            {
                try { if (!_server.HasExited) _server.Kill(); } catch { }
                try { _server.Dispose(); } catch { }
                _server = null;
            }
            else if (pid > 0)
            {
                KillIfOurServer(pid);
            }
        }

        // --- Paths and compilation (also used by the integration tests) ------------------------

        public static string MonoExecutablePath
        {
            get
            {
                string bin = Path.Combine(EditorApplication.applicationContentsPath, "MonoBleedingEdge", "bin");
                return Application.platform == RuntimePlatform.WindowsEditor
                    ? Path.Combine(bin, "mono.exe")
                    : Path.Combine(bin, "mono");
            }
        }

        public static string CompilerPath
        {
            get { return Path.Combine(EditorApplication.applicationContentsPath, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"); }
        }

        public static string ServerSourcePath
        {
            get { return Path.Combine(GetPackagePath(), "Editor", "Server~", "Server.cs"); }
        }

        public static string ServerExePath
        {
            get { return Path.GetFullPath(Path.Combine("Library", "UniSlop", "Server.exe")); }
        }

        public static string UnityApiPortFilePath
        {
            get { return Path.GetFullPath(Path.Combine("Library", "UniSlop", "unity-api-port.txt")); }
        }

        // Compiles ServerSourcePath to outputExe with the bundled compiler under mono. Returns true
        // on success; otherwise output holds the combined compiler stdout/stderr. Safe to call from a
        // background thread.
        public static bool CompileServer(string outputExe, out string output)
        {
            return CompileServerInternal(MonoExecutablePath, CompilerPath, ServerSourcePath, outputExe, out output);
        }

        static bool CompileServerInternal(string mono, string mcs, string source, string outputExe, out string output)
        {
            if (!File.Exists(mono)) { output = $"mono runtime not found at '{mono}'."; return false; }
            if (!File.Exists(mcs)) { output = $"C# compiler not found at '{mcs}'."; return false; }
            if (!File.Exists(source)) { output = $"MCP server source not found at '{source}'."; return false; }

            Directory.CreateDirectory(Path.GetDirectoryName(outputExe));

            var psi = new ProcessStartInfo
            {
                FileName = mono,
                Arguments = $"\"{mcs}\" -target:exe \"-out:{outputExe}\" -nologo \"{source}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                // Drain both pipes concurrently on dedicated threads; reading them one after another
                // can deadlock if the compiler fills the other buffer first. Dedicated threads (not
                // the ThreadPool) keep this working even when the pool is saturated.
                string outText = null, errText = null;
                var tOut = new Thread(() => { try { outText = process.StandardOutput.ReadToEnd(); } catch { outText = ""; } }) { IsBackground = true };
                var tErr = new Thread(() => { try { errText = process.StandardError.ReadToEnd(); } catch { errText = ""; } }) { IsBackground = true };
                tOut.Start();
                tErr.Start();

                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { }
                    output = "Compiler timed out after 60s.";
                    return false;
                }

                tOut.Join(5000);
                tErr.Join(5000);
                output = ((outText ?? "") + (errText ?? "")).Trim();
                return process.ExitCode == 0 && File.Exists(outputExe);
            }
        }

        static string GetPackagePath()
        {
            return Path.GetFullPath("Packages/com.bananaparty.unislop");
        }

        public static string GetServerUrl() => $"http://localhost:{McpServerPort}/mcp";

        // --- Internal JSON API (dynamic ephemeral port) ----------------------------------------

        // Binds the internal Unity API on a fresh ephemeral localhost port each domain. The mono MCP
        // process reads UnityApiPortFilePath before every call.
        //
        // Fixed :5108 is deliberately avoided. Unity/Mono can leak old-domain socket callbacks or
        // threads across a reload; if a leaked listener squats on a fixed port, the new domain can
        // never rebind and all tool calls hang. Binding port 0 makes leaked old ports irrelevant: the
        // new domain always gets a clean port and publishes it atomically for the external process.
        //
        // Acceptance is ASYNCHRONOUS (BeginAccept), not a thread parked in accept. Closing the socket
        // cancels pending accept and prevents a managed listener thread from surviving reload.
        static bool StartListener()
        {
            if (_listenerRunning)
                return true;

            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.Listen(16);

                if (_isShuttingDown)
                {
                    socket.Close();
                    return false;
                }

                _listenSocket = socket;
                _listenerRunning = true;
                IsListening = true;
                int port = ((IPEndPoint)socket.LocalEndPoint).Port;
                WriteUnityApiPort(port);
                UnityEngine.Debug.Log($"[UniSlop] Internal API listening on 127.0.0.1:{port}");

                ArmAccept(socket);
                return true;
            }
            catch (Exception e)
            {
                try { if (socket != null) socket.Close(); } catch { }
                LastError = "Failed to bind Unity API listener: " + e.Message;
                SetStatus(ServerStatus.Error);
                return false;
            }
        }

        static void ArmAccept(Socket socket)
        {
            if (!_listenerRunning)
                return;
            try
            {
                socket.BeginAccept(OnAccept, socket);
            }
            catch
            {
                // Socket closed (StopListener / reload) — nothing to re-arm.
            }
        }

        static void OnAccept(IAsyncResult ar)
        {
            Socket socket = (Socket)ar.AsyncState;
            Socket client = null;
            try
            {
                client = socket.EndAccept(ar);
            }
            catch
            {
                return;  // socket closed (domain reload / StopListener); do not re-arm
            }

            // Re-arm for the next connection before handling this one.
            ArmAccept(socket);

            if (client == null)
                return;

            // One dedicated thread per connection (NOT the shared ThreadPool). Status polls must stay
            // answerable even while another handler is parked waiting on the main thread, so handlers
            // can never compete for a finite pool of threads.
            new Thread(() => HandleClient(client)) { IsBackground = true, Name = "UniSlop API Handler" }.Start();
        }

        static void StopListener()
        {
            IsListening = false;
            _listenerRunning = false;

            // Closing the socket cancels any pending BeginAccept and releases the port. There is no
            // parked accept thread to outlive the domain, so this alone prevents a zombie listener.
            Socket socket = _listenSocket;
            _listenSocket = null;
            if (socket != null)
            {
                try { socket.Close(); } catch { }
                try { socket.Dispose(); } catch { }
            }
        }

        static void WriteUnityApiPort(int port)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UnityApiPortFilePath));
                string temp = UnityApiPortFilePath + ".tmp";
                File.WriteAllText(temp, port.ToString());
                if (File.Exists(UnityApiPortFilePath))
                    File.Delete(UnityApiPortFilePath);
                File.Move(temp, UnityApiPortFilePath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[UniSlop] Failed to publish Unity API port: " + e.Message);
            }
        }

        static void HandleClient(Socket client)
        {
            try
            {
                client.Blocking = true;
                client.ReceiveTimeout = 30_000;
                client.SendTimeout = 15_000;

                string requestBody = ReadHttpRequestBody(client);
                if (requestBody == null)
                    return;

                string responseJson;
                try
                {
                    responseJson = ProcessApiBody(requestBody);
                }
                catch (Exception e)
                {
                    responseJson = McpUnityBridge.Error($"Internal API error: {e.Message}");
                }

                WriteHttpResponse(client, responseJson);
            }
            catch { }
            finally
            {
                try { client.Shutdown(SocketShutdown.Both); } catch { }
                try { client.Close(); } catch { }
            }
        }

        // Minimal HTTP/1.1 request reader: consume headers, then Content-Length bytes of body.
        // Returns null when no complete request is available (peer closed / idle timeout).
        static string ReadHttpRequestBody(Socket client)
        {
            var buffer = new byte[8192];
            var received = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read;
                try { read = client.Receive(buffer); }
                catch { return null; }  // idle ReceiveTimeout or socket error
                if (read <= 0) return null;  // peer closed
                received.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            byte[] all = received.GetBuffer();
            int total = (int)received.Length;
            string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            int contentLength = ParseContentLength(headers);

            int bodyStart = headerEnd + 4;
            var body = new MemoryStream();
            if (total > bodyStart)
                body.Write(all, bodyStart, total - bodyStart);

            while (contentLength >= 0 && body.Length < contentLength)
            {
                int read = client.Receive(buffer);
                if (read <= 0) break;
                body.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }

        static void WriteHttpResponse(Socket client, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 200 OK\r\n" +
                          "Content-Type: application/json\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Connection: close\r\n\r\n";
            client.Send(Encoding.ASCII.GetBytes(head));
            if (body.Length > 0)
                client.Send(body);
        }

        static int FindHeaderEnd(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        static int ParseContentLength(string headers)
        {
            foreach (string line in headers.Split('\n'))
            {
                string trimmed = line.Trim();
                int colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                if (!trimmed.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(trimmed.Substring(colon + 1).Trim(), out int len))
                    return len;
            }
            return -1;
        }

        static string ProcessApiBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return McpUnityBridge.Error("Empty request body");

            string command = ExtractString(body, "command");
            if (string.IsNullOrEmpty(command))
                return McpUnityBridge.Error("Missing command");

            if (command == "agent_connected")
            {
                MarkAgentConnected();
                return "{\"status\":\"success\",\"message\":\"Agent connected\"}";
            }

            if (command == "mcp_request")
            {
                string method = ExtractString(body, "method") ?? "unknown";
                UnityEngine.Debug.Log($"[UniSlop] MCP request: {method}");
                return "{\"status\":\"success\",\"message\":\"MCP request logged\"}";
            }

            UnityEngine.Debug.Log($"[UniSlop] API command: {command}");

            var request = new McpRequest
            {
                command = command,
                mode = ExtractString(body, "mode") ?? "all",
                filter = ExtractString(body, "filter")
            };

            return McpUnityBridge.Handle(request);
        }

        static void MarkAgentConnected()
        {
            McpMainThread.Post(() =>
            {
                if (_isShuttingDown) return;
                SessionState.SetBool(ConnectedKey, true);
                SetStatus(ServerStatus.Running);
            });
        }

        static void SetStatus(ServerStatus status)
        {
            Status = status;
            if (!_isShuttingDown)
            {
                StatusChanged?.Invoke();
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        // --- Minimal JSON field extraction (thread-safe, avoids JsonUtility off main thread) ----

        static string ExtractString(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return null;

            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length || json[p] != '"') return null;

            var sb = new StringBuilder();
            p++;
            while (p < json.Length)
            {
                char c = json[p];
                if (c == '\\' && p + 1 < json.Length)
                {
                    char n = json[p + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); p += 2; continue;
                        case '\\': sb.Append('\\'); p += 2; continue;
                        case 'n': sb.Append('\n'); p += 2; continue;
                        case 'r': sb.Append('\r'); p += 2; continue;
                        case 't': sb.Append('\t'); p += 2; continue;
                    }
                }
                if (c == '"') break;
                sb.Append(c);
                p++;
            }
            return sb.ToString();
        }

    }
}
