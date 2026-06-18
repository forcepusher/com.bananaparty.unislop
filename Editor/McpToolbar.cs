using UnityEngine;
using UnityEditor;
using UnityEditor.Toolbars;
using System.Collections.Generic;

namespace UniSlop.MCP
{
    [InitializeOnLoad]
    public static class McpToolbar
    {
        const string ToolbarPath = "UniSlop/MCP";

        static Texture2D _statusIcon;
        static string _statusIconKey;

        static McpToolbar()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            McpServer.StatusChanged += OnStatusChanged;
        }

        static bool _refreshPending;

        static void OnStatusChanged()
        {
            // The toolbar can't be refreshed mid-compile/import; defer so the label doesn't get
            // stuck showing a stale status (e.g. "Off") after a reload sets it to Connected.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (!_refreshPending)
                {
                    _refreshPending = true;
                    EditorApplication.update += DeferredRefresh;
                }
                return;
            }

            try { MainToolbar.Refresh(ToolbarPath); } catch { }
        }

        static void DeferredRefresh()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= DeferredRefresh;
            _refreshPending = false;
            try { MainToolbar.Refresh(ToolbarPath); } catch { }
        }

        [MainToolbarElement(ToolbarPath, defaultDockPosition = MainToolbarDockPosition.Right)]
        static IEnumerable<MainToolbarElement> CreateMcpToolbar()
        {
            yield return new MainToolbarLabel(new MainToolbarContent(GetStatusText(), GetStatusIcon(), GetStatusTooltip()));

            bool isDisabled = McpServer.Status == McpServer.ServerStatus.Disabled;
            yield return new MainToolbarButton(
                new MainToolbarContent(isDisabled ? "Start" : "Stop", null,
                    isDisabled ? "Start the MCP server" : "Stop the MCP server"),
                () =>
                {
                    if (McpServer.Status == McpServer.ServerStatus.Disabled) McpServer.StartServer();
                    else McpServer.StopServer();
                });

            yield return new MainToolbarButton(
                new MainToolbarContent("Copy MCP Address", null, "Copy the MCP server URL to the clipboard"),
                CopyMcpAddress);
        }

        static void CopyMcpAddress()
        {
            string url = McpServer.GetServerUrl();
            EditorGUIUtility.systemCopyBuffer = url;
            Debug.Log($"[UniSlop] Copied MCP address: {url}");
        }

        static string GetStatusText()
        {
            return McpServer.Status switch
            {
                McpServer.ServerStatus.Disabled => "Off",
                McpServer.ServerStatus.Error => "Error",
                McpServer.ServerStatus.Starting => McpServer.IsListening ? "Waiting" : "Starting",
                McpServer.ServerStatus.Running => "Connected",
                _ => "Unknown"
            };
        }

        static string GetStatusTooltip()
        {
            string detail = McpServer.Status switch
            {
                McpServer.ServerStatus.Disabled => "MCP server is disabled",
                McpServer.ServerStatus.Error => string.IsNullOrEmpty(McpServer.LastError) ? "MCP server error" : McpServer.LastError,
                McpServer.ServerStatus.Starting => McpServer.IsListening ? "Awaiting agent connection" : "Starting MCP server...",
                McpServer.ServerStatus.Running => "Agent connected",
                _ => "Unknown MCP server status"
            };

            return $"MCP: {detail}";
        }

        static Color GetStatusColor()
        {
            return McpServer.Status switch
            {
                McpServer.ServerStatus.Disabled => Color.gray,
                McpServer.ServerStatus.Error => new Color(1f, 0.25f, 0.25f),
                McpServer.ServerStatus.Starting => new Color(1f, 0.85f, 0.1f),
                McpServer.ServerStatus.Running => new Color(0.2f, 0.9f, 0.3f),
                _ => Color.gray
            };
        }

        static Texture2D GetStatusIcon()
        {
            string key = $"{McpServer.Status}:{McpServer.IsListening}";
            if (_statusIcon != null && _statusIconKey == key)
                return _statusIcon;

            if (_statusIcon != null)
                Object.DestroyImmediate(_statusIcon);

            _statusIcon = CreateStatusIcon(GetStatusColor());
            _statusIconKey = key;
            return _statusIcon;
        }

        static Texture2D CreateStatusIcon(Color color)
        {
            const int size = 12;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            float radius = center;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    pixels[y * size + x] = (dx * dx + dy * dy <= radius * radius) ? color : Color.clear;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
