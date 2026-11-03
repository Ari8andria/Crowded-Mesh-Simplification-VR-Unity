using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    [CreateAssetMenu(menuName = "Crowding Aware LOD/Mesh Variant Library", fileName = "CrowdingMeshVariantLibrary")]
    public class CrowdingMeshVariantLibrary : ScriptableObject
    {
        public string generatedFromManifest;
        public string generatedAtUtc;
        public List<CrowdingMeshVariantEntry> entries = new List<CrowdingMeshVariantEntry>();

        public List<CrowdingMeshVariantEntry> GetEntriesForVariant(string variantKey)
        {
            List<CrowdingMeshVariantEntry> result = new List<CrowdingMeshVariantEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry e = entries[i];
                if (e != null && e.variantKey == variantKey)
                    result.Add(e);
            }
            return result;
        }


        public List<string> GetDistinctVariantKeys()
        {
            List<string> keys = new List<string>();
            HashSet<string> seen = new HashSet<string>();

            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry e = entries[i];
                if (e == null || string.IsNullOrWhiteSpace(e.variantKey))
                    continue;

                if (seen.Add(e.variantKey))
                    keys.Add(e.variantKey);
            }

            keys.Sort();
            return keys;
        }

        public void AddOrReplaceEntry(CrowdingMeshVariantEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.variantKey) || string.IsNullOrWhiteSpace(entry.slotId))
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry existing = entries[i];
                if (existing != null && existing.variantKey == entry.variantKey && existing.slotId == entry.slotId)
                {
                    entries[i] = entry;
                    return;
                }
            }

            entries.Add(entry);
        }

        public Mesh FindMesh(string variantKey, string slotId)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                CrowdingMeshVariantEntry e = entries[i];
                if (e != null && e.variantKey == variantKey && e.slotId == slotId)
                    return e.decimatedMesh;
            }
            return null;
        }
    }

    [Serializable]
    public class CrowdingMeshVariantEntry
    {
        public string variantKey;
        public string slotId;
        public string objectId;
        public string rootName;
        public string childPath;
        public string rendererType;
        public float requestedKeepRatio;
        public int initialTriangles;
        public int finalTriangles;
        public float achievedKeepRatio;
        public Mesh decimatedMesh;
    }
}
