using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

public class TaggedObjectTriangleDensity : MonoBehaviour
{
    [Header("Object selection")]
    [SerializeField] private string objectTag = "Objet";
    [SerializeField] private bool excludeInactiveObjects = true;

    [Header("Density definition")]
    [Tooltip("If enabled: triangles / (pi * radius^2). " +
             "Otherwise: triangles / (4 * pi * radius^2).")]
    [SerializeField] private bool useProjectedSphereArea = true;

    [Header("Export")]
    [SerializeField] private string csvFileName = "object_triangle_density.csv";
    [SerializeField] private bool calculateOnStart = false;

    [Serializable]
    public class ObjectDensityResult
    {
        public string objectName;
        public int triangleCount;
        public Vector3 sphereCenter;
        public float sphereRadius;
        public float normalizationArea;
        public float triangleDensity;
    }

    public List<ObjectDensityResult> results =
        new List<ObjectDensityResult>();

    private void Start()
    {
        if (calculateOnStart)
            CalculateAndExport();
    }

    [ContextMenu("Calculate and export triangle densities")]
    public void CalculateAndExport()
    {
        results.Clear();

        GameObject[] taggedObjects =
            GameObject.FindGameObjectsWithTag(objectTag);

        IEnumerable<GameObject> objectsToAnalyse = taggedObjects;

        if (excludeInactiveObjects)
        {
            objectsToAnalyse = objectsToAnalyse.Where(
                obj => obj.activeInHierarchy);
        }

        // Prevent duplicated analysis when both a parent and one of its
        // descendants have the tag.
        objectsToAnalyse = objectsToAnalyse.Where(
            obj => !HasTaggedParent(obj.transform));

        foreach (GameObject obj in objectsToAnalyse)
        {
            ObjectDensityResult result = AnalyseObject(obj);

            if (result != null)
                results.Add(result);
        }

        results = results
            .OrderBy(result => result.triangleDensity)
            .ToList();

        ExportCsv();

        Debug.Log(
            $"Triangle densities calculated for {results.Count} objects.");
    }

    private ObjectDensityResult AnalyseObject(GameObject root)
    {
        int triangleCount = CountTriangles(root);

        if (!TryCalculateBoundingSphere(
                root, out Vector3 center, out float radius))
        {
            Debug.LogWarning(
                $"No valid renderer found for {root.name}.");
            return null;
        }

        if (radius <= Mathf.Epsilon)
        {
            Debug.LogWarning(
                $"Bounding sphere radius is zero for {root.name}.");
            return null;
        }

        float area = useProjectedSphereArea
            ? Mathf.PI * radius * radius
            : 4f * Mathf.PI * radius * radius;

        return new ObjectDensityResult
        {
            objectName = root.name,
            triangleCount = triangleCount,
            sphereCenter = center,
            sphereRadius = radius,
            normalizationArea = area,
            triangleDensity = triangleCount / area
        };
    }

    private int CountTriangles(GameObject root)
    {
        int count = 0;

        MeshFilter[] meshFilters =
            root.GetComponentsInChildren<MeshFilter>(
                includeInactive: !excludeInactiveObjects);

        foreach (MeshFilter meshFilter in meshFilters)
        {
            Mesh mesh = meshFilter.sharedMesh;

            if (mesh == null)
                continue;

            for (int submesh = 0;
                 submesh < mesh.subMeshCount;
                 submesh++)
            {
                count += (int)mesh.GetIndexCount(submesh) / 3;
            }
        }

        SkinnedMeshRenderer[] skinnedRenderers =
            root.GetComponentsInChildren<SkinnedMeshRenderer>(
                includeInactive: !excludeInactiveObjects);

        foreach (SkinnedMeshRenderer renderer in skinnedRenderers)
        {
            Mesh mesh = renderer.sharedMesh;

            if (mesh == null)
                continue;

            for (int submesh = 0;
                 submesh < mesh.subMeshCount;
                 submesh++)
            {
                count += (int)mesh.GetIndexCount(submesh) / 3;
            }
        }

        return count;
    }

    private bool TryCalculateBoundingSphere(
        GameObject root,
        out Vector3 center,
        out float radius)
    {
        Renderer[] renderers =
            root.GetComponentsInChildren<Renderer>(
                includeInactive: !excludeInactiveObjects);

        Renderer firstValidRenderer = renderers.FirstOrDefault(
            renderer =>
                renderer.enabled &&
                renderer is MeshRenderer ||
                renderer is SkinnedMeshRenderer);

        if (firstValidRenderer == null)
        {
            center = Vector3.zero;
            radius = 0f;
            return false;
        }

        Bounds combinedBounds = firstValidRenderer.bounds;

        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled)
                continue;

            if (renderer is MeshRenderer ||
                renderer is SkinnedMeshRenderer)
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }

        center = combinedBounds.center;

        // Sphere enclosing the axis-aligned world-space bounding box.
        radius = combinedBounds.extents.magnitude;

        return true;
    }

    private bool HasTaggedParent(Transform objectTransform)
    {
        Transform parent = objectTransform.parent;

        while (parent != null)
        {
            if (parent.CompareTag(objectTag))
                return true;

            parent = parent.parent;
        }

        return false;
    }

    private void ExportCsv()
    {
        string path = Path.Combine(
            Application.persistentDataPath,
            csvFileName);

        using StreamWriter writer = new StreamWriter(path, false);

        writer.WriteLine(
            "objectName,triangleCount,sphereCenterX,sphereCenterY," +
            "sphereCenterZ,sphereRadius,normalizationArea," +
            "triangleDensity");

        foreach (ObjectDensityResult result in results)
        {
            writer.WriteLine(string.Join(",",
                EscapeCsv(result.objectName),
                result.triangleCount.ToString(
                    CultureInfo.InvariantCulture),
                result.sphereCenter.x.ToString(
                    CultureInfo.InvariantCulture),
                result.sphereCenter.y.ToString(
                    CultureInfo.InvariantCulture),
                result.sphereCenter.z.ToString(
                    CultureInfo.InvariantCulture),
                result.sphereRadius.ToString(
                    CultureInfo.InvariantCulture),
                result.normalizationArea.ToString(
                    CultureInfo.InvariantCulture),
                result.triangleDensity.ToString(
                    CultureInfo.InvariantCulture)));
        }

        Debug.Log($"CSV exported to: {path}");
    }

    private string EscapeCsv(string value)
    {
        if (value.Contains(",") || value.Contains("\""))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}