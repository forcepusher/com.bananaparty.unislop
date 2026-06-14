using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace UniSlop.MCP
{
    [Serializable]
    public class McpRequest
    {
        public string command;
        public bool wait = true;
        public string mode = "editmode";
        public string filter;
    }

    public static class McpUnityBridge
    {
        const int DefaultTimeoutMs = 120_000;
        const int TestTimeoutMs = 300_000;

        public static string Handle(McpRequest request)
        {
            switch (request.command)
            {
                case "agent_connected":
                    return RunOnMainThread(() => Success("Agent connected"));
                case "compile":
                    return request.wait ? CompileAndWait() : RunOnMainThread(CompileFireAndForget);
                case "run_tests":
                    return RunTests(request.mode, request.filter);
                default:
                    return Error($"Unknown command: {request.command}");
            }
        }

        static string RunOnMainThread(Func<string> action, int timeoutMs = DefaultTimeoutMs)
        {
            string result = null;
            Exception error = null;
            var done = new ManualResetEventSlim(false);

            EditorApplication.delayCall += () =>
            {
                try { result = action(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            };

            if (!done.Wait(timeoutMs))
                return Error($"Unity main thread timed out after {timeoutMs / 1000}s");

            if (error != null)
                return Error(error.Message);

            return result;
        }

        static void RunOnMainThread(Action action, int timeoutMs = DefaultTimeoutMs)
        {
            RunOnMainThread(() => { action(); return null; }, timeoutMs);
        }

        static string CompileFireAndForget()
        {
            CompilationPipeline.RequestScriptCompilation();
            return Success("Compilation requested");
        }

        static string CompileAndWait()
        {
            var errors = new List<CompilerMessage>();
            Action<string, CompilerMessage[]> onFinished = (path, messages) =>
            {
                if (messages == null) return;
                foreach (var msg in messages)
                {
                    if (msg.type == CompilerMessageType.Error)
                        errors.Add(msg);
                }
            };

            RunOnMainThread(() =>
            {
                CompilationPipeline.assemblyCompilationFinished += onFinished;
                if (!EditorApplication.isCompiling)
                    CompilationPipeline.RequestScriptCompilation();
            });

            try
            {
                WaitUntil(() => !QueryIsCompiling(), DefaultTimeoutMs);
            }
            catch (TimeoutException e)
            {
                return Error(e.Message);
            }
            finally
            {
                RunOnMainThread(() => CompilationPipeline.assemblyCompilationFinished -= onFinished);
            }

            if (errors.Count == 0)
                return Success("Compilation finished with no errors", "{\"errorCount\":0}");

            return Error(
                $"Compilation finished with {errors.Count} error(s)",
                BuildCompileErrorsJson(errors));
        }

        static string RunTests(string mode, string filter)
        {
            var testMode = mode?.ToLowerInvariant() == "playmode"
                ? TestMode.PlayMode
                : TestMode.EditMode;

            var callback = new TestRunCallback();
            RunOnMainThread(() =>
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                callback.Api = api;
                api.RegisterCallbacks(callback);

                var settings = new ExecutionSettings(new Filter
                {
                    testMode = testMode,
                    testNames = string.IsNullOrWhiteSpace(filter) ? null : new[] { filter }
                });
                api.Execute(settings);
            });

            if (!callback.FinishedEvent.Wait(TestTimeoutMs))
            {
                RunOnMainThread(() =>
                {
                    if (callback.Api != null)
                        callback.Api.UnregisterCallbacks(callback);
                });
                return Error($"Tests timed out after {TestTimeoutMs / 1000}s");
            }

            RunOnMainThread(() =>
            {
                if (callback.Api != null)
                    callback.Api.UnregisterCallbacks(callback);
            });

            return callback.ResultJson ?? Error("Tests finished without a result");
        }

        static bool QueryIsCompiling()
        {
            bool compiling = true;
            var done = new ManualResetEventSlim(false);

            EditorApplication.delayCall += () =>
            {
                compiling = EditorApplication.isCompiling;
                done.Set();
            };

            done.Wait(5000);
            return compiling;
        }

        static void WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return;
                Thread.Sleep(100);
            }
            throw new TimeoutException($"Timed out after {timeoutMs / 1000}s");
        }

        static string BuildCompileErrorsJson(List<CompilerMessage> errors)
        {
            var sb = new StringBuilder();
            sb.Append("{\"errorCount\":").Append(errors.Count).Append(",\"errors\":[");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = errors[i];
                sb.Append("{\"file\":").Append(JsonStr(e.file)).Append(',');
                sb.Append("\"line\":").Append(e.line).Append(',');
                sb.Append("\"message\":").Append(JsonStr(e.message)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
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

        static string JsonStr(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }

        class TestRunCallback : ICallbacks
        {
            public TestRunnerApi Api;
            public ManualResetEventSlim FinishedEvent = new ManualResetEventSlim(false);
            public string ResultJson;

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
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
                string data = sb.ToString();

                ResultJson = success
                    ? Success($"Tests passed ({passed}/{total})", data)
                    : Error($"Tests failed ({failed} failure(s), {passed}/{total} passed)", data);

                FinishedEvent.Set();
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            static void CollectFailures(ITestResultAdaptor result, List<string> failures)
            {
                if (result.TestStatus == TestStatus.Failed)
                {
                    failures.Add("{\"name\":" + JsonStr(result.FullName)
                        + ",\"message\":" + JsonStr(result.Message)
                        + ",\"stackTrace\":" + JsonStr(Truncate(result.StackTrace, 2000)) + "}");
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
