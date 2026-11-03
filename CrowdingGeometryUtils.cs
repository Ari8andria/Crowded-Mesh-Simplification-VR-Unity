using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingGeometryUtils
    {
        public const float ArcsecPerRadian = 206265f;

        public static CrowdingViewReference ViewFromCamera(Camera cam, string viewName = null)
        {
            if (cam == null)
                return new CrowdingViewReference(Vector3.zero, Quaternion.identity, viewName ?? "NullCameraView");

            return new CrowdingViewReference(
                cam.transform.position,
                cam.transform.rotation,
                string.IsNullOrWhiteSpace(viewName) ? cam.name : viewName
            );
        }

        public static CrowdingViewReference LookAtView(
            Vector3 eyePosition,
            Vector3 fixationWorldPosition,
            Vector3 up,
            string viewName = null)
        {
            Vector3 forward = fixationWorldPosition - eyePosition;
            if (forward.sqrMagnitude < 0.000001f)
                forward = Vector3.forward;

            forward.Normalize();

            if (up.sqrMagnitude < 0.000001f)
                up = Vector3.up;

            // Avoid invalid LookRotation when up is nearly parallel to forward.
            if (Mathf.Abs(Vector3.Dot(forward, up.normalized)) > 0.985f)
                up = Vector3.up;

            if (Mathf.Abs(Vector3.Dot(forward, up.normalized)) > 0.985f)
                up = Vector3.right;

            Quaternion rotation = Quaternion.LookRotation(forward, up);
            return new CrowdingViewReference(eyePosition, rotation, viewName ?? "PerFixationLookAt");
        }

        public static float DepthFromCamera(Camera cam, Vector3 worldPoint)
        {
            if (cam == null) return Mathf.Max(0.0001f, worldPoint.z);
            return DepthFromView(ViewFromCamera(cam), worldPoint);
        }

        public static float DepthFromView(CrowdingViewReference view, Vector3 worldPoint)
        {
            Vector3 local = Quaternion.Inverse(view.viewRotation) * (worldPoint - view.eyePosition);
            return Mathf.Max(0.0001f, local.z);
        }

        public static Vector2 AngularPositionDeg(Camera cam, Vector3 worldPoint)
        {
            return AngularPositionDeg(ViewFromCamera(cam), worldPoint);
        }

        public static Vector2 AngularPositionDeg(CrowdingViewReference view, Vector3 worldPoint)
        {
            Vector3 dirWorld = (worldPoint - view.eyePosition).normalized;
            Vector3 dirLocal = Quaternion.Inverse(view.viewRotation) * dirWorld;

            float yaw = Mathf.Atan2(dirLocal.x, dirLocal.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(dirLocal.y, Mathf.Sqrt(dirLocal.x * dirLocal.x + dirLocal.z * dirLocal.z)) * Mathf.Rad2Deg;
            return new Vector2(yaw, pitch);
        }

        public static float AngularDistanceDeg(Camera cam, Vector3 aWorld, Vector3 bWorld)
        {
            if (cam == null) return Vector3.Angle(aWorld.normalized, bWorld.normalized);
            return AngularDistanceDeg(cam.transform.position, aWorld, bWorld);
        }

        public static float AngularDistanceDeg(CrowdingViewReference view, Vector3 aWorld, Vector3 bWorld)
        {
            return AngularDistanceDeg(view.eyePosition, aWorld, bWorld);
        }

        public static float AngularDistanceDeg(Vector3 eyePosition, Vector3 aWorld, Vector3 bWorld)
        {
            Vector3 da = (aWorld - eyePosition).normalized;
            Vector3 db = (bWorld - eyePosition).normalized;
            return Vector3.Angle(da, db);
        }

        public static float ApproxApparentRadiusDeg(Camera cam, Bounds bounds)
        {
            return ApproxApparentRadiusDeg(ViewFromCamera(cam), bounds);
        }

        public static float ApproxApparentRadiusDeg(CrowdingViewReference view, Bounds bounds)
        {
            float depth = DepthFromView(view, bounds.center);
            float radius = bounds.extents.magnitude;
            return Mathf.Atan2(radius, depth) * Mathf.Rad2Deg;
        }

        public static float AngularGapDeg(
            CrowdingObjectSignature a,
            CrowdingObjectSignature b,
            bool useObjectApparentRadius)
        {
            if (a == null || b == null) return float.PositiveInfinity;

            float centerDistanceDeg = Vector2.Distance(a.angularPositionDeg, b.angularPositionDeg);

            if (!useObjectApparentRadius)
                return centerDistanceDeg;

            float radiusA = Mathf.Max(0f, a.apparentRadiusDeg);
            float radiusB = Mathf.Max(0f, b.apparentRadiusDeg);
            float gapDeg = centerDistanceDeg - radiusA - radiusB;

            return Mathf.Max(0f, gapDeg);
        }

        public static float DepthSpanArcsec(float ipdMeters, float zNear, float zFar)
        {
            zNear = Mathf.Max(0.0001f, zNear);
            zFar = Mathf.Max(0.0001f, zFar);
            return ipdMeters * Mathf.Abs((1f / zNear) - (1f / zFar)) * ArcsecPerRadian;
        }

        public static float PairwiseDepthArcsec(float ipdMeters, CrowdingObjectSignature a, CrowdingObjectSignature b)
        {
            if (a == null || b == null) return float.PositiveInfinity;
            return DepthSpanArcsec(ipdMeters, a.depthMeters, b.depthMeters);
        }

        public static float DepthSpanArcsec(float ipdMeters, IList<CrowdingObjectSignature> members, CrowdingObjectSignature candidate)
        {
            float zMin = candidate.depthMeters;
            float zMax = candidate.depthMeters;

            for (int i = 0; i < members.Count; i++)
            {
                float z = members[i].depthMeters;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }

            return DepthSpanArcsec(ipdMeters, zMin, zMax);
        }

        public static float GroupDepthSpanArcsec(float ipdMeters, IList<CrowdingObjectSignature> members)
        {
            if (members == null || members.Count <= 1)
                return 0f;

            float zMin = float.PositiveInfinity;
            float zMax = 0f;

            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] == null) continue;
                float z = members[i].depthMeters;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }

            if (float.IsInfinity(zMin))
                return 0f;

            return DepthSpanArcsec(ipdMeters, zMin, zMax);
        }

        public static Bounds CombineRendererBounds(Renderer[] renderers)
        {
            bool initialized = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.enabled) continue;
                if (!initialized)
                {
                    b = r.bounds;
                    initialized = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }

            return b;
        }

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
