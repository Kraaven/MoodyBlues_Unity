using System;
using System.Collections.Generic;
using UnityEngine;
using static ThreadInstanceManager;

public partial class ThreadInstanceManager : MonoBehaviour
{
    private static ThreadInstanceManager _singleton;
    private static readonly Dictionary<string, IThreadInstance> _threadDictionary = new Dictionary<string, IThreadInstance>();

    private static ThreadInstanceManager EnsureSingleton()
    {
        if (_singleton == null)
        {
            var go = new GameObject("ThreadsManager");
            _singleton = go.AddComponent<ThreadInstanceManager>();
            DontDestroyOnLoad(go);
        }
        return _singleton;
    }

    private void Awake()
    {
        if (_singleton != null && _singleton != this)
        {
            Destroy(gameObject);
            return;
        }
        _singleton = this;
        DontDestroyOnLoad(gameObject);
    }

    public static ThreadInstance<T1, T2> CreateThreadInstance<T1, T2>(
        Func<T1, T2> processor,
        Action<T2> outputProcessor,
        string instanceName = "ThreadInstance") where T1 : struct where T2 : struct
    {
        EnsureSingleton();

        if (_threadDictionary.ContainsKey(instanceName))
        {
            Debug.LogWarning($"Thread instance '{instanceName}' already exists — disposing old one and creating a new one.");
            _threadDictionary[instanceName].Dispose();
            _threadDictionary.Remove(instanceName);
        }

        var newThreadInstance = new ThreadInstance<T1, T2>(instanceName, processor, outputProcessor);
        _threadDictionary.Add(instanceName, newThreadInstance);
        return newThreadInstance;
    }

    public static bool RemoveThreadInstance(string instanceName)
    {
        if (_threadDictionary.TryGetValue(instanceName, out var instance))
        {
            instance.Dispose();
            return _threadDictionary.Remove(instanceName);
        }
        return false;
    }

    // Worker threads can't touch Unity APIs, so results are queued and
    // drained here on the main thread every frame.
    private void Update()
    {
        foreach (var thread in _threadDictionary.Values)
        {
            thread.ProcessResults();
        }
    }

    private void OnDestroy()
    {
        foreach (var thread in _threadDictionary.Values)
        {
            thread.Dispose();
        }
        _threadDictionary.Clear();

        if (_singleton == this)
        {
            _singleton = null;
        }
    }
}