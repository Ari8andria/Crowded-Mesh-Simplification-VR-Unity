namespace CrowdingAwareLOD
{
    /// <summary>
    /// Mesh decimation backend used by the mesh-only pipeline.
    /// Triangle-budget logic remains unchanged: every backend receives the same keepRatio.
    /// </summary>
    public enum CrowdingLODDecimationBackend
    {
        BlenderDecimate = 0,
        UnityMeshSimplifierQEM = 1
    }
}
