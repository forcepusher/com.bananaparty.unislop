using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace UniSlop.MCP
{
    // Non-blocking test job. "all" runs Edit Mode then Play Mode sequentially; state survives reload.
    [InitializeOnLoad]
    static class McpTestJob
    {
        const string StateKey = "unislop.tests.state";
        const string DataKey = "unislop.tests.data";
        const string MessageKey = "unislop.tests.message";
        const string RemainingKey = "unislop.tests.remaining";
        const string FilterKey = "unislop.tests.filter";
        const string AccPassedKey = "unislop.tests.acc.passed";
        const string AccFailedKey = "unislop.tests.acc.failed";
        const string AccSkippedKey = "unislop.tests.acc.skipped";
        const string AccDurationKey = "unislop.tests.acc.durationMs";
        const string AccFailuresKey = "unislop.tests.acc.failures";
        const string AccAbortedKey = "unislop.tests.acc.aborted";

        const string StateIdle = "idle";
        public const string StateRunning = "running";
        public const string StateDone = "done";

        static readonly object CacheLock = new object();
        static string _state;
        static string _data;
        static string _message;

        static McpTestJob()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _state = SessionState.GetString(StateKey, StateIdle);
            _data = SessionState.GetString(DataKey, "");
            _message = SessionState.GetString(MessageKey, "");

            if (_state == StateRunning && !McpTestRunState.IsRunActive && RemainingModes().Count == 0)
                Finalize();

            EditorApplication.update += Tick;
        }

        public static string State { get { lock (CacheLock) return _state; } }
        public static string Message { get { lock (CacheLock) return _message; } }

        public static bool RequestStart(string mode, string filter, out string error)
        {
            error = null;

            if (!TryParseModes(mode, out List<TestMode> modes, out error))
                return false;

            if (modes.Contains(TestMode.EditMode) && EditorApplication.isPlaying)
            {
                error = "Cannot run Edit Mode tests while Play Mode is active";
                return false;
            }

            // With compile errors the Test Runner never starts a run, so the job would poll forever.
            if (EditorUtility.scriptCompilationFailed)
            {
                error = "Cannot run tests: the project has compile errors (run unity_compile for details)";
                return false;
            }

            if (State == StateRunning || McpTestRunState.IsRunActive)
            {
                error = "A test run is already in progress";
                return false;
            }

            McpTestRunState.ClearActive();
            ResetAccumulator();
            SessionState.SetString(RemainingKey, ModesToString(modes));
            SessionState.SetString(FilterKey, filter ?? "");
            Persist(StateRunning, "", "");

            return true;
        }

        public static string BuildStatusData()
        {
            string state, data;
            lock (CacheLock) { state = _state; data = _data; }

            if (state != StateDone)
                return "{\"state\":\"" + state + "\"}";

            if (string.IsNullOrEmpty(data) || data[0] != '{')
                return "{\"state\":\"done\",\"passed\":0,\"failed\":0,\"total\":0,\"aborted\":true}";

            return "{\"state\":\"done\"," + data.Substring(1);
        }

        static void Tick()
        {
            if (State != StateRunning)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || McpTestRunState.IsRunActive)
                return;

            List<TestMode> remaining = RemainingModes();
            if (remaining.Count == 0)
                return;

            if (EditorUtility.scriptCompilationFailed)
            {
                MarkAborted();
                Finalize("Test run aborted: the project has compile errors (run unity_compile for details)");
                return;
            }

            StartRun(remaining[0]);
        }

        static void StartRun(TestMode mode)
        {
            McpMainThread.BringEditorToForeground();
            string filter = SessionState.GetString(FilterKey, "");
            McpTestRunState.MarkExecuting();
            try
            {
                var settings = new ExecutionSettings(BuildTestFilter(mode, filter));
                McpTestRunState.Api.Execute(settings);
            }
            catch (Exception e)
            {
                McpTestRunState.ClearActive();
                MarkAborted();
                PopMode();
                AdvanceOrFinish("Failed to start " + ModeLabel(mode) + " tests: " + e.Message);
            }
        }

        public static void RecordResult(ITestResultAdaptor result, string error)
        {
            if (result == null)
                MarkAborted();
            else
                Accumulate(result);
            PopMode();
            AdvanceOrFinish(error);
        }

        static void AdvanceOrFinish(string startError)
        {
            if (RemainingModes().Count > 0)
                return;

            Finalize(startError);
        }

        static void Finalize(string startError = null)
        {
            int passed = SessionState.GetInt(AccPassedKey, 0);
            int failed = SessionState.GetInt(AccFailedKey, 0);
            int skipped = SessionState.GetInt(AccSkippedKey, 0);
            int durationMs = SessionState.GetInt(AccDurationKey, 0);
            int total = passed + failed + skipped;
            bool aborted = SessionState.GetBool(AccAbortedKey, false);
            string failures = SessionState.GetString(AccFailuresKey, "");

            var sb = new StringBuilder();
            sb.Append("{\"passed\":").Append(passed);
            sb.Append(",\"failed\":").Append(failed);
            sb.Append(",\"skipped\":").Append(skipped);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"durationMs\":").Append(durationMs);
            if (!string.IsNullOrEmpty(failures))
                sb.Append(",\"failures\":[").Append(failures).Append(']');
            if (aborted)
                sb.Append(",\"aborted\":true");
            sb.Append('}');

            string message;
            if (!string.IsNullOrEmpty(startError))
                message = startError;
            else if (aborted)
                message = $"Test run aborted before completing ({passed}/{total} passed so far)";
            else if (total == 0)
                message = "No tests matched the requested mode/filter";
            else if (failed > 0)
                message = $"Tests failed ({failed} failure(s), {passed}/{total} passed)";
            else
                message = $"Tests passed ({passed}/{total})";

            ClearRun();
            Persist(StateDone, sb.ToString(), message);
        }

        static void Accumulate(ITestResultAdaptor result)
        {
            SessionState.SetInt(AccPassedKey, SessionState.GetInt(AccPassedKey, 0) + result.PassCount);
            SessionState.SetInt(AccFailedKey, SessionState.GetInt(AccFailedKey, 0) + result.FailCount);
            SessionState.SetInt(AccSkippedKey, SessionState.GetInt(AccSkippedKey, 0) + result.SkipCount);
            SessionState.SetInt(AccDurationKey, SessionState.GetInt(AccDurationKey, 0) + (int)(result.Duration * 1000));

            var failures = new List<string>();
            CollectFailures(result, failures);
            if (failures.Count > 0)
            {
                string existing = SessionState.GetString(AccFailuresKey, "");
                string added = string.Join(",", failures);
                SessionState.SetString(AccFailuresKey, string.IsNullOrEmpty(existing) ? added : existing + "," + added);
            }
        }

        static void MarkAborted() => SessionState.SetBool(AccAbortedKey, true);

        static void ResetAccumulator()
        {
            SessionState.SetInt(AccPassedKey, 0);
            SessionState.SetInt(AccFailedKey, 0);
            SessionState.SetInt(AccSkippedKey, 0);
            SessionState.SetInt(AccDurationKey, 0);
            SessionState.SetBool(AccAbortedKey, false);
            SessionState.EraseString(AccFailuresKey);
        }

        static void ClearRun()
        {
            SessionState.EraseString(RemainingKey);
            SessionState.EraseString(FilterKey);
        }

        static List<TestMode> RemainingModes()
        {
            return ParseModeList(SessionState.GetString(RemainingKey, ""));
        }

        static void PopMode()
        {
            List<TestMode> modes = RemainingModes();
            if (modes.Count > 0)
                modes.RemoveAt(0);
            SessionState.SetString(RemainingKey, ModesToString(modes));
        }

        static bool TryParseModes(string mode, out List<TestMode> modes, out string error)
        {
            error = null;
            modes = new List<TestMode>();

            string m = string.IsNullOrEmpty(mode) ? "all" : mode.ToLowerInvariant();
            if (m == "all" || m == "editmode") modes.Add(TestMode.EditMode);
            if (m == "all" || m == "playmode") modes.Add(TestMode.PlayMode);

            if (modes.Count == 0)
            {
                error = $"Unknown test mode '{mode}' (expected 'all', 'editmode' or 'playmode')";
                return false;
            }
            return true;
        }

        static string ModesToString(List<TestMode> modes)
        {
            var sb = new StringBuilder();
            foreach (TestMode mode in modes)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(mode == TestMode.EditMode ? "editmode" : "playmode");
            }
            return sb.ToString();
        }

        static List<TestMode> ParseModeList(string value)
        {
            var modes = new List<TestMode>();
            if (string.IsNullOrEmpty(value))
                return modes;

            foreach (string part in value.Split(','))
            {
                if (part == "editmode") modes.Add(TestMode.EditMode);
                else if (part == "playmode") modes.Add(TestMode.PlayMode);
            }
            return modes;
        }

        static string ModeLabel(TestMode mode) => mode == TestMode.EditMode ? "Edit Mode" : "Play Mode";

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
                // Filter.assemblyNames expects the assembly name WITHOUT the .dll extension.
                settings.assemblyNames = new[]
                {
                    trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        ? trimmed.Substring(0, trimmed.Length - 4)
                        : trimmed
                };
                return settings;
            }

            settings.testNames = new[] { trimmed };
            return settings;
        }

        static void CollectFailures(ITestResultAdaptor result, List<string> failures)
        {
            if (result.TestStatus == TestStatus.Failed && !result.HasChildren)
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
