using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Spatial clustering in angular camera space.
    ///
    /// Important change compared with the previous DBSCAN-like version:
    /// this implementation uses a complete-linkage agglomerative clustering.
    ///
    /// Consequence:
    /// - no recursive chain effect;
    /// - a cluster can grow only if every object in the merged cluster remains
    ///   within the epsilon criterion from every other object in that cluster;
    /// - spatialClusterEpsilonDeg is therefore interpreted as the maximum
    ///   allowed angular distance/gap inside one spatial cluster.
    ///
    /// This is intentionally stricter than DBSCAN and better suited for creating
    /// compact pre-clusters before depth/similarity refinement.
    /// </summary>
    public static class CrowdingSpatialClusterer
    {
        public static List<List<CrowdingObjectSignature>> ClusterByAngularSpace(
            List<CrowdingObjectSignature> objects,
            float epsilonDeg,
            int minPts,
            bool useObjectApparentRadius)
        {
            List<List<CrowdingObjectSignature>> output = new List<List<CrowdingObjectSignature>>();

            if (objects == null || objects.Count == 0)
                return output;

            epsilonDeg = Mathf.Max(0f, epsilonDeg);

            int n = objects.Count;
            float[,] distance = BuildPairwiseAngularDistanceMatrix(objects, useObjectApparentRadius);

            // Start with one cluster per object.
            List<List<int>> clusters = new List<List<int>>();
            for (int i = 0; i < n; i++)
            {
                List<int> singleton = new List<int>();
                singleton.Add(i);
                clusters.Add(singleton);
            }

            // Repeatedly merge the two closest clusters, but only if the complete-linkage
            // distance of the merged cluster remains <= epsilonDeg.
            // This prevents DBSCAN-style chain clusters.
            bool mergedSomething = true;
            while (mergedSomething)
            {
                mergedSomething = false;

                int bestA = -1;
                int bestB = -1;
                float bestCompleteDistance = float.PositiveInfinity;

                for (int a = 0; a < clusters.Count; a++)
                {
                    for (int b = a + 1; b < clusters.Count; b++)
                    {
                        float completeDistance = CompleteLinkageDistance(clusters[a], clusters[b], distance);

                        if (completeDistance <= epsilonDeg && completeDistance < bestCompleteDistance)
                        {
                            bestCompleteDistance = completeDistance;
                            bestA = a;
                            bestB = b;
                        }
                    }
                }

                if (bestA >= 0 && bestB >= 0)
                {
                    clusters[bestA].AddRange(clusters[bestB]);
                    clusters.RemoveAt(bestB);
                    mergedSomething = true;
                }
            }

            // Convert index clusters back to object clusters.
            // minPts is intentionally not used to discard clusters here: the existing
            // minObjectsPerGroup setting in CrowdingSegmentation decides what becomes
            // a final group. This keeps the debug view honest by showing all raw spatial clusters.
            for (int c = 0; c < clusters.Count; c++)
            {
                List<CrowdingObjectSignature> cluster = new List<CrowdingObjectSignature>();
                for (int i = 0; i < clusters[c].Count; i++)
                {
                    int objectIndex = clusters[c][i];
                    if (objectIndex >= 0 && objectIndex < objects.Count)
                        cluster.Add(objects[objectIndex]);
                }

                if (cluster.Count > 0)
                    output.Add(cluster);
            }

            return output;
        }

        private static float[,] BuildPairwiseAngularDistanceMatrix(
            List<CrowdingObjectSignature> objects,
            bool useObjectApparentRadius)
        {
            int n = objects.Count;
            float[,] distance = new float[n, n];

            for (int i = 0; i < n; i++)
            {
                distance[i, i] = 0f;

                for (int j = i + 1; j < n; j++)
                {
                    float d = CrowdingGeometryUtils.AngularGapDeg(
                        objects[i],
                        objects[j],
                        useObjectApparentRadius
                    );

                    distance[i, j] = d;
                    distance[j, i] = d;
                }
            }

            return distance;
        }

        private static float CompleteLinkageDistance(
            List<int> clusterA,
            List<int> clusterB,
            float[,] distance)
        {
            float maxDistance = 0f;

            for (int i = 0; i < clusterA.Count; i++)
            {
                int a = clusterA[i];

                for (int j = 0; j < clusterB.Count; j++)
                {
                    int b = clusterB[j];
                    float d = distance[a, b];

                    if (d > maxDistance)
                        maxDistance = d;
                }
            }

            return maxDistance;
        }
    }
}
