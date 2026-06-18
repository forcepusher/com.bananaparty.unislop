using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UniSlop.MCP
{
    // Single shared TestRunnerApi for the editor session, plus a session-long listener that
    // tracks whether a run is active. Creating a new TestRunnerApi per call corrupts the runner.
    [InitializeOnLoad]
    static class McpTestRunState
    {
        static TestRunnerApi _api;
        static volatile bool _runActive;

        public static bool IsRunActive => _runActive;

        public static TestRunnerApi Api
        {
            get { return _api; }
        }

        static McpTestRunState()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new RunStateListener());
        }

        sealed class RunStateListener : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) => _runActive = true;

            public void RunFinished(ITestResultAdaptor result) => _runActive = false;

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
