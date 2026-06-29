using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace UniSlop.MCP
{
    // Runs Unity tests as a non-blocking job. run_tests_start kicks it off and returns immediately;
    // the MCP server polls run_tests_status.
    //
    // "all" runs Edit Mode and Play Mode SEQUENTIALLY, one TestRunnerApi.Execute per mode, and
    // aggregates the results. Passing both modes to a single Execute fires RunFinished after Edit
    // Mode alone, so the poller would see "done" with only the Edit Mode counts and miss every Play
    // Mode test. Each mode's result is captured by the persistent listener in McpTestRunState
    // (RecordResult) — a transient per-run callback would be lost across the Play Mode domain reload.
    //
    // The run plan (remaining modes), the running accumulator and the pending request all live in
    // SessionState so a Play Mode reload mid-run resumes cleanly instead of stalling or under-counting.
    [InitializeOnLoad]
    static class McpTestJob
    {
        const string StateKey = "unislop.tests.state";
        const string DataKey = "unislop.tests.data";
        const string MessageKey = "unislop.tests.message";
        const string RemainingKey = "unislop.tests.remaining";
        const string FilterKey = "unislop.tests.filter";
        const string StartTimeKey = "unislop.tests.startTime";
        const string ExecutingKey = "unislop.tests.executing";

        const string AccPassedKey = "unislop.tests.acc.passed";
        const string AccFailedKey = "unislop.tests.acc.failed";
        const string AccSkippedKey = "unislop.tests.acc.skipped";
        const string AccDurationKey = "unislop.tests.acc.durationMs";
        const string AccFailuresKey = "unislop.tests.acc.failures";
        const string AccAbortedKey = "unislop.tests.acc.aborted";

        const string StateIdle = "idle";
        public const string StateRunning = "running";
        public const string StateDone = "done";

        // A run with no progress after this long (editor seconds) is treated as dead so a new run
        // can start. Comfortably longer than the MCP server's per-job budget.
        const double StaleRunSeconds = 360.0;

        static volatile bool _executing;

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
            _executing = SessionState.GetBool(ExecutingKey, false);

            // Recover from a domain reload that happened mid-run (Play Mode) or a dead run.
            if (_state == StateRunning && !_executing && !McpTestRunState.IsRunActive)
            {
                if (RemainingModes().Count == 0)
                    Finalize(); // all modes ran; just publish the aggregate (or reset if empty)
                // else: a mode is still queued — Tick will start it.
            }

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

            if (!TryParseModes(mode, out List<TestMode> modes, out error))
                return false;

            if (modes.Contains(TestMode.EditMode) && EditorApplication.isPlaying)
            {
                error = "Cannot run Edit Mode tests while Play Mode is active";
                return false;
            }

            if (IsRunInProgress())
            {
                error = "A test run is already in progress";
                return false;
            }

            McpTestRunState.ClearActive();
            ResetAccumulator();
            SessionState.SetString(RemainingKey, ModesToString(modes));
            SessionState.SetString(FilterKey, filter ?? "");
            SessionState.SetFloat(StartTimeKey, (float)EditorApplication.timeSinceStartup);
            SessionState.SetBool(ExecutingKey, false);
            _executing = false;
            Persist(StateRunning, "", "");

            McpEditorPump.NotifyWork();
            return true;
        }

        static bool IsRunInProgress()
        {
            if (_executing)
                return true;
            if (State != StateRunning)
                return false;

            float start = SessionState.GetFloat(StartTimeKey, 0f);
            double elapsed = EditorApplication.timeSinceStartup - start;
            return elapsed >= 0 && elapsed < StaleRunSeconds;
        }

        // Merges the persisted result data with the current state for run_tests_status.
        public static string BuildStatusData()
        {
            string state, data;
            lock (CacheLock) { state = _state; data = _data; }

            if (state != StateDone)
                return "{\"state\":\"" + state + "\"}";

            // Done with no payload means the run never produced a result (aborted). Report it as
            // such instead of fabricating a zero-failure (which reads as "all passed").
            if (string.IsNullOrEmpty(data) || data[0] != '{')
                return "{\"state\":\"done\",\"passed\":0,\"failed\":0,\"total\":0,\"aborted\":true}";

            return "{\"state\":\"done\"," + data.Substring(1);
        }

        static void Tick()
        {
            if (_executing)
                return;
            if (State != StateRunning)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || McpTestRunState.IsRunActive)
                return;

            List<TestMode> remaining = RemainingModes();
            if (remaining.Count == 0)
                return;

            StartRun(remaining[0]);
        }

        static void StartRun(TestMode mode)
        {
            string filter = SessionState.GetString(FilterKey, "");
            SessionState.SetBool(ExecutingKey, true);
            _executing = true;
            McpTestRunState.MarkExecuting();
            try
            {
                var settings = new ExecutionSettings(BuildTestFilter(mode, filter));
                McpTestRunState.Api.Execute(settings);
            }
            catch (Exception e)
            {
                ClearExecuting();
                MarkAborted();
                PopMode();
                AdvanceOrFinish("Failed to start " + ModeLabel(mode) + " tests: " + e.Message);
            }
        }

        // Called by McpTestRunState's persistent listener when a mode's run finishes (main thread).
        public static void RecordResult(ITestResultAdaptor result)
        {
            Accumulate(result);
            PopMode();
            ClearExecuting();
            AdvanceOrFinish(null);
        }

        // TestRunnerApi reports RunFailed (not RunFinished) when the internal task pipeline errors.
        public static void RecordFailure(string message)
        {
            MarkAborted();
            PopMode();
            ClearExecuting();
            AdvanceOrFinish(message);
        }

        static void ClearExecuting()
        {
            SessionState.SetBool(ExecutingKey, false);
            _executing = false;
        }

        // Starts the next queued mode, or finalizes the aggregate when none remain.
        static void AdvanceOrFinish(string startError)
        {
            if (RemainingModes().Count > 0)
            {
                McpEditorPump.NotifyWork(); // Tick starts the next mode
                return;
            }

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
            if (result == null)
            {
                MarkAborted();
                return;
            }

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

        // --- run plan (remaining modes) -------------------------------------------------------

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
            // Edit Mode first: it cannot run once Play Mode has been entered, and it is the faster
            // pass, so failures surface sooner.
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

        // --- persistence ----------------------------------------------------------------------

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
