using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    [Serializable]
    public class CrowdingMeshDecimationManifest
    {
        public string manifestVersion = "2.0-mesh-only";
        public string generatedAtUtc;
        public string unitySceneName;
        public float depthSpanArcsec;
        public int availableFixationPointCount;
        public int usedFixationPointCount;
        public int objectCount;
        public int meshSlotCount;
        public int variantCount;
        public string sourceMeshDirectory;
        public string blenderOutputDirectory;
        public string notes;
        public string variantGenerationMode;

        public List<CrowdingMeshSlotManifestRecord> meshSlots = new List<CrowdingMeshSlotManifestRecord>();
        public List<CrowdingMeshVariantManifestRecord> variants = new List<CrowdingMeshVariantManifestRecord>();
    }

    [Serializable]
    public class CrowdingMeshSlotManifestRecord
    {
        public string slotId;
        public string objectId;
        public string rootName;
        public string objectHierarchyPath;
        public string childPath;
        public string rendererType;
        public string unityMeshName;
        public string unityMeshAssetPath;
        public string unityMeshAssetGuid;
        public string sourceObjPath;
        public int initialTriangles;
        public int subMeshCount;
        public bool supported;
        public string warning;
    }

    [Serializable]
    public class CrowdingMeshVariantManifestRecord
    {
        public string variantKey;
        public int fixationIndex;
        public string fixationName;
        public Vector3 fixationWorldPosition;
        public float groupKeepRatioK;
        public CrowdingDecimationCondition condition;
        public string conditionName;
        public bool isGlobalSceneVariant;
        public string assignmentMode;
        public List<CrowdingMeshVariantSlotRecord> meshRecords = new List<CrowdingMeshVariantSlotRecord>();
    }

    [Serializable]
    public class CrowdingMeshVariantSlotRecord
    {
        public string variantKey;
        public string slotId;
        public string objectId;
        public string rootName;
        public string childPath;
        public string rendererType;
        public int groupId;
        public string role;
        public int assignedFixationIndex;
        public string assignedFixationName;
        public float keepRatio;
        public float eccentricityDeg;
        public float boumaMean;
        public float targetWeight;
        public int initialTriangles;
        public int estimatedFinalTriangles;
    }

    [Serializable]
    public class CrowdingMeshImportMap
    {
        public string manifestVersion = "2.0-mesh-only-import-map";
        public string generatedAtUtc;
        public string sourceManifestPath;
        public string blenderOutputDirectory;
        public int recordCount;
        public List<CrowdingMeshImportMapRecord> records = new List<CrowdingMeshImportMapRecord>();
    }

    [Serializable]
    public class CrowdingMeshImportMapRecord
    {
        public string variantKey;
        public string slotId;
        public string objectId;
        public string rootName;
        public string childPath;
        public string rendererType;
        public string exportedFbxPath;
        public string exportedFbxAssetPathHint;
        public string exportedObjectName;
        public float requestedKeepRatio;
        public int initialTriangles;
        public int finalTriangles;
        public float achievedKeepRatio;
    }
}
