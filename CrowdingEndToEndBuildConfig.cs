using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Scene-level configuration for the offline Unity -> Blender -> Unity LOD generation pipeline.
    /// Put this component on the same GameObject as CrowdingAwarePipelineController, or assign the references manually.
    /// The actual execution is done by the editor menu in Editor/CrowdingEndToEndBuilder.cs.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingEndToEndBuildConfig : MonoBehaviour
    {
        [Header("Required scene references")]
        public CrowdingAwarePipelineController controller;
        public CrowdingBlenderBatchExporter manifestExporter;
        public CrowdingCombinationLODSelector lodSelector;

        [Tooltip("Optional root containing the original visible scene. The selector can hide it when a generated variant is active.")]
        public GameObject originalSceneRoot;

        [Header("Unity -> Blender export")]
        [Tooltip("Relative to the Unity project root, not Assets.")]
        public string fbxExportRelativePath = "CrowdingExports/UnitySceneExport/Scene_CID.fbx";

        [Tooltip("Relative to the Unity project root, not Assets.")]
        public string manifestRelativePath = "CrowdingExports/crowding_batch_manifest.json";

        [Tooltip("Bake __CID_<objectId> into marked object names before FBX export, then optionally restore names afterward.")]
        public bool bakeCidNamesBeforeExport = true;

        public bool restoreNamesAfterExport = true;

        [Tooltip("Automatically add CrowdingObjectMarker to roots found by the selected controller before export.")]
        public bool addMissingMarkersFromController = true;

        [Tooltip("Automatically regenerate missing or duplicated object IDs before export.")]
        public bool fixMissingAndDuplicateIds = true;

        [Header("Batch variants")]
        public List<float> keepRatiosK = new List<float> { 0.6f, 0.3f };
        public bool generateUniformCondition = true;
        public bool generateWeightedCondition = true;
        public string sceneVariantPrefix = "SceneVariant";

        [Header("Blender execution")]
        [Tooltip("Full path to Blender executable. Example: C:/Program Files/Blender Foundation/Blender 4.2/blender.exe")]
        public string blenderExecutablePath = "blender";

        [Tooltip("Path to blender_generate_crowding_lods_cli.py. Can be absolute or relative to the Unity project root.")]
        public string blenderScriptPath = "Assets/Scripts/CrowdingAwareLOD/Blender/blender_generate_crowding_lods_cli.py";

        [Tooltip("Relative to the Unity project root. Put this under Assets if you want Unity to import the generated FBX files automatically.")]
        public string blenderOutputRelativePath = "Assets/CrowdingLODGenerated/GeneratedFBXVariants";

        [Tooltip("If true, Unity waits until Blender finishes. Recommended.")]
        public bool waitForBlender = true;

        [Tooltip("If true, Blender stops if any objectId from the manifest cannot be matched exactly once in the FBX scene.")]
        public bool strictBlenderValidation = true;

        [Header("Unity import / selector population")]
        [Tooltip("Only works automatically if blenderOutputRelativePath is inside Assets/.")]
        public bool importGeneratedVariantsIntoUnity = true;

        [Tooltip("Remove previously generated variant children under the selector before instantiating the new variants.")]
        public bool clearExistingSelectorVariantChildren = true;

        [Tooltip("Run selector.AutoPopulateVariantsFromChildren() after importing / instantiating variants.")]
        public bool autoPopulateSelector = true;

        [Tooltip("Validate that every imported variant contains every expected objectId exactly once.")]
        public bool validateImportedVariants = true;

        [Header("Logging")]
        public bool verboseLogs = true;

        private void Reset()
        {
            controller = GetComponent<CrowdingAwarePipelineController>();
            manifestExporter = GetComponent<CrowdingBlenderBatchExporter>();
            lodSelector = GetComponent<CrowdingCombinationLODSelector>();

            if (controller == null)
                controller = FindFirstObjectByType<CrowdingAwarePipelineController>();

            if (lodSelector == null)
                lodSelector = FindFirstObjectByType<CrowdingCombinationLODSelector>();
        }
    }
}
