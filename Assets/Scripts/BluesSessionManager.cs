using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BluesSessionManager : MonoBehaviour
{
    List<Transform> TransformsTree = new();

    private void Awake()
    {
        foreach (var rootObject in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            InsertTransformIntoList(rootObject.transform);
        }

        Debug.Log(String.Join(", ", TransformsTree.Select(t => t.name)));
    }

    private void Start()
    {
        
    }

    private void FixedUpdate()
    {
        
    }

    public void InsertTransformIntoList(Transform T) {
        TransformsTree.Add(T);

        if (T.childCount > 0) {
            foreach (Transform child in T)
            {
                InsertTransformIntoList(child);
            }
        }
    }
}
