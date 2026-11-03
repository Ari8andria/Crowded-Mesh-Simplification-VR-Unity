using UnityEngine;

namespace CrowdingAwareLOD
{
    public enum CrowdingObjectDiscoveryMode
    {
        MarkerComponent,
        Tag,
        Layer,
        TagOrLayer
    }

    public enum CrowdingRole
    {
        None,
        Target,
        NonTarget,
        FlankerForAtLeastOneTarget
    }

    public enum SpatialTargetDistributionMode
    {
        HorizontalColumns,
        VerticalRows,
        CameraPlaneKMeans
    }

    public enum DecimationApplyMode
    {
        ComputeOnly,
        ApplyLODSetIfPresent
    }
}
