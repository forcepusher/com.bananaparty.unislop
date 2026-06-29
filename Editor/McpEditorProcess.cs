using System;

namespace UniSlop.MCP
{
    internal static class McpEditorProcess
    {
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
