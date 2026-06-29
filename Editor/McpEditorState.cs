using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UniSlop.MCP
{
    [InitializeOnLoad]
    static class McpTestRunState
    {
        const string ActiveKey = "unislop.tests.active";

        static TestRunnerApi _api;
        static volatile bool _runActive;

        public static bool IsRunActive => _runActive;
        public static TestRunnerApi Api => _api;

        public static void ClearActive() => SetActive(false);
        public static void MarkExecuting() => SetActive(true);

        static McpTestRunState()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _runActive = SessionState.GetBool(ActiveKey, false);
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new RunStateListener());
        }

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
                McpTestJob.RecordResult(result, null);
            }

            public void OnError(string message)
            {
                SetActive(false);
                McpTestJob.RecordResult(null, message);
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
