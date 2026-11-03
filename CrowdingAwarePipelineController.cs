using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Main entry point. Put it on an empty GameObject in the scene.
    ///
    /// New PerFixationLookAt behavior:
    /// - for each fixation point, a virtual view is created at the reference camera position
    ///   and rotated toward the fixation point;
    /// - signatures, spatial clusters, groups, targets/flankers and weights are rebuilt for that view;
    /// - debug data are stored per fixation index.
    /// </summary>
    public class CrowdingAwarePipelineController : MonoBehaviour
    {
        [Header("Scene references")]
        public Camera viewCamera;
        public List<Transform> fixationPoints = new List<Transform>();

        [Header("Object discovery")]
        public CrowdingObjectDiscoveryMode discoveryMode = CrowdingObjectDiscoveryMode.TagOrLayer;
        public string objectTag = "Objet";
        public LayerMask objectLayerMask;
        public bool includeInactiveObjects = false;

        [Header("Semantic similarity")]
        public TextAsset semanticSimilarityJson;
        [Range(0f, 1f)] public float fallbackSemanticSimilarity = 0.5f;

        [Header("Pipeline settings")]
        public CrowdingSegmentationSettings segmentationSettings = new CrowdingSegmentationSettings();
        public CrowdingTargetSettings targetSettings = new CrowdingTargetSettings();
        public CrowdingWeightSettings weightSettings = new CrowdingWeightSettings();
        public CrowdingBudgetSettings budgetSettings = new CrowdingBudgetSettings();
        public CrowdingDebugSettings debugSettings = new CrowdingDebugSettings();

        [Header("Application")]
        public DecimationApplyMode applyMode = DecimationApplyMode.ComputeOnly;
        public bool runOnStart = false;
        public bool applyFirstFixationOnStart = false;

        private readonly List<CrowdingObjectSignature> _signatures = new List<CrowdingObjectSignature>();
        private readonly List<CrowdingGroupData> _groups = new List<CrowdingGroupData>();
        private readonly List<CrowdingFixationPlan> _fixationPlans = new List<CrowdingFixationPlan>();
        private readonly List<CrowdingFixationDebugData> _fixationDebugData = new List<CrowdingFixationDebugData>();

        public List<CrowdingObjectSignature> Signatures { get { return _signatures; } }
        public List<CrowdingGroupData> Groups { get { return _groups; } }
        public List<CrowdingFixationPlan> FixationPlans { get { return _fixationPlans; } }
        public List<CrowdingFixationDebugData> FixationDebugData { get { return _fixationDebugData; } }

        private void Reset()
        {
            viewCamera = Camera.main;
            segmentationSettings.maxDepthSpanArcsec = 600f;
            segmentationSettings.maxPairwiseDepthArcsec = 600f;
            segmentationSettings.groupingReferenceMode = CrowdingGroupingReferenceMode.PerFixationLookAt;
        }

        private void Start()
        {
            if (runOnStart)
            {
                RunPipeline();
                if (applyFirstFixationOnStart && _fixationPlans.Count > 0)
                    ApplyFixationPlan(_fixationPlans[0].fixationIndex);
            }
        }

        [ContextMenu("Crowding Pipeline/Run Full Pipeline")]
        public void RunPipeline()
        {
            if (!ResolveCamera())
                return;

            _signatures.Clear();
            _groups.Clear();
            _fixationPlans.Clear();
            _fixationDebugData.Clear();

            if (segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt)
                BuildPerFixationPipeline(refreshPublicSignaturesAndGroups: true);
            else
                BuildFixedReferencePipeline();

            if (debugSettings.createDebugGroupObjects)
                CreateDebugGroupObjects();

            if (debugSettings.addMemberComponents)
                UpdateMemberComponents(-1, null);

            EnsureDebugDrawers();

            if (debugSettings.logSummary)
                Debug.Log(BuildSummaryString());
        }

        private bool ResolveCamera()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (viewCamera == null)
            {
                Debug.LogError("CrowdingAwarePipelineController: no camera assigned and Camera.main not found.");
                return false;
            }

            return true;
        }

        private void BuildFixedReferencePipeline()
        {
            SemanticSimilarityMatrix semanticMatrix = SemanticSimilarityMatrix.FromJson(semanticSimilarityJson, fallbackSemanticSimilarity);

            List<CrowdingObjectSignature> signatures = CrowdingObjectScanner.FindAndBuildSignatures(
                viewCamera, discoveryMode, objectTag, objectLayerMask, includeInactiveObjects);
            _signatures.AddRange(signatures);

            List<CrowdingGroupData> groups = CrowdingSegmentation.Segment(_signatures, segmentationSettings, semanticMatrix);
            _groups.AddRange(groups);

            TargetFlankerSelector.SelectTargetsAndFlankers(_groups, targetSettings);
            BuildAllFixationPlans();
        }

        private void BuildPerFixationPipeline(bool refreshPublicSignaturesAndGroups)
        {
            if (!ResolveCamera())
                return;

            _fixationPlans.Clear();
            _fixationDebugData.Clear();

            if (refreshPublicSignaturesAndGroups)
            {
                _signatures.Clear();
                _groups.Clear();
            }

            SemanticSimilarityMatrix semanticMatrix = SemanticSimilarityMatrix.FromJson(semanticSimilarityJson, fallbackSemanticSimilarity);
            List<Transform> fixations = GetEffectiveFixationPoints();

            if (fixations.Count == 0)
            {
                CrowdingViewReference defaultView = CrowdingGeometryUtils.ViewFromCamera(viewCamera, "DefaultCameraForward");
                BuildOneFixationView(
                    0,
                    null,
                    viewCamera.transform.position + viewCamera.transform.forward * 10f,
                    defaultView,
                    semanticMatrix,
                    refreshPublicSignaturesAndGroups
                );
                return;
            }

            for (int i = 0; i < fixations.Count; i++)
            {
                Transform fixation = fixations[i];
                if (fixation == null)
                    continue;

                CrowdingViewReference viewRef = BuildViewForFixation(i, fixation);
                BuildOneFixationView(
                    i,
                    fixation,
                    fixation.position,
                    viewRef,
                    semanticMatrix,
                    refreshPublicSignaturesAndGroups && _signatures.Count == 0
                );
            }
        }

        private void BuildOneFixationView(
            int fixationIndex,
            Transform fixationTransform,
            Vector3 fixationWorldPosition,
            CrowdingViewReference viewRef,
            SemanticSimilarityMatrix semanticMatrix,
            bool copyToPublicSignaturesAndGroups)
        {
            List<CrowdingObjectSignature> signatures = CrowdingObjectScanner.FindAndBuildSignatures(
                viewRef,
                discoveryMode,
                objectTag,
                objectLayerMask,
                includeInactiveObjects);

            List<CrowdingGroupData> groups = CrowdingSegmentation.Segment(signatures, segmentationSettings, semanticMatrix);
            TargetFlankerSelector.SelectTargetsAndFlankers(groups, targetSettings);

            CrowdingFixationPlan plan = CrowdingWeightCalculator.BuildFixationPlanFromPrecomputedAngular(
                fixationIndex,
                fixationTransform,
                fixationWorldPosition,
                groups,
                weightSettings,
                budgetSettings);

            _fixationPlans.Add(plan);

            CrowdingFixationDebugData debug = new CrowdingFixationDebugData();
            debug.fixationIndex = fixationIndex;
            debug.fixationName = fixationTransform != null ? fixationTransform.name : "DefaultCameraForward";
            debug.eyePosition = viewRef.eyePosition;
            debug.viewRotation = viewRef.viewRotation;
            debug.fixationWorldPosition = fixationWorldPosition;
            debug.signatures.AddRange(signatures);
            CopySpatialClusters(CrowdingSegmentation.LastSpatialClusters, debug.spatialClusters);
            debug.groups.AddRange(groups);
            _fixationDebugData.Add(debug);

            if (copyToPublicSignaturesAndGroups)
            {
                _signatures.Clear();
                _signatures.AddRange(signatures);

                _groups.Clear();
                _groups.AddRange(groups);
            }
        }

        private CrowdingViewReference BuildViewForFixation(int fixationIndex, Transform fixation)
        {
            Vector3 up = segmentationSettings.perFixationUseReferenceCameraUp && viewCamera != null
                ? viewCamera.transform.up
                : Vector3.up;

            return CrowdingGeometryUtils.LookAtView(
                viewCamera.transform.position,
                fixation.position,
                up,
                "F" + fixationIndex.ToString("00") + "_LookAt_" + fixation.name
            );
        }

        private List<Transform> GetEffectiveFixationPoints()
        {
            List<Transform> valid = new List<Transform>();

            if (fixationPoints == null)
                return valid;

            for (int i = 0; i < fixationPoints.Count; i++)
            {
                if (fixationPoints[i] != null)
                    valid.Add(fixationPoints[i]);
            }

            return valid;
        }

        private static void CopySpatialClusters(
            List<List<CrowdingObjectSignature>> source,
            List<List<CrowdingObjectSignature>> destination)
        {
            destination.Clear();
            if (source == null) return;

            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null) continue;
                destination.Add(new List<CrowdingObjectSignature>(source[i]));
            }
        }

        [ContextMenu("Crowding Pipeline/Rebuild Fixation Plans Only")]
        public void BuildAllFixationPlans()
        {
            if (!ResolveCamera())
                return;

            if (segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt)
            {
                BuildPerFixationPipeline(refreshPublicSignaturesAndGroups: false);
                return;
            }

            _fixationPlans.Clear();
            if (_groups.Count == 0 || viewCamera == null) return;

            if (fixationPoints == null || fixationPoints.Count == 0)
            {
                Debug.LogWarning("No fixation points assigned. A default fixation direction in front of the camera will be used.");
                CrowdingFixationPlan plan = CrowdingWeightCalculator.BuildFixationPlan(0, null, viewCamera, _groups, weightSettings, budgetSettings);
                _fixationPlans.Add(plan);
                return;
            }

            for (int i = 0; i < fixationPoints.Count; i++)
            {
                if (fixationPoints[i] == null) continue;
                CrowdingFixationPlan plan = CrowdingWeightCalculator.BuildFixationPlan(i, fixationPoints[i], viewCamera, _groups, weightSettings, budgetSettings);
                _fixationPlans.Add(plan);
            }
        }

        public void ApplyFixationPlan(int fixationIndex)
        {
            CrowdingFixationPlan plan = GetFixationPlan(fixationIndex);
            if (plan == null)
            {
                Debug.LogWarning("No fixation plan found for fixation index " + fixationIndex);
                return;
            }

            for (int g = 0; g < plan.groupPlans.Count; g++)
            {
                CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                for (int i = 0; i < gp.objectPlans.Count; i++)
                {
                    CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                    if (op.obj == null || op.obj.root == null) continue;

                    if (applyMode == DecimationApplyMode.ApplyLODSetIfPresent)
                    {
                        CrowdingLODSet lodSet = op.obj.root.GetComponent<CrowdingLODSet>();
                        if (lodSet != null) lodSet.ApplyKeepRatio(op.keepRatio);
                    }
                }
            }

            UpdateMemberComponents(fixationIndex, plan);

            if (debugSettings.logSummary)
                Debug.Log(BuildFixationPlanSummary(plan));
        }

        public CrowdingFixationPlan GetFixationPlan(int fixationIndex)
        {
            for (int i = 0; i < _fixationPlans.Count; i++)
            {
                if (_fixationPlans[i].fixationIndex == fixationIndex) return _fixationPlans[i];
            }
            return null;
        }

        public CrowdingFixationDebugData GetFixationDebugData(int fixationIndex)
        {
            for (int i = 0; i < _fixationDebugData.Count; i++)
            {
                if (_fixationDebugData[i].fixationIndex == fixationIndex)
                    return _fixationDebugData[i];
            }

            if (_fixationDebugData.Count > 0)
                return _fixationDebugData[0];

            return null;
        }

        private List<CrowdingGroupData> GetGroupsForFixationOrDefault(int fixationIndex)
        {
            CrowdingFixationDebugData debug = GetFixationDebugData(fixationIndex);
            if (debug != null && debug.groups != null && debug.groups.Count > 0)
                return debug.groups;

            return _groups;
        }

        private void UpdateMemberComponents(int fixationIndex, CrowdingFixationPlan activePlan)
        {
            Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan> active = new Dictionary<CrowdingObjectSignature, CrowdingObjectDecimationPlan>();
            if (activePlan != null)
            {
                for (int g = 0; g < activePlan.groupPlans.Count; g++)
                {
                    for (int i = 0; i < activePlan.groupPlans[g].objectPlans.Count; i++)
                    {
                        CrowdingObjectDecimationPlan op = activePlan.groupPlans[g].objectPlans[i];
                        if (op != null && op.obj != null) active[op.obj] = op;
                    }
                }
            }

            List<CrowdingGroupData> groupsToUpdate = fixationIndex >= 0
                ? GetGroupsForFixationOrDefault(fixationIndex)
                : _groups;

            for (int g = 0; g < groupsToUpdate.Count; g++)
            {
                CrowdingGroupData group = groupsToUpdate[g];
                HashSet<CrowdingObjectSignature> targetSet = new HashSet<CrowdingObjectSignature>(group.targets);

                for (int m = 0; m < group.members.Count; m++)
                {
                    CrowdingObjectSignature sig = group.members[m];
                    if (sig == null || sig.root == null) continue;

                    CrowdingGroupMember member = sig.root.GetComponent<CrowdingGroupMember>();
                    if (member == null) member = sig.root.AddComponent<CrowdingGroupMember>();

                    member.groupId = group.groupId;
                    member.objectId = sig.objectId;
                    member.semanticName = sig.semanticName;
                    member.initialTriangles = sig.triangleCount;
                    member.baseRole = targetSet.Contains(sig) ? CrowdingRole.Target : CrowdingRole.NonTarget;

                    CrowdingObjectDecimationPlan op;
                    if (active.TryGetValue(sig, out op))
                    {
                        member.fixationIndex = fixationIndex;
                        member.lastEccentricityDeg = op.eccentricityDeg;
                        member.lastBoumaMean = op.boumaMean;
                        member.lastTargetWeight = op.targetWeight;
                        member.lastKeepRatio = op.keepRatio;
                        member.baseRole = op.role;
                    }
                }
            }
        }

        private void CreateDebugGroupObjects()
        {
            Transform oldRoot = transform.Find("CrowdingGroupsRoot");
            if (oldRoot != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(oldRoot.gameObject);
                else Destroy(oldRoot.gameObject);
#else
                Destroy(oldRoot.gameObject);
#endif
            }

            GameObject root = new GameObject("CrowdingGroupsRoot");
            root.transform.SetParent(transform, false);

            for (int g = 0; g < _groups.Count; g++)
            {
                CrowdingGroupData group = _groups[g];
                GameObject groupGo = new GameObject("CrowdingGroup_" + group.groupId.ToString("000"));
                groupGo.transform.SetParent(root.transform, false);

                CrowdingGroupComponent comp = groupGo.AddComponent<CrowdingGroupComponent>();
                comp.groupId = group.groupId;
                comp.memberCount = group.members.Count;
                comp.totalTriangles = group.TotalTriangles;
                comp.maxDepthSpanArcsec = segmentationSettings.maxDepthSpanArcsec;

                for (int m = 0; m < group.members.Count; m++)
                {
                    CrowdingObjectSignature sig = group.members[m];
                    if (sig == null || sig.root == null) continue;
                    comp.members.Add(sig.root);
                    if (debugSettings.reparentObjectsUnderDebugGroups)
                        sig.root.transform.SetParent(groupGo.transform, true);
                }
            }
        }

        private void EnsureDebugDrawers()
        {
            if (!debugSettings.drawGizmos) return;

            CrowdingDebugDrawer drawer = GetComponent<CrowdingDebugDrawer>();
            if (drawer == null) drawer = gameObject.AddComponent<CrowdingDebugDrawer>();
            drawer.controller = this;

            CrowdingSpatialClusterDebugDrawer clusterDrawer = GetComponent<CrowdingSpatialClusterDebugDrawer>();
            if (clusterDrawer == null) clusterDrawer = gameObject.AddComponent<CrowdingSpatialClusterDebugDrawer>();
            clusterDrawer.controller = this;
        }

        private string BuildSummaryString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Crowding-aware pipeline summary");
            sb.AppendLine("Grouping reference mode: " + segmentationSettings.groupingReferenceMode);
            sb.AppendLine("Objects found in public signature set: " + _signatures.Count);
            sb.AppendLine("Groups in public debug set: " + _groups.Count);
            sb.AppendLine("Depth span limit: " + segmentationSettings.maxDepthSpanArcsec + " arcsec");
            sb.AppendLine("Pairwise depth limit: " + segmentationSettings.maxPairwiseDepthArcsec + " arcsec");
            sb.AppendLine("Semantic similarity: " + (segmentationSettings.useSemanticSimilarity ? "ON" : "OFF"));
            sb.AppendLine("Fixation plans: " + _fixationPlans.Count);
            sb.AppendLine("Fixation debug sets: " + _fixationDebugData.Count);

            if (segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt)
            {
                for (int f = 0; f < _fixationDebugData.Count; f++)
                {
                    CrowdingFixationDebugData d = _fixationDebugData[f];
                    sb.AppendLine(string.Format("  F{0:00} {1}: signatures={2}, spatialClusters={3}, groups={4}",
                        d.fixationIndex, d.fixationName, d.signatures.Count, d.spatialClusters.Count, d.groups.Count));
                }
            }
            else
            {
                for (int i = 0; i < _groups.Count; i++)
                {
                    CrowdingGroupData g = _groups[i];
                    sb.AppendLine(string.Format("  G{0}: members={1}, targets={2}, triangles={3}", g.groupId, g.members.Count, g.targets.Count, g.TotalTriangles));
                }
            }

            return sb.ToString();
        }

        private string BuildFixationPlanSummary(CrowdingFixationPlan plan)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Applied crowding decimation plan for fixation index " + plan.fixationIndex);
            if (plan.fixationTransform != null) sb.AppendLine("Fixation object: " + plan.fixationTransform.name);

            for (int g = 0; g < plan.groupPlans.Count; g++)
            {
                CrowdingGroupDecimationPlan gp = plan.groupPlans[g];
                sb.AppendLine(string.Format(
                    "  G{0}: requested K={1:F2}, effective K={2:F2}, tris {3}->{4}",
                    gp.groupId, gp.requestedGroupKeepRatio, gp.effectiveGroupKeepRatio, gp.InitialTriangles, gp.EstimatedFinalTriangles));

                for (int i = 0; i < gp.objectPlans.Count; i++)
                {
                    CrowdingObjectDecimationPlan op = gp.objectPlans[i];
                    sb.AppendLine(string.Format(
                        "    {0} | {1} | E={2:F2} deg | B={3:F3} | w={4:F2} | keep={5:F2}",
                        op.obj.semanticName, op.role, op.eccentricityDeg, op.boumaMean, op.targetWeight, op.keepRatio));
                }
            }
            return sb.ToString();
        }
    }
}
