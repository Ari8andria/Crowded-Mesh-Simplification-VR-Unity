#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Validates that imported Blender variants contain exactly the same stable object IDs as the Unity source scene.
    /// Use this after importing the generated FBX variants and assigning them to CrowdingCombinationLODSelector.
    /// </summary>
    public static class CrowdingImportedVariantValidator
    {
        [MenuItem("Crowding Pipeline/Imported Variants/Validate Selected Selector Variants")]
        public static bool ValidateSelectedSelectorVariants()
        {
            CrowdingCombinationLODSelector selector = GetSelectedSelector();
            if (selector == null)
            {
                Debug.LogError("Select a GameObject with CrowdingCombinationLODSelector first.");
                return false;
            }

            HashSet<string> expectedIds = BuildExpectedIdsFromSceneMarkers();
            if (expectedIds.Count == 0)
            {
                Debug.LogError("No CrowdingObjectMarker IDs found in the source scene. Add/validate markers first.");
                return false;
            }

            int variantErrors = 0;
            for (int i = 0; i < selector.variants.Count; i++)
            {
                CrowdingSceneVariantEntry entry = selector.variants[i];
                if (entry == null || entry.sceneVariantRoot == null)
                {
                    Debug.LogError("Variant entry " + i + " is missing a sceneVariantRoot.", selector);
                    variantErrors++;
                    continue;
                }

                if (!ValidateVariantRoot(entry.sceneVariantRoot, expectedIds, entry.variantKey))
                    variantErrors++;
            }

            if (variantErrors == 0)
            {
                Debug.Log("Imported crowding variants validation passed. Variants checked: " + selector.variants.Count + ", expected IDs: " + expectedIds.Count);
                return true;
            }

            Debug.LogError("Imported crowding variants validation failed. Variant errors: " + variantErrors);
            return false;
        }

        private static bool ValidateVariantRoot(GameObject root, HashSet<string> expectedIds, string variantKey)
        {
            Dictionary<string, List<GameObject>> found = new Dictionary<string, List<GameObject>>();
            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
            {
                string id = CrowdingObjectIdUtility.ExtractObjectIdFromName(all[i].name);
                if (string.IsNullOrEmpty(id)) continue;

                if (!found.ContainsKey(id)) found[id] = new List<GameObject>();
                found[id].Add(all[i].gameObject);
            }

            int errors = 0;

            foreach (string expected in expectedIds)
            {
                if (!found.ContainsKey(expected))
                {
                    Debug.LogError("Variant " + variantKey + " is missing objectId " + expected + " under root " + root.name, root);
                    errors++;
                }
                else if (found[expected].Count != 1)
                {
                    string msg = "Variant " + variantKey + " has duplicated objectId " + expected;
                    for (int i = 0; i < found[expected].Count; i++)
                        msg += "\n  - " + CrowdingObjectIdUtility.GetHierarchyPath(found[expected][i].transform);
                    Debug.LogError(msg, found[expected][0]);
                    errors++;
                }
            }

            foreach (KeyValuePair<string, List<GameObject>> kvp in found)
            {
                if (!expectedIds.Contains(kvp.Key))
                {
                    Debug.LogWarning("Variant " + variantKey + " contains an extra objectId not found in source markers: " + kvp.Key, kvp.Value[0]);
                }
            }

            return errors == 0;
        }

        private static HashSet<string> BuildExpectedIdsFromSceneMarkers()
        {
            HashSet<string> ids = new HashSet<string>();
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;
                if (CrowdingObjectIdUtility.IsValidObjectId(marker.ObjectId)) ids.Add(marker.ObjectId);
            }

            return ids;
        }

        private static CrowdingCombinationLODSelector GetSelectedSelector()
        {
            if (Selection.activeGameObject == null) return null;
            return Selection.activeGameObject.GetComponent<CrowdingCombinationLODSelector>();
        }
    }
}
#endif
