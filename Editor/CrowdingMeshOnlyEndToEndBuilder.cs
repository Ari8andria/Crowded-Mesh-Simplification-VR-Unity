#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Mesh-only end-to-end builder.
    /// No full Unity scene is exported to Blender. Unity exports one clean OBJ per MeshFilter/SkinnedMeshRenderer slot,
    /// Blender decimates those source meshes, then Unity creates Mesh .asset files and the runtime selector swaps sharedMesh.
    /// </summary>
    public static class CrowdingMeshOnlyEndToEndBuilder
    {
        [MenuItem("Crowding Pipeline/Mesh Only/Run Full Mesh-Only Unity-Blender-Unity Build")]
        public static void RunFullMeshOnlyBuildFromMenu()
        {
            CrowdingMeshLODBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingMeshLODBuildConfig found. Add it to the same GameObject as CrowdingAwarePipelineController.");
                return;
            }

            RunFullBuild(config);
        }

        [MenuItem("Crowding Pipeline/Mesh Only/1 Export Source Meshes And Manifest Only")]
        public static void ExportManifestOnlyFromMenu()
        {
            CrowdingMeshLODBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingMeshLODBuildConfig found.");
                return;
            }

            ExportSourceMeshesAndManifest(config, out string manifestPath);
            Debug.Log("Mesh-only manifest exported: " + manifestPath);
        }

        [MenuItem("Crowding Pipeline/Mesh Only/2 Run Blender Mesh Decimation Only")]
        public static void RunBlenderOnlyFromMenu()
        {
            CrowdingMeshLODBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingMeshLODBuildConfig found.");
                return;
            }

            string manifestPath = ResolveProjectPath(config.meshManifestRelativePath);
            string outputFolder = ResolveProjectPath(config.blenderOutputRelativeFolder);
            string importMapPath = ResolveProjectPath(config.blenderImportMapRelativePath);
            string scriptPath = ResolveProjectPath(config.blenderMeshScriptPath);
            RunBlender(config, manifestPath, outputFolder, importMapPath, scriptPath);
        }

        [MenuItem("Crowding Pipeline/Mesh Only/2B Run UnityMeshSimplifier QEM Only")]
        public static void RunUnityMeshSimplifierOnlyFromMenu()
        {
            CrowdingMeshLODBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingMeshLODBuildConfig found.");
                return;
            }

            string manifestPath = ResolveProjectPath(config.meshManifestRelativePath);
            CrowdingUnityMeshSimplifierBackend.Build(config, manifestPath);
        }

        [MenuItem("Crowding Pipeline/Mesh Only/3 Build Mesh Variant Library Only")]
        public static void BuildLibraryOnlyFromMenu()
        {
            CrowdingMeshLODBuildConfig config = ResolveConfigFromSelectionOrScene();
            if (config == null)
            {
                Debug.LogError("No CrowdingMeshLODBuildConfig found.");
                return;
            }

            BuildMeshVariantLibrary(config);
        }

        public static bool RunFullBuild(CrowdingMeshLODBuildConfig config)
        {
            if (!ExportSourceMeshesAndManifest(config, out string manifestPath))
                return false;

            if (config.decimationBackend == CrowdingLODDecimationBackend.UnityMeshSimplifierQEM)
            {
                bool ok = CrowdingUnityMeshSimplifierBackend.Build(config, manifestPath);
                if (ok)
                    Debug.Log("Mesh-only crowding LOD build finished successfully using UnityMeshSimplifier QEM backend.");
                return ok;
            }

            string outputFolder = ResolveProjectPath(config.blenderOutputRelativeFolder);
            string importMapPath = ResolveProjectPath(config.blenderImportMapRelativePath);
            string scriptPath = ResolveProjectPath(config.blenderMeshScriptPath);

            if (!RunBlender(config, manifestPath, outputFolder, importMapPath, scriptPath))
                return false;

            AssetDatabase.Refresh();

            if (config.importLibraryAfterBlender)
            {
                if (!BuildMeshVariantLibrary(config))
                    return false;
            }

            Debug.Log("Mesh-only crowding LOD build finished successfully using Blender backend.");
            return true;
        }

        private static bool ExportSourceMeshesAndManifest(CrowdingMeshLODBuildConfig config, out string manifestPath)
        {
            manifestPath = string.Empty;

            if (!ValidateConfig(config))
                return false;

            CrowdingAwarePipelineController controller = config.controller;

            if (config.addMissingMarkersFromController)
                AddMarkersFromController(controller);

            if (config.fixMissingAndDuplicateIds)
                FixMissingAndDuplicateIds();

            if (!ValidateMarkers(out List<CrowdingObjectMarker> markers))
                return false;

            List<Transform> originalFixationList = new List<Transform>(controller.fixationPoints);
            bool changedFixationList = false;

            try
            {
                if (!config.useAllControllerFixationPoints)
                {
                    int n = Mathf.Clamp(config.fixationPointCountToUse, 1, Mathf.Max(1, originalFixationList.Count));
                    controller.fixationPoints = originalFixationList.Take(n).ToList();
                    changedFixationList = true;
                }

                controller.RunPipeline();
                if (controller.Signatures.Count == 0)
                {
                    Debug.LogError("Crowding pipeline produced no object signatures.", controller);
                    return false;
                }

                if (controller.FixationPlans.Count == 0)
                {
                    Debug.LogError("Crowding pipeline produced no fixation plans.", controller);
                    return false;
                }

                Dictionary<string, CrowdingObjectSignature> signatureByObjectId = controller.Signatures
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.objectId))
                    .GroupBy(s => s.objectId)
                    .ToDictionary(g => g.Key, g => g.First());

                string sourceMeshFolder = ResolveProjectPath(config.sourceMeshObjRelativeFolder);
                if (!Directory.Exists(sourceMeshFolder)) Directory.CreateDirectory(sourceMeshFolder);

                List<CrowdingMeshSlotManifestRecord> meshSlots = BuildAndExportMeshSlots(markers, signatureByObjectId, sourceMeshFolder, config);
                if (meshSlots.Count == 0)
                {
                    Debug.LogError("No mesh slots were exported. Check MeshFilters under marked objects.");
                    return false;
                }

                CrowdingMeshDecimationManifest manifest = BuildManifest(config, controller, meshSlots);
                string json = JsonUtility.ToJson(manifest, true);
                manifestPath = ResolveProjectPath(config.meshManifestRelativePath);
                EnsureParentDirectory(manifestPath);
                File.WriteAllText(manifestPath, json);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log("Mesh-only manifest exported: " + manifestPath + "\nMesh slots: " + meshSlots.Count + ", variants: " + manifest.variantCount);
                return true;
            }
            finally
            {
                if (changedFixationList)
                {
                    controller.fixationPoints = originalFixationList;
                    EditorUtility.SetDirty(controller);
                }
            }
        }

        private static CrowdingMeshDecimationManifest BuildManifest(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            List<CrowdingMeshSlotManifestRecord> meshSlots)
        {
            CrowdingMeshDecimationManifest manifest = new CrowdingMeshDecimationManifest();
            manifest.generatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            manifest.unitySceneName = SceneManager.GetActiveScene().name;
            manifest.depthSpanArcsec = controller.segmentationSettings.maxDepthSpanArcsec;
            manifest.availableFixationPointCount = controller.fixationPoints != null ? controller.fixationPoints.Count : 0;
            manifest.usedFixationPointCount = controller.FixationPlans.Count;
            manifest.objectCount = controller.Signatures.Count;
            manifest.meshSlotCount = meshSlots.Count;
            manifest.sourceMeshDirectory = ResolveProjectPath(config.sourceMeshObjRelativeFolder);
            manifest.blenderOutputDirectory = ResolveProjectPath(config.blenderOutputRelativeFolder);
            manifest.variantGenerationMode = config.variantGenerationMode.ToString();
            manifest.notes = "Mesh-only crowding LOD manifest. Triangle-budget keepRatio is backend-independent. " +
                             "Selected backend: " + config.decimationBackend + ". Variant generation mode: " + config.variantGenerationMode;
            manifest.meshSlots.AddRange(meshSlots);

            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId = new Dictionary<string, List<CrowdingMeshSlotManifestRecord>>();
            for (int i = 0; i < meshSlots.Count; i++)
            {
                CrowdingMeshSlotManifestRecord slot = meshSlots[i];
                if (!slot.supported) continue;
                if (!slotsByObjectId.ContainsKey(slot.objectId)) slotsByObjectId[slot.objectId] = new List<CrowdingMeshSlotManifestRecord>();
                slotsByObjectId[slot.objectId].Add(slot);
            }

            float previousK = controller.budgetSettings.groupKeepRatioK;
            DecimationApplyMode previousApplyMode = controller.applyMode;
            controller.applyMode = DecimationApplyMode.ComputeOnly;

            try
            {
                for (int kIndex = 0; kIndex < config.keepRatiosK.Count; kIndex++)
                {
                    float k = Mathf.Clamp01(config.keepRatiosK[kIndex]);
                    controller.budgetSettings.groupKeepRatioK = k;
                    controller.BuildAllFixationPlans();

                    bool generatePerFixation =
                        config.variantGenerationMode == CrowdingMeshVariantGenerationMode.PerFixationVariants ||
                        config.variantGenerationMode == CrowdingMeshVariantGenerationMode.Both;

                    bool generateGlobal =
                        config.variantGenerationMode == CrowdingMeshVariantGenerationMode.GlobalNearestFixationVariants ||
                        config.variantGenerationMode == CrowdingMeshVariantGenerationMode.Both;

                    if (generatePerFixation)
                    {
                        for (int f = 0; f < controller.FixationPlans.Count; f++)
                        {
                            CrowdingFixationPlan plan = controller.FixationPlans[f];
                            if (config.generateUniformCondition)
                                manifest.variants.Add(BuildVariantRecord(config, controller, plan, k, CrowdingDecimationCondition.Uniform, slotsByObjectId));

                            if (config.generateWeightedCondition)
                                manifest.variants.Add(BuildVariantRecord(config, controller, plan, k, CrowdingDecimationCondition.WeightedCrowding, slotsByObjectId));
                        }
                    }

                    if (generateGlobal)
                    {
                        if (config.generateUniformCondition)
                        {
                            manifest.variants.Add(BuildGlobalNearestFixationVariantRecord(
                                config,
                                controller,
                                k,
                                CrowdingDecimationCondition.Uniform,
                                slotsByObjectId));
                        }

                        if (config.generateWeightedCondition)
                        {
                            manifest.variants.Add(BuildGlobalNearestFixationVariantRecord(
                                config,
                                controller,
                                k,
                                CrowdingDecimationCondition.WeightedCrowding,
                                slotsByObjectId));
                        }
                    }
                }
            }
            finally
            {
                controller.budgetSettings.groupKeepRatioK = previousK;
                controller.applyMode = previousApplyMode;
                controller.BuildAllFixationPlans();
            }

            manifest.variantCount = manifest.variants.Count;
            return manifest;
        }

        private static CrowdingMeshVariantManifestRecord BuildVariantRecord(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingFixationPlan plan,
            float k,
            CrowdingDecimationCondition condition,
            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId)
        {
            CrowdingMeshVariantManifestRecord variant = CreateVariantHeader(
                plan != null ? plan.fixationIndex : -1,
                plan != null && plan.fixationTransform != null ? plan.fixationTransform.name : "DefaultCameraForward",
                plan != null ? plan.fixationWorldPosition : Vector3.zero,
                k,
                condition,
                CrowdingVariantKey.Build(plan != null ? plan.fixationIndex : 0, k, condition),
                false,
                "PerFixation");

            if (plan == null)
                return variant;

            if (config == null || !config.useTwoStageGroupAwareBudget)
            {
                BuildVariantRecordLegacy(variant, plan, k, condition, slotsByObjectId);
                return variant;
            }

            List<VariantObjectBudgetItem> items = BuildBudgetItemsFromFixationPlan(
                config,
                controller,
                plan,
                condition,
                slotsByObjectId);

            ApplyTwoStageGroupAwareBudget(config, controller, variant, items, k, condition);
            AddBudgetItemsToVariant(variant, items);
            return variant;
        }

        private static void BuildVariantRecordLegacy(
            CrowdingMeshVariantManifestRecord variant,
            CrowdingFixationPlan plan,
            float k,
            CrowdingDecimationCondition condition,
            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId)
        {
            for (int g = 0; g < plan.groupPlans.Count; g++)
            {
                CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                for (int i = 0; i < gp.objectPlans.Count; i++)
                {
                    CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                    if (op == null || op.obj == null) continue;

                    List<CrowdingMeshSlotManifestRecord> slots;
                    if (!slotsByObjectId.TryGetValue(op.obj.objectId, out slots)) continue;

                    float keep = condition == CrowdingDecimationCondition.Uniform ? k : op.keepRatio;
                    keep = Mathf.Clamp01(keep);

                    for (int s = 0; s < slots.Count; s++)
                    {
                        CrowdingMeshSlotManifestRecord slot = slots[s];
                        CrowdingMeshVariantSlotRecord rec = CreateVariantSlotRecord(
                            variant,
                            slot,
                            op,
                            keep,
                            plan.fixationIndex,
                            variant.fixationName,
                            gp.groupId);

                        variant.meshRecords.Add(rec);
                    }
                }
            }
        }

        private static CrowdingMeshVariantManifestRecord BuildGlobalNearestFixationVariantRecord(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            float k,
            CrowdingDecimationCondition condition,
            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId)
        {
            CrowdingMeshVariantManifestRecord variant = CreateVariantHeader(
                -1,
                "GlobalNearestFixation",
                controller != null && controller.viewCamera != null
                    ? controller.viewCamera.transform.position + controller.viewCamera.transform.forward * 10f
                    : Vector3.zero,
                k,
                condition,
                CrowdingVariantKey.BuildGlobal(k, condition),
                true,
                condition == CrowdingDecimationCondition.WeightedCrowding
                    ? "NearestFixationAngularDistance"
                    : "GlobalUniform");

            if (config != null && config.useTwoStageGroupAwareBudget)
            {
                List<VariantObjectBudgetItem> items = BuildBudgetItemsForGlobalNearestVariant(
                    config,
                    controller,
                    condition,
                    slotsByObjectId);

                ApplyTwoStageGroupAwareBudget(config, controller, variant, items, k, condition);
                AddBudgetItemsToVariant(variant, items);
                return variant;
            }

            Dictionary<int, Dictionary<string, CrowdingObjectDecimationPlan>> planLookup = BuildPlanLookup(controller.FixationPlans);
            Dictionary<string, CrowdingObjectSignature> signatureLookup = BuildSignatureLookup(controller);

            foreach (KeyValuePair<string, List<CrowdingMeshSlotManifestRecord>> kvp in slotsByObjectId)
            {
                string objectId = kvp.Key;
                List<CrowdingMeshSlotManifestRecord> slots = kvp.Value;

                CrowdingObjectDecimationPlan selectedPlan = null;
                int assignedFixationIndex = -1;
                string assignedFixationName = string.Empty;

                if (condition == CrowdingDecimationCondition.WeightedCrowding)
                {
                    CrowdingObjectSignature sig;
                    signatureLookup.TryGetValue(objectId, out sig);

                    if (config == null || config.globalWeightedAssignObjectsToNearestFixation)
                    {
                        assignedFixationIndex = FindNearestFixationIndexForObject(sig, controller);

                        if (assignedFixationIndex >= 0)
                        {
                            Dictionary<string, CrowdingObjectDecimationPlan> byObject;
                            if (planLookup.TryGetValue(assignedFixationIndex, out byObject))
                                byObject.TryGetValue(objectId, out selectedPlan);
                        }
                    }

                    if (selectedPlan == null)
                    {
                        selectedPlan = FindFirstObjectPlan(objectId, controller.FixationPlans, out assignedFixationIndex);
                    }

                    assignedFixationName = GetFixationName(controller, assignedFixationIndex);
                }

                float keep = condition == CrowdingDecimationCondition.Uniform
                    ? k
                    : (selectedPlan != null ? selectedPlan.keepRatio : k);
                keep = Mathf.Clamp01(keep);

                for (int s = 0; s < slots.Count; s++)
                {
                    CrowdingMeshSlotManifestRecord slot = slots[s];
                    CrowdingMeshVariantSlotRecord rec = CreateVariantSlotRecord(
                        variant,
                        slot,
                        selectedPlan,
                        keep,
                        assignedFixationIndex,
                        assignedFixationName,
                        -1);

                    if (condition == CrowdingDecimationCondition.Uniform)
                    {
                        rec.groupId = -1;
                        rec.role = "Uniform";
                        rec.assignedFixationIndex = -1;
                        rec.assignedFixationName = "GlobalUniform";
                        rec.eccentricityDeg = 0f;
                        rec.boumaMean = 0f;
                        rec.targetWeight = 1f;
                    }

                    variant.meshRecords.Add(rec);
                }
            }

            return variant;
        }

        private static CrowdingMeshVariantManifestRecord CreateVariantHeader(
            int fixationIndex,
            string fixationName,
            Vector3 fixationWorldPosition,
            float k,
            CrowdingDecimationCondition condition,
            string variantKey,
            bool isGlobalSceneVariant,
            string assignmentMode)
        {
            CrowdingMeshVariantManifestRecord variant = new CrowdingMeshVariantManifestRecord();
            variant.fixationIndex = fixationIndex;
            variant.fixationName = fixationName ?? string.Empty;
            variant.fixationWorldPosition = fixationWorldPosition;
            variant.groupKeepRatioK = k;
            variant.condition = condition;
            variant.conditionName = condition == CrowdingDecimationCondition.WeightedCrowding ? "weighted" : "uniform";
            variant.variantKey = variantKey;
            variant.isGlobalSceneVariant = isGlobalSceneVariant;
            variant.assignmentMode = assignmentMode ?? string.Empty;
            return variant;
        }

        private static List<VariantObjectBudgetItem> BuildBudgetItemsFromFixationPlan(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingFixationPlan plan,
            CrowdingDecimationCondition condition,
            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId)
        {
            List<VariantObjectBudgetItem> items = new List<VariantObjectBudgetItem>();
            if (plan == null || slotsByObjectId == null)
                return items;

            for (int g = 0; g < plan.groupPlans.Count; g++)
            {
                CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                if (gp == null || gp.objectPlans == null)
                    continue;

                for (int i = 0; i < gp.objectPlans.Count; i++)
                {
                    CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                    if (op == null || op.obj == null || string.IsNullOrWhiteSpace(op.obj.objectId))
                        continue;

                    List<CrowdingMeshSlotManifestRecord> slots;
                    if (!slotsByObjectId.TryGetValue(op.obj.objectId, out slots) || slots == null || slots.Count == 0)
                        continue;

                    VariantObjectBudgetItem item = CreateBudgetItem(
                        config,
                        controller,
                        op,
                        slots,
                        plan.fixationIndex,
                        GetFixationName(controller, plan.fixationIndex),
                        gp.groupId,
                        "F" + plan.fixationIndex.ToString("00") + "_G" + gp.groupId.ToString(),
                        plan.fixationTransform);

                    items.Add(item);
                }
            }

            return items;
        }

        private static List<VariantObjectBudgetItem> BuildBudgetItemsForGlobalNearestVariant(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingDecimationCondition condition,
            Dictionary<string, List<CrowdingMeshSlotManifestRecord>> slotsByObjectId)
        {
            List<VariantObjectBudgetItem> items = new List<VariantObjectBudgetItem>();
            if (controller == null || slotsByObjectId == null)
                return items;

            Dictionary<int, Dictionary<string, PlanObjectInfo>> planInfoLookup = BuildPlanInfoLookup(controller.FixationPlans);
            Dictionary<string, CrowdingObjectSignature> signatureLookup = BuildSignatureLookup(controller);

            foreach (KeyValuePair<string, List<CrowdingMeshSlotManifestRecord>> kvp in slotsByObjectId)
            {
                string objectId = kvp.Key;
                List<CrowdingMeshSlotManifestRecord> slots = kvp.Value;
                if (string.IsNullOrWhiteSpace(objectId) || slots == null || slots.Count == 0)
                    continue;

                CrowdingObjectSignature sig;
                signatureLookup.TryGetValue(objectId, out sig);

                int assignedFixationIndex = -1;
                PlanObjectInfo info = null;

                if (config == null || config.globalWeightedAssignObjectsToNearestFixation)
                {
                    assignedFixationIndex = FindNearestFixationIndexForObject(sig, controller);
                    if (assignedFixationIndex >= 0)
                    {
                        Dictionary<string, PlanObjectInfo> byObject;
                        if (planInfoLookup.TryGetValue(assignedFixationIndex, out byObject))
                            byObject.TryGetValue(objectId, out info);
                    }
                }

                if (info == null)
                    info = FindFirstObjectPlanInfo(objectId, controller.FixationPlans, out assignedFixationIndex);

                CrowdingObjectDecimationPlan op = info != null ? info.objectPlan : null;
                int groupId = info != null ? info.groupId : -1;
                string fixationName = GetFixationName(controller, assignedFixationIndex);
                Transform fixationTransform = GetFixationTransform(controller, assignedFixationIndex);

                VariantObjectBudgetItem item = CreateBudgetItem(
                    config,
                    controller,
                    op,
                    slots,
                    assignedFixationIndex,
                    fixationName,
                    groupId,
                    "F" + assignedFixationIndex.ToString("00") + "_G" + groupId.ToString(),
                    fixationTransform);

                item.objectId = objectId;
                if (item.rootName == string.Empty && slots.Count > 0)
                    item.rootName = slots[0].rootName;

                items.Add(item);
            }

            return items;
        }

        private static VariantObjectBudgetItem CreateBudgetItem(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingObjectDecimationPlan op,
            List<CrowdingMeshSlotManifestRecord> slots,
            int assignedFixationIndex,
            string assignedFixationName,
            int groupId,
            string groupKey,
            Transform fixationTransform)
        {
            VariantObjectBudgetItem item = new VariantObjectBudgetItem();
            item.objectPlan = op;
            item.slots = slots;
            item.assignedFixationIndex = assignedFixationIndex;
            item.assignedFixationName = assignedFixationName ?? string.Empty;
            item.groupId = groupId;
            item.groupKey = string.IsNullOrWhiteSpace(groupKey) ? "Ungrouped" : groupKey;
            item.rootObject = op != null && op.obj != null ? op.obj.root : null;
            item.objectId = op != null && op.obj != null ? op.obj.objectId : (slots != null && slots.Count > 0 ? slots[0].objectId : string.Empty);
            item.rootName = op != null && op.obj != null && op.obj.root != null ? op.obj.root.name : (slots != null && slots.Count > 0 ? slots[0].rootName : string.Empty);
            item.initialTriangles = SumInitialTriangles(slots);
            // Angular footprint is intentionally not used by this allocation strategy.
            item.angularAreaDeg2 = 0f;
            item.crowdingQualityWeight = GetCrowdingQualityWeight(op, config != null ? config.twoStageIntraGroupCrowdingStrength : 1f);
            item.finalKeepRatio = 1f;
            item.groupKeepRatio = 1f;
            return item;
        }

        /// <summary>
        /// Crowding-aware allocation with a global upper budget (not exact budget matching).
        /// 1) Every object starts at the uniform keep ratio K.
        /// 2) Targets donate triangles according to their crowdingQualityWeight.
        /// 3) Saved triangles are given first to flankers in the same group.
        /// 4) Any surplus is offered to flankers in other groups.
        /// 5) If all flankers reach keepRatio=1, the remaining triangles are discarded.
        ///
        /// The legacy method name and config flag are retained for compatibility.
        /// No angular-size/density-based inter-group protection is applied.
        /// </summary>
        private static void ApplyTwoStageGroupAwareBudget(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingMeshVariantManifestRecord variant,
            List<VariantObjectBudgetItem> items,
            float globalK,
            CrowdingDecimationCondition condition)
        {
            if (items == null || items.Count == 0)
                return;

            float k = Mathf.Clamp01(globalK);
            double uniformBudget = 0.0;

            // Always start from the true global uniform baseline.
            for (int i = 0; i < items.Count; i++)
            {
                VariantObjectBudgetItem item = items[i];
                if (item == null) continue;

                item.groupKeepRatio = k; // Metadata only: groups no longer get separate budgets.
                item.finalKeepRatio = k;
                uniformBudget += (double)Mathf.Max(0, item.initialTriangles) * k;
            }

            // Uniform must be identical across all groups and fixation assignments.
            if (condition == CrowdingDecimationCondition.Uniform)
                return;

            List<BudgetGroup> groups = BuildBudgetGroups(items);
            float configuredMin = config != null
                ? Mathf.Clamp01(config.twoStageMinObjectKeepRatio)
                : 0.05f;
            // Never raise a target above K merely to satisfy a configured floor.
            float targetMin = Mathf.Min(configuredMin, k);

            double totalSaved = 0.0;
            double locallyRedistributed = 0.0;
            double globalSurplus = 0.0;
            List<VariantObjectBudgetItem> allFlankers = new List<VariantObjectBudgetItem>();

            for (int g = 0; g < groups.Count; g++)
            {
                BudgetGroup group = groups[g];
                double groupSaved = 0.0;
                bool hasTarget = false;
                List<VariantObjectBudgetItem> localFlankers = new List<VariantObjectBudgetItem>();

                for (int i = 0; i < group.items.Count; i++)
                {
                    VariantObjectBudgetItem item = group.items[i];
                    if (item == null || item.objectPlan == null || item.initialTriangles <= 0)
                        continue;

                    if (item.objectPlan.role == CrowdingRole.Target)
                    {
                        hasTarget = true;
                        float weight = Mathf.Clamp01(item.crowdingQualityWeight);
                        float targetKeep = Mathf.Clamp(k * weight, targetMin, k);
                        item.finalKeepRatio = targetKeep;
                        groupSaved += (double)item.initialTriangles * (k - targetKeep);
                    }
                    else if (item.objectPlan.role == CrowdingRole.NonTarget)
                    {
                        // NonTarget objects belonging to a group with a target are flankers.
                        localFlankers.Add(item);
                    }
                }

                if (!hasTarget || localFlankers.Count == 0)
                {
                    // No target-flanker relationship: do not create crowding savings.
                    for (int i = 0; i < group.items.Count; i++)
                    {
                        VariantObjectBudgetItem item = group.items[i];
                        if (item != null) item.finalKeepRatio = k;
                    }
                    continue;
                }

                allFlankers.AddRange(localFlankers);
                totalSaved += groupSaved;

                double unusedInGroup = RedistributeSurplusToFlankers(localFlankers, groupSaved);
                locallyRedistributed += groupSaved - unusedInGroup;
                globalSurplus += unusedInGroup;
            }

            // Transfer surplus between groups without density or angular-size weighting.
            double discarded = RedistributeSurplusToFlankers(allFlankers, globalSurplus);
            double globallyRedistributed = globalSurplus - discarded;

            // Useful invariant: weighted budget <= uniform budget (up to float rounding).
            double weightedBudget = 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                VariantObjectBudgetItem item = items[i];
                if (item != null)
                    weightedBudget += (double)Mathf.Max(0, item.initialTriangles) * item.finalKeepRatio;
            }

            if (weightedBudget > uniformBudget + Math.Max(0.1, uniformBudget * 1e-6))
                Debug.LogError("Crowding-aware allocation exceeded the uniform triangle budget.", config);

            if (config != null && config.twoStageLogBudgetSummary && config.verboseLogs)
            {
                Debug.Log(
                    "Crowding-aware budget (no angular group protection)\n" +
                    "Variant: " + (variant != null ? variant.variantKey : "unknown") + "\n" +
                    "K: " + k.ToString("F3", CultureInfo.InvariantCulture) + "\n" +
                    "Uniform budget: " + uniformBudget.ToString("F1", CultureInfo.InvariantCulture) + " triangles\n" +
                    "Target savings: " + totalSaved.ToString("F1", CultureInfo.InvariantCulture) + " triangles\n" +
                    "Local redistribution: " + locallyRedistributed.ToString("F1", CultureInfo.InvariantCulture) + " triangles\n" +
                    "Global redistribution: " + globallyRedistributed.ToString("F1", CultureInfo.InvariantCulture) + " triangles\n" +
                    "Discarded surplus: " + discarded.ToString("F1", CultureInfo.InvariantCulture) + " triangles\n" +
                    "Estimated weighted budget: " + weightedBudget.ToString("F1", CultureInfo.InvariantCulture) + " triangles",
                    config);
            }
        }

        /// <summary>
        /// Allocate up to availableTriangles among flankers in proportion to their
        /// remaining triangle capacity. Never increase a keep ratio above 1.
        /// Returns the amount that could not be allocated.
        /// </summary>
        private static double RedistributeSurplusToFlankers(
            List<VariantObjectBudgetItem> flankers,
            double availableTriangles)
        {
            if (flankers == null || flankers.Count == 0 || availableTriangles <= 0.0)
                return Math.Max(0.0, availableTriangles);

            double totalCapacity = 0.0;
            for (int i = 0; i < flankers.Count; i++)
            {
                VariantObjectBudgetItem item = flankers[i];
                if (item == null || item.initialTriangles <= 0) continue;
                totalCapacity += (double)item.initialTriangles *
                                 (1.0 - Mathf.Clamp01(item.finalKeepRatio));
            }

            if (totalCapacity <= 0.0)
                return availableTriangles;

            double toAllocate = Math.Min(availableTriangles, totalCapacity);
            double fillFraction = toAllocate / totalCapacity;
            double actuallyAllocated = 0.0;

            for (int i = 0; i < flankers.Count; i++)
            {
                VariantObjectBudgetItem item = flankers[i];
                if (item == null || item.initialTriangles <= 0) continue;

                float previous = Mathf.Clamp01(item.finalKeepRatio);
                double next = previous + (1.0 - previous) * fillFraction;
                item.finalKeepRatio = (float)Math.Min(1.0, next);
                actuallyAllocated += (double)item.initialTriangles *
                                     (item.finalKeepRatio - previous);
            }

            return Math.Max(0.0, availableTriangles - actuallyAllocated);
        }

        private static List<BudgetGroup> BuildBudgetGroups(List<VariantObjectBudgetItem> items)
        {
            Dictionary<string, BudgetGroup> byKey = new Dictionary<string, BudgetGroup>();

            for (int i = 0; i < items.Count; i++)
            {
                VariantObjectBudgetItem item = items[i];
                if (item == null || item.initialTriangles <= 0)
                    continue;

                string key = string.IsNullOrWhiteSpace(item.groupKey) ? "Ungrouped" : item.groupKey;
                BudgetGroup group;
                if (!byKey.TryGetValue(key, out group))
                {
                    group = new BudgetGroup();
                    group.groupKey = key;
                    group.groupId = item.groupId;
                    byKey.Add(key, group);
                }

                group.items.Add(item);
                group.totalTriangles += item.initialTriangles;
                // No group angular-density weighting.
            }

            return byKey.Values.ToList();
        }

        private static float EstimateObjectAngularAreaDeg2(
            CrowdingMeshLODBuildConfig config,
            CrowdingAwarePipelineController controller,
            CrowdingObjectDecimationPlan op,
            object rootObject,
            Transform fixationTransform)
        {
            if (config != null && config.twoStageUseAngularFootprintArea)
            {
                Camera camera = controller != null && controller.viewCamera != null ? controller.viewCamera : Camera.main;
                if (camera != null && rootObject != null)
                {
                    CrowdingAngularFootprintUtility.AngularFootprint footprint =
                        CrowdingAngularFootprintUtility.ComputeAngularFootprintFromRootObject(
                            rootObject,
                            camera,
                            fixationTransform,
                            includeInactiveChildren: true,
                            ignoreCornersBehindCamera: true);

                    if (footprint.isValid && footprint.areaDeg2 > 1e-6f)
                        return footprint.areaDeg2;
                }
            }

            float radius = 0.1f;
            if (op != null && op.obj != null)
                radius = Mathf.Max(0.01f, op.obj.apparentRadiusDeg);

            return Mathf.Max(Mathf.PI * radius * radius, 1e-6f);
        }

        private static float GetCrowdingQualityWeight(CrowdingObjectDecimationPlan op, float strength)
        {
            if (op == null)
                return 1f;

            strength = Mathf.Max(0f, strength);
            float baseWeight = Mathf.Clamp(op.targetWeight, 0.01f, 1f);
            return Mathf.Clamp(Mathf.Lerp(1f, baseWeight, strength), 0.01f, 1f);
        }

        private static int SumInitialTriangles(List<CrowdingMeshSlotManifestRecord> slots)
        {
            int sum = 0;
            if (slots == null)
                return sum;

            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] != null && slots[i].supported)
                    sum += Mathf.Max(0, slots[i].initialTriangles);
            }
            return sum;
        }

        private static void AddBudgetItemsToVariant(
            CrowdingMeshVariantManifestRecord variant,
            List<VariantObjectBudgetItem> items)
        {
            if (variant == null || items == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                VariantObjectBudgetItem item = items[i];
                if (item == null || item.slots == null)
                    continue;

                float keep = Mathf.Clamp01(item.finalKeepRatio);

                for (int s = 0; s < item.slots.Count; s++)
                {
                    CrowdingMeshSlotManifestRecord slot = item.slots[s];
                    if (slot == null)
                        continue;

                    CrowdingMeshVariantSlotRecord rec = CreateVariantSlotRecord(
                        variant,
                        slot,
                        item.objectPlan,
                        keep,
                        item.assignedFixationIndex,
                        item.assignedFixationName,
                        item.groupId);

                    if (item.objectPlan == null)
                    {
                        rec.objectId = item.objectId;
                        rec.rootName = item.rootName;
                    }

                    variant.meshRecords.Add(rec);
                }
            }
        }

        private static CrowdingMeshVariantSlotRecord CreateVariantSlotRecord(
            CrowdingMeshVariantManifestRecord variant,
            CrowdingMeshSlotManifestRecord slot,
            CrowdingObjectDecimationPlan op,
            float keep,
            int assignedFixationIndex,
            string assignedFixationName,
            int groupId)
        {
            CrowdingMeshVariantSlotRecord rec = new CrowdingMeshVariantSlotRecord();
            rec.variantKey = variant.variantKey;
            rec.slotId = slot.slotId;
            rec.objectId = slot.objectId;
            rec.rootName = op != null && op.obj != null && op.obj.root != null ? op.obj.root.name : slot.rootName;
            rec.childPath = slot.childPath;
            rec.rendererType = slot.rendererType;
            rec.groupId = groupId;
            rec.role = op != null ? op.role.ToString() : "NonTarget";
            rec.assignedFixationIndex = assignedFixationIndex;
            rec.assignedFixationName = assignedFixationName ?? string.Empty;
            rec.keepRatio = Mathf.Clamp01(keep);
            rec.eccentricityDeg = op != null ? op.eccentricityDeg : 0f;
            rec.boumaMean = op != null ? op.boumaMean : 0f;
            rec.targetWeight = op != null ? op.targetWeight : 1f;
            rec.initialTriangles = slot.initialTriangles;
            rec.estimatedFinalTriangles = Mathf.RoundToInt(slot.initialTriangles * rec.keepRatio);
            return rec;
        }

        private static Transform GetFixationTransform(CrowdingAwarePipelineController controller, int fixationIndex)
        {
            if (controller != null && controller.fixationPoints != null &&
                fixationIndex >= 0 && fixationIndex < controller.fixationPoints.Count)
            {
                return controller.fixationPoints[fixationIndex];
            }

            return null;
        }

        private static Dictionary<int, Dictionary<string, CrowdingObjectDecimationPlan>> BuildPlanLookup(
            IList<CrowdingFixationPlan> plans)
        {
            Dictionary<int, Dictionary<string, CrowdingObjectDecimationPlan>> lookup =
                new Dictionary<int, Dictionary<string, CrowdingObjectDecimationPlan>>();

            if (plans == null)
                return lookup;

            for (int p = 0; p < plans.Count; p++)
            {
                CrowdingFixationPlan plan = plans[p];
                if (plan == null) continue;

                Dictionary<string, CrowdingObjectDecimationPlan> byObject = new Dictionary<string, CrowdingObjectDecimationPlan>();

                for (int g = 0; g < plan.groupPlans.Count; g++)
                {
                    CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                    if (gp == null) continue;

                    for (int i = 0; i < gp.objectPlans.Count; i++)
                    {
                        CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                        if (op == null || op.obj == null || string.IsNullOrWhiteSpace(op.obj.objectId)) continue;
                        byObject[op.obj.objectId] = op;
                    }
                }

                lookup[plan.fixationIndex] = byObject;
            }

            return lookup;
        }

        private static Dictionary<int, Dictionary<string, PlanObjectInfo>> BuildPlanInfoLookup(
            IList<CrowdingFixationPlan> plans)
        {
            Dictionary<int, Dictionary<string, PlanObjectInfo>> lookup =
                new Dictionary<int, Dictionary<string, PlanObjectInfo>>();

            if (plans == null)
                return lookup;

            for (int p = 0; p < plans.Count; p++)
            {
                CrowdingFixationPlan plan = plans[p];
                if (plan == null) continue;

                Dictionary<string, PlanObjectInfo> byObject = new Dictionary<string, PlanObjectInfo>();

                for (int g = 0; g < plan.groupPlans.Count; g++)
                {
                    CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                    if (gp == null) continue;

                    for (int i = 0; i < gp.objectPlans.Count; i++)
                    {
                        CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                        if (op == null || op.obj == null || string.IsNullOrWhiteSpace(op.obj.objectId)) continue;

                        PlanObjectInfo info = new PlanObjectInfo();
                        info.objectPlan = op;
                        info.groupId = gp.groupId;
                        info.fixationIndex = plan.fixationIndex;
                        byObject[op.obj.objectId] = info;
                    }
                }

                lookup[plan.fixationIndex] = byObject;
            }

            return lookup;
        }

        private static PlanObjectInfo FindFirstObjectPlanInfo(
            string objectId,
            IList<CrowdingFixationPlan> plans,
            out int fixationIndex)
        {
            fixationIndex = -1;

            if (string.IsNullOrWhiteSpace(objectId) || plans == null)
                return null;

            for (int p = 0; p < plans.Count; p++)
            {
                CrowdingFixationPlan plan = plans[p];
                if (plan == null) continue;

                for (int g = 0; g < plan.groupPlans.Count; g++)
                {
                    CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                    if (gp == null) continue;

                    for (int i = 0; i < gp.objectPlans.Count; i++)
                    {
                        CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                        if (op != null && op.obj != null && op.obj.objectId == objectId)
                        {
                            fixationIndex = plan.fixationIndex;
                            PlanObjectInfo info = new PlanObjectInfo();
                            info.objectPlan = op;
                            info.groupId = gp.groupId;
                            info.fixationIndex = plan.fixationIndex;
                            return info;
                        }
                    }
                }
            }

            return null;
        }

        private class VariantObjectBudgetItem
        {
            public string objectId;
            public string rootName;
            public object rootObject;
            public CrowdingObjectDecimationPlan objectPlan;
            public List<CrowdingMeshSlotManifestRecord> slots;
            public int groupId;
            public string groupKey;
            public int assignedFixationIndex;
            public string assignedFixationName;
            public int initialTriangles;
            public float angularAreaDeg2;
            public float crowdingQualityWeight;
            public float groupKeepRatio;
            public float finalKeepRatio;
        }

        private class BudgetGroup
        {
            public string groupKey;
            public int groupId;
            public List<VariantObjectBudgetItem> items = new List<VariantObjectBudgetItem>();
            public int totalTriangles;
            public float totalAngularAreaDeg2;
            public float triangleDensity;
            public float groupKeepRatio;
        }

        private class PlanObjectInfo
        {
            public CrowdingObjectDecimationPlan objectPlan;
            public int groupId;
            public int fixationIndex;
        }

        private static Dictionary<string, CrowdingObjectSignature> BuildSignatureLookup(CrowdingAwarePipelineController controller)
        {
            Dictionary<string, CrowdingObjectSignature> lookup = new Dictionary<string, CrowdingObjectSignature>();

            if (controller == null)
                return lookup;

            AddSignaturesToLookup(controller.Signatures, lookup);

            if (controller.FixationDebugData != null)
            {
                for (int i = 0; i < controller.FixationDebugData.Count; i++)
                {
                    CrowdingFixationDebugData debug = controller.FixationDebugData[i];
                    if (debug != null)
                        AddSignaturesToLookup(debug.signatures, lookup);
                }
            }

            return lookup;
        }

        private static void AddSignaturesToLookup(
            IList<CrowdingObjectSignature> signatures,
            Dictionary<string, CrowdingObjectSignature> lookup)
        {
            if (signatures == null)
                return;

            for (int i = 0; i < signatures.Count; i++)
            {
                CrowdingObjectSignature sig = signatures[i];
                if (sig == null || string.IsNullOrWhiteSpace(sig.objectId)) continue;
                if (!lookup.ContainsKey(sig.objectId))
                    lookup.Add(sig.objectId, sig);
            }
        }

        private static int FindNearestFixationIndexForObject(
            CrowdingObjectSignature sig,
            CrowdingAwarePipelineController controller)
        {
            if (controller == null || controller.viewCamera == null || controller.fixationPoints == null || controller.fixationPoints.Count == 0)
                return controller != null && controller.FixationPlans != null && controller.FixationPlans.Count > 0
                    ? controller.FixationPlans[0].fixationIndex
                    : -1;

            if (sig == null)
                return controller.FixationPlans != null && controller.FixationPlans.Count > 0
                    ? controller.FixationPlans[0].fixationIndex
                    : -1;

            Vector3 eye = controller.viewCamera.transform.position;
            Vector3 toObject = sig.worldCenter - eye;

            if (toObject.sqrMagnitude < 1e-8f)
                return controller.FixationPlans != null && controller.FixationPlans.Count > 0
                    ? controller.FixationPlans[0].fixationIndex
                    : -1;

            Vector3 objectDir = toObject.normalized;
            float bestAngle = float.PositiveInfinity;
            int bestIndex = -1;

            for (int i = 0; i < controller.fixationPoints.Count; i++)
            {
                Transform fixation = controller.fixationPoints[i];
                if (fixation == null)
                    continue;

                Vector3 toFixation = fixation.position - eye;
                if (toFixation.sqrMagnitude < 1e-8f)
                    continue;

                float angle = Vector3.Angle(objectDir, toFixation.normalized);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static CrowdingObjectDecimationPlan FindFirstObjectPlan(
            string objectId,
            IList<CrowdingFixationPlan> plans,
            out int fixationIndex)
        {
            fixationIndex = -1;

            if (string.IsNullOrWhiteSpace(objectId) || plans == null)
                return null;

            for (int p = 0; p < plans.Count; p++)
            {
                CrowdingFixationPlan plan = plans[p];
                if (plan == null) continue;

                for (int g = 0; g < plan.groupPlans.Count; g++)
                {
                    CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                    if (gp == null) continue;

                    for (int i = 0; i < gp.objectPlans.Count; i++)
                    {
                        CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                        if (op != null && op.obj != null && op.obj.objectId == objectId)
                        {
                            fixationIndex = plan.fixationIndex;
                            return op;
                        }
                    }
                }
            }

            return null;
        }

        private static string GetFixationName(CrowdingAwarePipelineController controller, int fixationIndex)
        {
            if (controller != null && controller.fixationPoints != null &&
                fixationIndex >= 0 && fixationIndex < controller.fixationPoints.Count &&
                controller.fixationPoints[fixationIndex] != null)
            {
                return controller.fixationPoints[fixationIndex].name;
            }

            return fixationIndex >= 0 ? "F" + fixationIndex.ToString("00") : string.Empty;
        }

        private static List<CrowdingMeshSlotManifestRecord> BuildAndExportMeshSlots(
            List<CrowdingObjectMarker> markers,
            Dictionary<string, CrowdingObjectSignature> signatureByObjectId,
            string sourceMeshFolder,
            CrowdingMeshLODBuildConfig config)
        {
            List<CrowdingMeshSlotManifestRecord> slots = new List<CrowdingMeshSlotManifestRecord>();

            for (int i = 0; i < markers.Count; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;

                MeshFilter[] filters = marker.GetComponentsInChildren<MeshFilter>(true);
                for (int f = 0; f < filters.Length; f++)
                {
                    MeshFilter mf = filters[f];
                    if (mf == null || mf.sharedMesh == null) continue;
                    slots.Add(BuildSlotAndExportObj(marker, mf.transform, "MeshFilter", mf.sharedMesh, sourceMeshFolder, config));
                }

                SkinnedMeshRenderer[] skins = marker.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int s = 0; s < skins.Length; s++)
                {
                    SkinnedMeshRenderer smr = skins[s];
                    if (smr == null || smr.sharedMesh == null) continue;
                    CrowdingMeshSlotManifestRecord slot = BuildSlotAndExportObj(marker, smr.transform, "SkinnedMeshRenderer", smr.sharedMesh, sourceMeshFolder, config);
                    if (config.skipSkinnedMeshes)
                    {
                        slot.supported = false;
                        slot.warning = "Skipped because skipSkinnedMeshes is enabled. Decimating skinned meshes safely requires preserving bindposes and bone weights.";
                    }
                    slots.Add(slot);
                }
            }

            return slots;
        }

        private static CrowdingMeshSlotManifestRecord BuildSlotAndExportObj(
            CrowdingObjectMarker marker,
            Transform child,
            string rendererType,
            Mesh mesh,
            string sourceMeshFolder,
            CrowdingMeshLODBuildConfig config)
        {
            string childPath = CrowdingMeshSlotUtility.GetRelativePath(marker.transform, child);
            string slotId = CrowdingMeshSlotUtility.BuildSlotId(marker.ObjectId, childPath, rendererType);
            string objPath = Path.Combine(sourceMeshFolder, slotId + ".obj");

            CrowdingMeshSlotManifestRecord slot = new CrowdingMeshSlotManifestRecord();
            slot.slotId = slotId;
            slot.objectId = marker.ObjectId;
            slot.rootName = marker.gameObject.name;
            slot.objectHierarchyPath = CrowdingObjectIdUtility.GetHierarchyPath(marker.transform);
            slot.childPath = childPath;
            slot.rendererType = rendererType;
            slot.unityMeshName = mesh.name;
            slot.unityMeshAssetPath = AssetDatabase.GetAssetPath(mesh);
            slot.unityMeshAssetGuid = string.IsNullOrEmpty(slot.unityMeshAssetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(slot.unityMeshAssetPath);
            slot.sourceObjPath = objPath;
            slot.initialTriangles = CountTriangles(mesh);
            slot.subMeshCount = mesh.subMeshCount;
            slot.supported = rendererType == "MeshFilter" || !config.skipSkinnedMeshes;

            if (slot.supported)
            {
                if (config.overwriteExistingSourceObjs || !File.Exists(objPath))
                {
                    try
                    {
                        WriteMeshAsObjForBlender(mesh, objPath, slotId);
                    }
                    catch (Exception ex)
                    {
                        slot.supported = false;
                        slot.warning = "Failed to export source OBJ: " + ex.Message;
                        Debug.LogError("Failed to export source OBJ for " + slotId + ": " + ex);
                    }
                }
            }

            return slot;
        }

        private static void WriteMeshAsObjForBlender(Mesh mesh, string path, string objectName)
        {
            EnsureParentDirectory(path);
            using (StreamWriter sw = new StreamWriter(path))
            {
                sw.WriteLine("# Exported by Crowding Mesh-Only LOD Builder");
                sw.WriteLine("# Coordinates are converted Unity(x,y,z) -> Blender(x,z,-y).");
                sw.WriteLine("o " + CrowdingMeshSlotUtility.Sanitize(objectName));

                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uvs = mesh.uv;

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 v = vertices[i];
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", v.x, v.z, -v.y));
                }

                if (uvs != null && uvs.Length == vertices.Length)
                {
                    for (int i = 0; i < uvs.Length; i++)
                    {
                        Vector2 uv = uvs[i];
                        sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "vt {0} {1}", uv.x, uv.y));
                    }
                }

                if (normals != null && normals.Length == vertices.Length)
                {
                    for (int i = 0; i < normals.Length; i++)
                    {
                        Vector3 n = normals[i];
                        sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "vn {0} {1} {2}", n.x, n.z, -n.y));
                    }
                }

                bool hasUv = uvs != null && uvs.Length == vertices.Length;
                bool hasNormals = normals != null && normals.Length == vertices.Length;

                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    sw.WriteLine("g submesh_" + sm);
                    sw.WriteLine("usemtl material_" + sm);
                    int[] tris = mesh.GetTriangles(sm);
                    for (int t = 0; t < tris.Length; t += 3)
                    {
                        int a = tris[t] + 1;
                        int b = tris[t + 1] + 1;
                        int c = tris[t + 2] + 1;
                        sw.WriteLine("f " + ObjIndex(a, hasUv, hasNormals) + " " + ObjIndex(b, hasUv, hasNormals) + " " + ObjIndex(c, hasUv, hasNormals));
                    }
                }
            }
        }

        private static string ObjIndex(int index, bool hasUv, bool hasNormal)
        {
            if (hasUv && hasNormal) return index + "/" + index + "/" + index;
            if (hasUv) return index + "/" + index;
            if (hasNormal) return index + "//" + index;
            return index.ToString(CultureInfo.InvariantCulture);
        }

        private static bool BuildMeshVariantLibrary(CrowdingMeshLODBuildConfig config)
        {
            string importMapPath = ResolveProjectPath(config.blenderImportMapRelativePath);
            if (!File.Exists(importMapPath))
            {
                Debug.LogError("Blender import map not found: " + importMapPath);
                return false;
            }

            string mapJson = File.ReadAllText(importMapPath);
            CrowdingMeshImportMap importMap = JsonUtility.FromJson<CrowdingMeshImportMap>(mapJson);
            if (importMap == null || importMap.records == null || importMap.records.Count == 0)
            {
                Debug.LogError("Import map is empty or invalid: " + importMapPath);
                return false;
            }

            string libraryAssetPath = config.variantLibraryAssetPath;
            EnsureAssetFolder(Path.GetDirectoryName(libraryAssetPath));

            CrowdingMeshVariantLibrary library = AssetDatabase.LoadAssetAtPath<CrowdingMeshVariantLibrary>(libraryAssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<CrowdingMeshVariantLibrary>();
                AssetDatabase.CreateAsset(library, libraryAssetPath);
            }

            library.generatedFromManifest = config.meshManifestRelativePath;
            library.generatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            library.entries.Clear();

            int createdMeshes = 0;
            int missingJson = 0;

            for (int i = 0; i < importMap.records.Count; i++)
            {
                CrowdingMeshImportMapRecord rec = importMap.records[i];
                string jsonPath = rec.exportedFbxPath; // In mesh-only v5 this field stores the decimated mesh JSON path for backward compatibility.
                if (!Path.IsPathRooted(jsonPath))
                    jsonPath = ResolveProjectPath(jsonPath);

                if (!File.Exists(jsonPath))
                {
                    missingJson++;
                    Debug.LogWarning("Missing decimated mesh JSON: " + jsonPath);
                    continue;
                }

                Mesh mesh = CreateMeshAssetFromJson(jsonPath, config, rec);
                if (mesh == null)
                    continue;

                CrowdingMeshVariantEntry entry = new CrowdingMeshVariantEntry();
                entry.variantKey = rec.variantKey;
                entry.slotId = rec.slotId;
                entry.objectId = rec.objectId;
                entry.rootName = rec.rootName;
                entry.childPath = rec.childPath;
                entry.rendererType = rec.rendererType;
                entry.requestedKeepRatio = rec.requestedKeepRatio;
                entry.initialTriangles = rec.initialTriangles;
                entry.finalTriangles = rec.finalTriangles;
                entry.achievedKeepRatio = rec.achievedKeepRatio;
                entry.decimatedMesh = mesh;
                library.entries.Add(entry);
                createdMeshes++;
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (config.assignLibraryToSelector)
            {
                if (config.meshSelector == null)
                    config.meshSelector = config.GetComponent<CrowdingSharedMeshLODSelector>();
                if (config.meshSelector == null)
                    config.meshSelector = Undo.AddComponent<CrowdingSharedMeshLODSelector>(config.gameObject);
                config.meshSelector.variantLibrary = library;
                EditorUtility.SetDirty(config.meshSelector);
            }

            Debug.Log("Mesh variant library built: " + libraryAssetPath + "\nEntries=" + library.entries.Count + ", meshes created/updated=" + createdMeshes + ", missing JSON=" + missingJson);
            return true;
        }

        private static string GetGeneratedMeshAssetRoot(CrowdingMeshLODBuildConfig config)
        {
            // Mesh .asset files produced from Blender JSON must be written inside Assets.
            // Use the configured output folder instead of the old global MeshAssets folder.
            string folder = (config != null ? config.blenderOutputRelativeFolder : string.Empty) ?? string.Empty;
            folder = folder.Replace("\\", "/").Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith("Assets/"))
            {
                string libraryFolder = config != null ? Path.GetDirectoryName(config.variantLibraryAssetPath) : null;
                if (string.IsNullOrWhiteSpace(libraryFolder))
                    libraryFolder = "Assets/CrowdingGeneratedLODs";

                folder = (libraryFolder.Replace("\\", "/").TrimEnd('/') + "/Meshes").Replace("\\", "/");
                Debug.LogWarning(
                    "Generated Mesh assets must be written inside Assets/. " +
                    "Using: " + folder);
            }

            return folder;
        }

        private static Mesh CreateMeshAssetFromJson(string jsonPath, CrowdingMeshLODBuildConfig config, CrowdingMeshImportMapRecord rec)
        {
            string json = File.ReadAllText(jsonPath);
            CrowdingDecimatedMeshJson meshJson = JsonUtility.FromJson<CrowdingDecimatedMeshJson>(json);
            if (meshJson == null || meshJson.vertices == null || meshJson.vertices.Count < 3 || meshJson.subMeshes == null || meshJson.subMeshes.Count == 0)
            {
                Debug.LogWarning("Invalid decimated mesh JSON: " + jsonPath);
                return null;
            }

            Mesh mesh = new Mesh();
            mesh.name = CrowdingMeshSlotUtility.Sanitize(rec.variantKey + "__" + rec.slotId);

            int vertexCount = meshJson.vertices.Count / 3;
            if (vertexCount > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            Vector3[] vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = new Vector3(meshJson.vertices[i * 3], meshJson.vertices[i * 3 + 1], meshJson.vertices[i * 3 + 2]);
            }
            mesh.vertices = vertices;

            if (meshJson.uv != null && meshJson.uv.Count >= vertexCount * 2)
            {
                Vector2[] uv = new Vector2[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                    uv[i] = new Vector2(meshJson.uv[i * 2], meshJson.uv[i * 2 + 1]);
                mesh.uv = uv;
            }

            mesh.subMeshCount = meshJson.subMeshes.Count;
            for (int sm = 0; sm < meshJson.subMeshes.Count; sm++)
            {
                List<int> tris = meshJson.subMeshes[sm].triangles;
                mesh.SetTriangles(tris != null ? tris.ToArray() : new int[0], sm);
            }

            mesh.RecalculateNormals();
            if (mesh.uv != null && mesh.uv.Length == vertexCount)
                mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            string meshAssetRoot = GetGeneratedMeshAssetRoot(config);
            string folder = Path.Combine(meshAssetRoot, CrowdingMeshSlotUtility.Sanitize(rec.variantKey)).Replace("\\", "/");
            EnsureAssetFolder(folder);
            string meshAssetPath = Path.Combine(folder, CrowdingMeshSlotUtility.Sanitize(rec.slotId) + ".asset").Replace("\\", "/");

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(mesh, meshAssetPath);
            return mesh;
        }

        private static bool RunBlender(CrowdingMeshLODBuildConfig config, string manifestPath, string outputFolder, string importMapPath, string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(config.blenderExecutablePath))
            {
                Debug.LogError("Blender executable path is empty.");
                return false;
            }

            if (!File.Exists(scriptPath))
            {
                Debug.LogError("Blender mesh script not found: " + scriptPath);
                return false;
            }

            if (!File.Exists(manifestPath))
            {
                Debug.LogError("Mesh manifest not found: " + manifestPath);
                return false;
            }

            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
            EnsureParentDirectory(importMapPath);

            string args = (config.runBlenderInBackground ? "--background " : string.Empty) +
                          "--python \"" + scriptPath + "\" -- " +
                          "\"" + manifestPath + "\" " +
                          "\"" + outputFolder + "\" " +
                          "\"" + importMapPath + "\"";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = config.blenderExecutablePath;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            if (config.verboseLogs)
                Debug.Log("Launching Blender mesh decimation:\n" + psi.FileName + " " + psi.Arguments);

            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();

                    if (config.waitForBlenderToFinish)
                    {
                        string stdout = p.StandardOutput.ReadToEnd();
                        string stderr = p.StandardError.ReadToEnd();
                        p.WaitForExit();

                        if (!string.IsNullOrWhiteSpace(stdout)) Debug.Log("Blender stdout:\n" + stdout);
                        if (!string.IsNullOrWhiteSpace(stderr)) Debug.LogWarning("Blender stderr:\n" + stderr);

                        if (p.ExitCode != 0)
                        {
                            Debug.LogError("Blender mesh decimation failed with exit code " + p.ExitCode);
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to launch Blender: " + ex);
                return false;
            }

            if (!File.Exists(importMapPath))
            {
                Debug.LogError("Blender finished but did not produce import map: " + importMapPath);
                return false;
            }

            return true;
        }

        private static bool ValidateConfig(CrowdingMeshLODBuildConfig config)
        {
            if (config == null) return false;

            if (config.controller == null)
                config.controller = config.GetComponent<CrowdingAwarePipelineController>();

            if (config.controller == null)
            {
                Debug.LogError("CrowdingMeshLODBuildConfig requires a CrowdingAwarePipelineController.", config);
                return false;
            }

            if (config.keepRatiosK == null || config.keepRatiosK.Count == 0)
            {
                Debug.LogError("At least one K value is required.", config);
                return false;
            }

            if (!config.generateUniformCondition && !config.generateWeightedCondition)
            {
                Debug.LogError("At least one condition must be enabled.", config);
                return false;
            }

            if (!config.useAllControllerFixationPoints && config.fixationPointCountToUse <= 0)
            {
                Debug.LogError("fixationPointCountToUse must be >= 1.", config);
                return false;
            }

            return true;
        }

        private static void AddMarkersFromController(CrowdingAwarePipelineController controller)
        {
            List<GameObject> roots = CrowdingObjectScanner.FindObjectRoots(
                controller.discoveryMode,
                controller.objectTag,
                controller.objectLayerMask,
                controller.includeInactiveObjects);

            int added = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                GameObject root = roots[i];
                if (root == null) continue;
                CrowdingObjectMarker marker = root.GetComponent<CrowdingObjectMarker>();
                if (marker == null)
                {
                    marker = Undo.AddComponent<CrowdingObjectMarker>(root);
                    marker.semanticName = CrowdingObjectIdUtility.RemoveCidSuffix(root.name);
                    marker.EnsureObjectId();
                    added++;
                }
                else marker.EnsureObjectId();
            }
            AssetDatabase.SaveAssets();
            Debug.Log("Markers prepared. Added: " + added + ", roots found: " + roots.Count);
        }

        private static void FixMissingAndDuplicateIds()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Dictionary<string, CrowdingObjectMarker> seen = new Dictionary<string, CrowdingObjectMarker>();
            int fixedCount = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;
                marker.EnsureObjectId();
                string id = marker.ObjectId;
                if (!CrowdingObjectIdUtility.IsValidObjectId(id) || seen.ContainsKey(id))
                {
                    marker.RegenerateObjectId();
                    fixedCount++;
                    id = marker.ObjectId;
                }
                if (!seen.ContainsKey(id)) seen.Add(id, marker);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Object ID repair finished. Regenerated IDs: " + fixedCount);
        }

        private static bool ValidateMarkers(out List<CrowdingObjectMarker> includedMarkers)
        {
            includedMarkers = new List<CrowdingObjectMarker>();
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Dictionary<string, CrowdingObjectMarker> byId = new Dictionary<string, CrowdingObjectMarker>();
            int errors = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;
                string id = marker.ObjectId;
                if (!CrowdingObjectIdUtility.IsValidObjectId(id))
                {
                    Debug.LogError("Invalid objectId on " + CrowdingObjectIdUtility.GetHierarchyPath(marker.transform), marker);
                    errors++;
                    continue;
                }
                if (byId.ContainsKey(id))
                {
                    Debug.LogError("Duplicate objectId " + id + " on " + marker.name + " and " + byId[id].name, marker);
                    errors++;
                    continue;
                }
                byId.Add(id, marker);
                includedMarkers.Add(marker);
            }

            if (includedMarkers.Count == 0)
            {
                Debug.LogError("No included CrowdingObjectMarker found.");
                return false;
            }
            return errors == 0;
        }

        private static int CountTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += mesh.GetTriangles(i).Length / 3;
            return count;
        }

        private static CrowdingMeshLODBuildConfig ResolveConfigFromSelectionOrScene()
        {
            if (Selection.activeGameObject != null)
            {
                CrowdingMeshLODBuildConfig selected = Selection.activeGameObject.GetComponent<CrowdingMeshLODBuildConfig>();
                if (selected != null) return selected;
            }

            return Object.FindFirstObjectByType<CrowdingMeshLODBuildConfig>();
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ResolveProjectPath(string relativeOrAbsolute)
        {
            if (Path.IsPathRooted(relativeOrAbsolute)) return Path.GetFullPath(relativeOrAbsolute);
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), relativeOrAbsolute));
        }

        private static void EnsureParentDirectory(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder)) return;
            assetFolder = assetFolder.Replace("\\", "/");
            if (AssetDatabase.IsValidFolder(assetFolder)) return;

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
