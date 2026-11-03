#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CrowdingAwareLOD.EditorTools
{
    public static class CrowdingVariantLibraryDebugWindow
    {
        [MenuItem("Crowding Pipeline/Debug/List Variant Keys In Assigned Library")]
        public static void ListVariantKeysInAssignedLibrary()
        {
            CrowdingSharedMeshLODSelector selector = Object.FindFirstObjectByType<CrowdingSharedMeshLODSelector>();
            if (selector == null || selector.variantLibrary == null)
            {
                Debug.LogWarning("No CrowdingSharedMeshLODSelector with an assigned CrowdingMeshVariantLibrary was found.");
                return;
            }

            List<string> keys = selector.variantLibrary.GetDistinctVariantKeys();
            Debug.Log(
                "CrowdingMeshVariantLibrary variant keys: " + keys.Count +
                "\nContains global variants: " + keys.Exists(CrowdingVariantKey.IsGlobalVariantKey) +
                "\n" + string.Join("\n", keys),
                selector.variantLibrary);
        }
    }
}
#endif
