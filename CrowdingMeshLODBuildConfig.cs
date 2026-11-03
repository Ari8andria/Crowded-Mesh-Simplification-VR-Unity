using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CrowdingAwareLOD
{
    public enum CrowdingMeshVariantGenerationMode
    {
        PerFixationVariants,
        GlobalNearestFixationVariants,
        Both
    }

    public enum CrowdingTwoStageWeightedMode
    {
        RedistributeWithinGroupBudget,
        ExtraSimplifyCrowdedObjects
    }

    [DisallowMultipleComponent]
    public class CrowdingMeshLODBuildConfig : MonoBehaviour
    {
        [Header("References")]
        public CrowdingAwarePipelineController controller;
        public CrowdingSharedMeshLODSelector meshSelector;

        [Header("Fixation points")]
        [Tooltip("Use all fixation points assigned on the controller when true. Otherwise, only the first fixationPointCountToUse are exported.")]
        public bool useAllControllerFixationPoints = true;

        [Min(1)]
        public int fixationPointCountToUse = 5;

        [Header("Experimental conditions")]
        public List<float> keepRatiosK = new List<float> { 0.75f, 0.5f, 0.25f };
        public bool generateUniformCondition = true;
        public bool generateWeightedCondition = true;

        [Header("Variant scope")]
        [Tooltip("PerFixationVariants keeps the existing behavior: F00_K075_weighted, F01_K075_weighted, etc. GlobalNearestFixationVariants creates one stable scene state per K/condition: K075_weighted_global. Both generates both families so the old dynamic/per-fixation workflow and the new stable global workflow can coexist.")]
        public CrowdingMeshVariantGenerationMode variantGenerationMode =
            CrowdingMeshVariantGenerationMode.PerFixationVariants;

        [Tooltip("Only used for GlobalNearestFixationVariants. For each object, select the fixation point with the smallest angular distance from the reference camera and use the keep ratio computed for that fixation.")]
        public bool globalWeightedAssignObjectsToNearestFixation = true;

        /*
         * Two-stage group-aware budget allocation
         * ------------------------------------------------------------
         * Not used anymore.
         * These fields are kept for compatibility with the rest of the pipeline,
         * but they are hidden from the Inspector and useTwoStageGroupAwareBudget
         * is forced to false in Reset(), OnValidate() and Awake().
         */

        [HideInInspector]
        public bool useTwoStageGroupAwareBudget = false;

        [HideInInspector]
        [Range(0f, 2f)]
        public float twoStageInterGroupDensityProtectionStrength = 0.5f;

        [HideInInspector]
        [Range(0.01f, 1f)]
        public float twoStageMinGroupKeepRatio = 0.10f;

        [HideInInspector]
        [Range(0.01f, 1f)]
        public float twoStageMaxGroupKeepRatio = 1.00f;

        [HideInInspector]
        [Range(0.01f, 1f)]
        public float twoStageMinObjectKeepRatio = 0.05f;

        [HideInInspector]
        [Range(0.01f, 1f)]
        public float twoStageMaxObjectKeepRatio = 1.00f;

        [HideInInspector]
        public CrowdingTwoStageWeightedMode twoStageWeightedMode =
            CrowdingTwoStageWeightedMode.RedistributeWithinGroupBudget;

        [HideInInspector]
        [Range(0f, 2f)]
        public float twoStageIntraGroupCrowdingStrength = 1.0f;

        [HideInInspector]
        public bool twoStageUseAngularFootprintArea = true;

        [HideInInspector]
        public bool twoStageUseMedianGroupDensity = true;

        [HideInInspector]
        public bool twoStageLogBudgetSummary = false;

        [Header("Decimation backend")]
        [Tooltip("UnityMeshSimplifierQEM is now the default backend. BlenderDecimate is kept only for compatibility.")]
        public CrowdingLODDecimationBackend decimationBackend =
            CrowdingLODDecimationBackend.UnityMeshSimplifierQEM;

        [Header("UnityMeshSimplifier backend")]
        private const bool DefaultUmsEnableSmartLink = true;
        private const double DefaultUmsVertexLinkDistance = 0.01d;

        private const bool DefaultUmsPreserveBorderEdges = false;
        private const bool DefaultUmsPreserveUVSeamEdges = false;
        private const bool DefaultUmsPreserveUVFoldoverEdges = false;
        private const bool DefaultUmsPreserveSurfaceCurvature = false;

        private const int DefaultUmsMaxIterationCount = 150;
        private const double DefaultUmsAgressiveness = 10.0d;

        private const bool DefaultUmsRecalculateBounds = false;
        private const bool DefaultUmsRecalculateNormals = false;
        private const bool DefaultUmsRecalculateTangents = false;

        public bool umsEnableSmartLink => DefaultUmsEnableSmartLink;
        public double umsVertexLinkDistance => DefaultUmsVertexLinkDistance;

        public bool umsPreserveBorderEdges => DefaultUmsPreserveBorderEdges;
        public bool umsPreserveUVSeamEdges => DefaultUmsPreserveUVSeamEdges;
        public bool umsPreserveUVFoldoverEdges => DefaultUmsPreserveUVFoldoverEdges;
        public bool umsPreserveSurfaceCurvature => DefaultUmsPreserveSurfaceCurvature;

        public int umsMaxIterationCount => DefaultUmsMaxIterationCount;
        public double umsAgressiveness => DefaultUmsAgressiveness;

        public bool umsRecalculateBounds => DefaultUmsRecalculateBounds;
        public bool umsRecalculateNormals => DefaultUmsRecalculateNormals;
        public bool umsRecalculateTangents => DefaultUmsRecalculateTangents;

        /*
         * Mesh-only paths
         * ------------------------------------------------------------
         * Hidden from Inspector.
         * Automatically generated from the active scene name.
         * Example for scene "HP_alchemist":
         *
         * CrowdingExports/HP_alchemist/SourceMeshes
         * CrowdingExports/HP_alchemist/crowding_mesh_lod_manifest.json
         * Assets/CrowdingGeneratedLODs/HP_alchemist/Meshes
         * Assets/CrowdingGeneratedLODs/HP_alchemist/crowding_lod_mesh_import_map.json
         * Assets/CrowdingGeneratedLODs/HP_alchemist/CrowdingMeshVariantLibrary.asset
         */

        [HideInInspector]
        public string sourceMeshObjRelativeFolder;

        [HideInInspector]
        public string meshManifestRelativePath;

        [HideInInspector]
        public string blenderOutputRelativeFolder;

        [HideInInspector]
        public string blenderImportMapRelativePath;

        [HideInInspector]
        public string variantLibraryAssetPath;

        /*
         * Blender options
         * ------------------------------------------------------------
         * Hidden from Inspector because UnityMeshSimplifierQEM is now
         * the default backend. These fields are kept for compatibility
         * with the old Blender pipeline.
         */

        [HideInInspector]
        public string blenderExecutablePath = "blender";

        [HideInInspector]
        public string blenderMeshScriptPath =
            "Assets/Scripts/CrowdingAwareLOD/Blender/blender_generate_mesh_lods_cli.py";

        [HideInInspector]
        public bool runBlenderInBackground = true;

        [HideInInspector]
        public bool waitForBlenderToFinish = true;

        [Header("Options")]
        public bool addMissingMarkersFromController = true;
        public bool fixMissingAndDuplicateIds = true;
        public bool skipSkinnedMeshes = true;
        public bool overwriteExistingSourceObjs = true;
        public bool importLibraryAfterBlender = true;
        public bool assignLibraryToSelector = true;
        public bool verboseLogs = true;

        private void Reset()
        {
            controller = GetComponent<CrowdingAwarePipelineController>();
            meshSelector = GetComponent<CrowdingSharedMeshLODSelector>();

            DisableTwoStageBudget();
            RefreshSceneDependentPaths();
        }

        private void Awake()
        {
            DisableTwoStageBudget();
            RefreshSceneDependentPaths();
        }

        private void OnValidate()
        {
            DisableTwoStageBudget();
            RefreshSceneDependentPaths();
        }

        [ContextMenu("Refresh scene-dependent paths")]
        public void RefreshSceneDependentPaths()
        {
            string sceneName = GetSafeActiveSceneName();

            sourceMeshObjRelativeFolder =
                $"CrowdingExports/{sceneName}/SourceMeshes";

            meshManifestRelativePath =
                $"CrowdingExports/{sceneName}/crowding_mesh_lod_manifest.json";

            blenderOutputRelativeFolder =
                $"Assets/CrowdingGeneratedLODs/{sceneName}/Meshes";

            blenderImportMapRelativePath =
                $"Assets/CrowdingGeneratedLODs/{sceneName}/crowding_lod_mesh_import_map.json";

            variantLibraryAssetPath =
                $"Assets/CrowdingGeneratedLODs/{sceneName}/CrowdingMeshVariantLibrary.asset";
        }

        private void DisableTwoStageBudget()
        {
            useTwoStageGroupAwareBudget = false;
            twoStageLogBudgetSummary = false;
        }

        private string GetSafeActiveSceneName()
        {
            string sceneName = SceneManager.GetActiveScene().name;

            if (string.IsNullOrWhiteSpace(sceneName))
                sceneName = "UnsavedScene";

            return SanitizePathPart(sceneName);
        }

        private string SanitizePathPart(string value)
        {
            foreach (char invalidChar in System.IO.Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            value = value.Replace(' ', '_');

            return value;
        }
    }
}