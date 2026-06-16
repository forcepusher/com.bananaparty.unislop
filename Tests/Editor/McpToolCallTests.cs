using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UniSlop.MCP;

namespace UniSlop.MCP.Tests
{
    // Exercises every MCP tool end to end: the real mono server process is driven over the MCP
    // socket (:5107-style) and proxies to a mock Unity API standing in for the editor's internal
    // listener. This covers the full server path (MCP parse -> tool dispatch -> Unity client ->
    // result formatting) for each tool, plus the success/failure branches, without triggering real
    // compiles or domain reloads. The server is launched with a fixed numeric Unity port here (the
    // runtime instead passes a port file path, which the server re-reads before each call).
    public class McpToolCallTests
    {
        const int McpPort = 5612;
        const int UnityPort = 5613;

        static string _exe;
        static Process _server;
        static MockUnityApi _mock;

        [OneTimeSetUp]
        public void StartServer()
        {
            _mock = new MockUnityApi(UnityPort);

            _exe = Path.Combine(Path.GetTempPath(), "unislop-toolcall-" + Guid.NewGuid().ToString("N") + ".exe");
            string output;
            Assert.IsTrue(McpServer.CompileServer(_exe, out output), "Compile failed:\n" + output);

            _server = Process.Start(new ProcessStartInfo
            {
                FileName = McpServer.MonoExecutablePath,
                Arguments = $"\"{_exe}\" {McpPort} {UnityPort}",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 15)
            {
                if (TryConnect(McpPort)) return;
                Thread.Sleep(100);
            }
            Assert.Fail("MCP server never started listening on port " + McpPort);
        }

        [OneTimeTearDown]
        public void StopServer()
        {
            try { if (_server != null && !_server.HasExited) _server.Kill(); } catch { }
            try { if (_server != null) _server.Dispose(); } catch { }
            try { if (_mock != null) _mock.Dispose(); } catch { }
            try { if (_exe != null && File.Exists(_exe)) File.Delete(_exe); } catch { }
        }

        [SetUp]
        public void ResetMock()
        {
            _mock.CompileErrorCount = 0;
            _mock.TestsFailed = 0;
            _mock.TestsPassed = 3;
        }

        [Test]
        public void ListTestsTool_ReturnsTestCounts()
        {
            string result = CallTool("unity_list_tests", "{}");
            StringAssert.Contains("Listed 2 test(s)", result);
            StringAssert.Contains("editmode", result);
            Assert.IsFalse(IsError(result), "unexpected isError:\n" + result);
        }

        [Test]
        public void CompileTool_NoErrors_ReportsSuccess()
        {
            string result = CallTool("unity_compile", "{\"wait\":true}");
            StringAssert.Contains("Compilation finished with no errors", result);
            StringAssert.Contains("errorCount", result);
            Assert.IsFalse(IsError(result), "expected isError to be absent:\n" + result);
        }

        [Test]
        public void CompileTool_WithErrors_ReportsIsError()
        {
            _mock.CompileErrorCount = 2;
            string result = CallTool("unity_compile", "{\"wait\":true}");
            StringAssert.Contains("2 error", result);
            Assert.IsTrue(IsError(result), "expected isError:true:\n" + result);
        }

        [Test]
        public void CompileTool_NoWait_ReturnsImmediately()
        {
            string result = CallTool("unity_compile", "{\"wait\":false}");
            StringAssert.Contains("Compilation started", result);
            Assert.IsFalse(IsError(result), "unexpected isError:\n" + result);
        }

        [Test]
        public void RunTestsTool_AllPass_ReportsSuccess()
        {
            string result = CallTool("unity_run_tests", "{\"mode\":\"all\"}");
            StringAssert.Contains("Tests passed", result);
            StringAssert.Contains("total", result);
            Assert.IsFalse(IsError(result), "unexpected isError:\n" + result);
        }

        [Test]
        public void RunTestsTool_WithFailures_ReportsIsError()
        {
            _mock.TestsFailed = 1;
            string result = CallTool("unity_run_tests", "{\"mode\":\"editmode\",\"filter\":\"Some.Test\"}");
            StringAssert.Contains("Tests failed", result);
            Assert.IsTrue(IsError(result), "expected isError:true:\n" + result);
        }

        // --- helpers -------------------------------------------------------------------------

        static string CallTool(string name, string argumentsJson)
        {
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
                + name + "\",\"arguments\":" + argumentsJson + "}}";
            return PostMcp(McpPort, body);
        }

        static bool IsError(string responseBody)
        {
            return responseBody.Contains("\"isError\":true") || responseBody.Contains("\"isError\": true");
        }

        static bool TryConnect(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect("127.0.0.1", port);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        static string PostMcp(int port, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (NetworkStream stream = client.GetStream())
                {
                    string head = "POST /mcp HTTP/1.1\r\n"
                        + "Host: 127.0.0.1\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();

                    using (var received = new MemoryStream())
                    {
                        var buffer = new byte[4096];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            received.Write(buffer, 0, read);

                        string raw = Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
                        int bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        return bodyStart >= 0 ? raw.Substring(bodyStart + 4) : raw;
                    }
                }
            }
        }

        // A stand-in for the editor's internal JSON API. Answers the same command set the real
        // McpUnityBridge does, with canned, test-configurable results.
        sealed class MockUnityApi : IDisposable
        {
            public volatile int CompileErrorCount;
            public volatile int TestsFailed;
            public volatile int TestsPassed = 3;

            readonly TcpListener _listener;
            readonly Thread _thread;
            volatile bool _running;

            public MockUnityApi(int port)
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "MockUnityApi" };
                _thread.Start();
            }

            void Loop()
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { break; }
                    try { Handle(client); }
                    catch { }
                }
            }

            void Handle(TcpClient client)
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string requestBody = ReadRequest(stream);
                    string response = Respond(requestBody ?? "");
                    byte[] body = Encoding.UTF8.GetBytes(response);
                    string head = "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                }
            }

            string Respond(string body)
            {
                if (body.Contains("compile_start"))
                    return "{\"status\":\"success\",\"message\":\"Compilation started\",\"data\":{\"state\":\"running\"}}";

                if (body.Contains("compile_status"))
                {
                    int n = CompileErrorCount;
                    string message = n == 0
                        ? "Compilation finished with no errors"
                        : "Compilation finished with " + n + " error(s)";
                    return "{\"status\":\"success\",\"message\":\"" + message
                        + "\",\"data\":{\"state\":\"done\",\"errorCount\":" + n + ",\"errors\":[]}}";
                }

                if (body.Contains("run_tests_start"))
                    return "{\"status\":\"success\",\"message\":\"Test run started\",\"data\":{\"state\":\"running\"}}";

                if (body.Contains("run_tests_status"))
                {
                    int failed = TestsFailed;
                    int passed = TestsPassed;
                    int total = passed + failed;
                    string message = failed == 0
                        ? "Tests passed (" + passed + "/" + total + ")"
                        : "Tests failed (" + failed + " failure(s), " + passed + "/" + total + " passed)";
                    return "{\"status\":\"success\",\"message\":\"" + message
                        + "\",\"data\":{\"state\":\"done\",\"passed\":" + passed + ",\"failed\":" + failed
                        + ",\"total\":" + total + "}}";
                }

                if (body.Contains("list_tests"))
                    return "{\"status\":\"success\",\"message\":\"Listed 2 test(s) (1 edit, 1 play)\","
                        + "\"data\":{\"editmode\":{\"count\":1},\"playmode\":{\"count\":1}}}";

                if (body.Contains("agent_connected"))
                    return "{\"status\":\"success\",\"message\":\"Agent connected\"}";

                return "{\"status\":\"error\",\"message\":\"Unknown command\"}";
            }

            static string ReadRequest(NetworkStream stream)
            {
                var received = new MemoryStream();
                var buffer = new byte[4096];
                int headerEnd = -1;

                while (headerEnd < 0)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return null;
                    received.Write(buffer, 0, read);
                    headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
                }

                byte[] all = received.GetBuffer();
                int total = (int)received.Length;
                string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
                int contentLength = ParseContentLength(headers);

                int bodyStart = headerEnd + 4;
                var bodyStream = new MemoryStream();
                if (total > bodyStart)
                    bodyStream.Write(all, bodyStart, total - bodyStart);

                while (contentLength >= 0 && bodyStream.Length < contentLength)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
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
                foreach (string line in headers.Split('\n'))
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

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { if (_thread != null) _thread.Join(500); } catch { }
            }
        }
    }
}
