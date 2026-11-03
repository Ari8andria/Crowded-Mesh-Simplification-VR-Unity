#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace CrowdingAwareLOD.EditorTools
{
    [CustomEditor(typeof(CrowdingLODPreviewControlsPlus))]
    public class CrowdingLODPreviewControlsPlusEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CrowdingLODPreviewControlsPlus preview = (CrowdingLODPreviewControlsPlus)target;

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Computed key", preview.CurrentComputedVariantKey, EditorStyles.helpBox);

            EditorGUILayout.Space(6f);

            if (GUILayout.Button("Apply Selected Variant"))
                preview.ApplySelectedVariant();

            if (GUILayout.Button("Apply Pre-Normalized Reference"))
                preview.ApplyPreNormalizedReference();

            if (GUILayout.Button("Restore Original Meshes"))
                preview.RestoreOriginalMeshes();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Global scene presets", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("K075 Uniform"))
                preview.ApplyGlobalPreset(0.75f, CrowdingLODPreviewCondition.Uniform);
            if (GUILayout.Button("K075 Weighted"))
                preview.ApplyGlobalPreset(0.75f, CrowdingLODPreviewCondition.Weighted);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("K050 Uniform"))
                preview.ApplyGlobalPreset(0.50f, CrowdingLODPreviewCondition.Uniform);
            if (GUILayout.Button("K050 Weighted"))
                preview.ApplyGlobalPreset(0.50f, CrowdingLODPreviewCondition.Weighted);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("K025 Uniform"))
                preview.ApplyGlobalPreset(0.25f, CrowdingLODPreviewCondition.Uniform);
            if (GUILayout.Button("K025 Weighted"))
                preview.ApplyGlobalPreset(0.25f, CrowdingLODPreviewCondition.Weighted);
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
