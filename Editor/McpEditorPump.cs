using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniSlop.MCP
{
    // Unity throttles/stops EditorApplication.update when the editor window is not focused.
    // To let MCP-driven work (compile/tests) progress while the user stays in their agent/editor,
    // this pump posts a non-blocking WM_NULL to every visible Unity window so the message loop
    // keeps ticking. While backgrounded it also chains QueuePlayerLoopUpdate on each editor tick.
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
        static readonly List<IntPtr> WindowBuffer = new List<IntPtr>();
        static readonly EnumWindowsProc EnumWindowsCallback = CollectWindow;
        static Thread _pumpThread;
        static volatile bool _pumpRunning;
        static volatile bool _suspended;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lProcessId);

        static McpEditorPump()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += StopPumpThread;
            EditorApplication.update += SustainPlayerLoop;
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
            EnsurePumpThread();
            if (NeedsEditorTick())
                NotifyWork();
        }

        // Called on the main thread when MCP work needs the editor to keep ticking.
        public static void NotifyWork()
        {
            Kick();
            if (_suspended) return;
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
        }

        public static void Kick() => EnsurePumpThread();

        static void SustainPlayerLoop()
        {
            if (_suspended || !ShouldSustainPlayerLoop()) return;
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
        }

        static bool ShouldSustainPlayerLoop()
        {
            if (!NeedsEditorTick()) return false;
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return !IsUnityForeground();
            return true;
        }

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

            try { thread?.Join(500); } catch { }
        }

        static void PumpLoop()
        {
            while (_pumpRunning)
            {
                bool needs = !_suspended && NeedsEditorTick();
                if (needs && Application.platform == RuntimePlatform.WindowsEditor)
                    PulseUnityWindows();

                Thread.Sleep(needs ? 8 : 50);
            }
        }

        static bool NeedsEditorTick()
        {
            return McpMainThread.HasPendingWork
                || McpCompileJob.IsActive
                || McpTestJob.IsActive;
        }

        static bool CollectWindow(IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)Process.GetCurrentProcess().Id)
                return true;
            if (!IsWindowVisible(hWnd))
                return true;

            WindowBuffer.Add(hWnd);
            return true;
        }

        static void PulseUnityWindows()
        {
            WindowBuffer.Clear();
            EnumWindows(EnumWindowsCallback, IntPtr.Zero);

            if (WindowBuffer.Count == 0)
            {
                IntPtr main = ResolveMainWindowHandle();
                if (main != IntPtr.Zero)
                    WindowBuffer.Add(main);
            }

            for (int i = 0; i < WindowBuffer.Count; i++)
                PostMessage(WindowBuffer[i], WmNull, IntPtr.Zero, IntPtr.Zero);
        }

        static IntPtr ResolveMainWindowHandle()
        {
            try
            {
                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                if (main != IntPtr.Zero && IsWindow(main))
                    return main;
            }
            catch { }

            WindowBuffer.Clear();
            EnumWindows(EnumWindowsCallback, IntPtr.Zero);
            return WindowBuffer.Count > 0 ? WindowBuffer[0] : IntPtr.Zero;
        }

        static bool IsUnityForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(foreground, out uint pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }
    }
}
