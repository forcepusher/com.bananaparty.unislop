using NUnit.Framework;

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

        [Test]
        public void Handle_CompileStatus_ReturnsSuccessWithState()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "compile_status" });
            Assert.That(json, Does.Contain("\"status\":\"success\""));
            Assert.That(json, Does.Contain("\"state\":"));
            Assert.That(json, Does.Contain("\"errorCount\":"));
        }

        [Test]
        public void Handle_TestStatus_ReturnsSuccessWithState()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "run_tests_status" });
            Assert.That(json, Does.Contain("\"status\":\"success\""));
            Assert.That(json, Does.Contain("\"state\":"));
        }

        [Test]
        public void Handle_RunTestsStart_UnknownMode_ReturnsError()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "run_tests_start", mode = "bogus" });
            Assert.That(json, Does.Contain("\"status\":\"error\""));
            Assert.That(json, Does.Contain("Unknown test mode"));
        }

        // These tests themselves execute inside a Unity test run, so the run-active guards are
        // deterministically triggered.
        [Test]
        public void Handle_RunTestsStart_WhileRunActive_ReturnsError()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "run_tests_start", mode = "editmode" });
            Assert.That(json, Does.Contain("\"status\":\"error\""));
            Assert.That(json, Does.Contain("already in progress"));
        }

        [Test]
        public void Handle_ListTests_WhileRunActive_ReturnsError()
        {
            string json = McpUnityBridge.Handle(new McpRequest { command = "list_tests" });
            Assert.That(json, Does.Contain("\"status\":\"error\""));
            Assert.That(json, Does.Contain("test run is in progress"));
        }

        [Test]
        public void JsonStr_Null_ReturnsJsonNull()
        {
            Assert.AreEqual("null", McpUnityBridge.JsonStr(null));
        }

        [Test]
        public void JsonStr_EscapesSpecialCharacters()
        {
            Assert.AreEqual("\"a\\\"b\\\\c\\nd\\re\"", McpUnityBridge.JsonStr("a\"b\\c\nd\re"));
        }

        [Test]
        public void Error_WithData_EmbedsDataJson()
        {
            string json = McpUnityBridge.Error("boom", "{\"x\":1}");
            Assert.AreEqual("{\"status\":\"error\",\"message\":\"boom\",\"data\":{\"x\":1}}", json);
        }
    }
}
