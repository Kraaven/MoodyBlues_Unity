using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Collections.Generic;

public partial class BluesSessionManager : MonoBehaviour
{

    Transform T_reference;
    JObject J_reference;
    public void PopulateObjectUserData(Transform transform, JObject ObjectExtras) {

        T_reference = transform;
        J_reference = ObjectExtras;

        InsertActiveStatus();
        InsertObjectComponentList();
        
    
    }

    private void InsertActiveStatus() {
        if (!T_reference.gameObject.activeSelf) J_reference["isHidden"] = true;
    }

    private void InsertObjectComponentList() {

        List<string> ObjectComponents = new();

        CheckAndInsertComponent<Rigidbody>(ObjectComponents);


        J_reference["Components"] = JArray.FromObject(ObjectComponents);
    }

    private void CheckAndInsertComponent<T>(List<string> comps) where T : Component
    {
        if (T_reference.gameObject.GetComponent<T>()) comps.Add(typeof(T).ToString());
    }
}
