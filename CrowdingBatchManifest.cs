using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    [Serializable]
    public class CrowdingBatchManifest
    {
        public string manifestVersion = "1.0";
        public string generatedAtUtc;
        public string unitySceneName;
        public float depthSpanArcsec;
        public int fixationCount;
        public int objectCount;
        public int combinationCount;
        public string notes;

        public List<CrowdingBatchObjectInfo> objects = new List<CrowdingBatchObjectInfo>();
        public List<CrowdingBatchCombination> combinations = new List<CrowdingBatchCombination>();
    }

    [Serializable]
    public class CrowdingBatchObjectInfo
    {
        public string objectId;
        public string rootName;
        public string hierarchyPath;
        public string exportNameWithCid;
        public string semanticName;
        public int groupId;
        public int initialTriangles;
        public Vector3 worldCenter;
        public Bounds worldBounds;
        public List<CrowdingBatchChildMeshInfo> childMeshes = new List<CrowdingBatchChildMeshInfo>();
    }

    [Serializable]
    public class CrowdingBatchChildMeshInfo
    {
        public string childPath;
        public string meshName;
        public int subMeshCount;
        public int triangleCount;
    }

    [Serializable]
    public class CrowdingBatchCombination
    {
        public string variantKey;
        public int fixationIndex;
        public string fixationName;
        public Vector3 fixationWorldPosition;
        public float groupKeepRatioK;
        public CrowdingDecimationCondition condition;
        public string conditionName;
        public string expectedSceneVariantName;
        public List<CrowdingBatchObjectRecord> objectRecords = new List<CrowdingBatchObjectRecord>();
    }

    [Serializable]
    public class CrowdingBatchObjectRecord
    {
        public string objectId;
        public string rootName;
        public string hierarchyPath;
        public string exportNameWithCid;
        public string semanticName;
        public int groupId;
        public string role;
        public float requestedGroupKeepRatioK;
        public float keepRatio;
        public float eccentricityDeg;
        public float boumaMean;
        public float targetWeight;
        public int initialTriangles;
        public int estimatedFinalTriangles;
        public string expectedObjectVariantName;
        public List<string> selectedFlankerObjectIds = new List<string>();
    }
}
