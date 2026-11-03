using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CrowdingAwareLOD
{
    public class CrowdingSpatialClusterDebugDrawer : MonoBehaviour
    {
        public CrowdingAwarePipelineController controller;

        [Header("Per-fixation spatial clusters")]
        public bool drawSpatialClusters = false;

        [Tooltip("Which fixation debug set to draw when using PerFixationLookAt.")]
        public int selectedFixationIndex = 0;

        [Tooltip("Use -1 to draw all spatial clusters. Use 0, 1, 2... to isolate one spatial cluster.")]
        public int selectedSpatialCluster = -1;

        public bool drawClusterCentroidLinks = true;
        public bool drawClusterLabels = true;
        public bool drawViewRay = true;

        [Header("Drawing")]
        public float sphereRadius = 0.08f;
        public float centroidRadius = 0.16f;
        public float labelVerticalOffset = 0.10f;

        private void OnDrawGizmos()
        {
            if (!drawSpatialClusters)
                return;

            if (controller == null || !controller.debugSettings.drawGizmos)
                return;

            CrowdingFixationDebugData debugData = controller.GetFixationDebugData(selectedFixationIndex);
            List<List<CrowdingObjectSignature>> clusters = null;

            if (controller.segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt)
            {
                if (debugData == null) return;
                clusters = debugData.spatialClusters;
            }
            else
            {
                clusters = CrowdingSegmentation.LastSpatialClusters;
            }

            if (clusters == null || clusters.Count == 0)
                return;

            if (debugData != null && drawViewRay)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(debugData.eyePosition, debugData.fixationWorldPosition);
                Gizmos.DrawSphere(debugData.fixationWorldPosition, sphereRadius * 1.3f);
            }

            for (int cIndex = 0; cIndex < clusters.Count; cIndex++)
            {
                if (selectedSpatialCluster >= 0 && cIndex != selectedSpatialCluster)
                    continue;

                List<CrowdingObjectSignature> cluster = clusters[cIndex];
                if (cluster == null || cluster.Count == 0)
                    continue;

                Color color = Color.HSVToRGB((cIndex * 0.191f) % 1f, 0.95f, 1f);
                color.a = 0.9f;

                Vector3 centroid = ComputeClusterCentroid(cluster);
                Gizmos.color = color;
                Gizmos.DrawWireSphere(centroid, centroidRadius);

                for (int i = 0; i < cluster.Count; i++)
                {
                    CrowdingObjectSignature obj = cluster[i];
                    if (obj == null || obj.root == null)
                        continue;

                    Gizmos.color = color;
                    Gizmos.DrawWireSphere(obj.worldCenter, sphereRadius);

                    if (drawClusterCentroidLinks)
                        Gizmos.DrawLine(centroid, obj.worldCenter);

#if UNITY_EDITOR
                    if (drawClusterLabels)
                    {
                        Handles.color = color;
                        string prefix = debugData != null
                            ? string.Format("F{0:00}/", debugData.fixationIndex)
                            : string.Empty;

                        Handles.Label(
                            obj.worldCenter + Vector3.up * labelVerticalOffset,
                            string.Format("{0}SC{1} {2}", prefix, cIndex, obj.semanticName)
                        );
                    }
#endif
                }

#if UNITY_EDITOR
                if (drawClusterLabels)
                {
                    Handles.color = color;
                    string prefix = debugData != null
                        ? string.Format("F{0:00}/", debugData.fixationIndex)
                        : string.Empty;

                    Handles.Label(
                        centroid + Vector3.up * (labelVerticalOffset * 2f),
                        string.Format("{0}SC{1} | n={2}", prefix, cIndex, cluster.Count)
                    );
                }
#endif
            }
        }

        private Vector3 ComputeClusterCentroid(List<CrowdingObjectSignature> cluster)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;

            for (int i = 0; i < cluster.Count; i++)
            {
                CrowdingObjectSignature obj = cluster[i];
                if (obj == null || obj.root == null)
                    continue;

                sum += obj.worldCenter;
                count++;
            }

            if (count == 0)
                return transform.position;

            return sum / count;
        }
    }
}
