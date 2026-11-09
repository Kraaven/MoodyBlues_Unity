using UnityEditor;
using UnityEngine;

namespace MoodyBlues.EditorTools
{
    /// <summary>
    /// Inspector for <see cref="MoodyBluesClientConfig"/>. Shows the recording and scene export
    /// settings directly and hides the quantization ranges behind a foldout, since most projects
    /// never need to change them. Warns, with a shortcut, while the project has no connection.
    /// </summary>
    [CustomEditor(typeof(MoodyBluesClientConfig))]
    public sealed class MoodyBluesClientConfigEditor : Editor
    {
        private const string FoldoutPrefKey = "MoodyBlues.ConfigEditor.QuantizationExpanded";

        private static readonly string[] GeneralFields =
        {
            "recordingEnabled",
            "recordingMode",
            "messageSize",
        };

        private static readonly (string Name, float Default)[] QuantizationFields =
        {
            ("positionRange", MoodyBluesClientConfig.DefaultPositionRange),
            ("scaleMin", MoodyBluesClientConfig.DefaultScaleMin),
            ("scaleMax", MoodyBluesClientConfig.DefaultScaleMax),
            ("deltaPositionRange", MoodyBluesClientConfig.DefaultDeltaPositionRange),
            ("deltaRotationComponentRange", MoodyBluesClientConfig.DefaultDeltaRotationComponentRange),
            ("deltaSingleAxisAngleRange", MoodyBluesClientConfig.DefaultDeltaSingleAxisAngleRange),
            ("deltaScaleRange", MoodyBluesClientConfig.DefaultDeltaScaleRange),
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (!MoodyBluesProjectSettings.TryGetConnection(out _, out string connectionError))
            {
                EditorGUILayout.HelpBox(connectionError, MessageType.Warning);
                if (GUILayout.Button("Open MoodyBlues Settings")) MoodyBluesSettingsProvider.Open();
                EditorGUILayout.Space();
            }

            foreach (string field in GeneralFields)
            {
                SerializedProperty property = serializedObject.FindProperty(field);
                if (property != null) EditorGUILayout.PropertyField(property);
            }

            SerializedProperty mode = serializedObject.FindProperty("recordingMode");
            if (mode != null && mode.enumValueIndex == (int)RecordingMode.Custom)
            {
                EditorGUILayout.HelpBox(
                    "Nothing records until your code calls MoodyBluesSessionManager.StartRecording. " +
                    "On Quest and mobile, also call StopRecording from OnApplicationPause(true).",
                    MessageType.Info);
            }

            DrawExportSection();
            DrawQuantizationSection();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawExportSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene Export", EditorStyles.boldLabel);

            SerializedProperty onBuild = serializedObject.FindProperty("exportSceneOnBuild");
            SerializedProperty onRuntime = serializedObject.FindProperty("exportSceneOnRuntimeStart");
            SerializedProperty always = serializedObject.FindProperty("alwaysExportAtRuntime");
            SerializedProperty fromEditor = serializedObject.FindProperty("exportSceneFromEditor");

            bool wasExporting = onBuild.boolValue || onRuntime.boolValue;

            EditorGUILayout.PropertyField(onBuild);
            EditorGUILayout.PropertyField(onRuntime);
            if (onRuntime.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(always);
                EditorGUI.indentLevel--;
                EditorGUILayout.HelpBox(
                    "Runtime export needs Read/Write on every model in this scene, which keeps a CPU copy " +
                    "of each mesh in memory (roughly double the mesh memory on Quest). Builds enable it " +
                    "automatically; MoodyBlues > Make Scene Meshes Readable does it now.",
                    MessageType.None);
            }

            bool isExporting = onBuild.boolValue || onRuntime.boolValue;
            if (wasExporting && !isExporting)
            {
                // Deferred: a modal dialog in the middle of an inspector layout pass breaks the layout.
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog(
                    "MoodyBlues: scene export is off",
                    "Neither build-time nor runtime export is enabled for scenes using this config.\n\n" +
                    "Recordings will still be made, but they can only be replayed once you export the scene " +
                    "yourself with MoodyBlues > Export This Scene (or Export All Scenes).",
                    "OK");
            }

            if (!isExporting)
            {
                EditorGUILayout.HelpBox(
                    "Scene export is off. Export the scene yourself with MoodyBlues > Export This Scene, " +
                    "otherwise recordings wait for their scene and cannot be replayed.",
                    MessageType.Warning);
            }

            EditorGUILayout.PropertyField(fromEditor);
        }

        private void DrawQuantizationSection()
        {
            EditorGUILayout.Space();

            bool expanded = EditorPrefs.GetBool(FoldoutPrefKey, false);
            bool nowExpanded = EditorGUILayout.Foldout(expanded, "Quantization Ranges", toggleOnLabelClick: true);
            if (nowExpanded != expanded) EditorPrefs.SetBool(FoldoutPrefKey, nowExpanded);
            if (!nowExpanded) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "Tighter ranges are more precise but overflow sooner. The defaults suit most " +
                "scenes; change them only if EndSession reports promotions or hard clamps. " +
                "Values are sent as 16-bit floats, so they are rounded to about 3 significant digits.",
                MessageType.None);

            foreach ((string name, _) in QuantizationFields)
            {
                SerializedProperty property = serializedObject.FindProperty(name);
                if (property != null) EditorGUILayout.PropertyField(property);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset to defaults", GUILayout.Width(140)))
                {
                    foreach ((string name, float value) in QuantizationFields)
                    {
                        SerializedProperty property = serializedObject.FindProperty(name);
                        if (property != null) property.floatValue = value;
                    }
                }
            }

            EditorGUI.indentLevel--;
        }
    }
}
