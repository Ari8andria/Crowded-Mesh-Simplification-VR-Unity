using System;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Optional receiver used by the pipeline to apply precomputed LOD meshes.
    /// Put one CrowdingLODSet on each logical object root if you want Unity to swap meshes.
    /// If absent, the pipeline only computes and stores keep ratios for Blender / external decimation.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingLODSet : MonoBehaviour
    {
        [Serializable]
        public class RendererLOD
        {
            public MeshFilter meshFilter;
            [Tooltip("LOD meshes ordered from highest quality to lowest quality.")]
            public Mesh[] lodMeshesHighToLow;
            [Tooltip("Approximate keep ratios for the meshes above. Same length as lodMeshesHighToLow. Example: 1, 0.75, 0.5, 0.25, 0.1.")]
            public float[] keepRatiosHighToLow;
        }

        public RendererLOD[] rendererLods;

        [Range(0.01f, 1f)] public float lastAppliedKeepRatio = 1f;

        public void ApplyKeepRatio(float requestedKeepRatio)
        {
            lastAppliedKeepRatio = Mathf.Clamp(requestedKeepRatio, 0.01f, 1f);

            if (rendererLods == null) return;
            for (int i = 0; i < rendererLods.Length; i++)
                ApplyToRenderer(rendererLods[i], lastAppliedKeepRatio);
        }

        private static void ApplyToRenderer(RendererLOD rendererLod, float requestedKeepRatio)
        {
            if (rendererLod == null || rendererLod.meshFilter == null || rendererLod.lodMeshesHighToLow == null || rendererLod.lodMeshesHighToLow.Length == 0)
                return;

            int bestIndex = -1;
            float bestScore = float.PositiveInfinity;

            for (int i = 0; i < rendererLod.lodMeshesHighToLow.Length; i++)
            {
                Mesh mesh = rendererLod.lodMeshesHighToLow[i];
                if (mesh == null) continue;

                float ratio = 1f;
                if (rendererLod.keepRatiosHighToLow != null && i < rendererLod.keepRatiosHighToLow.Length)
                    ratio = Mathf.Clamp(rendererLod.keepRatiosHighToLow[i], 0.01f, 1f);

                float score = Mathf.Abs(ratio - requestedKeepRatio);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                rendererLod.meshFilter.sharedMesh = rendererLod.lodMeshesHighToLow[bestIndex];
        }
    }
}
