using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;

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
                if (TestHttp.TryConnect(McpPort)) return;
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

        // --- tools ---------------------------------------------------------------------------

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

        [Test]
        public void UnknownTool_ReportsIsError()
        {
            string result = CallTool("unity_does_not_exist", "{}");
            StringAssert.Contains("Unknown tool", result);
            Assert.IsTrue(IsError(result), "expected isError:true:\n" + result);
        }

        // --- protocol ------------------------------------------------------------------------

        [Test]
        public void Ping_ReturnsEmptyResult()
        {
            string result = TestHttp.Post(McpPort, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
            StringAssert.Contains("\"result\"", result);
            StringAssert.Contains("\"id\":7", result);
        }

        [Test]
        public void Initialize_EchoesRequestedProtocolVersion()
        {
            string result = TestHttp.Post(McpPort, "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
            StringAssert.Contains("2024-11-05", result);
            StringAssert.Contains("\"serverInfo\"", result);
        }

        [Test]
        public void MalformedJson_ReturnsParseError()
        {
            string result = TestHttp.Post(McpPort, "/mcp", "{this is not json");
            StringAssert.Contains("-32700", result);
        }

        [Test]
        public void Notification_ReturnsAcceptedWithNoBody()
        {
            string raw = TestHttp.Request(McpPort, "POST", "/mcp",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            StringAssert.Contains("202", raw.Split('\r')[0]);
        }

        [Test]
        public void BatchRequest_ReturnsResponseForEachRequest()
        {
            string result = TestHttp.Post(McpPort, "/mcp",
                "[{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"ping\"}]");
            StringAssert.Contains("\"id\":11", result);
            StringAssert.Contains("\"id\":12", result);
        }

        [Test]
        public void GetRequest_IsRejected()
        {
            string raw = TestHttp.Request(McpPort, "GET", "/mcp", null);
            StringAssert.Contains("405", raw.Split('\r')[0]);
        }

        // --- helpers -------------------------------------------------------------------------

        static string CallTool(string name, string argumentsJson)
        {
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
                + name + "\",\"arguments\":" + argumentsJson + "}}";
            return TestHttp.Post(McpPort, "/mcp", body);
        }

        static bool IsError(string responseBody)
        {
            return responseBody.Contains("\"isError\":true") || responseBody.Contains("\"isError\": true");
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
                    string requestBody = TestHttp.ReadRequestBody(stream);
                    TestHttp.WriteJsonResponse(stream, Respond(requestBody ?? ""));
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

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { if (_thread != null) _thread.Join(500); } catch { }
            }
        }
    }
}
