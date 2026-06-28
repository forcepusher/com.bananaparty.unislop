using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniSlop.MCP
{
    // Unity throttles EditorApplication.update when the editor loses focus (and harder still when
    // minimized). MCP compile/tests need main-thread callbacks (CompilationPipeline events, domain
    // reload) to keep firing while the user works in their agent.
    //
    // Three layers while MCP work is active:
    //   1. Temporarily force Interaction Mode to No Throttling (Unity's own background idle gate).
    //   2. Post WM_NULL to every main-thread window + the main OS thread message queue (Windows).
    //   3. Chain QueuePlayerLoopUpdate whenever the editor does tick while unfocused.
    //
    // Hard rules to avoid deadlocking domain reload:
    //   - PostMessage only (async). Never SendMessage.
    //   - The pump thread touches no managed Unity API.
    //   - Pump + throttling override are torn down before assembly reload.
    [InitializeOnLoad]
    static class McpEditorPump
    {
        const uint WmNull = 0;
        const int InteractionModeDefault = 0;
        const int InteractionModeNoThrottling = 1;
        const string IdleTimePrefKey = "ApplicationIdleTime";
        const string InteractionModePrefKey = "InteractionMode";

        static readonly object PumpLock = new object();
        static readonly List<IntPtr> WindowBuffer = new List<IntPtr>();
        static readonly EnumWindowsProc EnumWindowsCallback = CollectWindow;
        static Thread _pumpThread;
        static volatile bool _pumpRunning;
        static volatile bool _suspended;
        static bool _boostActive;
        static bool _savedHadIdleKey;
        static bool _savedHadModeKey;
        static int _savedIdleTimeMs;
        static int _savedInteractionMode;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        static McpEditorPump()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            TouchDependentTypes();

            // Pre-apply no-throttling settings so the editor ticks at full rate even while unfocused.
            // Without this, SyncInteractionBoost (which runs on EditorApplication.update) creates a
            // chicken-and-egg: Unity is throttled when unfocused → update fires too slowly or not at all
            // → boost never applied → stays throttled. Clicking the editor fixes it because focus forces
            // an update tick, which then applies the settings. Applying them here during initialization
            // breaks that deadlock so background compilation works on first request.
            EnterInteractionBoost();

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.update += OnEditorUpdate;
            EnsurePumpThread();
        }

        static void TouchDependentTypes()
        {
            // Force InitializeOnLoad types to init on the editor main thread before the pump thread
            // reads their static state (otherwise their .cctor can run on the pump thread).
            _ = McpMainThread.HasPendingWork;
            _ = McpCompileJob.IsActive;
            _ = McpTestJob.IsActive;
        }

        static void OnBeforeAssemblyReload()
        {
            ExitInteractionBoost();
            _suspended = true;
            StopPumpThread();
        }

        static void OnAfterAssemblyReload()
        {
            _suspended = false;
            TouchDependentTypes();
            EnsurePumpThread();
            if (NeedsEditorTick())
                NotifyWork();
        }

        static void OnEditorQuitting()
        {
            ExitInteractionBoost();
            StopPumpThread();
        }

        static void OnFocusChanged(bool focused)
        {
            if (focused || !NeedsEditorTick()) return;
            NotifyWork();
        }

        static void OnEditorUpdate()
        {
            SyncInteractionBoost();
            SustainPlayerLoop();
        }

        // Called on the main thread when MCP work needs the editor to keep ticking.
        public static void NotifyWork()
        {
            Kick();
            if (_suspended) return;
            SyncInteractionBoost();
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
        }

        public static void Kick() => EnsurePumpThread();

        static void SyncInteractionBoost()
        {
            if (_suspended)
            {
                ExitInteractionBoost();
                return;
            }

            if (NeedsEditorTick())
                EnterInteractionBoost();
            else
                ExitInteractionBoost();
        }

        static void EnterInteractionBoost()
        {
            if (_boostActive) return;

            _savedHadIdleKey = EditorPrefs.HasKey(IdleTimePrefKey);
            _savedIdleTimeMs = EditorPrefs.GetInt(IdleTimePrefKey, 4);
            _savedHadModeKey = EditorPrefs.HasKey(InteractionModePrefKey);
            _savedInteractionMode = EditorPrefs.GetInt(InteractionModePrefKey, InteractionModeDefault);

            EditorPrefs.SetInt(IdleTimePrefKey, 0);
            EditorPrefs.SetInt(InteractionModePrefKey, InteractionModeNoThrottling);
            ApplyInteractionModeSettings();
            _boostActive = true;
        }

        static void ExitInteractionBoost()
        {
            if (!_boostActive) return;

            if (_savedHadIdleKey)
                EditorPrefs.SetInt(IdleTimePrefKey, _savedIdleTimeMs);
            else
                EditorPrefs.DeleteKey(IdleTimePrefKey);

            if (_savedHadModeKey)
                EditorPrefs.SetInt(InteractionModePrefKey, _savedInteractionMode);
            else
                EditorPrefs.DeleteKey(InteractionModePrefKey);

            ApplyInteractionModeSettings();
            _boostActive = false;
        }

        static void ApplyInteractionModeSettings()
        {
            MethodInfo method = typeof(EditorApplication).GetMethod(
                "UpdateInteractionModeSettings",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(null, null);
        }

        static void SustainPlayerLoop()
        {
            if (_suspended || !ShouldSustainPlayerLoop()) return;
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
        }

        static bool ShouldSustainPlayerLoop()
        {
            if (!NeedsEditorTick()) return false;
            return !EditorApplication.isFocused;
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
            if (!IsWindow(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)Process.GetCurrentProcess().Id)
                return true;

            WindowBuffer.Add(hWnd);
            return true;
        }

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lProcessId);

        static void PulseUnityWindows()
        {
            WindowBuffer.Clear();

            uint osThreadId = McpMainThread.UnityOsThreadId;
            if (osThreadId != 0)
                EnumThreadWindows(osThreadId, EnumWindowsCallback, IntPtr.Zero);

            if (WindowBuffer.Count == 0)
                EnumWindows(EnumWindowsCallback, IntPtr.Zero);

            if (WindowBuffer.Count == 0)
            {
                IntPtr main = ResolveMainWindowHandle();
                if (main != IntPtr.Zero)
                    WindowBuffer.Add(main);
            }

            for (int i = 0; i < WindowBuffer.Count; i++)
                PostMessage(WindowBuffer[i], WmNull, IntPtr.Zero, IntPtr.Zero);

            if (osThreadId != 0)
                PostThreadMessage(osThreadId, WmNull, IntPtr.Zero, IntPtr.Zero);
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

            return IntPtr.Zero;
        }
    }
}
