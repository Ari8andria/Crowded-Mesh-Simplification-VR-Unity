using System;
using UnityEngine;

namespace CrowdingAwareLOD
{

    public enum CrowdingSizeSimilarityMode
    {
        /// <summary>
        /// Previous behavior. Uses apparentRadiusDeg stored in CrowdingObjectSignature.
        /// This keeps old scenes and old grouping behavior unchanged unless another mode is selected.
        /// </summary>
        LegacyApparentRadiusDeg = 0,

        /// <summary>
        /// Compares the 2D angular footprint of the full object hierarchy from a reference view.
        /// Uses apparent angular area and apparent aspect ratio. Recommended for avoiding
        /// absurd matches between horizontally elongated and vertically elongated objects.
        /// </summary>
        AngularFootprintWidthHeight = 1
    }

    public enum CrowdingSegmentationMode
    {
        DepthFirst,
        SpatialClusteringFirst
    }

    public enum CrowdingGroupingReferenceMode
    {
        FixedReferenceCamera,
        PerFixationLookAt
    }


    public enum CrowdingBudgetStrategy
    {
        /// <summary>
        /// Previous behavior: target objects are simplified according to their crowding weight,
        /// then flanker/non-target ratios are adjusted to keep the requested group triangle budget K.
        /// This makes uniform and weighted variants comparable at approximately equal total budget.
        /// </summary>
        RedistributeWithinGroup,

        /// <summary>
        /// New behavior: every object receives the baseline budget K, but targets receive an
        /// additional simplification according to their crowding weight. No compensation is applied
        /// to flankers/non-targets, so the weighted variant can have a lower total triangle count
        /// than the uniform variant.
        /// </summary>
        ExtraSimplifyTargetsOnly
    }

    public enum CrowdingTargetSelectionCriterion
    {
        RandomInFictiveGroup,
        MostTrianglesInFictiveGroup,

        // Selects the medoid of the fictive subgroup: the object whose sum of
        // angular distances to the other objects in the fictive subgroup is minimal.
        // For a subgroup of 3 objects, this tends to select the object located between
        // the two others when such a configuration exists.
        MostSurroundedInFictiveGroup
    }

    [Serializable]
    public class CrowdingSegmentationSettings
    {
        [Header("Reference view")]
        [Tooltip("FixedReferenceCamera keeps the old behavior: one global segmentation from the reference camera. PerFixationLookAt rebuilds signatures, clusters, groups, targets and flankers for each fixation point using a virtual camera looking at that fixation.")]
        public CrowdingGroupingReferenceMode groupingReferenceMode = CrowdingGroupingReferenceMode.PerFixationLookAt;

        [Tooltip("If true, the virtual per-fixation camera uses the reference camera up vector. If false, it uses Vector3.up.")]
        public bool perFixationUseReferenceCameraUp = true;

        [Header("Segmentation mode")]
        [Tooltip("DepthFirst keeps the previous behavior. SpatialClusteringFirst first groups objects by angular proximity in the current view, then refines by local depth and similarity.")]
        public CrowdingSegmentationMode segmentationMode = CrowdingSegmentationMode.SpatialClusteringFirst;

        [Header("Spatial clustering first")]
        [Tooltip("Angular epsilon. In DBSCAN-like mode, two objects are spatial neighbours when the empty angular gap between their approximate silhouettes is <= this value.")]
        public float spatialClusterEpsilonDeg = 1.5f;

        [Tooltip("Minimum number of neighbours required to form a dense cluster. Use 1 if you want isolated objects to become singleton clusters before the minObjectsPerGroup filtering.")]
        public int spatialClusterMinPts = 1;

        [Tooltip("If true, clustering uses the empty angular gap between approximate object silhouettes: center distance - radiusA - radiusB. If false, it uses center-to-center angular distance.")]
        public bool useObjectApparentRadiusForSpatialClustering = true;

        [Header("Local compatibility inside spatial clusters")]
        [Tooltip("If true, each spatial cluster is split into connected components using local pairwise constraints: depth and similarity.")]
        public bool refineSpatialClustersByLocalCompatibility = true;

        [Tooltip("Interpupillary distance in meters. 0.062 m is a conservative Vive Pro Eye value.")]
        public float ipdMeters = 0.062f;

        [Tooltip("Maximum pairwise binocular-disparity difference allowed between two neighbouring objects, in arcsec. Requested value: 600 arcsec.")]
        public float maxPairwiseDepthArcsec = 600f;

        [Tooltip("If true, local pairwise depth compatibility is required inside spatial clusters.")]
        public bool usePairwiseDepthCompatibility = true;

        [Tooltip("If true, local pairwise visual/semantic similarity is required inside spatial clusters.")]
        public bool usePairwiseSimilarityCompatibility = true;

        [Header("Legacy depth-first mode")]
        [Tooltip("Maximum binocular-disparity span inside one group, in arcsec. Used only by DepthFirst mode.")]
        public float maxDepthSpanArcsec = 600f;

        [Header("Similarity")]
        public bool useSemanticSimilarity = false;
        [Range(0f, 1f)] public float weightSize = 0.55f;
        [Range(0f, 1f)] public float weightColor = 0.25f;
        [Range(0f, 1f)] public float weightSemantic = 0.20f;
        [Tooltip("Similarity temperature in S = exp(-D/tau).")]
        public float tau = 0.55f;
        [Range(0f, 1f)] public float minSimilarityToJoin = 0.55f;

        [Header("Size similarity mode")]
        [Tooltip("LegacyApparentRadiusDeg keeps the previous behavior. AngularFootprintWidthHeight compares apparent 2D angular area and aspect ratio from a reference view.")]
        public CrowdingSizeSimilarityMode sizeSimilarityMode = CrowdingSizeSimilarityMode.LegacyApparentRadiusDeg;

        [Tooltip("Used only by AngularFootprintWidthHeight. If true, size similarity = areaSimilarity * aspectSimilarity. If false, size similarity = 0.7 * areaSimilarity + 0.3 * aspectSimilarity.")]
        public bool angularFootprintUseStrictProduct = true;

        [Tooltip("Optional camera used to compute the angular footprint. If null, Camera.main is used. The legacy behavior is used as a fallback when no camera exists.")]
        public Camera angularFootprintReferenceCamera;

        [Tooltip("Optional fixation point used to orient a virtual camera when computing angular footprints. If null, the reference camera rotation is used.")]
        public Transform angularFootprintReferenceFixationPoint;

        [Tooltip("Used only by AngularFootprintWidthHeight. If true, projected bounds corners behind the camera are ignored.")]
        public bool angularFootprintIgnoreCornersBehindCamera = true;

        [Header("Safety filters")]
        public bool rejectIfSizeRatioAboveLimit = true;
        public float maxSizeRatio = 1.5f;
        public int minObjectsPerGroup = 3;

        [Header("Debug")]
        public bool logSpatialClusteringSummary = true;
    }

    [Serializable]
    public class CrowdingTargetSettings
    {
        [Tooltip("Reference target ratio. 1/3 means roughly two flankers for one target.")]
        public float targetRatio = 1f / 3f;

        [Tooltip("Groups smaller than this will not receive targets.")]
        public int minGroupSizeForTargets = 4;

        [Tooltip("Number of nearest non-targets used to compute the Bouma mean for each target.")]
        public int flankersPerTarget = 4;

        public SpatialTargetDistributionMode distributionMode = SpatialTargetDistributionMode.HorizontalColumns;

        [Tooltip("How to choose the target inside each fictive spatial subgroup. RandomInFictiveGroup keeps the previous behavior. MostTrianglesInFictiveGroup picks the largest mesh. MostSurroundedInFictiveGroup picks the medoid: the object whose sum of angular distances to the other objects in the fictive subgroup is minimal.")]
        public CrowdingTargetSelectionCriterion targetSelectionCriterion = CrowdingTargetSelectionCriterion.RandomInFictiveGroup;

        [Tooltip("If true, target selection is deterministic for a given random seed.")]
        public bool deterministicRandom = true;
        public int randomSeed = 12345;
    }

    [Serializable]
    public class CrowdingWeightSettings
    {
        [Tooltip("Targets closer than this to the fixation direction are protected from extra simplification.")]
        public float fovealProtectionEccentricityDeg = 2.0f;

        [Tooltip("Avoid division by zero when a target is extremely close to the fixation direction.")]
        public float minEccentricityDeg = 0.25f;

        [Header("Bouma mean -> target weight")]
        public float b0 = 0.15f;
        public float b1 = 0.30f;
        public float b2 = 0.45f;
        public float b3 = 0.60f;

        public float wVeryCrowded = 0.20f;
        public float wCrowded = 0.40f;
        public float wModerate = 0.60f;
        public float wWeak = 0.80f;
        public float wUncrowded = 1.00f;
    }

    [Serializable]
    public class CrowdingBudgetSettings
    {
        [Tooltip("Global triangle keep ratio for each group. K=0.5 means keep 50% of the initial group triangles.")]
        [Range(0.01f, 1f)] public float groupKeepRatioK = 0.50f;

        [Header("Budget strategy")]
        [Tooltip("RedistributeWithinGroup keeps the old equal-budget behavior. ExtraSimplifyTargetsOnly applies K to all objects, then simplifies targets more according to their crowding weight without redistributing triangles to the other objects.")]
        public CrowdingBudgetStrategy budgetStrategy = CrowdingBudgetStrategy.RedistributeWithinGroup;

        [Tooltip("Only used by ExtraSimplifyTargetsOnly. 0 means weighted = uniform. 1 means target ratio = K * targetWeight. Intermediate values apply a moderated effect: K * Lerp(1, targetWeight, strength).")]
        [Range(0f, 1f)] public float targetExtraSimplificationStrength = 1.0f;

        [Tooltip("Clamp target ratios so that decimation never destroys the object completely.")]
        [Range(0.01f, 1f)] public float minTargetKeepRatio = 0.05f;

        [Tooltip("Clamp flanker/non-target ratios.")]
        [Range(0.01f, 1f)] public float minNonTargetKeepRatio = 0.05f;

        public bool warnIfBudgetCannotBeRedistributed = true;
    }

    [Serializable]
    public class CrowdingDebugSettings
    {
        public bool createDebugGroupObjects = true;
        public bool reparentObjectsUnderDebugGroups = false;
        public bool addMemberComponents = true;
        public bool drawGizmos = true;
        public bool logSummary = true;
    }
}
