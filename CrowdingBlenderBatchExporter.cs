using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CrowdingAwareLOD
{
    [Serializable]
    public class CrowdingBatchGenerationSettings
    {
        [Tooltip("Two values requested for the experiment: 0.6 and 0.3.")]
        public List<float> groupKeepRatiosK = new List<float> { 0.6f, 0.3f };

        public bool includeUniformCondition = true;
        public bool includeWeightedCrowdingCondition = true;

        [Tooltip("Relative to the Unity project root, not Assets. Example: CrowdingExports/crowding_batch_manifest.json")]
        public string exportRelativePath = "CrowdingExports/crowding_batch_manifest.json";

        [Tooltip("Prefix used by the Blender script for full-scene variant objects or exported files.")]
        public string sceneVariantPrefix = "SceneVariant";

        public bool prettyPrintJson = true;
    }

    /// <summary>
    /// Exports all planned keep ratios for Blender.
    /// The resulting manifest contains 2 K values x 2 conditions x N fixation points.
    /// Blender reads the manifest, duplicates/decimates meshes once, then Unity can select the desired variant at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingBlenderBatchExporter : MonoBehaviour
    {
        public CrowdingAwarePipelineController controller;
        public CrowdingBatchGenerationSettings batchSettings = new CrowdingBatchGenerationSettings();

        [Header("Execution")]
        public bool runPipelineBeforeExport = true;
        public bool forceComputeOnlyModeDuringExport = true;

        [ContextMenu("Crowding Pipeline/Export Blender Batch Manifest")]
        public void ExportBlenderBatchManifest()
        {
            if (controller == null) controller = GetComponent<CrowdingAwarePipelineController>();
            if (controller == null)
            {
                Debug.LogError("CrowdingBlenderBatchExporter: no CrowdingAwarePipelineController assigned or found on the same GameObject.");
                return;
            }

            if (runPipelineBeforeExport || controller.Groups.Count == 0)
                controller.RunPipeline();

            if (controller.Groups.Count == 0 || controller.Signatures.Count == 0)
            {
                Debug.LogError("CrowdingBlenderBatchExporter: pipeline produced no groups/objects. Check object tag/layer/markers.");
                return;
            }

            CrowdingBatchManifest manifest = BuildManifest();
            string json = JsonUtility.ToJson(manifest, batchSettings.prettyPrintJson);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputPath = Path.GetFullPath(Path.Combine(projectRoot, batchSettings.exportRelativePath));
            string dir = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);

            Debug.Log(string.Format(
                "Crowding batch manifest exported: {0}\nObjects={1}, combinations={2}. Expected combinations = K values ({3}) x conditions x fixation points.",
                outputPath, manifest.objectCount, manifest.combinationCount, batchSettings.groupKeepRatiosK.Count));
        }

        public CrowdingBatchManifest BuildManifest()
        {
            CrowdingBatchManifest manifest = new CrowdingBatchManifest();
            manifest.generatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            manifest.unitySceneName = SceneManager.GetActiveScene().name;
            manifest.depthSpanArcsec = controller.segmentationSettings.maxDepthSpanArcsec;
            manifest.fixationCount = controller.fixationPoints != null ? controller.fixationPoints.Count : 0;
            manifest.objectCount = controller.Signatures.Count;
            manifest.notes = "Generated for offline Blender decimation. K values are global group triangle keep ratios. WeightedCrowding uses crowding weights; Uniform applies K to every object.";

            Dictionary<CrowdingObjectSignature, int> groupByObject = BuildGroupLookup(controller.Groups);
            for (int i = 0; i < controller.Signatures.Count; i++)
                manifest.objects.Add(BuildObjectInfo(controller.Signatures[i], groupByObject));

            DecimationApplyMode previousApplyMode = controller.applyMode;
            float previousK = controller.budgetSettings.groupKeepRatioK;

            if (forceComputeOnlyModeDuringExport)
                controller.applyMode = DecimationApplyMode.ComputeOnly;

            try
            {
                for (int kIndex = 0; kIndex < batchSettings.groupKeepRatiosK.Count; kIndex++)
                {
                    float k = Mathf.Clamp01(batchSettings.groupKeepRatiosK[kIndex]);
                    controller.budgetSettings.groupKeepRatioK = k;
                    controller.BuildAllFixationPlans();

                    for (int f = 0; f < controller.FixationPlans.Count; f++)
                    {
                        CrowdingFixationPlan fixationPlan = controller.FixationPlans[f];

                        if (batchSettings.includeUniformCondition)
                            manifest.combinations.Add(BuildCombination(fixationPlan, k, CrowdingDecimationCondition.Uniform));

                        if (batchSettings.includeWeightedCrowdingCondition)
                            manifest.combinations.Add(BuildCombination(fixationPlan, k, CrowdingDecimationCondition.WeightedCrowding));
                    }
                }
            }
            finally
            {
                controller.budgetSettings.groupKeepRatioK = previousK;
                controller.applyMode = previousApplyMode;
                controller.BuildAllFixationPlans();
            }

            manifest.combinationCount = manifest.combinations.Count;
            return manifest;
        }

        private CrowdingBatchCombination BuildCombination(
            CrowdingFixationPlan fixationPlan,
            float k,
            CrowdingDecimationCondition condition)
        {
            CrowdingBatchCombination combination = new CrowdingBatchCombination();
            combination.fixationIndex = fixationPlan.fixationIndex;
            combination.fixationName = fixationPlan.fixationTransform != null ? fixationPlan.fixationTransform.name : "DefaultCameraForward";
            combination.fixationWorldPosition = fixationPlan.fixationWorldPosition;
            combination.groupKeepRatioK = k;
            combination.condition = condition;
            combination.conditionName = condition == CrowdingDecimationCondition.WeightedCrowding ? "weighted" : "uniform";
            combination.variantKey = CrowdingVariantKey.Build(fixationPlan.fixationIndex, k, condition);
            combination.expectedSceneVariantName = batchSettings.sceneVariantPrefix + "_" + combination.variantKey;

            for (int g = 0; g < fixationPlan.groupPlans.Count; g++)
            {
                CrowdingGroupDecimationPlan gp = fixationPlan.groupPlans[g];
                for (int i = 0; i < gp.objectPlans.Count; i++)
                {
                    CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                    if (op == null || op.obj == null) continue;
                    combination.objectRecords.Add(BuildRecord(op, k, condition, combination.variantKey));
                }
            }

            return combination;
        }

        private CrowdingBatchObjectRecord BuildRecord(
            CrowdingObjectDecimationPlan op,
            float k,
            CrowdingDecimationCondition condition,
            string variantKey)
        {
            CrowdingBatchObjectRecord record = new CrowdingBatchObjectRecord();
            record.objectId = op.obj.objectId;
            record.rootName = op.obj.root != null ? op.obj.root.name : op.obj.semanticName;
            record.hierarchyPath = op.obj.root != null ? CrowdingGeometryUtils.GetHierarchyPath(op.obj.root.transform) : op.obj.objectId;
            record.semanticName = op.obj.semanticName;
            record.groupId = FindGroupId(op.obj);
            record.role = op.role.ToString();
            record.requestedGroupKeepRatioK = k;
            record.keepRatio = condition == CrowdingDecimationCondition.Uniform ? k : Mathf.Clamp01(op.keepRatio);
            record.eccentricityDeg = op.eccentricityDeg;
            record.boumaMean = float.IsInfinity(op.boumaMean) ? -1f : op.boumaMean;
            record.targetWeight = op.targetWeight;
            record.initialTriangles = op.InitialTriangles;
            record.estimatedFinalTriangles = Mathf.RoundToInt(record.initialTriangles * record.keepRatio);
            record.exportNameWithCid = CrowdingObjectIdUtility.BuildExportName(record.rootName, record.objectId);
            record.expectedObjectVariantName = record.exportNameWithCid + "__" + variantKey;

            for (int i = 0; i < op.selectedFlankers.Count; i++)
            {
                if (op.selectedFlankers[i] != null)
                    record.selectedFlankerObjectIds.Add(op.selectedFlankers[i].objectId);
            }

            return record;
        }

        private int FindGroupId(CrowdingObjectSignature obj)
        {
            for (int g = 0; g < controller.Groups.Count; g++)
            {
                CrowdingGroupData group = controller.Groups[g];
                if (group.members.Contains(obj)) return group.groupId;
            }
            return -1;
        }

        private Dictionary<CrowdingObjectSignature, int> BuildGroupLookup(List<CrowdingGroupData> groups)
        {
            Dictionary<CrowdingObjectSignature, int> lookup = new Dictionary<CrowdingObjectSignature, int>();
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].members.Count; i++)
                    lookup[groups[g].members[i]] = groups[g].groupId;
            }
            return lookup;
        }

        private CrowdingBatchObjectInfo BuildObjectInfo(CrowdingObjectSignature sig, Dictionary<CrowdingObjectSignature, int> groupByObject)
        {
            CrowdingBatchObjectInfo info = new CrowdingBatchObjectInfo();
            info.objectId = sig.objectId;
            info.rootName = sig.root != null ? sig.root.name : sig.semanticName;
            info.hierarchyPath = sig.root != null ? CrowdingGeometryUtils.GetHierarchyPath(sig.root.transform) : sig.objectId;
            info.exportNameWithCid = CrowdingObjectIdUtility.BuildExportName(info.rootName, info.objectId);
            info.semanticName = sig.semanticName;
            info.initialTriangles = sig.triangleCount;
            info.worldCenter = sig.worldCenter;
            info.worldBounds = sig.worldBounds;
            int groupId;
            info.groupId = groupByObject.TryGetValue(sig, out groupId) ? groupId : -1;

            if (sig.root != null)
            {
                MeshFilter[] filters = sig.root.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < filters.Length; i++)
                {
                    Mesh mesh = filters[i].sharedMesh;
                    if (mesh == null) continue;

                    CrowdingBatchChildMeshInfo child = new CrowdingBatchChildMeshInfo();
                    child.childPath = CrowdingGeometryUtils.GetHierarchyPath(filters[i].transform);
                    child.meshName = mesh.name;
                    child.subMeshCount = mesh.subMeshCount;
                    child.triangleCount = CountTriangles(mesh);
                    info.childMeshes.Add(child);
                }
            }

            return info;
        }

        private int CountTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int tris = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                tris += mesh.GetTriangles(i).Length / 3;
            return tris;
        }
    }
}
