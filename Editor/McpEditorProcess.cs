using UnityEditor;

namespace UniSlop.MCP
{
    // Asset Import Workers are separate Unity.exe processes with their own AppDomain. UniSlop owns
    // one detached mono process, one fixed MCP port, and one unity-api-port.txt file — all of that
    // must stay in the main Editor process only.
    internal static class McpEditorProcess
    {
        public static bool IsMainEditor => !AssetDatabase.IsAssetImportWorkerProcess();
    }
}
