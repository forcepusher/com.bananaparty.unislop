using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UniSlop.MCP;

namespace UniSlop.MCP.Tests
{
    // Integration coverage for the mono compile + run chain that replaced Bun. These spawn the real
    // MCP server process and talk to it over the loopback socket, so they exercise the exact path
    // the editor uses at runtime. HttpWebRequest is broken under Unity's MonoBleedingEdge, so the
    // client side here speaks HTTP over a raw TcpClient, just like Server.cs does.
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
                    if (TryConnect(TestMcpPort)) { listening = true; break; }
                    yield return null;
                }
                Assert.IsTrue(listening, "MCP server never started listening on port " + TestMcpPort);

                string initialize = PostMcp(TestMcpPort,
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                StringAssert.Contains("\"serverInfo\"", initialize);
                StringAssert.Contains("UniSlop", initialize);

                string tools = PostMcp(TestMcpPort, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
                StringAssert.Contains("unity_compile", tools);
                StringAssert.Contains("unity_run_tests", tools);
                StringAssert.Contains("unity_list_tests", tools);

                string unknown = PostMcp(TestMcpPort, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"does_not_exist\"}");
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

        // Minimal HTTP/1.1 POST to the MCP endpoint, returning the response body.
        static string PostMcp(int port, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 20000;
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
    }
}
