using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace UniSlop.MCP
{
    // Runs Unity tests as a non-blocking job. run_tests_start kicks it off and returns
    // immediately; the MCP server polls run_tests_status. Results are mirrored into SessionState
    // so they survive any domain reload triggered during the run. Reload is left unrestricted.
    [InitializeOnLoad]
    static class McpTestJob
    {
        const string StateKey = "unislop.tests.state";
        const string DataKey = "unislop.tests.data";
        const string MessageKey = "unislop.tests.message";

        const string StateIdle = "idle";
        public const string StateRunning = "running";
        public const string StateDone = "done";

        static volatile bool _pending;
        static Filter[] _pendingFilters;

        static readonly object CacheLock = new object();
        static string _state;
        static string _data;
        static string _message;

        static McpTestJob()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            // Seed the thread-safe cache from SessionState so status is correct after a reload.
            _state = SessionState.GetString(StateKey, StateIdle);
            _data = SessionState.GetString(DataKey, "");
            _message = SessionState.GetString(MessageKey, "");

            EditorApplication.update += Tick;
        }

        // Thread-safe reads for the MCP poller (must not touch Unity API off the main thread).
        public static string State { get { lock (CacheLock) return _state; } }
        public static bool IsActive => State == StateRunning;
        public static string Message { get { lock (CacheLock) return _message; } }

        // Call on the main thread.
        // mode: "all" (default) runs Edit Mode + Play Mode tests, "editmode" / "playmode" run one.
        public static bool RequestStart(string mode, string filter, out string error)
        {
            error = null;
            string m = string.IsNullOrEmpty(mode) ? "all" : mode.ToLowerInvariant();
            bool runEdit = m == "all" || m == "editmode";
            bool runPlay = m == "all" || m == "playmode";

            if (!runEdit && !runPlay)
            {
                error = $"Unknown test mode '{mode}' (expected 'all', 'editmode' or 'playmode')";
                return false;
            }

            if (runEdit && EditorApplication.isPlaying)
            {
                error = "Cannot run Edit Mode tests while Play Mode is active";
                return false;
            }

            var filters = new List<Filter>();
            if (runEdit) filters.Add(BuildTestFilter(TestMode.EditMode, filter));
            if (runPlay) filters.Add(BuildTestFilter(TestMode.PlayMode, filter));

            Persist(StateRunning, "", "");

            _pendingFilters = filters.ToArray();
            _pending = true;

            return true;
        }

        // Merges the persisted result data with the current state for run_tests_status.
        public static string BuildStatusData()
        {
            string state, data;
            lock (CacheLock) { state = _state; data = _data; }

            if (state != StateDone)
                return "{\"state\":\"" + state + "\"}";

            if (string.IsNullOrEmpty(data) || data[0] != '{')
                return "{\"state\":\"done\",\"failed\":0}";

            return "{\"state\":\"done\"," + data.Substring(1);
        }

        static void Tick()
        {
            if (!_pending)
                return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating || McpTestRunState.IsRunActive)
                return;

            _pending = false;
            StartRun(_pendingFilters);
        }

        static void StartRun(Filter[] filters)
        {
            try
            {
                var api = McpTestRunState.Api;
                var callback = new RunCallback { Api = api };
                api.RegisterCallbacks(callback);
                // Multiple filters run in a single pass: "all" passes both an Edit Mode and a
                // Play Mode filter, and the RunFinished result aggregates both.
                api.Execute(new ExecutionSettings(filters));
            }
            catch (Exception e)
            {
                Finish("Failed to start tests: " + e.Message, null);
            }
        }

        static void Finish(string message, string dataJson)
        {
            Persist(StateDone, dataJson ?? "", message);
        }

        // Writes both durable SessionState (survives reload) and the thread-safe cache (read by
        // the background poller). Always called on the main thread.
        static void Persist(string state, string data, string message)
        {
            SessionState.SetString(StateKey, state);
            SessionState.SetString(DataKey, data);
            SessionState.SetString(MessageKey, message);
            lock (CacheLock)
            {
                _state = state;
                _data = data;
                _message = message;
            }
        }

        static Filter BuildTestFilter(TestMode testMode, string filter)
        {
            var settings = new Filter { testMode = testMode };
            if (string.IsNullOrWhiteSpace(filter))
                return settings;

            string trimmed = filter.Trim();
            if (trimmed.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase))
            {
                settings.assemblyNames = new[]
                {
                    trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".dll"
                };
                return settings;
            }

            settings.testNames = new[] { trimmed };
            return settings;
        }

        sealed class RunCallback : ICallbacks
        {
            public TestRunnerApi Api;

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    if (result == null)
                    {
                        Finish("Tests aborted without a result (likely a domain reload during the run)", null);
                        return;
                    }

                    int passed = result.PassCount;
                    int failed = result.FailCount;
                    int skipped = result.SkipCount;
                    int total = passed + failed + skipped;
                    bool success = result.TestStatus == TestStatus.Passed;

                    var sb = new StringBuilder();
                    sb.Append("{\"passed\":").Append(passed);
                    sb.Append(",\"failed\":").Append(failed);
                    sb.Append(",\"skipped\":").Append(skipped);
                    sb.Append(",\"total\":").Append(total);
                    sb.Append(",\"durationMs\":").Append((long)(result.Duration * 1000));

                    var failures = new List<string>();
                    CollectFailures(result, failures);
                    if (failures.Count > 0)
                    {
                        sb.Append(",\"failures\":[");
                        sb.Append(string.Join(",", failures));
                        sb.Append(']');
                    }

                    sb.Append('}');

                    string message = success
                        ? $"Tests passed ({passed}/{total})"
                        : $"Tests failed ({failed} failure(s), {passed}/{total} passed)";

                    Finish(message, sb.ToString());
                }
                finally
                {
                    try { Api?.UnregisterCallbacks(this); }
                    catch { }
                }
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            static void CollectFailures(ITestResultAdaptor result, List<string> failures)
            {
                if (result.TestStatus == TestStatus.Failed)
                {
                    failures.Add("{\"name\":" + McpUnityBridge.JsonStr(result.FullName)
                        + ",\"message\":" + McpUnityBridge.JsonStr(result.Message)
                        + ",\"stackTrace\":" + McpUnityBridge.JsonStr(Truncate(result.StackTrace, 2000)) + "}");
                }

                if (!result.HasChildren) return;
                foreach (var child in result.Children)
                    CollectFailures(child, failures);
            }

            static string Truncate(string value, int max)
            {
                if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
                return value.Substring(0, max) + "...";
            }
        }
    }
}
