using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace UniSlop.MCP
{
    // Tracks a compile request across domain reloads. Compiler messages arrive (and the state
    // flips to "done") before the AppDomain unloads, so everything is mirrored into SessionState
    // — which survives reload — and read back once the editor comes back up.
    //
    // The state is ALSO mirrored into a thread-safe in-memory cache. The MCP server polls
    // compile_status from a background thread, and while the editor is unfocused the main thread may
    // never tick — so the poll must be answerable without marshaling to it. The cache is the source
    // of truth for those reads; SessionState only seeds it after a reload.
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

            // Seed the cache from SessionState (main thread) so status is correct right after a reload.
            _state = SessionState.GetString(StateKey, StateIdle);
            _errors = SessionState.GetString(ErrorsKey, "");
            _count = SessionState.GetInt(CountKey, 0);

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        // Call on the main thread.
        public static void Start()
        {
            Persist(StateRunning, "", 0);
            CompilationPipeline.RequestScriptCompilation();
        }

        // Thread-safe reads for the MCP poller (must not touch Unity API off the main thread).
        public static string State { get { lock (CacheLock) return _state; } }
        public static int ErrorCount { get { lock (CacheLock) return _count; } }

        // JSON object with state, errorCount and the accumulated error entries.
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

        static void OnCompilationStarted(object context) => Persist(StateRunning, "", 0);

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

            // Keep state running; only errors/count change here.
            Persist(StateRunning, sb.ToString(), count);
        }

        static void OnCompilationFinished(object context)
        {
            string errors;
            int count;
            lock (CacheLock) { errors = _errors; count = _count; }
            Persist(StateDone, errors, count);
        }

        // Writes both the durable SessionState (survives reload) and the thread-safe cache
        // (read by the background MCP poller). Always called on the main thread.
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
