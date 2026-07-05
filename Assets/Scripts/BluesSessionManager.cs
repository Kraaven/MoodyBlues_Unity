using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BluesSessionManager : MonoBehaviour
{
    List<TransformReference> TransformsTree = new();
    private BluesStreamer DataStreamer;

    private void Awake()
    {
        foreach (var rootObject in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            InsertTransformIntoList(rootObject.transform);
        }
        Debug.Log(String.Join(", ", TransformsTree.Select(t => $"[{t.PollingTransform.name} : {t.TransformID}]")));
    }

    private void Start()
    {
        DataStreamer = new BluesStreamer();
    }

    private void FixedUpdate()
    {
        // Retrieve the TransformData for every tracked object and hand each one to the
        // serialization worker thread. Reading transform.position/rotation/localScale
        // and building the struct happens here on the main thread (Unity API access is
        // main-thread-only); everything after EnqueueRequest happens off-thread.
        for (int i = 0; i < TransformsTree.Count; i++)
        {
            TransformReference reference = TransformsTree[i];
            Transform t = reference.PollingTransform;

            if (t == null)
            {
                // Object was destroyed since last tick; skip it. (A production version
                // would also notify the receiver to despawn TransformID, but that's a
                // separate concern from the memory-reuse work here.)
                continue;
            }

            bool transformUnchanged =
    reference.LastSentPosition == t.localPosition &&
    reference.LastSentRotation == t.localRotation &&
    reference.LastSentScale == t.localScale;

            if (reference.HasSentFirstTick && transformUnchanged) continue;


            TransformData data = new TransformData
            {
                ObjectId = reference.TransformID,
                currentPosition = t.localPosition,
                currentRotation = t.localRotation,
                currentScale = t.localScale,
                previousPosition = reference.HasSentFirstTick ? reference.LastSentPosition : t.position,
                previousRotation = reference.HasSentFirstTick ? reference.LastSentRotation : t.rotation,
                previousScale = reference.HasSentFirstTick ? reference.LastSentScale : t.localScale
            };

            DataStreamer.EnqueueTransform(data);

            // Advance "previous" state for next tick. This is main-thread bookkeeping on
            // TransformReference (not touched by the worker thread), so no locking needed.
            reference.HasSentFirstTick = true;
            reference.LastSentPosition = data.currentPosition;
            reference.LastSentRotation = data.currentRotation;
            reference.LastSentScale = data.currentScale;
        }
    }

    public void InsertTransformIntoList(Transform T)
    {
        TransformsTree.Add(new TransformReference
        {
            PollingTransform = T,
            TransformID = (ushort)(TransformsTree.Count + 1)
        });
        if (T.childCount > 0)
        {
            foreach (Transform child in T)
            {
                InsertTransformIntoList(child);
            }
        }
    }

    private void OnDestroy()
    {
        DataStreamer.Dispose();
    }
}

public class TransformReference
{
    public Transform PollingTransform;
    public ushort TransformID;

    // Tracks per-object "have we ever sent a True* event for this" and the last state we
    // sent, so BluesSessionManager can compute this tick's previous/current pair without
    // needing a second parallel dictionary keyed by TransformID.
    public bool HasSentFirstTick;
    public Vector3 LastSentPosition;
    public Quaternion LastSentRotation = Quaternion.identity;
    public Vector3 LastSentScale = Vector3.one;
}

public struct TransformData
{
    public ushort ObjectId;
    //public bool IsFirstTick;
    public Vector3 currentPosition;
    public Quaternion currentRotation;
    public Vector3 currentScale;
    public Vector3 previousPosition;
    public Quaternion previousRotation;
    public Vector3 previousScale;
}