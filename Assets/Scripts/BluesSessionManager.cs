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
        // Retrieve the Transformdata in the tree, and send the thread all the data.
    }

    public void InsertTransformIntoList(Transform T) {
        TransformsTree.Add(new TransformReference { 
            PollingTransform = T,
            TransformID = (ushort)TransformsTree.Count
        });

        if (T.childCount > 0) {
            foreach (Transform child in T)
            {
                InsertTransformIntoList(child);
            }
        }
    }
}

public class TransformReference {
    public Transform PollingTransform;
    public ushort TransformID;
}

public struct TransformData {

    public Vector3 currentPosition;
    public Quaternion currentRotation;
    public Vector3 currentScale;

    public Vector3 previousPosition;
    public Quaternion previousRotation;
    public Vector3 previousScale;
}
