using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniSlop.MCP.Tests
{
    // Integration coverage for the mono compile + run chain. These spawn the real MCP server
    // process and talk to it over the loopback socket, so they exercise the exact path the editor
    // uses at runtime.
    public class McpServerIntegrationTests
    {
        const int TestMcpPort = 5610;
        const int TestUnityPort = 5611; // intentionally has no listener; agent_connected just fails

        [Test]
        public void ServerSource_CompilesWithBundledCompiler()
        {
            string exe = TempExePath();
            try
            {
                string output;
                bool compiled = McpServer.CompileServer(exe, out output);
                Assert.IsTrue(compiled, "MCP server failed to compile with the bundled mcs:\n" + output);
                FileAssert.Exists(exe);
            }
            finally
            {
                TryDelete(exe);
            }
        }

        [UnityTest]
        public IEnumerator CompiledServer_AnswersMcpHandshake()
        {
            string exe = TempExePath();
            string output;
            Assert.IsTrue(McpServer.CompileServer(exe, out output), "Compile failed:\n" + output);

            Process process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = McpServer.MonoExecutablePath,
                    Arguments = $"\"{exe}\" {TestMcpPort} {TestUnityPort}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);

                float deadline = Time.realtimeSinceStartup + 15f;
                bool listening = false;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (TestHttp.TryConnect(TestMcpPort)) { listening = true; break; }
                    yield return null;
                }
                Assert.IsTrue(listening, "MCP server never started listening on port " + TestMcpPort);

                string initialize = TestHttp.Post(TestMcpPort, "/mcp",
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                StringAssert.Contains("\"serverInfo\"", initialize);
                StringAssert.Contains("unity-mcp", initialize);

                string tools = TestHttp.Post(TestMcpPort, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
                StringAssert.Contains("unity_compile", tools);
                StringAssert.Contains("unity_run_tests", tools);
                StringAssert.Contains("unity_list_tests", tools);

                string unknown = TestHttp.Post(TestMcpPort, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"does_not_exist\"}");
                StringAssert.Contains("-32601", unknown);
            }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
                try { if (process != null) process.Dispose(); } catch { }
                TryDelete(exe);
            }
        }

        static string TempExePath()
        {
            return Path.Combine(Path.GetTempPath(), "unislop-test-" + Guid.NewGuid().ToString("N") + ".exe");
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
