using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public class CrowdingGroupComponent : MonoBehaviour
    {
        public int groupId;
        public int memberCount;
        public int totalTriangles;
        public float maxDepthSpanArcsec;
        public List<GameObject> members = new List<GameObject>();
    }

    [DisallowMultipleComponent]
    public class CrowdingGroupMember : MonoBehaviour
    {
        public int groupId = -1;
        public string objectId;
        public string semanticName;
        public int initialTriangles;
        public CrowdingRole baseRole;

        [Header("Last applied fixation plan")]
        public int fixationIndex = -1;
        public float lastEccentricityDeg;
        public float lastBoumaMean;
        public float lastTargetWeight = 1f;
        public float lastKeepRatio = 1f;
    }
}
