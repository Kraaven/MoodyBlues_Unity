using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

public partial class ThreadInstanceManager : MonoBehaviour
{
    // Non-generic handle so instances of different <T1,T2> can share one dictionary.
    private interface IThreadInstance
    {
        void Dispose();
        void ProcessResults();
    }

    public class ThreadInstance<T1, T2> : IThreadInstance where T1 : struct where T2 : struct
    {
        public string ThreadName { get; }

        private readonly BlockingCollection<T1> _requests;
        private readonly ConcurrentQueue<T2> _results;
        private readonly Thread _workerThread;
        private readonly Func<T1, T2> _executeFunc;
        private readonly Action<T2> _outputProcessor;
        private volatile bool _isRunning;

        internal ThreadInstance(string instanceName, Func<T1, T2> executeFunc, Action<T2> outputProcessor)
        {
            _executeFunc = executeFunc ?? throw new ArgumentNullException(nameof(executeFunc));
            _outputProcessor = outputProcessor;
            ThreadName = instanceName;

            _requests = new BlockingCollection<T1>();
            _results = new ConcurrentQueue<T2>();
            _isRunning = true;

            _workerThread = new Thread(ThreadExecutionLoop)
            {
                IsBackground = true,
                Name = instanceName
            };
            _workerThread.Start();
        }

        public void EnqueueRequest(T1 data)
        {
            if (!_isRunning)
                throw new InvalidOperationException($"Thread instance '{ThreadName}' has been disposed.");
            _requests.Add(data);
        }

        private void ThreadExecutionLoop()
        {
            foreach (T1 incomingData in _requests.GetConsumingEnumerable())
            {
                if (!_isRunning) break;
                try
                {
                    T2 processedData = _executeFunc(incomingData);
                    _results.Enqueue(processedData);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        // Called from the main thread (via Update) to flush results safely.
        public void ProcessResults()
        {
            while (_results.TryDequeue(out T2 result))
            {
                _outputProcessor?.Invoke(result);
            }
        }

        public void Dispose()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _requests.CompleteAdding();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(100);
            }

            _requests.Dispose();
        }
    }
}