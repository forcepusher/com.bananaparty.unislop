using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;

namespace UniSlop.MCP
{
    [InitializeOnLoad]
    static class McpMainThread
    {
        static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        static readonly Queue<WorkItem> Queue = new Queue<WorkItem>();
        static readonly object QueueLock = new object();
        static volatile bool _isReloading;

        sealed class WorkItem
        {
            public Action Action;
            public ManualResetEventSlim Done;
            public string Result;
            public Exception Error;
        }

        static McpMainThread()
        {
            if (!McpEditorProcess.IsMainEditor) return;

            EditorApplication.update += Drain;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static string Invoke(Func<string> action, int timeoutMs = 120_000)
        {
            if (_isReloading)
                return McpUnityBridge.Error("Unity is reloading scripts");

            BringEditorToForeground();

            if (IsMainThread)
            {
                try { return action(); }
                catch (Exception e) { return McpUnityBridge.Error(e.Message); }
            }

            var item = new WorkItem { Done = new ManualResetEventSlim(false) };
            item.Action = () =>
            {
                try { item.Result = action(); }
                catch (Exception e) { item.Error = e; }
            };
            Enqueue(item);

            if (!item.Done.Wait(timeoutMs))
                return McpUnityBridge.Error($"Unity main thread timed out after {timeoutMs / 1000}s");

            if (item.Error != null)
                return McpUnityBridge.Error(item.Error.Message);
            return item.Result;
        }

        public static void Post(Action action)
        {
            if (_isReloading)
                return;

            BringEditorToForeground();

            if (IsMainThread)
            {
                try { action(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
                return;
            }

            Enqueue(new WorkItem
            {
                Action = () =>
                {
                    try { action(); }
                    catch (Exception e) { UnityEngine.Debug.LogException(e); }
                }
            });
        }

        static void Enqueue(WorkItem item)
        {
            lock (QueueLock)
                Queue.Enqueue(item);
        }

        public static void BringEditorToForeground()
        {
#if UNITY_EDITOR_WIN
            IntPtr hWnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hWnd == IntPtr.Zero) return;
            AllowSetForegroundWindow(Process.GetCurrentProcess().Id);
            ShowWindow(hWnd, IsIconic(hWnd) ? 9 : 5);
            SetForegroundWindow(hWnd);
#endif
        }

        static void OnBeforeAssemblyReload()
        {
            _isReloading = true;
            lock (QueueLock)
            {
                while (Queue.Count > 0)
                {
                    WorkItem item = Queue.Dequeue();
                    item.Error = new Exception("Unity is reloading scripts");
                    item.Done?.Set();
                }
            }
        }

        static void OnAfterAssemblyReload() => _isReloading = false;

        static void Drain()
        {
            if (_isReloading)
                return;

            while (true)
            {
                WorkItem item;
                lock (QueueLock)
                {
                    if (Queue.Count == 0)
                        break;
                    item = Queue.Dequeue();
                }

                try { item.Action?.Invoke(); }
                catch (Exception e) { item.Error = e; }
                finally { item.Done?.Set(); }
            }
        }

#if UNITY_EDITOR_WIN
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int dwProcessId);
#endif
    }
}
