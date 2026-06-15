using NUnit.Framework;
using UniSlop.MCP;

namespace UniSlop.MCP.Tests
{
    public class McpUnityBridgeTests
    {
        [Test]
        public void Handle_UnknownCommand_ReturnsError()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "missing" });
            Assert.That(json, Does.Contain("\"status\":\"error\""));
            Assert.That(json, Does.Contain("Unknown command"));
        }
    }
}
