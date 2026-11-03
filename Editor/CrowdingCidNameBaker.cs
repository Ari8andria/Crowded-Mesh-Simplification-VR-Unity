#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CrowdingAwareLOD.EditorTools
{
    /// <summary>
    /// Adds the stable CID suffix to logical object root names before exporting the scene to FBX/GLB.
    /// This is the safest automatic bridge because Blender can always recover the objectId from the exported name.
    /// </summary>
    public static class CrowdingCidNameBaker
    {
        [MenuItem("Crowding Pipeline/Blender Export/Bake CID Suffixes Into Marked Object Names")]
        public static void BakeCidSuffixesIntoNames()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            int changed = 0;
            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;

                marker.EnsureObjectId();
                marker.StoreOriginalNameBeforeBake();

                string newName = marker.ExportNameWithCid;
                if (marker.gameObject.name == newName) continue;

                Undo.RecordObject(marker.gameObject, "Bake Crowding CID Suffix");
                marker.gameObject.name = newName;
                EditorUtility.SetDirty(marker.gameObject);
                changed++;
            }

            EditorSceneManagerSaveIfWanted();
            Debug.Log("Baked CID suffixes into marked object names. Changed objects: " + changed + ". Export your FBX/GLB now, then restore names if desired.");
        }

        [MenuItem("Crowding Pipeline/Blender Export/Restore Names Before CID Bake")]
        public static void RestoreNamesBeforeBake()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            int changed = 0;
            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null) continue;
                string originalName = marker.OriginalNameBeforeCidBake;
                if (string.IsNullOrEmpty(originalName)) continue;

                Undo.RecordObject(marker.gameObject, "Restore Crowding Name Before CID Bake");
                marker.gameObject.name = originalName;
                marker.ClearStoredOriginalName();
                EditorUtility.SetDirty(marker.gameObject);
                changed++;
            }

            EditorSceneManagerSaveIfWanted();
            Debug.Log("Restored names before CID bake. Changed objects: " + changed);
        }

        [MenuItem("Crowding Pipeline/Blender Export/Validate CID Suffixes In Marked Object Names")]
        public static bool ValidateCidSuffixesInNames()
        {
            CrowdingObjectMarker[] markers = Object.FindObjectsByType<CrowdingObjectMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            int errors = 0;
            for (int i = 0; i < markers.Length; i++)
            {
                CrowdingObjectMarker marker = markers[i];
                if (marker == null || !marker.includeInCrowdingPipeline) continue;

                string idInName = CrowdingObjectIdUtility.ExtractObjectIdFromName(marker.gameObject.name);
                if (idInName != marker.ObjectId)
                {
                    Debug.LogError("CID name mismatch on " + marker.gameObject.name + ". Marker ID=" + marker.ObjectId + ", ID in name=" + idInName, marker);
                    errors++;
                }
            }

            if (errors == 0)
            {
                Debug.Log("CID suffix validation passed for marked object names.");
                return true;
            }

            Debug.LogError("CID suffix validation failed. Errors: " + errors);
            return false;
        }

        private static void EditorSceneManagerSaveIfWanted()
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        }
    }
}
#endif
