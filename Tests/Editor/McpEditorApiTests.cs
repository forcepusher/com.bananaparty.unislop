using System.IO;
using NUnit.Framework;

namespace UniSlop.MCP.Tests
{
    // Talks to the editor's real in-process JSON listener (the one the mono MCP server proxies to)
    // over the port published in Library/UniSlop/unity-api-port.txt. Exercises the raw-socket HTTP
    // parsing, command extraction and response writing exactly as the MCP server does at runtime.
    // Only commands that never wait on the main thread are used, since these tests hold it.
    public class McpEditorApiTests
    {
        static int LivePort()
        {
            if (!McpServer.IsListening)
                Assert.Ignore("MCP internal listener is not running in this editor.");

            string portFile = McpServer.UnityApiPortFilePath;
            int port = 0;
            if (!File.Exists(portFile) || !int.TryParse(File.ReadAllText(portFile).Trim(), out port))
                Assert.Ignore("MCP internal API port file is missing or invalid.");
            return port;
        }

        [Test]
        public void UnknownCommand_ReturnsErrorJson()
        {
            string response = TestHttp.Post(LivePort(), "/", "{\"command\":\"definitely_not_a_command\"}");
            StringAssert.Contains("\"status\":\"error\"", response);
            StringAssert.Contains("Unknown command", response);
        }

        [Test]
        public void MissingCommand_ReturnsErrorJson()
        {
            string response = TestHttp.Post(LivePort(), "/", "{\"other\":\"field\"}");
            StringAssert.Contains("\"status\":\"error\"", response);
            StringAssert.Contains("Missing command", response);
        }

        [Test]
        public void CompileStatus_ReturnsSuccessJson()
        {
            string response = TestHttp.Post(LivePort(), "/", "{\"command\":\"compile_status\"}");
            StringAssert.Contains("\"status\":\"success\"", response);
            StringAssert.Contains("\"state\":", response);
        }

        [Test]
        public void EscapedCommandString_IsUnescapedBeforeDispatch()
        {
            // The \" escape must survive extraction; the echoed error proves the parser decoded it.
            string response = TestHttp.Post(LivePort(), "/", "{\"command\":\"quo\\\"ted\"}");
            StringAssert.Contains("\"status\":\"error\"", response);
            StringAssert.Contains("quo\\\"ted", response);
        }
    }
}
