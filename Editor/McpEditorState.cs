using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UniSlop.MCP
{
    // Single shared TestRunnerApi for the editor session, plus a session-long listener that
    // tracks whether a run is active and funnels results to McpTestJob. The listener is registered
    // fresh on every domain load (InitializeOnLoad), which is the only way a Play Mode run — whose
    // domain reloads mid-run — still delivers RunFinished. A transient per-run callback would be
    // dropped by that reload and the result silently lost.
    [InitializeOnLoad]
    static class McpTestRunState
    {
        // Active flag is mirrored two ways: a volatile bool so the background MCP poller can read it
        // off the main thread (SessionState is main-thread only), and SessionState so an in-progress
        // run started before a Play Mode reload is still recognized as active afterwards.
        const string ActiveKey = "unislop.tests.active";

        static TestRunnerApi _api;
        static volatile bool _runActive;

        public static bool IsRunActive => _runActive;

        public static TestRunnerApi Api
        {
            get { return _api; }
        }

        // Main thread only.
        public static void ClearActive() => SetActive(false);

        // Called before TestRunnerApi.Execute so a Play Mode domain reload still sees an active run
        // and McpTestJob.Tick does not start a second Execute on top of TestJobDataHolder's resume.
        public static void MarkExecuting() => SetActive(true);

        static McpTestRunState()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _runActive = SessionState.GetBool(ActiveKey, false);

            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new RunStateListener());
        }

        // Main thread only (Unity test callbacks fire on the main thread).
        static void SetActive(bool active)
        {
            _runActive = active;
            SessionState.SetBool(ActiveKey, active);
        }

        sealed class RunStateListener : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) => SetActive(true);

            public void RunFinished(ITestResultAdaptor result)
            {
                SetActive(false);
                McpTestJob.RecordResult(result);
            }

            public void OnError(string message)
            {
                SetActive(false);
                McpTestJob.RecordFailure(message);
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
