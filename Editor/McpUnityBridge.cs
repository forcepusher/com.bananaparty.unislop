using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace UniSlop.MCP
{
    [Serializable]
    public class McpRequest
    {
        public string command;
        public string mode = "all";
        public string filter;
    }

    // Internal API behind the detached MCP server. Each command is short and non-blocking: compile
    // and tests are kicked off as jobs (McpCompileJob / McpTestJob) and the MCP server polls
    // *_status across domain reloads. Nothing here blocks waiting for a reload.
    public static class McpUnityBridge
    {
        const int DefaultTimeoutMs = 30_000;
        const int ListTestsTimeoutMs = 60_000;

        public static string Handle(McpRequest request)
        {
            // Status polls are answered straight from a thread-safe cache, WITHOUT marshaling to
            // the Unity main thread. While the editor is unfocused (user working in their agent) it
            // may not tick, and a domain reload tears the main loop down — so a main-thread-bound
            // poll would hang. The cache always has the latest state the editor wrote before reload.
            switch (request.command)
            {
                case "compile_status":
                    return CompileStatus();
                case "run_tests_status":
                    return TestStatus();
            }

            McpMainThread.BeginRequest();
            try
            {
                switch (request.command)
                {
                    case "compile_start":
                        return McpMainThread.Invoke(() =>
                        {
                            McpCompileJob.Start();
                            return Success("Compilation started", "{\"state\":\"running\"}");
                        }, DefaultTimeoutMs);
                    case "run_tests_start":
                        return McpMainThread.Invoke(() => StartTestRun(request.mode, request.filter), DefaultTimeoutMs);
                    case "list_tests":
                        if (McpTestRunState.IsRunActive)
                            return Error("Cannot list tests while a Unity test run is in progress");
                        return ListTests();
                    default:
                        return Error($"Unknown command: {request.command}");
                }
            }
            finally
            {
                McpMainThread.EndRequest();
            }
        }

        static string CompileStatus()
        {
            string state = McpCompileJob.State;
            string data = McpCompileJob.BuildStatusData();

            if (state == McpCompileJob.StateDone)
            {
                int count = McpCompileJob.ErrorCount;
                string message = count == 0
                    ? "Compilation finished with no errors"
                    : $"Compilation finished with {count} error(s)";
                return Success(message, data);
            }

            if (state == McpCompileJob.StateRunning)
                return Success("Compilation in progress", data);

            return Success("No compilation has been requested", data);
        }

        static string StartTestRun(string mode, string filter)
        {
            if (McpTestJob.State == McpTestJob.StateRunning || McpTestRunState.IsRunActive)
                return Success("A test run is already in progress", "{\"state\":\"running\"}");

            if (!McpTestJob.RequestStart(mode, filter, out string error))
                return Error(error);

            return Success("Test run started", "{\"state\":\"running\"}");
        }

        static string TestStatus()
        {
            string state = McpTestJob.State;
            string data = McpTestJob.BuildStatusData();

            if (state == McpTestJob.StateDone)
                return Success(McpTestJob.Message, data);

            if (state == McpTestJob.StateRunning)
                return Success("Test run in progress", data);

            return Success("No test run has been requested", data);
        }

        static string ListTests()
        {
            string editModeJson = RetrieveTestModeJson(TestMode.EditMode, out string editError);
            if (editError != null)
                return editError;

            string playModeJson = RetrieveTestModeJson(TestMode.PlayMode, out string playError);
            if (playError != null)
                return playError;

            int editCount = CountTestsInModeJson(editModeJson);
            int playCount = CountTestsInModeJson(playModeJson);
            int total = editCount + playCount;

            string data = "{\"editmode\":" + editModeJson
                + ",\"playmode\":" + playModeJson
                + ",\"player\":{\"count\":0,\"tests\":[],\"note\":"
                + JsonStr("Player tests run in a standalone build and cannot be listed from the editor Test Runner API.")
                + "}}";

            return Success($"Listed {total} test(s) ({editCount} edit, {playCount} play)", data);
        }

        static string RetrieveTestModeJson(TestMode mode, out string error)
        {
            error = null;
            var holder = new TestListHolder();
            string modeLabel = ModeLabel(mode);

            McpMainThread.Post(() =>
            {
                try
                {
                    McpTestRunState.Api.RetrieveTestList(mode, root =>
                    {
                        holder.Payload = BuildModeTestListJson(mode, root);
                        holder.Done.Set();
                    });
                }
                catch (Exception e)
                {
                    holder.Payload = Error($"Failed to list {modeLabel} tests: {e.Message}");
                    holder.Done.Set();
                }
            });

            if (!holder.Done.Wait(ListTestsTimeoutMs))
            {
                error = Error($"Timed out listing {modeLabel} tests after {ListTestsTimeoutMs / 1000}s");
                return null;
            }

            if (holder.Payload != null && holder.Payload.Contains("\"status\":\"error\""))
            {
                error = holder.Payload;
                return null;
            }

            return holder.Payload ?? "{\"count\":0,\"tests\":[]}";
        }

        static string BuildModeTestListJson(TestMode mode, ITestAdaptor root)
        {
            var tests = new List<ITestAdaptor>();
            if (root != null)
                CollectListableTests(root, tests);

            var sb = new StringBuilder();
            sb.Append("{\"mode\":").Append(JsonStr(ModeLabel(mode)));
            sb.Append(",\"count\":").Append(tests.Count);
            sb.Append(",\"tests\":[");
            for (int i = 0; i < tests.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendTestEntry(sb, tests[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static void CollectListableTests(ITestAdaptor node, List<ITestAdaptor> tests)
        {
            if (!node.IsSuite)
            {
                tests.Add(node);
                return;
            }

            if (!node.HasChildren)
                return;

            foreach (var child in node.Children)
                CollectListableTests(child, tests);
        }

        static void AppendTestEntry(StringBuilder sb, ITestAdaptor test)
        {
            sb.Append("{\"fullName\":").Append(JsonStr(test.FullName));
            sb.Append(",\"name\":").Append(JsonStr(test.Name));

            if (test.Categories != null && test.Categories.Length > 0)
            {
                sb.Append(",\"categories\":[");
                for (int i = 0; i < test.Categories.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonStr(test.Categories[i]));
                }
                sb.Append(']');
            }

            sb.Append('}');
        }

        static string ModeLabel(TestMode mode)
        {
            return mode == TestMode.PlayMode ? "playmode" : "editmode";
        }

        static int CountTestsInModeJson(string modeJson)
        {
            const string key = "\"count\":";
            int i = modeJson.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            int start = i + key.Length;
            int end = start;
            while (end < modeJson.Length && "0123456789".IndexOf(modeJson[end]) >= 0) end++;
            return int.TryParse(modeJson.Substring(start, end - start), out int count) ? count : 0;
        }

        static string Success(string message, string dataJson = null)
        {
            if (dataJson == null)
                return $"{{\"status\":\"success\",\"message\":{JsonStr(message)}}}";
            return $"{{\"status\":\"success\",\"message\":{JsonStr(message)},\"data\":{dataJson}}}";
        }

        public static string Error(string message, string dataJson = null)
        {
            if (dataJson == null)
                return $"{{\"status\":\"error\",\"message\":{JsonStr(message)}}}";
            return $"{{\"status\":\"error\",\"message\":{JsonStr(message)},\"data\":{dataJson}}}";
        }

        public static string JsonStr(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }

        class TestListHolder
        {
            public ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Payload;
        }
    }
}
