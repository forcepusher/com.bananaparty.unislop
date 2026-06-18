// UniSlop MCP server.
//
// Standalone program compiled by Unity's bundled C# compiler (mcs) and run by Unity's bundled
// mono runtime as a DETACHED process. It owns the agent-facing MCP connection on :5107 and proxies
// every tool call to the Unity editor's internal JSON API.
//
//   Agent ──(MCP, Streamable HTTP, :5107)──▶ this process ──(JSON, dynamic port)──▶ Unity editor
//
// Because this process lives outside Unity's AppDomain it keeps the agent connection open while the
// editor reloads its domain (compile / enter-play-mode / test runs). Unity binds a fresh ephemeral
// port each domain and publishes it to a file; this process re-reads that file before every call,
// so a stale or leaked old port on the editor side can never wedge us — while Unity is mid-reload we
// simply retry until the new port appears, and the in-flight tool call resumes.
//
// Constraints: must compile under Unity's MonoBleedingEdge mcs and reference only assemblies that
// runtime ships by default (mscorlib, System). No NuGet, no System.Text.Json, no LINQ.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UniSlop.Server
{
    static class Program
    {
        const int DefaultMcpPort = 5107;

        // Overall budget for a tool call, covering one or more Unity domain reloads.
        const int JobTimeoutMs = 300000;
        // How often to poll Unity for job status.
        const int PollIntervalMs = 150;
        // While Unity is reloading its AppDomain the internal API is down; keep retrying.
        const int ReconnectIntervalMs = 100;
        // Some projects legitimately need long editor-side API calls (large compile/test discovery).
        const int CallTimeoutMs = 15000;

        const string ProtocolVersion = "2025-06-18";
        const string ServerName = "UniSlop";
        const string ServerVersion = "2.1.0";

        const string UnityHost = "127.0.0.1";
        static int _unityPort;
        static string _unityPortFile;
        static int _agentNotified;

        static readonly string LogFile = Path.Combine(Path.GetTempPath(), "unislop-server.log");

        static int Main(string[] args)
        {
            int mcpPort = ParsePort(args, 0, DefaultMcpPort);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log("unhandled: " + (e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString()));

            try
            {
                ConfigureUnityPort(args);
            }
            catch (Exception e)
            {
                Log("bad Unity endpoint: " + e.Message);
                return 1;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + mcpPort + "/");
            try
            {
                listener.Start();
            }
            catch (Exception e)
            {
                Log("failed to bind :" + mcpPort + " - " + e.Message);
                return 1;
            }

            Log("listening on http://127.0.0.1:" + mcpPort + "/mcp -> Unity " + UnityEndpointDescription());

            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception e)
                {
                    Log("listener stopped: " + e.Message);
                    break;
                }

                ThreadPool.QueueUserWorkItem(delegate { HandleHttp(context); });
            }

            return 0;
        }

        // --- MCP transport (Streamable HTTP, JSON responses) ---------------------------------

        static void HandleHttp(HttpListenerContext context)
        {
            try
            {
                NotifyAgentConnectedOnce();

                string method = context.Request.HttpMethod;
                if (method == "GET")
                {
                    // No server-initiated SSE stream; spec allows declining the GET.
                    Respond(context, 405, null, null);
                    return;
                }
                if (method != "POST")
                {
                    Respond(context, 404, null, null);
                    return;
                }

                string requestBody = ReadBody(context.Request);
                object parsed;
                try
                {
                    parsed = Json.Parse(requestBody);
                }
                catch (Exception e)
                {
                    Respond(context, 200, "application/json",
                        Json.Write(RpcError(null, -32700, "Parse error: " + e.Message)));
                    return;
                }

                // A JSON-RPC payload is either a single message or a batch array.
                List<object> batch = parsed as List<object>;
                if (batch != null)
                {
                    var responses = new List<object>();
                    foreach (object item in batch)
                    {
                        var message = item as Dictionary<string, object>;
                        NotifyMcpRequest(message);
                        object response = ProcessMessage(message);
                        if (response != null) responses.Add(response);
                    }
                    if (responses.Count == 0)
                        Respond(context, 202, null, null);
                    else
                        Respond(context, 200, "application/json", Json.Write(responses));
                    return;
                }

                var singleMessage = parsed as Dictionary<string, object>;
                NotifyMcpRequest(singleMessage);
                object single = ProcessMessage(singleMessage);
                if (single == null)
                    Respond(context, 202, null, null);
                else
                    Respond(context, 200, "application/json", Json.Write(single));
            }
            catch (Exception e)
            {
                Log("handler error: " + e);
                try { Respond(context, 500, null, null); }
                catch { }
            }
        }

        // Returns a JSON-RPC response object, or null for notifications (which get a bare 202).
        static object ProcessMessage(Dictionary<string, object> message)
        {
            if (message == null)
                return RpcError(null, -32600, "Invalid request");

            string method = Json.GetString(message, "method", null);
            bool isRequest = message.ContainsKey("id");
            object id = isRequest ? message["id"] : null;

            if (method == null)
                return isRequest ? RpcError(id, -32600, "Invalid request") : (object)null;

            // Notifications (no id, "notifications/*") never get a response.
            if (!isRequest)
                return null;

            switch (method)
            {
                case "initialize":
                    return RpcResult(id, Initialize(Json.GetObject(message, "params")));
                case "ping":
                    return RpcResult(id, new Dictionary<string, object>());
                case "tools/list":
                    return RpcResult(id, ToolsList());
                case "tools/call":
                    return RpcResult(id, ToolsCall(Json.GetObject(message, "params")));
                default:
                    return RpcError(id, -32601, "Method not found: " + method);
            }
        }

        static object Initialize(Dictionary<string, object> args)
        {
            string requested = Json.GetString(args, "protocolVersion", ProtocolVersion);

            var caps = new Dictionary<string, object>();
            caps["tools"] = new Dictionary<string, object>();

            var info = new Dictionary<string, object>();
            info["name"] = ServerName;
            info["version"] = ServerVersion;

            var result = new Dictionary<string, object>();
            result["protocolVersion"] = string.IsNullOrEmpty(requested) ? ProtocolVersion : requested;
            result["capabilities"] = caps;
            result["serverInfo"] = info;
            return result;
        }

        // --- Tools ---------------------------------------------------------------------------

        static object ToolsList()
        {
            var tools = new List<object>();
            tools.Add(Tool("unity_compile",
                "Compile the Unity project's C# scripts in the Unity Editor. Triggers a Unity domain "
                + "reload. wait:true (default) waits through the reload and returns compiler errors.",
                "{\"type\":\"object\",\"properties\":{\"wait\":{\"type\":\"boolean\",\"default\":true,"
                + "\"description\":\"When true, wait for compilation to finish (across domain reload) "
                + "and return errors. When false, only request compilation.\"}}}"));
            tools.Add(Tool("unity_run_tests",
                "Run Unity tests via the Unity Test Runner and return pass/fail counts with failure "
                + "details. Defaults to running all tests (Edit Mode + Play Mode).",
                "{\"type\":\"object\",\"properties\":{"
                + "\"mode\":{\"type\":\"string\",\"enum\":[\"all\",\"editmode\",\"playmode\"],"
                + "\"default\":\"all\",\"description\":\"Which tests to run: 'all' (default) runs both "
                + "Edit Mode and Play Mode.\"},"
                + "\"filter\":{\"type\":\"string\",\"description\":\"Optional test name filter passed "
                + "to the Unity Test Runner.\"}}}"));
            tools.Add(Tool("unity_list_tests",
                "List available Unity tests (Edit Mode and Play Mode) from the Unity Test Runner. "
                + "Player/build tests are not enumerable from the Unity Editor.",
                "{\"type\":\"object\",\"properties\":{}}"));

            var result = new Dictionary<string, object>();
            result["tools"] = tools;
            return result;
        }

        static object Tool(string name, string description, string schemaJson)
        {
            var tool = new Dictionary<string, object>();
            tool["name"] = name;
            tool["description"] = description;
            tool["inputSchema"] = Json.Parse(schemaJson);
            return tool;
        }

        static object ToolsCall(Dictionary<string, object> args)
        {
            string name = Json.GetString(args, "name", null);
            Dictionary<string, object> arguments = Json.GetObject(args, "arguments");

            try
            {
                switch (name)
                {
                    case "unity_compile":
                        return CallCompile(arguments);
                    case "unity_run_tests":
                        return CallRunTests(arguments);
                    case "unity_list_tests":
                        return CallListTests();
                    default:
                        return TextContent("Unknown tool: " + name, true);
                }
            }
            catch (Exception e)
            {
                return TextContent(name + " failed: " + e.Message, true);
            }
        }

        static object CallCompile(Dictionary<string, object> arguments)
        {
            bool wait = Json.GetBool(arguments, "wait", true);

            if (!wait)
            {
                var p = new Dictionary<string, object>();
                p["wait"] = false;
                UnityResponse started = CallUnity("compile_start", p);
                return TextContent(FormatResult(started), false);
            }

            var jobParams = new Dictionary<string, object>();
            jobParams["wait"] = true;
            UnityResponse result = RunJob("compile_start", "compile_status", jobParams);
            int errorCount = (int)Json.GetNumber(result.Data as Dictionary<string, object>, "errorCount", 0);
            return TextContent(FormatResult(result), errorCount > 0);
        }

        static object CallRunTests(Dictionary<string, object> arguments)
        {
            var p = new Dictionary<string, object>();
            p["mode"] = Json.GetString(arguments, "mode", "all");
            string filter = Json.GetString(arguments, "filter", null);
            if (filter != null) p["filter"] = filter;

            UnityResponse result = RunJob("run_tests_start", "run_tests_status", p);
            int failed = (int)Json.GetNumber(result.Data as Dictionary<string, object>, "failed", 0);
            return TextContent(FormatResult(result), failed > 0);
        }

        static object CallListTests()
        {
            long deadline = NowMs() + JobTimeoutMs;
            UnityResponse result = CallUnityResilient("list_tests", new Dictionary<string, object>(), deadline);
            return TextContent(FormatResult(result), false);
        }

        // --- Unity internal API client (dynamic port) ---------------------------------------

        // Drives a Unity job that may span domain reloads: kick it off, then poll status until done.
        static UnityResponse RunJob(string startCommand, string statusCommand, Dictionary<string, object> parameters)
        {
            long deadline = NowMs() + JobTimeoutMs;

            UnityResponse started = CallUnityResilient(startCommand, parameters, deadline);

            // The start call itself may already report a terminal result (e.g. nothing to compile).
            if (StateOf(started) == "done")
                return started;

            while (NowMs() < deadline)
            {
                Thread.Sleep(PollIntervalMs);
                // A status poll is never terminal: during a domain reload Unity may be unreachable or
                // briefly report "reloading". Swallow any error and keep polling until the job reports
                // a terminal state or the deadline passes.
                try
                {
                    UnityResponse status = CallUnity(statusCommand, null);
                    string state = StateOf(status);
                    if (state == "done" || state == "idle")
                        return status;
                }
                catch (UnityErrorException)
                {
                    throw;
                }
                catch
                {
                    // transient - Unity is mid-reload or the socket dropped; retry.
                }
            }

            throw new Exception("Job '" + startCommand + "' did not finish within " + (JobTimeoutMs / 1000) + "s");
        }

        // Like CallUnity, but tolerates the connection dropping while Unity reloads its AppDomain.
        // Retries transport failures until the deadline; surfaces Unity error statuses immediately.
        static UnityResponse CallUnityResilient(string command, Dictionary<string, object> parameters, long deadline)
        {
            string lastError = "none";
            while (NowMs() < deadline)
            {
                try
                {
                    return CallUnity(command, parameters);
                }
                catch (UnityErrorException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    Thread.Sleep(ReconnectIntervalMs);
                }
            }
            throw new Exception("Unity did not respond within " + (JobTimeoutMs / 1000) + "s (last error: " + lastError + ")");
        }

        // Single POST to the Unity in-process API. Throws UnityErrorException on a Unity-reported
        // error status, or a transport exception if Unity is unreachable (e.g. mid-domain-reload).
        //
        // Deliberately uses a raw socket rather than HttpWebRequest: under Unity's MonoBleedingEdge
        // the WebRequest stack throws MissingFieldException, and a generic retry would then spin
        // forever. The editor side speaks the same minimal HTTP/1.1 with Content-Length framing.
        static UnityResponse CallUnity(string command, Dictionary<string, object> parameters)
        {
            var payload = new Dictionary<string, object>();
            payload["command"] = command;
            if (parameters != null)
            {
                foreach (KeyValuePair<string, object> kv in parameters)
                    payload[kv.Key] = kv.Value;
            }

            byte[] body = Encoding.UTF8.GetBytes(Json.Write(payload));
            string responseText = PostHttp(body);

            var obj = Json.Parse(responseText) as Dictionary<string, object>;
            string status = Json.GetString(obj, "status", null);
            string message = Json.GetString(obj, "message", "");
            object data = obj != null && obj.ContainsKey("data") ? obj["data"] : null;

            if (status == "error")
                throw new UnityErrorException(message);

            return new UnityResponse(status, message, data);
        }

        // Sends one HTTP/1.1 POST to the editor and returns the response body. One fresh connection
        // per call (Connection: close) keeps the protocol trivial.
        static string PostHttp(byte[] body)
        {
            int unityPort = ResolveUnityPort();
            using (var client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(UnityHost, unityPort, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(CallTimeoutMs))
                    throw new IOException("Connect to Unity timed out");
                client.EndConnect(connect);

                client.NoDelay = true;
                client.SendTimeout = CallTimeoutMs;
                client.ReceiveTimeout = CallTimeoutMs;

                using (NetworkStream stream = client.GetStream())
                {
                    string head = "POST / HTTP/1.1\r\n"
                        + "Host: " + UnityHost + "\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();

                    return ReadHttpResponseBody(stream);
                }
            }
        }

        static void ConfigureUnityPort(string[] args)
        {
            int fixedPort;
            if (args != null && args.Length > 1 && int.TryParse(args[1], out fixedPort) && fixedPort > 0)
            {
                _unityPort = fixedPort;
                _unityPortFile = null;
                return;
            }

            if (args == null || args.Length <= 1 || string.IsNullOrEmpty(args[1]))
                throw new ArgumentException("Unity API port or port-file argument is required");

            _unityPortFile = args[1];
        }

        static int ResolveUnityPort()
        {
            if (string.IsNullOrEmpty(_unityPortFile))
                return _unityPort;

            string text = File.ReadAllText(_unityPortFile).Trim();
            int port;
            if (!int.TryParse(text, out port) || port <= 0)
                throw new IOException("Invalid Unity API port file: " + _unityPortFile);
            return port;
        }

        static string UnityEndpointDescription()
        {
            return string.IsNullOrEmpty(_unityPortFile)
                ? UnityHost + ":" + _unityPort
                : "port-file " + _unityPortFile;
        }

        // Reads an HTTP/1.1 response: consume headers, then exactly Content-Length bytes of body.
        // Even though we use one connection per request, framing on Content-Length avoids waiting on
        // connection teardown.
        static string ReadHttpResponseBody(Stream stream)
        {
            var received = new MemoryStream();
            var buffer = new byte[8192];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) throw new IOException("Connection closed before headers");
                received.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            byte[] all = received.GetBuffer();
            int total = (int)received.Length;
            string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            int contentLength = ParseContentLength(headers);
            if (contentLength < 0)
                throw new IOException("Response missing Content-Length");

            int bodyStart = headerEnd + 4;
            var bodyStream = new MemoryStream();
            if (total > bodyStart)
                bodyStream.Write(all, bodyStart, total - bodyStart);

            while (bodyStream.Length < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) throw new IOException("Connection closed before response body completed");
                bodyStream.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(bodyStream.GetBuffer(), 0, (int)bodyStream.Length);
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
            string[] lines = headers.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                int colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                if (!trimmed.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                int value;
                if (int.TryParse(trimmed.Substring(colon + 1).Trim(), out value))
                    return value;
            }
            return -1;
        }

        static void NotifyAgentConnectedOnce()
        {
            if (Interlocked.Exchange(ref _agentNotified, 1) == 1)
                return;
            try
            {
                CallUnity("agent_connected", null);
            }
            catch
            {
                // Unity may not be ready yet; the toolbar updates on the next tool call.
            }
        }

        static void NotifyMcpRequest(Dictionary<string, object> message)
        {
            string method = Json.GetString(message, "method", null);
            if (string.IsNullOrEmpty(method))
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var parameters = new Dictionary<string, object>();
                    parameters["method"] = method;
                    CallUnity("mcp_request", parameters);
                }
                catch
                {
                    // Debug logging is best-effort; never delay or fail the MCP request.
                }
            });
        }

        static string FormatResult(UnityResponse result)
        {
            var data = result.Data as Dictionary<string, object>;
            if (data == null)
                return result.Message;
            return result.Message + "\n\n" + Json.WritePretty(result.Data);
        }

        static string StateOf(UnityResponse result)
        {
            var data = result.Data as Dictionary<string, object>;
            return Json.GetString(data, "state", null);
        }

        // --- JSON-RPC helpers ----------------------------------------------------------------

        static Dictionary<string, object> RpcResult(object id, object result)
        {
            var d = new Dictionary<string, object>();
            d["jsonrpc"] = "2.0";
            d["id"] = id;
            d["result"] = result;
            return d;
        }

        static Dictionary<string, object> RpcError(object id, int code, string message)
        {
            var error = new Dictionary<string, object>();
            error["code"] = code;
            error["message"] = message;

            var d = new Dictionary<string, object>();
            d["jsonrpc"] = "2.0";
            d["id"] = id;
            d["error"] = error;
            return d;
        }

        static object TextContent(string text, bool isError)
        {
            var entry = new Dictionary<string, object>();
            entry["type"] = "text";
            entry["text"] = text;

            var content = new List<object>();
            content.Add(entry);

            var result = new Dictionary<string, object>();
            result["content"] = content;
            if (isError) result["isError"] = true;
            return result;
        }

        // --- HTTP / misc helpers -------------------------------------------------------------

        static string ReadBody(HttpListenerRequest request)
        {
            Encoding encoding = request.ContentEncoding != null ? request.ContentEncoding : Encoding.UTF8;
            using (var reader = new StreamReader(request.InputStream, encoding))
                return reader.ReadToEnd();
        }

        static void Respond(HttpListenerContext context, int statusCode, string contentType, string body)
        {
            try
            {
                context.Response.StatusCode = statusCode;
                if (contentType != null)
                    context.Response.ContentType = contentType;

                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    context.Response.ContentLength64 = 0;
                }
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        static int ParsePort(string[] args, int index, int fallback)
        {
            int value;
            if (args != null && args.Length > index && int.TryParse(args[index], out value) && value > 0)
                return value;
            return fallback;
        }

        static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static void Log(string line)
        {
            try
            {
                File.AppendAllText(LogFile, "[" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "] " + line + "\n");
            }
            catch
            {
                // Logging must never throw.
            }
        }

        sealed class UnityResponse
        {
            public readonly string Status;
            public readonly string Message;
            public readonly object Data;

            public UnityResponse(string status, string message, object data)
            {
                Status = status;
                Message = message;
                Data = data;
            }
        }

        sealed class UnityErrorException : Exception
        {
            public UnityErrorException(string message) : base(message) { }
        }
    }

    // Minimal JSON reader/writer for the subset MCP needs. Objects map to Dictionary<string,object>,
    // arrays to List<object>, numbers to double, plus string / bool / null.
    static class Json
    {
        public static object Parse(string text)
        {
            int index = 0;
            object value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
                throw new FormatException("Trailing characters in JSON");
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, false, 0);
            return sb.ToString();
        }

        public static string WritePretty(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, true, 0);
            return sb.ToString();
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback)
        {
            if (obj == null || !obj.ContainsKey(key)) return fallback;
            string s = obj[key] as string;
            return s != null ? s : fallback;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj == null || !obj.ContainsKey(key)) return fallback;
            object v = obj[key];
            if (v is bool) return (bool)v;
            return fallback;
        }

        public static double GetNumber(Dictionary<string, object> obj, string key, double fallback)
        {
            if (obj == null || !obj.ContainsKey(key)) return fallback;
            object v = obj[key];
            if (v is double) return (double)v;
            return fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key)) return null;
            return obj[key] as Dictionary<string, object>;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':'");
                i++;
                object value = ParseValue(s, ref i);
                result[key] = value;
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("Expected ',' or '}'");
            }
            return result;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                object value = ParseValue(s, ref i);
                result.Add(value);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("Expected ',' or ']'");
            }
            return result;
        }

        static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected string");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw new FormatException("Bad escape '\\" + e + "'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            string token = s.Substring(start, i - start);
            double value;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("Invalid number '" + token + "'");
            return value;
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException("Expected '" + literal + "'");
            i += literal.Length;
        }

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static void WriteValue(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is double) { WriteNumber(sb, (double)value); return; }
            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }

            var dict = value as Dictionary<string, object>;
            if (dict != null) { WriteObject(sb, dict, pretty, depth); return; }

            var list = value as List<object>;
            if (list != null) { WriteArray(sb, list, pretty, depth); return; }

            WriteString(sb, value.ToString());
        }

        static void WriteObject(StringBuilder sb, Dictionary<string, object> dict, bool pretty, int depth)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, kv.Value, pretty, depth + 1);
            }
            NewlineIndent(sb, pretty, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, List<object> list, bool pretty, int depth)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                NewlineIndent(sb, pretty, depth + 1);
                WriteValue(sb, item, pretty, depth + 1);
            }
            NewlineIndent(sb, pretty, depth);
            sb.Append(']');
        }

        static void NewlineIndent(StringBuilder sb, bool pretty, int depth)
        {
            if (!pretty) return;
            sb.Append('\n');
            for (int n = 0; n < depth; n++) sb.Append("  ");
        }

        static void WriteNumber(StringBuilder sb, double value)
        {
            // Emit integral values without a trailing ".0" so ids and counts stay clean.
            if (value == Math.Floor(value) && !double.IsInfinity(value)
                && value >= long.MinValue && value <= long.MaxValue)
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
