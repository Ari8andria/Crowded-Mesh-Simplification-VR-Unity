using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CrowdingAwareLOD
{
    [ExecuteAlways]
    public class CrowdingTriangleBudgetCounter : MonoBehaviour
    {
        [Header("Object selection")]
        public string targetTag = "Objet";
        public string targetLayerName = "Objet";

        [Tooltip("If true, objects with CrowdingObjectMarker are also counted.")]
        public bool includeCrowdingMarkers = true;

        [Tooltip("If true, SkinnedMeshRenderer meshes are counted too.")]
        public bool includeSkinnedMeshRenderers = false;

        [Header("Report")]
        public bool writeCsvReport = true;
        public string csvRelativePath = "CrowdingExports/triangle_budget_report.csv";

        [Header("Results")]
        [SerializeField] private long initialTriangleTotal = 0;
        [SerializeField] private long currentTriangleTotal = 0;
        [SerializeField] private float currentToInitialRatio = 1f;

        [SerializeField] private List<MeshTriangleRecord> records = new();

        public long InitialTriangleTotal => initialTriangleTotal;
        public long CurrentTriangleTotal => currentTriangleTotal;
        public float CurrentToInitialRatio => currentToInitialRatio;

        [ContextMenu("Capture Initial Triangle Count")]
        public void CaptureInitialTriangleCount()
        {
            List<MeshSlot> slots = FindMeshSlots();

            records.Clear();
            initialTriangleTotal = 0;

            foreach (MeshSlot slot in slots)
            {
                int triangles = CountTriangles(slot.mesh);

                MeshTriangleRecord record = new MeshTriangleRecord
                {
                    slotId = slot.slotId,
                    rootName = slot.rootName,
                    rendererPath = slot.rendererPath,
                    rendererType = slot.rendererType,
                    initialTriangles = triangles,
                    currentTriangles = triangles,
                    meshName = slot.mesh != null ? slot.mesh.name : ""
                };

                records.Add(record);
                initialTriangleTotal += triangles;
            }

            currentTriangleTotal = initialTriangleTotal;
            currentToInitialRatio = 1f;

            Debug.Log(
                $"[CrowdingTriangleBudgetCounter] Initial triangle count captured.\n" +
                $"Mesh slots: {records.Count}\n" +
                $"Initial triangles: {initialTriangleTotal}",
                this
            );

            if (writeCsvReport)
                WriteCsv();
        }

        [ContextMenu("Compute Current / Initial Ratio")]
        public void ComputeCurrentToInitialRatio()
        {
            if (records == null || records.Count == 0 || initialTriangleTotal <= 0)
            {
                Debug.LogWarning(
                    "[CrowdingTriangleBudgetCounter] No initial triangle count found. " +
                    "Run 'Capture Initial Triangle Count' first while the scene is in its original state.",
                    this
                );
                return;
            }

            Dictionary<string, MeshSlot> currentSlots = new();

            foreach (MeshSlot slot in FindMeshSlots())
            {
                if (!currentSlots.ContainsKey(slot.slotId))
                    currentSlots.Add(slot.slotId, slot);
            }

            currentTriangleTotal = 0;
            int missingSlots = 0;

            for (int i = 0; i < records.Count; i++)
            {
                MeshTriangleRecord record = records[i];

                if (!currentSlots.TryGetValue(record.slotId, out MeshSlot currentSlot))
                {
                    record.currentTriangles = 0;
                    missingSlots++;
                    continue;
                }

                int triangles = CountTriangles(currentSlot.mesh);
                record.currentTriangles = triangles;
                record.meshName = currentSlot.mesh != null ? currentSlot.mesh.name : "";

                currentTriangleTotal += triangles;
            }

            currentToInitialRatio =
                initialTriangleTotal > 0
                    ? (float)currentTriangleTotal / initialTriangleTotal
                    : 0f;

            Debug.Log(
                $"[CrowdingTriangleBudgetCounter] Current / Initial triangle ratio computed.\n" +
                $"Initial triangles: {initialTriangleTotal}\n" +
                $"Current triangles: {currentTriangleTotal}\n" +
                $"Ratio: {currentToInitialRatio:F4}\n" +
                $"Reduction: {(1f - currentToInitialRatio) * 100f:F2}%\n" +
                $"Missing slots: {missingSlots}",
                this
            );

            if (writeCsvReport)
                WriteCsv();
        }

        [ContextMenu("Count Current Triangles Only")]
        public void CountCurrentTrianglesOnly()
        {
            List<MeshSlot> slots = FindMeshSlots();

            long total = 0;

            foreach (MeshSlot slot in slots)
                total += CountTriangles(slot.mesh);

            Debug.Log(
                $"[CrowdingTriangleBudgetCounter] Current triangle count only.\n" +
                $"Mesh slots: {slots.Count}\n" +
                $"Current triangles: {total}",
                this
            );
        }

        [ContextMenu("Clear Captured Initial Count")]
        public void ClearCapturedInitialCount()
        {
            records.Clear();
            initialTriangleTotal = 0;
            currentTriangleTotal = 0;
            currentToInitialRatio = 1f;

            Debug.Log("[CrowdingTriangleBudgetCounter] Captured initial count cleared.", this);
        }

        private List<MeshSlot> FindMeshSlots()
        {
            List<GameObject> roots = FindObjectRoots();
            List<MeshSlot> slots = new();

            foreach (GameObject root in roots)
            {
                if (root == null)
                    continue;

                MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);

                foreach (MeshFilter filter in filters)
                {
                    if (filter == null || filter.sharedMesh == null)
                        continue;

                    string path = GetRelativePath(root.transform, filter.transform);
                    string slotId = BuildSlotId(root, path, "MeshFilter");

                    slots.Add(new MeshSlot
                    {
                        slotId = slotId,
                        rootName = root.name,
                        rendererPath = path,
                        rendererType = "MeshFilter",
                        mesh = filter.sharedMesh
                    });
                }

                if (includeSkinnedMeshRenderers)
                {
                    SkinnedMeshRenderer[] skinned =
                        root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

                    foreach (SkinnedMeshRenderer smr in skinned)
                    {
                        if (smr == null || smr.sharedMesh == null)
                            continue;

                        string path = GetRelativePath(root.transform, smr.transform);
                        string slotId = BuildSlotId(root, path, "SkinnedMeshRenderer");

                        slots.Add(new MeshSlot
                        {
                            slotId = slotId,
                            rootName = root.name,
                            rendererPath = path,
                            rendererType = "SkinnedMeshRenderer",
                            mesh = smr.sharedMesh
                        });
                    }
                }
            }

            return slots;
        }

        private List<GameObject> FindObjectRoots()
        {
            GameObject[] allObjects = FindObjectsByType<GameObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            List<GameObject> candidates = new();
            int layerId = LayerMask.NameToLayer(targetLayerName);

            foreach (GameObject go in allObjects)
            {
                if (go == null)
                    continue;

                bool isCandidate = false;

                if (!string.IsNullOrWhiteSpace(targetTag))
                    isCandidate |= HasTagSafe(go, targetTag);

                if (layerId >= 0)
                    isCandidate |= go.layer == layerId;

                if (includeCrowdingMarkers)
                {
                    CrowdingObjectMarker marker = go.GetComponent<CrowdingObjectMarker>();
                    if (marker != null)
                        isCandidate = true;
                }

                if (isCandidate)
                    candidates.Add(go);
            }

            // Avoid double-counting if a child is also tagged/layered/marked.
            List<GameObject> roots = new();

            foreach (GameObject candidate in candidates)
            {
                bool hasCandidateParent = false;
                Transform parent = candidate.transform.parent;

                while (parent != null)
                {
                    if (candidates.Contains(parent.gameObject))
                    {
                        hasCandidateParent = true;
                        break;
                    }

                    parent = parent.parent;
                }

                if (!hasCandidateParent)
                    roots.Add(candidate);
            }

            return roots;
        }

        private static bool HasTagSafe(GameObject go, string tagName)
        {
            try
            {
                return go.CompareTag(tagName);
            }
            catch
            {
                return false;
            }
        }

        private static int CountTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int triangles = 0;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    continue;

                triangles += (int)mesh.GetIndexCount(s) / 3;
            }

            return triangles;
        }

        private static string BuildSlotId(GameObject root, string relativePath, string rendererType)
        {
            CrowdingObjectMarker marker = root.GetComponent<CrowdingObjectMarker>();

            string rootId =
                marker != null && !string.IsNullOrWhiteSpace(marker.ObjectId)
                    ? marker.ObjectId
                    : GetHierarchyPath(root.transform);

            return $"{rootId}::{relativePath}::{rendererType}";
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (root == child)
                return ".";

            List<string> parts = new();
            Transform current = child;

            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GetHierarchyPath(Transform t)
        {
            string path = t.name;

            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return path;
        }

        private void WriteCsv()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.Combine(projectRoot, csvRelativePath);

            string dir = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using StreamWriter writer = new StreamWriter(fullPath);

            writer.WriteLine("slotId,rootName,rendererPath,rendererType,meshName,initialTriangles,currentTriangles,currentToInitialRatio");

            foreach (MeshTriangleRecord record in records)
            {
                float ratio =
                    record.initialTriangles > 0
                        ? (float)record.currentTriangles / record.initialTriangles
                        : 0f;

                writer.WriteLine(
                    $"{EscapeCsv(record.slotId)}," +
                    $"{EscapeCsv(record.rootName)}," +
                    $"{EscapeCsv(record.rendererPath)}," +
                    $"{EscapeCsv(record.rendererType)}," +
                    $"{EscapeCsv(record.meshName)}," +
                    $"{record.initialTriangles}," +
                    $"{record.currentTriangles}," +
                    $"{ratio:F6}"
                );
            }

            Debug.Log($"[CrowdingTriangleBudgetCounter] CSV written:\n{fullPath}", this);
        }

        private static string EscapeCsv(string s)
        {
            if (s == null)
                return "";

            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";

            return s;
        }

        [Serializable]
        private class MeshTriangleRecord
        {
            public string slotId;
            public string rootName;
            public string rendererPath;
            public string rendererType;
            public string meshName;
            public int initialTriangles;
            public int currentTriangles;
        }

        private class MeshSlot
        {
            public string slotId;
            public string rootName;
            public string rendererPath;
            public string rendererType;
            public Mesh mesh;
        }
    }
}