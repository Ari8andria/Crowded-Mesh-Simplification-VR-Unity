using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CrowdingAwareLOD
{
    public class CrowdingDebugDrawer : MonoBehaviour
    {
        public CrowdingAwarePipelineController controller;

        [Header("Per-fixation debug")]
        [Tooltip("Which fixation debug set to draw. Only used when the controller is in PerFixationLookAt mode.")]
        public int selectedFixationIndex = 0;

        public bool drawVirtualViewRay = true;
        public bool drawFixationPoint = true;

        [Header("Final groups")]
        public bool drawGroups = true;
        public bool drawTargetFlankerLinks = true;
        public bool drawLabels = true;
        public float sphereRadius = 0.06f;
        public float labelVerticalOffset = 0.08f;

        private void OnDrawGizmos()
        {
            if (controller == null || !controller.debugSettings.drawGizmos) return;

            CrowdingFixationDebugData debugData = null;
            List<CrowdingGroupData> groups = null;

            if (controller.segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt)
            {
                debugData = controller.GetFixationDebugData(selectedFixationIndex);
                if (debugData != null)
                    groups = debugData.groups;
            }
            else
            {
                groups = controller.Groups;
            }

            if (debugData != null)
                DrawViewReference(debugData);

            if (groups == null) return;

            for (int g = 0; g < groups.Count; g++)
            {
                CrowdingGroupData group = groups[g];
                Color c = Color.HSVToRGB((group.groupId * 0.137f) % 1f, 0.75f, 1f);
                c.a = 0.8f;
                Gizmos.color = c;

                if (drawGroups)
                {
                    for (int i = 0; i < group.members.Count; i++)
                    {
                        CrowdingObjectSignature obj = group.members[i];
                        if (obj == null || obj.root == null) continue;
                        Gizmos.DrawWireSphere(obj.worldCenter, sphereRadius);
                    }
                }

                if (drawTargetFlankerLinks)
                {
                    for (int t = 0; t < group.targetFlankerData.Count; t++)
                    {
                        CrowdingTargetFlankerData tf = group.targetFlankerData[t];
                        if (tf == null || tf.target == null) continue;
                        Gizmos.color = Color.red;
                        Gizmos.DrawSphere(tf.target.worldCenter, sphereRadius * 1.5f);
                        Gizmos.color = c;
                        for (int f = 0; f < tf.flankers.Count; f++)
                        {
                            if (tf.flankers[f] == null) continue;
                            Gizmos.DrawLine(tf.target.worldCenter, tf.flankers[f].worldCenter);
                        }
                    }
                }

#if UNITY_EDITOR
                if (drawLabels)
                {
                    for (int i = 0; i < group.members.Count; i++)
                    {
                        CrowdingObjectSignature obj = group.members[i];
                        if (obj == null) continue;

                        string prefix = controller.segmentationSettings.groupingReferenceMode == CrowdingGroupingReferenceMode.PerFixationLookAt
                            ? string.Format("F{0:00}/", debugData != null ? debugData.fixationIndex : selectedFixationIndex)
                            : string.Empty;

                        Handles.Label(
                            obj.worldCenter + Vector3.up * labelVerticalOffset,
                            string.Format("{0}G{1} {2}", prefix, group.groupId, obj.semanticName)
                        );
                    }
                }
#endif
            }
        }

        private void DrawViewReference(CrowdingFixationDebugData debugData)
        {
            if (debugData == null) return;

            if (drawVirtualViewRay)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(debugData.eyePosition, debugData.fixationWorldPosition);
            }

            if (drawFixationPoint)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(debugData.fixationWorldPosition, sphereRadius * 1.4f);
            }

#if UNITY_EDITOR
            if (drawLabels)
            {
                Handles.color = Color.cyan;
                Handles.Label(
                    debugData.fixationWorldPosition + Vector3.up * (labelVerticalOffset * 2f),
                    string.Format("F{0:00} {1}", debugData.fixationIndex, debugData.fixationName)
                );
            }
#endif
        }
    }
}
