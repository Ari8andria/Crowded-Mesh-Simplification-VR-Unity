#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Editor-only validation and repair tools for stable object IDs.
    /// These menus make prefab duplicates safe by ensuring every logical object has exactly one persistent GUID.
    /// </summary>
    public static class CrowdingObjectIdValidator
    {
        [MenuItem("Crowding Pipeline/Object IDs/Validate All Markers")]
        public static bool ValidateAllMarkers()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            Dictionary<string, List<CrowdingObjectMarker>> byId = new Dictionary<string, List<CrowdingObjectMarker>>();
            int missingOrInvalid = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;

                if (!CrowdingObjectIdUtility.IsValidObjectId(marker.ObjectId))
                {
                    missingOrInvalid++;
                    Debug.LogError("Invalid or missing crowding objectId on " + GetHierarchyPath(marker.transform), marker);
                    continue;
                }

                string id = marker.ObjectId;
                if (!byId.ContainsKey(id)) byId[id] = new List<CrowdingObjectMarker>();
                byId[id].Add(marker);
            }

            int duplicateIds = 0;
            foreach (KeyValuePair<string, List<CrowdingObjectMarker>> kvp in byId)
            {
                if (kvp.Value.Count <= 1) continue;
                duplicateIds++;

                string msg = "Duplicate crowding objectId detected: " + kvp.Key;
                for (int i = 0; i < kvp.Value.Count; i++)
                    msg += "\n  - " + GetHierarchyPath(kvp.Value[i].transform);

                Debug.LogError(msg, kvp.Value[0]);
            }

            if (missingOrInvalid == 0 && duplicateIds == 0)
            {
                Debug.Log("Crowding object ID validation passed. Markers checked: " + markers.Length);
                return true;
            }

            Debug.LogError("Crowding object ID validation failed. Invalid IDs: " + missingOrInvalid + ", duplicate IDs: " + duplicateIds);
            return false;
        }

        [MenuItem("Crowding Pipeline/Object IDs/Fix Missing And Duplicate IDs")]
        public static void FixMissingAndDuplicateIds()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            Dictionary<string, CrowdingObjectMarker> seen = new Dictionary<string, CrowdingObjectMarker>();
            int fixedCount = 0;

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;

                marker.EnsureObjectId();
                string id = marker.ObjectId;

                if (!CrowdingObjectIdUtility.IsValidObjectId(id))
                {
                    marker.RegenerateObjectId();
                    fixedCount++;
                    continue;
                }

                if (seen.ContainsKey(id))
                {
                    string oldId = id;
                    marker.RegenerateObjectId();
                    fixedCount++;
                    Debug.LogWarning("Regenerated duplicate crowding objectId on " + GetHierarchyPath(marker.transform) + ". Old ID: " + oldId + ", new ID: " + marker.ObjectId, marker);
                }
                else
                {
                    seen.Add(id, marker);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Crowding object ID repair finished. IDs regenerated: " + fixedCount);
        }

        [MenuItem("Crowding Pipeline/Object IDs/Add Markers To Objects Found By Selected Controller")]
        public static void AddMarkersFromSelectedController()
        {
            CrowdingAwarePipelineController controller = GetSelectedController();
            if (controller == null)
            {
                Debug.LogError("Select a GameObject with CrowdingAwarePipelineController first.");
                return;
            }

            List<GameObject> roots = CrowdingObjectScanner.FindObjectRoots(
                controller.discoveryMode,
                controller.objectTag,
                controller.objectLayerMask,
                controller.includeInactiveObjects);

            int added = 0;
            int existing = 0;

            for (int i = 0; i < roots.Count; i++)
            {
                GameObject root = roots[i];
                if (root == null) continue;

                CrowdingObjectMarker marker = root.GetComponent<CrowdingObjectMarker>();
                if (marker == null)
                {
                    Undo.AddComponent<CrowdingObjectMarker>(root);
                    marker = root.GetComponent<CrowdingObjectMarker>();
                    marker.semanticName = CrowdingObjectIdUtility.RemoveCidSuffix(root.name);
                    marker.EnsureObjectId();
                    EditorUtility.SetDirty(marker);
                    added++;
                }
                else
                {
                    marker.EnsureObjectId();
                    existing++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("Crowding markers prepared from selected controller. Added: " + added + ", already existing: " + existing + ", roots found: " + roots.Count);
        }

        private static CrowdingAwarePipelineController GetSelectedController()
        {
            if (Selection.activeGameObject == null) return null;
            return Selection.activeGameObject.GetComponent<CrowdingAwarePipelineController>();
        }

        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
#endif
