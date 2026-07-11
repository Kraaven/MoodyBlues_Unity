using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class BluesSessionManager : MonoBehaviour
{

    Transform T_reference;
    JObject J_reference;
    public void PopulateObjectUserData(Transform transform, JObject ObjectExtras) {

        T_reference = transform;
        J_reference = ObjectExtras;

        InsertActiveStatus();
        
    
    }

    private void InsertActiveStatus() {
        if (!T_reference.gameObject.activeSelf) J_reference["isHidden"] = true;
    }
}
