using System;

namespace UniSlop.MCP
{
    // Asset Import Workers are separate Unity.exe processes with their own AppDomain. UniSlop owns
    // one detached mono process, one fixed MCP port, and one unity-api-port.txt file — all of that
    // must stay in the main Editor process only.
    internal static class McpEditorProcess
    {
        // AssetDatabase.IsAssetImportWorkerProcess() is main-thread-only. Static ctors and the
        // editor pump can run type initializers from background threads, so detect workers from the
        // command line instead (safe on any thread, evaluated once at type load).
        static readonly bool IsAssetImportWorker = DetectAssetImportWorker();

        public static bool IsMainEditor => !IsAssetImportWorker;

        static bool DetectAssetImportWorker()
        {
            string cmd = Environment.CommandLine;
            if (cmd.IndexOf("AssetImportWorker", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (cmd.IndexOf("-name AssetImport", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }
    }
}
