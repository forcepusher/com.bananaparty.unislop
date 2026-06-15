using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniSlop.MCP
{
    // Supervises the detached Bun MCP server and exposes an in-process JSON API for it.
    //
    //   Zed ──(mcp-remote, :5107)──▶ Bun (detached process) ──(:5108 JSON)──▶ this listener
    //
    // The Bun process is intentionally NOT killed on domain reload — it owns the Zed connection
    // and keeps polling while Unity reloads. Its PID is stored in SessionState so that after a
    // reload we reattach to the running process instead of spawning a duplicate. Only the
    // in-process :5108 listener is torn down and rebuilt around a reload.
    [InitializeOnLoad]
    public class McpServer
    {
        const int McpServerPort = 5107;  // Bun MCP server (Zed connects here via mcp-remote)
        const int UnityApiPort = 5108;   // Internal API: Bun -> Unity

        const string PidKey = "unislop.bun.pid";
        const string ConnectedKey = "unislop.bun.connected";
        const string SessionStartedKey = "unislop.session.started"; // SessionState: set once per editor session
        const string LastPidPrefKey = "UniSlop.LastBunPid";          // EditorPrefs: survives editor restarts/crashes

        static Process _bun;
        static Socket _listenSocket;
        static volatile bool _listenerRunning;
        static volatile bool _listenerStarting;
        static volatile bool _isShuttingDown;

        public enum ServerStatus { Disabled, Starting, Running, Error }
        public static ServerStatus Status { get; private set; } = ServerStatus.Disabled;
        public static bool HasBeenAccessed { get; private set; }
        public static bool IsListening { get; private set; }
        public static string LastError { get; private set; } = "";
        public static event Action StatusChanged;

        static McpServer()
        {
            EditorApplication.update += UpdateLoop;
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
            // Tear down only the in-process listener; leave the Bun process running.
            _isShuttingDown = true;
            StopListener();
        }

        static void OnEditorQuitting()
        {
            _isShuttingDown = true;
            StopListener();
            KillBun();
        }

        static void UpdateLoop()
        {
            if (_isShuttingDown) return;
            if (Status != ServerStatus.Running && Status != ServerStatus.Starting) return;

            if (!_listenerRunning)
            {
                UnityEngine.Debug.LogWarning("[UniSlop] Internal API listener stopped; restarting...");
                StartListener();
            }

            try
            {
                if (_bun != null && _bun.HasExited)
                {
                    LastError = "Bun MCP server process exited unexpectedly.";
                    SessionState.EraseInt(PidKey);
                    SetStatus(ServerStatus.Error);
                }
            }
            catch (InvalidOperationException) { }
        }

        public static void StartServer()
        {
            _isShuttingDown = false;

            // Bind the internal port (no-op if already listening or a bind is already in flight).
            // The bind runs on a background thread and retries transient conflicts, so it never
            // needs the editor to tick and never stacks duplicate listeners.
            StartListener();

            try
            {
                EnsureBun();

                bool connected = SessionState.GetBool(ConnectedKey, false);
                HasBeenAccessed = connected;
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
            KillBun();
            HasBeenAccessed = false;
            IsListening = false;
            SessionState.SetBool(ConnectedKey, false);
            SetStatus(ServerStatus.Disabled);
        }

        // --- Bun process lifecycle -------------------------------------------------------

        static void EnsureBun()
        {
            // SessionState survives domain reloads but is cleared when the editor closes, so an
            // unset flag means this is a fresh editor launch (not a reload).
            bool firstLaunchThisSession = !SessionState.GetBool(SessionStartedKey, false);

            if (firstLaunchThisSession)
            {
                SessionState.SetBool(SessionStartedKey, true);
                KillDanglingBun();
                SpawnBun();
                return;
            }

            // Domain reload: reattach to the process we spawned earlier this session.
            int pid = SessionState.GetInt(PidKey, -1);
            if (pid > 0 && TryReattachBun(pid))
                return;

            SpawnBun();
        }

        // Kill a Bun process left over from a previous editor session (e.g. after a crash).
        static void KillDanglingBun()
        {
            int pid = EditorPrefs.GetInt(LastPidPrefKey, -1);
            EditorPrefs.DeleteKey(LastPidPrefKey);
            if (pid > 0)
                KillIfBun(pid);
        }

        static void KillIfBun(int pid)
        {
            if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited && proc.ProcessName.StartsWith("bun", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill();
                    UnityEngine.Debug.Log($"[UniSlop] Killed dangling Bun MCP server (pid {pid}) from a previous session.");
                }
            }
            catch { }
        }

        static bool TryReattachBun(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.HasExited || !proc.ProcessName.StartsWith("bun", StringComparison.OrdinalIgnoreCase))
                    return false;

                _bun = proc;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void SpawnBun()
        {
            string packagePath = GetPackagePath();
            string bunPath = GetBunPath(packagePath);
            string serverScript = Path.Combine(packagePath, "Editor", "Server", "index.ts");

            if (bunPath == null || !File.Exists(bunPath))
                throw new FileNotFoundException($"Bun runtime not found at '{bunPath}'.");
            if (!File.Exists(serverScript))
                throw new FileNotFoundException($"Server script not found at '{serverScript}'.");

            // Do NOT redirect stdout/stderr. Bun is a detached process that outlives this
            // AppDomain; if its pipes were redirected, the draining tasks would die on domain
            // reload, the OS pipe buffer would fill, and Bun would block on its next write
            // (wedging the MCP server). Inherited handles are always drained by the OS.
            var psi = new ProcessStartInfo
            {
                FileName = bunPath,
                Arguments = $"run \"{serverScript}\"",
                WorkingDirectory = Path.GetDirectoryName(serverScript),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            _bun = Process.Start(psi);
            if (_bun == null)
                throw new InvalidOperationException("Process.Start returned null for the Bun runtime.");

            SessionState.SetInt(PidKey, _bun.Id);
            SessionState.SetBool(ConnectedKey, false);
            EditorPrefs.SetInt(LastPidPrefKey, _bun.Id);

            UnityEngine.Debug.Log($"[UniSlop] Started Bun MCP server (pid {_bun.Id}) at http://localhost:{McpServerPort}/mcp");
        }

        static void KillBun()
        {
            int pid = SessionState.GetInt(PidKey, -1);
            SessionState.EraseInt(PidKey);
            EditorPrefs.DeleteKey(LastPidPrefKey);

            if (_bun != null)
            {
                try { if (!_bun.HasExited) _bun.Kill(); } catch { }
                try { _bun.Dispose(); } catch { }
                _bun = null;
            }
            else if (pid > 0)
            {
                KillIfBun(pid);
            }
        }

        // --- Internal :5108 JSON API -----------------------------------------------------

        // Binds the internal :5108 listener on a background thread, retrying until the port is free.
        //
        // We deliberately do NOT set SO_REUSEADDR. Unity's Mono does not reliably release a
        // listening socket's port when Close() is called across a domain reload, so the old
        // domain's socket can linger as a dead listener for a short while. With SO_REUSEADDR the
        // new domain would bind a SECOND listener right next to the dead one; Windows then hands
        // some connections to the dead socket (it accepts at the OS level but nothing ever replies),
        // and the Bun poller hangs. Without SO_REUSEADDR only ONE listener can own the port at a
        // time: the rebind simply fails with EADDRINUSE until the lingering socket is released,
        // then succeeds — so a dead socket can never coexist and steal connections.
        static void StartListener()
        {
            if (_listenerRunning || _listenerStarting)
                return;

            _listenerStarting = true;
            Task.Run(BindLoop);
        }

        static void BindLoop()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            try
            {
                while (!_isShuttingDown && DateTime.UtcNow < deadline)
                {
                    Socket socket = null;
                    try
                    {
                        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        socket.Bind(new IPEndPoint(IPAddress.Loopback, UnityApiPort));
                        socket.Listen(16);
                        socket.Blocking = false;  // see AcceptLoop: never park inside a blocking syscall
                    }
                    catch (SocketException)
                    {
                        try { socket?.Close(); } catch { }
                        Thread.Sleep(150);  // port still held by a not-yet-released old socket
                        continue;
                    }

                    if (_isShuttingDown)
                    {
                        try { socket.Close(); } catch { }
                        return;
                    }

                    _listenSocket = socket;
                    _listenerRunning = true;
                    IsListening = true;
                    UnityEngine.Debug.Log($"[UniSlop] Internal API listening on 127.0.0.1:{UnityApiPort}");

                    AcceptLoop(socket);  // blocks here (non-blocking accept loop) until torn down
                    return;
                }
            }
            finally
            {
                _listenerStarting = false;
            }
        }

        static void AcceptLoop(Socket socket)
        {
            // Non-blocking accept + sleep, never a blocking Accept(). A thread parked inside a
            // blocking accept pins the socket in a syscall, and Mono then fails to release the port
            // when StopListener closes it during a domain reload (see StartListener). Staying out of
            // blocking syscalls lets Close() free the socket cleanly.
            while (_listenerRunning)
            {
                Socket client;
                try
                {
                    client = socket.Accept();
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    Thread.Sleep(50);
                    continue;
                }
                catch
                {
                    break;  // socket closed (domain reload / StopListener)
                }

                _ = Task.Run(() => HandleClient(client));
            }
        }

        static void StopListener()
        {
            IsListening = false;
            _listenerRunning = false;
            _listenerStarting = false;
            Socket socket = _listenSocket;
            _listenSocket = null;
            if (socket == null) return;

            try { socket.Close(); } catch { }
            try { socket.Dispose(); } catch { }
        }

        static void HandleClient(Socket client)
        {
            try
            {
                // The listening socket is non-blocking; the accepted socket can inherit that, which
                // would make the blocking Receive/Send below fail immediately. Force blocking mode.
                client.Blocking = true;
                client.ReceiveTimeout = 30_000;  // idle keep-alive timeout
                client.SendTimeout = 15_000;

                // Keep-alive loop: the Bun poller hits this API every few hundred ms, so reusing one
                // connection for many requests avoids churning a fresh TCP connection (and a lingering
                // TIME_WAIT socket) per poll. We exit when the peer closes or the connection goes idle.
                while (_listenerRunning)
                {
                    string requestBody = ReadHttpRequestBody(client);
                    if (requestBody == null)
                        break;  // peer closed, idle timeout, or malformed request

                    string responseJson;
                    try
                    {
                        responseJson = ProcessApiBody(requestBody);
                    }
                    catch (Exception e)
                    {
                        responseJson = McpUnityBridge.Error($"Internal API error: {e.Message}");
                    }

                    if (!WriteHttpResponse(client, responseJson))
                        break;
                }
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

        // Writes an HTTP/1.1 keep-alive response. Returns false if the send failed (connection dead).
        static bool WriteHttpResponse(Socket client, string json)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                string head = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: application/json\r\n" +
                              "Content-Length: " + body.Length + "\r\n" +
                              "Connection: keep-alive\r\n\r\n";
                client.Send(Encoding.ASCII.GetBytes(head));
                if (body.Length > 0)
                    client.Send(body);
                return true;
            }
            catch
            {
                return false;
            }
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
                MarkAgentConnected();
            else
                UnityEngine.Debug.Log($"[UniSlop] API command: {command}");

            var request = new McpRequest
            {
                command = command,
                wait = ExtractBool(body, "wait", true),
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
                HasBeenAccessed = true;
                SessionState.SetBool(ConnectedKey, true);
                SetStatus(ServerStatus.Running);
            });
        }

        static void SetStatus(ServerStatus status, bool notify = true)
        {
            Status = status;
            if (notify && !_isShuttingDown)
            {
                StatusChanged?.Invoke();
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        // --- Paths -----------------------------------------------------------------------

        static string GetPackagePath()
        {
            return Path.GetFullPath("Packages/com.bananaparty.unislop");
        }

        static string GetBunPath(string packagePath)
        {
            string osFolder = null;
            string exeName = "bun";

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                osFolder = "bun-windows-x64";
                exeName = "bun.exe";
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                osFolder = "bun-darwin-aarch64";
            }
            else if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                osFolder = "bun-linux-x64";
            }

            if (osFolder == null) return null;
            return Path.Combine(packagePath, "Editor", "Bun", osFolder, exeName);
        }

        public static string GetServerUrl() => $"http://localhost:{McpServerPort}/mcp";

        // --- Minimal JSON field extraction (thread-safe, avoids JsonUtility off main thread) --

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

        static bool ExtractBool(string json, string key, bool defaultValue)
        {
            int i = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (i < 0) return defaultValue;
            int colon = json.IndexOf(':', i + key.Length + 2);
            if (colon < 0) return defaultValue;

            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (json.IndexOf("true", p, StringComparison.Ordinal) == p) return true;
            if (json.IndexOf("false", p, StringComparison.Ordinal) == p) return false;
            return defaultValue;
        }

    }
}
