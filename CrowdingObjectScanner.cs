using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingObjectScanner
    {
        public static List<CrowdingObjectSignature> FindAndBuildSignatures(
            Camera cam,
            CrowdingObjectDiscoveryMode discoveryMode,
            string objectTag,
            LayerMask objectLayerMask,
            bool includeInactive)
        {
            return FindAndBuildSignatures(
                CrowdingGeometryUtils.ViewFromCamera(cam),
                discoveryMode,
                objectTag,
                objectLayerMask,
                includeInactive
            );
        }

        public static List<CrowdingObjectSignature> FindAndBuildSignatures(
            CrowdingViewReference view,
            CrowdingObjectDiscoveryMode discoveryMode,
            string objectTag,
            LayerMask objectLayerMask,
            bool includeInactive)
        {
            List<GameObject> roots = FindObjectRoots(discoveryMode, objectTag, objectLayerMask, includeInactive);
            List<CrowdingObjectSignature> signatures = new List<CrowdingObjectSignature>();

            for (int i = 0; i < roots.Count; i++)
            {
                CrowdingObjectSignature sig = BuildSignature(view, roots[i]);
                if (sig != null) signatures.Add(sig);
            }

            return signatures;
        }

        public static List<GameObject> FindObjectRoots(
            CrowdingObjectDiscoveryMode discoveryMode,
            string objectTag,
            LayerMask objectLayerMask,
            bool includeInactive)
        {
            HashSet<GameObject> unique = new HashSet<GameObject>();

            if (discoveryMode == CrowdingObjectDiscoveryMode.MarkerComponent)
            {
                CrowdingObjectMarker[] markers = UnityEngine.Object.FindObjectsOfType<CrowdingObjectMarker>(includeInactive);
                for (int i = 0; i < markers.Length; i++)
                {
                    if (markers[i] != null && markers[i].includeInCrowdingPipeline)
                        unique.Add(markers[i].gameObject);
                }
            }
            else
            {
                Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(includeInactive);
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i].gameObject;
                    bool byTag = false;
                    bool byLayer = false;

                    if ((discoveryMode == CrowdingObjectDiscoveryMode.Tag || discoveryMode == CrowdingObjectDiscoveryMode.TagOrLayer) && !string.IsNullOrEmpty(objectTag))
                    {
                        try { byTag = go.CompareTag(objectTag); }
                        catch (UnityException) { byTag = go.tag == objectTag; }
                    }

                    if (discoveryMode == CrowdingObjectDiscoveryMode.Layer || discoveryMode == CrowdingObjectDiscoveryMode.TagOrLayer)
                    {
                        byLayer = ((1 << go.layer) & objectLayerMask.value) != 0;
                    }

                    if (byTag || byLayer)
                        unique.Add(go);
                }
            }

            // If both a parent and one of its children are marked, keep the highest marked parent.
            List<GameObject> sorted = new List<GameObject>(unique);
            sorted.Sort((a, b) => GetDepth(a.transform).CompareTo(GetDepth(b.transform)));

            List<GameObject> filtered = new List<GameObject>();
            for (int i = 0; i < sorted.Count; i++)
            {
                Transform t = sorted[i].transform;
                bool hasMarkedAncestor = false;
                Transform p = t.parent;
                while (p != null)
                {
                    if (unique.Contains(p.gameObject))
                    {
                        hasMarkedAncestor = true;
                        break;
                    }
                    p = p.parent;
                }
                if (!hasMarkedAncestor) filtered.Add(sorted[i]);
            }

            return filtered;
        }

        private static int GetDepth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        public static CrowdingObjectSignature BuildSignature(Camera cam, GameObject root)
        {
            if (cam == null) return null;
            return BuildSignature(CrowdingGeometryUtils.ViewFromCamera(cam), root);
        }

        public static CrowdingObjectSignature BuildSignature(CrowdingViewReference view, GameObject root)
        {
            if (root == null) return null;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) return null;

            Bounds bounds = CrowdingGeometryUtils.CombineRendererBounds(renderers);
            if (bounds.size == Vector3.zero) return null;

            Renderer mainRenderer = FindLargestRenderer(renderers);
            int triangles = CountTriangles(root);
            Color dominant = EstimateDominantColor(mainRenderer);

            CrowdingObjectMarker marker = root.GetComponent<CrowdingObjectMarker>();
            if (marker != null && !marker.includeInCrowdingPipeline) return null;
            string semanticName = marker != null ? marker.EffectiveSemanticName : root.name;
            string objectId = marker != null && !string.IsNullOrWhiteSpace(marker.ObjectId)
                ? marker.ObjectId
                : CrowdingObjectIdUtility.ExtractObjectIdFromName(root.name);

            if (string.IsNullOrWhiteSpace(objectId))
                objectId = CrowdingObjectIdUtility.HashToObjectId(CrowdingGeometryUtils.GetHierarchyPath(root.transform));

            CrowdingObjectSignature sig = new CrowdingObjectSignature();
            sig.root = root;
            sig.objectId = objectId;
            sig.semanticName = semanticName;
            sig.worldCenter = bounds.center;
            sig.worldBounds = bounds;
            sig.depthMeters = CrowdingGeometryUtils.DepthFromView(view, bounds.center);
            sig.angularPositionDeg = CrowdingGeometryUtils.AngularPositionDeg(view, bounds.center);
            sig.apparentRadiusDeg = Mathf.Max(0.0001f, CrowdingGeometryUtils.ApproxApparentRadiusDeg(view, bounds));
            sig.dominantColor = dominant;
            sig.triangleCount = triangles;
            sig.mainRenderer = mainRenderer;
            return sig;
        }

        private static Renderer FindLargestRenderer(Renderer[] renderers)
        {
            Renderer best = null;
            float bestVolume = -1f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                Vector3 s = r.bounds.size;
                float volume = Mathf.Max(0f, s.x * s.y * s.z);
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = r;
                }
            }
            return best;
        }

        private static int CountTriangles(GameObject root)
        {
            int count = 0;

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh m = filters[i].sharedMesh;
                if (m != null) count += m.triangles.Length / 3;
            }

            SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                Mesh m = skins[i].sharedMesh;
                if (m != null) count += m.triangles.Length / 3;
            }

            return count;
        }

        private static Color EstimateDominantColor(Renderer renderer)
        {
            if (renderer == null || renderer.sharedMaterial == null) return Color.gray;

            Material mat = renderer.sharedMaterial;
            Color tint = Color.white;
            if (mat.HasProperty("_BaseColor")) tint = mat.GetColor("_BaseColor");
            else if (mat.HasProperty("_Color")) tint = mat.GetColor("_Color");

            Texture2D tex = null;
            if (mat.HasProperty("_BaseMap")) tex = mat.GetTexture("_BaseMap") as Texture2D;
            else if (mat.HasProperty("_MainTex")) tex = mat.GetTexture("_MainTex") as Texture2D;

            if (tex == null) return tint;

            try
            {
                // Requires Read/Write enabled. If not, we silently fall back to material tint.
                Color32[] pixels = tex.GetPixels32();
                if (pixels == null || pixels.Length == 0) return tint;

                long r = 0, g = 0, b = 0, a = 0;
                int step = Mathf.Max(1, pixels.Length / 4096); // Cheap subsampling for large textures.
                int n = 0;
                for (int i = 0; i < pixels.Length; i += step)
                {
                    Color32 p = pixels[i];
                    r += p.r; g += p.g; b += p.b; a += p.a;
                    n++;
                }
                if (n == 0) return tint;
                Color avg = new Color(r / (255f * n), g / (255f * n), b / (255f * n), a / (255f * n));
                return new Color(avg.r * tint.r, avg.g * tint.g, avg.b * tint.b, avg.a * tint.a);
            }
            catch (Exception)
            {
                return tint;
            }
        }
    }
}
