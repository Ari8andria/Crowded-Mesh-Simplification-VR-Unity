using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Utility used by the optional AngularFootprintWidthHeight size-similarity mode.
    ///
    /// It compares the apparent 2D angular footprint of complete object hierarchies
    /// from a reference camera/fixation view. This is more robust than a single 3D
    /// bounds magnitude because it accounts for horizontal/vertical elongation and
    /// object orientation in the current view.
    ///
    /// This file is additive. It does not change legacy behavior unless
    /// CrowdingSegmentationSettings.sizeSimilarityMode is set to AngularFootprintWidthHeight.
    /// </summary>
    public static class CrowdingAngularFootprintUtility
    {
        public struct AngularFootprint
        {
            public bool isValid;
            public float minXDeg;
            public float maxXDeg;
            public float minYDeg;
            public float maxYDeg;
            public float widthDeg;
            public float heightDeg;
            public float areaDeg2;
            public float aspectRatio;
        }

        public static float ComputeSimilarityFromRoots(
            object objectRootA,
            object objectRootB,
            Camera referenceCamera,
            Transform fixationPoint,
            bool includeInactiveChildren,
            bool ignoreCornersBehindCamera,
            bool useStrictProduct)
        {
            Transform a = ResolveTransform(objectRootA);
            Transform b = ResolveTransform(objectRootB);

            if (a == null || b == null)
                return 0f;

            AngularFootprint fa = ComputeAngularFootprint(
                a,
                referenceCamera,
                fixationPoint,
                includeInactiveChildren,
                ignoreCornersBehindCamera);

            AngularFootprint fb = ComputeAngularFootprint(
                b,
                referenceCamera,
                fixationPoint,
                includeInactiveChildren,
                ignoreCornersBehindCamera);

            return ComputeAngularFootprintSimilarity(fa, fb, useStrictProduct);
        }

        public static float ComputeAngularFootprintSimilarity(
            AngularFootprint a,
            AngularFootprint b,
            bool useStrictProduct)
        {
            if (!a.isValid || !b.isValid)
                return 0f;

            float areaA = Mathf.Max(a.areaDeg2, 1e-6f);
            float areaB = Mathf.Max(b.areaDeg2, 1e-6f);
            float aspectA = Mathf.Max(a.aspectRatio, 1e-6f);
            float aspectB = Mathf.Max(b.aspectRatio, 1e-6f);

            float areaSimilarity = Mathf.Min(areaA, areaB) / Mathf.Max(areaA, areaB);
            float aspectSimilarity = Mathf.Min(aspectA, aspectB) / Mathf.Max(aspectA, aspectB);

            if (useStrictProduct)
                return Mathf.Clamp01(areaSimilarity * aspectSimilarity);

            return Mathf.Clamp01(0.7f * areaSimilarity + 0.3f * aspectSimilarity);
        }

        public static AngularFootprint ComputeAngularFootprintFromRootObject(
            object objectRoot,
            Camera referenceCamera,
            Transform fixationPoint,
            bool includeInactiveChildren,
            bool ignoreCornersBehindCamera)
        {
            Transform t = ResolveTransform(objectRoot);
            if (t == null)
                return new AngularFootprint { isValid = false, aspectRatio = 1f };

            return ComputeAngularFootprint(
                t,
                referenceCamera,
                fixationPoint,
                includeInactiveChildren,
                ignoreCornersBehindCamera);
        }

        public static AngularFootprint ComputeAngularFootprint(
            Transform objectRoot,
            Camera referenceCamera,
            Transform fixationPoint,
            bool includeInactiveChildren,
            bool ignoreCornersBehindCamera)
        {
            AngularFootprint result = new AngularFootprint
            {
                isValid = false,
                minXDeg = float.PositiveInfinity,
                maxXDeg = float.NegativeInfinity,
                minYDeg = float.PositiveInfinity,
                maxYDeg = float.NegativeInfinity,
                widthDeg = 0f,
                heightDeg = 0f,
                areaDeg2 = 0f,
                aspectRatio = 1f
            };

            if (objectRoot == null)
                return result;

            Camera camera = referenceCamera != null ? referenceCamera : Camera.main;
            if (camera == null)
                return result;

            Vector3 cameraPosition = camera.transform.position;
            Quaternion viewRotation = camera.transform.rotation;

            if (fixationPoint != null)
            {
                Vector3 toFixation = fixationPoint.position - cameraPosition;
                if (toFixation.sqrMagnitude > 1e-8f)
                {
                    Vector3 up = camera.transform.up.sqrMagnitude > 1e-8f ? camera.transform.up : Vector3.up;
                    viewRotation = Quaternion.LookRotation(toFixation.normalized, up);
                }
            }

            Vector3 forward = viewRotation * Vector3.forward;
            Vector3 right = viewRotation * Vector3.right;
            Vector3 upVec = viewRotation * Vector3.up;

            List<Vector3> corners = new List<Vector3>(128);
            CollectWorldBoundsCorners(objectRoot, includeInactiveChildren, corners);

            if (corners.Count == 0)
                return result;

            int validCount = 0;

            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 v = corners[i] - cameraPosition;
                float depth = Vector3.Dot(v, forward);

                if (ignoreCornersBehindCamera && depth <= 1e-4f)
                    continue;

                depth = Mathf.Max(depth, 1e-4f);

                float xDeg = Mathf.Atan2(Vector3.Dot(v, right), depth) * Mathf.Rad2Deg;
                float yDeg = Mathf.Atan2(Vector3.Dot(v, upVec), depth) * Mathf.Rad2Deg;

                if (xDeg < result.minXDeg) result.minXDeg = xDeg;
                if (xDeg > result.maxXDeg) result.maxXDeg = xDeg;
                if (yDeg < result.minYDeg) result.minYDeg = yDeg;
                if (yDeg > result.maxYDeg) result.maxYDeg = yDeg;

                validCount++;
            }

            if (validCount == 0)
                return result;

            result.widthDeg = Mathf.Max(0f, result.maxXDeg - result.minXDeg);
            result.heightDeg = Mathf.Max(0f, result.maxYDeg - result.minYDeg);
            result.areaDeg2 = Mathf.Max(result.widthDeg * result.heightDeg, 1e-6f);
            result.aspectRatio = result.widthDeg / Mathf.Max(result.heightDeg, 1e-6f);
            result.isValid = result.widthDeg > 1e-6f && result.heightDeg > 1e-6f;

            return result;
        }

        private static Transform ResolveTransform(object root)
        {
            if (root == null)
                return null;

            if (root is Transform t)
                return t;

            if (root is GameObject go)
                return go.transform;

            if (root is Component c)
                return c.transform;

            return null;
        }

        private static void CollectWorldBoundsCorners(
            Transform root,
            bool includeInactiveChildren,
            List<Vector3> outCorners)
        {
            if (root == null)
                return;

            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(includeInactiveChildren);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter mf = meshFilters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                AddLocalBoundsCorners(mf.transform.localToWorldMatrix, mf.sharedMesh.bounds, outCorners);
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactiveChildren);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer smr = skinned[i];
                if (smr == null || smr.sharedMesh == null)
                    continue;

                AddLocalBoundsCorners(smr.transform.localToWorldMatrix, smr.sharedMesh.bounds, outCorners);
            }

            if (outCorners.Count > 0)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactiveChildren);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                AddWorldAabbCorners(renderer.bounds, outCorners);
            }
        }

        private static void AddLocalBoundsCorners(
            Matrix4x4 localToWorld,
            Bounds bounds,
            List<Vector3> outCorners)
        {
            Vector3 c = bounds.center;
            Vector3 e = bounds.extents;

            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y, -e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y,  e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x,  e.y, -e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x,  e.y,  e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3( e.x, -e.y, -e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3( e.x, -e.y,  e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3( e.x,  e.y, -e.z)));
            outCorners.Add(localToWorld.MultiplyPoint3x4(c + new Vector3( e.x,  e.y,  e.z)));
        }

        private static void AddWorldAabbCorners(Bounds bounds, List<Vector3> outCorners)
        {
            Vector3 c = bounds.center;
            Vector3 e = bounds.extents;

            outCorners.Add(c + new Vector3(-e.x, -e.y, -e.z));
            outCorners.Add(c + new Vector3(-e.x, -e.y,  e.z));
            outCorners.Add(c + new Vector3(-e.x,  e.y, -e.z));
            outCorners.Add(c + new Vector3(-e.x,  e.y,  e.z));
            outCorners.Add(c + new Vector3( e.x, -e.y, -e.z));
            outCorners.Add(c + new Vector3( e.x, -e.y,  e.z));
            outCorners.Add(c + new Vector3( e.x,  e.y, -e.z));
            outCorners.Add(c + new Vector3( e.x,  e.y,  e.z));
        }
    }
}
