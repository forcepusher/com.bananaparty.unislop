using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace UniSlop.MCP
{
    [InitializeOnLoad]
    static class McpCompileJob
    {
        const string StateKey = "unislop.compile.state";
        const string ErrorsKey = "unislop.compile.errors";
        const string CountKey = "unislop.compile.errorCount";

        const string StateIdle = "idle";
        public const string StateRunning = "running";
        public const string StateDone = "done";

        static readonly object CacheLock = new object();
        static string _state;
        static string _errors;
        static int _count;

        static McpCompileJob()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _state = SessionState.GetString(StateKey, StateIdle);
            _errors = SessionState.GetString(ErrorsKey, "");
            _count = SessionState.GetInt(CountKey, 0);

            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static void Start()
        {
            Persist(StateRunning, "", 0);
            CompilationPipeline.RequestScriptCompilation();
        }

        public static string State { get { lock (CacheLock) return _state; } }
        public static int ErrorCount { get { lock (CacheLock) return _count; } }

        public static string BuildStatusData()
        {
            lock (CacheLock)
            {
                var sb = new StringBuilder();
                sb.Append("{\"state\":\"").Append(_state).Append('"');
                sb.Append(",\"errorCount\":").Append(_count);
                sb.Append(",\"errors\":[").Append(_errors).Append("]}");
                return sb.ToString();
            }
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;

            string existing;
            int count;
            lock (CacheLock) { existing = _errors; count = _count; }

            var sb = new StringBuilder(existing);
            foreach (var msg in messages)
            {
                if (msg.type != CompilerMessageType.Error) continue;

                if (sb.Length > 0) sb.Append(',');
                sb.Append("{\"file\":").Append(McpUnityBridge.JsonStr(msg.file));
                sb.Append(",\"line\":").Append(msg.line);
                sb.Append(",\"message\":").Append(McpUnityBridge.JsonStr(msg.message)).Append('}');
                count++;
            }

            Persist(StateRunning, sb.ToString(), count);
        }

        static void OnCompilationFinished(object context)
        {
            string errors;
            int count;
            lock (CacheLock) { errors = _errors; count = _count; }
            Persist(StateDone, errors, count);
        }

        static void Persist(string state, string errors, int count)
        {
            SessionState.SetString(StateKey, state);
            SessionState.SetString(ErrorsKey, errors);
            SessionState.SetInt(CountKey, count);
            lock (CacheLock)
            {
                _state = state;
                _errors = errors;
                _count = count;
            }
        }
    }
}
