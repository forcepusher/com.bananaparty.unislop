using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;

namespace UniSlop.MCP
{
    // Unity throttles/stops EditorApplication.update when the editor window is not focused.
    // To let MCP-driven work (compile/tests) progress while the user stays in their agent/editor,
    // this pump posts a non-blocking WM_NULL to the Unity window so its message loop keeps ticking.
    //
    // Hard rules to avoid deadlocking domain reload:
    //   - PostMessage only (async). Never SendMessage (it blocks until the window proc replies,
    //     which never happens mid-reload).
    //   - The pump thread touches no managed Unity API.
    //   - The thread is fully stopped before assembly reload and restarted after.
    [InitializeOnLoad]
    static class McpEditorPump
    {
        const uint WmNull = 0;

        static readonly object PumpLock = new object();
        static Thread _pumpThread;
        static volatile bool _pumpRunning;
        static volatile bool _suspended;
        static IntPtr _unityHwnd;

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static McpEditorPump()
        {
            _unityHwnd = ResolveUnityHwnd();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += StopPumpThread;
            EnsurePumpThread();
        }

        static void OnBeforeAssemblyReload()
        {
            _suspended = true;
            StopPumpThread();
        }

        static void OnAfterAssemblyReload()
        {
            _suspended = false;
            _unityHwnd = ResolveUnityHwnd();
            EnsurePumpThread();
        }

        public static void Kick() => EnsurePumpThread();

        static void EnsurePumpThread()
        {
            if (_pumpRunning || _suspended)
                return;

            lock (PumpLock)
            {
                if (_pumpRunning || _suspended)
                    return;

                _pumpRunning = true;
                _pumpThread = new Thread(PumpLoop)
                {
                    IsBackground = true,
                    Name = "UniSlop Editor Pump"
                };
                _pumpThread.Start();
            }
        }

        static void StopPumpThread()
        {
            if (!_pumpRunning)
                return;

            _pumpRunning = false;
            Thread thread;
            lock (PumpLock)
            {
                thread = _pumpThread;
                _pumpThread = null;
            }

            // Safe: PostMessage is non-blocking, so the loop exits within one Sleep tick.
            try { thread?.Join(500); } catch { }
        }

        static void PumpLoop()
        {
            while (_pumpRunning)
            {
                if (!_suspended && McpMainThread.HasPendingWork)
                {
                    // MainWindowHandle can briefly resolve to zero (e.g. right after a reload while
                    // the window isn't foreground). Re-resolve here so a transient zero can never
                    // permanently disable the pump and strand main-thread work forever.
                    if (_unityHwnd == IntPtr.Zero)
                        _unityHwnd = ResolveUnityHwnd();

                    if (_unityHwnd != IntPtr.Zero)
                        PostMessage(_unityHwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
                }

                Thread.Sleep(16);
            }
        }

        static IntPtr ResolveUnityHwnd()
        {
            try
            {
                return Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
