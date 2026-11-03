using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    [Serializable]
    public struct CrowdingViewReference
    {
        public Vector3 eyePosition;
        public Quaternion viewRotation;
        public string viewName;

        public Vector3 Forward { get { return viewRotation * Vector3.forward; } }
        public Vector3 Up { get { return viewRotation * Vector3.up; } }

        public CrowdingViewReference(Vector3 eyePosition, Quaternion viewRotation, string viewName)
        {
            this.eyePosition = eyePosition;
            this.viewRotation = viewRotation;
            this.viewName = string.IsNullOrWhiteSpace(viewName) ? "CrowdingView" : viewName;
        }
    }

    [Serializable]
    public class CrowdingFixationDebugData
    {
        public int fixationIndex;
        public string fixationName;
        public Vector3 eyePosition;
        public Quaternion viewRotation;
        public Vector3 fixationWorldPosition;

        public readonly List<CrowdingObjectSignature> signatures = new List<CrowdingObjectSignature>();
        public readonly List<List<CrowdingObjectSignature>> spatialClusters = new List<List<CrowdingObjectSignature>>();
        public readonly List<CrowdingGroupData> groups = new List<CrowdingGroupData>();
    }

    [Serializable]
    public class CrowdingObjectSignature
    {
        public GameObject root;
        public string objectId;
        public string semanticName;

        public Vector3 worldCenter;
        public Bounds worldBounds;
        public float depthMeters;
        public Vector2 angularPositionDeg; // x = yaw, y = pitch, view-relative
        public float apparentRadiusDeg;
        public Color dominantColor;
        public int triangleCount;
        public Renderer mainRenderer;

        public override string ToString()
        {
            return string.Format("{0} | z={1:F2} | r={2:F2}deg | tris={3}", semanticName, depthMeters, apparentRadiusDeg, triangleCount);
        }
    }

    [Serializable]
    public class CrowdingGroupData
    {
        public int groupId;
        public readonly List<CrowdingObjectSignature> members = new List<CrowdingObjectSignature>();
        public readonly List<CrowdingObjectSignature> targets = new List<CrowdingObjectSignature>();
        public readonly List<CrowdingTargetFlankerData> targetFlankerData = new List<CrowdingTargetFlankerData>();

        public int TotalTriangles
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < members.Count; i++) sum += Mathf.Max(0, members[i].triangleCount);
                return sum;
            }
        }
    }

    [Serializable]
    public class CrowdingTargetFlankerData
    {
        public CrowdingObjectSignature target;
        public readonly List<CrowdingObjectSignature> flankers = new List<CrowdingObjectSignature>();
    }

    [Serializable]
    public class CrowdingObjectDecimationPlan
    {
        public CrowdingObjectSignature obj;
        public CrowdingRole role;
        public float eccentricityDeg;
        public float boumaMean;
        public float targetWeight;
        public float keepRatio;
        public readonly List<CrowdingObjectSignature> selectedFlankers = new List<CrowdingObjectSignature>();

        public int InitialTriangles
        {
            get { return obj != null ? obj.triangleCount : 0; }
        }

        public int EstimatedFinalTriangles
        {
            get { return Mathf.RoundToInt(InitialTriangles * keepRatio); }
        }
    }

    [Serializable]
    public class CrowdingGroupDecimationPlan
    {
        public int groupId;
        public CrowdingGroupData group;
        public readonly List<CrowdingObjectDecimationPlan> objectPlans = new List<CrowdingObjectDecimationPlan>();
        public float requestedGroupKeepRatio;
        public float effectiveGroupKeepRatio;
        public bool budgetRedistributionClamped;

        public int InitialTriangles
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < objectPlans.Count; i++) sum += objectPlans[i].InitialTriangles;
                return sum;
            }
        }

        public int EstimatedFinalTriangles
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < objectPlans.Count; i++) sum += objectPlans[i].EstimatedFinalTriangles;
                return sum;
            }
        }
    }

    [Serializable]
    public class CrowdingFixationPlan
    {
        public int fixationIndex;
        public Transform fixationTransform;
        public Vector3 fixationWorldPosition;
        public readonly List<CrowdingGroupDecimationPlan> groupPlans = new List<CrowdingGroupDecimationPlan>();
    }
}
