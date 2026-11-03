using System;
using System.Collections.Generic;

namespace CrowdingAwareLOD
{
    [Serializable]
    public class CrowdingDecimatedMeshJson
    {
        public string meshName;
        public int vertexCount;
        public int triangleCount;
        public List<float> vertices = new List<float>(); // xyz xyz ... in Unity local coordinates
        public List<float> uv = new List<float>();       // uv uv ... optional
        public List<CrowdingDecimatedSubMeshJson> subMeshes = new List<CrowdingDecimatedSubMeshJson>();
    }

    [Serializable]
    public class CrowdingDecimatedSubMeshJson
    {
        public int materialIndex;
        public List<int> triangles = new List<int>();
    }
}
