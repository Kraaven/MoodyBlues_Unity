using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MoodyBlues
{
    public partial class MoodyBluesSessionManager : MonoBehaviour
    {
        /// <summary>
        /// Writes the MoodyBlues metadata a receiver needs but the live event stream never carries,
        /// into a tracked object's glTF node extras during scene export. Called from
        /// <see cref="ObjectIdExportPlugin"/>, which writes the <c>objectId</c> itself.
        /// </summary>
        public static void PopulateObjectUserData(Transform target, JObject nodeExtras) =>
            PopulateObjectUserData(target, nodeExtras, target.gameObject.activeSelf);

        /// <summary>
        /// As above, with the visibility to record given explicitly: a runtime export describes the
        /// scene as it was at Start, which may differ from its state at the moment of export.
        /// </summary>
        public static void PopulateObjectUserData(Transform target, JObject nodeExtras, bool activeSelf)
        {
            if (!activeSelf) nodeExtras["isHidden"] = true;
            WriteInvertedLookDirection(target, nodeExtras);
        }

        /// <summary>
        /// Flags nodes whose exported rotation UnityGLTF multiplied by a 180 degree turn about Y.
        /// The exporter does this for any GameObject with a Camera or Light component (enabled or
        /// not, and whether or not the light is itself exported), so a receiver cannot reliably
        /// infer it from the GLB. The backend needs it to convert streamed rotations, which are raw
        /// Unity local rotations, into the same space as the exported node.
        /// </summary>
        private static void WriteInvertedLookDirection(Transform target, JObject nodeExtras)
        {
            if (target.GetComponent<Camera>() != null || target.GetComponent<Light>() != null)
            {
                nodeExtras["invertLook"] = true;
            }
        }

        // Startup visibility (written by PopulateObjectUserData as isHidden). The wire protocol
        // deliberately never transmits this: it is static scene state, so it belongs with the
        // scene export rather than the event stream.
        //
        // activeSelf rather than activeInHierarchy on purpose. glTF consumers propagate a hidden
        // node to its descendants, so flagging only the object that is itself inactive reproduces
        // Unity's activeInHierarchy result for the whole subtree without flagging every child.
    }
}
