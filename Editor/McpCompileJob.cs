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

        // Grace period for Unity to actually begin compiling after RequestScriptCompilation.
        // If nothing changed since the last successful compile, Unity silently skips the request
        // (no compilationStarted/compilationFinished ever fire) and the job would stay "running"
        // until the MCP server's 300s timeout. The watchdog finalizes it as done instead.
        const double NoCompileGraceSeconds = 5.0;

        static readonly object CacheLock = new object();
        static string _state;
        static string _errors;
        static int _count;
        static double _requestedAt = -1;
        static bool _compilationStarted;

        static McpCompileJob()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            _state = SessionState.GetString(StateKey, StateIdle);
            _errors = SessionState.GetString(ErrorsKey, "");
            _count = SessionState.GetInt(CountKey, 0);

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.update += Tick;
        }

        public static void Start()
        {
            Persist(StateRunning, "", 0);
            _compilationStarted = false;
            _requestedAt = EditorApplication.timeSinceStartup;
            CompilationPipeline.RequestScriptCompilation();
        }

        // Fires for every compilation, including editor-triggered ones Start() never saw. Without
        // this reset, errors from a previous compile would be duplicated into the next report.
        static void OnCompilationStarted(object context)
        {
            _compilationStarted = true;
            Persist(StateRunning, "", 0);
        }

        // A domain reload wipes _requestedAt, which is correct: a reload means a compile actually
        // ran, so the watchdog only ever fires in the skipped-compile case within the same domain.
        static void Tick()
        {
            if (_requestedAt < 0 || _compilationStarted)
                return;
            if (State != StateRunning)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.timeSinceStartup - _requestedAt < NoCompileGraceSeconds)
                return;

            _requestedAt = -1;
            Persist(StateDone, "", 0);
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
