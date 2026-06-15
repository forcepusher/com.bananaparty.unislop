using System;
using System.Collections.Generic;
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
        static int _inFlightRequests;
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
            EditorApplication.update += Drain;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static bool HasPendingWork
        {
            get
            {
                if (_isReloading)
                    return false;
                lock (QueueLock)
                    return _inFlightRequests > 0 || Queue.Count > 0;
            }
        }

        public static void BeginRequest()
        {
            Interlocked.Increment(ref _inFlightRequests);
            RequestEditorUpdate();
        }

        public static void EndRequest() => Interlocked.Decrement(ref _inFlightRequests);

        public static void RequestEditorUpdate()
        {
            if (_isReloading)
                return;

            McpEditorPump.Kick();

            if (IsMainThread)
            {
                try { EditorApplication.QueuePlayerLoopUpdate(); }
                catch { }
            }
        }

        public static string Invoke(Func<string> action, int timeoutMs = 120_000)
        {
            if (_isReloading)
                return McpUnityBridge.Error("Unity is reloading scripts");

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
            RequestEditorUpdate();
        }

        static void OnBeforeAssemblyReload()
        {
            _isReloading = true;
            lock (QueueLock)
                Queue.Clear();
            _inFlightRequests = 0;
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

            if (HasPendingWork)
                EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
