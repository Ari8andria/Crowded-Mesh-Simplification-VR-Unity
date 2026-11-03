using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingSegmentation
    {
        public static List<List<CrowdingObjectSignature>> LastSpatialClusters { get; private set; }
            = new List<List<CrowdingObjectSignature>>();

        public static List<CrowdingGroupData> Segment(
            List<CrowdingObjectSignature> objects,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix)
        {
            List<CrowdingGroupData> groups = new List<CrowdingGroupData>();
            LastSpatialClusters = new List<List<CrowdingObjectSignature>>();

            if (objects == null || objects.Count == 0) return groups;
            if (settings == null) settings = new CrowdingSegmentationSettings();

            if (settings.segmentationMode == CrowdingSegmentationMode.SpatialClusteringFirst)
            {
                SegmentSpatialClusteringFirst(objects, settings, semanticMatrix, groups);
            }
            else
            {
                SegmentDepthFirst(objects, settings, semanticMatrix, groups);
            }

            ReassignGroupIds(groups);
            return groups;
        }

        private static void SegmentSpatialClusteringFirst(
            List<CrowdingObjectSignature> objects,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix,
            List<CrowdingGroupData> outputGroups)
        {
            List<List<CrowdingObjectSignature>> spatialClusters = CrowdingSpatialClusterer.ClusterByAngularSpace(
                objects,
                settings.spatialClusterEpsilonDeg,
                settings.spatialClusterMinPts,
                settings.useObjectApparentRadiusForSpatialClustering
            );

            StoreDebugSpatialClusters(spatialClusters);

            int candidateGroupCount = 0;

            for (int i = 0; i < spatialClusters.Count; i++)
            {
                List<CrowdingObjectSignature> cluster = spatialClusters[i];
                if (cluster == null || cluster.Count == 0)
                    continue;

                List<List<CrowdingObjectSignature>> refinedComponents;

                if (settings.refineSpatialClustersByLocalCompatibility)
                {
                    refinedComponents = SplitByLocalDepthAndSimilarity(cluster, settings, semanticMatrix);
                }
                else
                {
                    refinedComponents = new List<List<CrowdingObjectSignature>>();
                    refinedComponents.Add(cluster);
                }

                for (int j = 0; j < refinedComponents.Count; j++)
                {
                    List<CrowdingObjectSignature> component = refinedComponents[j];
                    if (component == null || component.Count < settings.minObjectsPerGroup)
                        continue;

                    CrowdingGroupData group = new CrowdingGroupData();
                    group.groupId = candidateGroupCount++;
                    group.members.AddRange(component);
                    outputGroups.Add(group);
                }
            }

            if (settings.logSpatialClusteringSummary)
            {
                Debug.Log(
                    "Crowding spatial clustering segmentation\n" +
                    "Objects: " + objects.Count + "\n" +
                    "Spatial clusters: " + spatialClusters.Count + "\n" +
                    "Final groups kept: " + outputGroups.Count + "\n" +
                    "epsilonDeg: " + settings.spatialClusterEpsilonDeg.ToString("F2") + "\n" +
                    "maxPairwiseDepthArcsec: " + settings.maxPairwiseDepthArcsec.ToString("F1")
                );
            }
        }

        private static void StoreDebugSpatialClusters(List<List<CrowdingObjectSignature>> spatialClusters)
        {
            LastSpatialClusters = new List<List<CrowdingObjectSignature>>();

            if (spatialClusters == null)
                return;

            for (int i = 0; i < spatialClusters.Count; i++)
            {
                if (spatialClusters[i] == null)
                    continue;

                LastSpatialClusters.Add(new List<CrowdingObjectSignature>(spatialClusters[i]));
            }
        }

        private static List<List<CrowdingObjectSignature>> SplitByLocalDepthAndSimilarity(
            List<CrowdingObjectSignature> cluster,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix)
        {
            List<List<CrowdingObjectSignature>> components = new List<List<CrowdingObjectSignature>>();

            if (cluster == null || cluster.Count == 0)
                return components;

            if (cluster.Count == 1)
            {
                components.Add(new List<CrowdingObjectSignature>(cluster));
                return components;
            }

            int n = cluster.Count;
            List<int>[] adjacency = new List<int>[n];
            for (int i = 0; i < n; i++)
                adjacency[i] = new List<int>();

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!AreLocallyCompatible(cluster[i], cluster[j], settings, semanticMatrix))
                        continue;

                    adjacency[i].Add(j);
                    adjacency[j].Add(i);
                }
            }

            bool[] visited = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (visited[i])
                    continue;

                List<CrowdingObjectSignature> component = new List<CrowdingObjectSignature>();
                Queue<int> queue = new Queue<int>();

                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(cluster[current]);

                    for (int k = 0; k < adjacency[current].Count; k++)
                    {
                        int neighbor = adjacency[current][k];
                        if (visited[neighbor])
                            continue;

                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static bool AreLocallyCompatible(
            CrowdingObjectSignature a,
            CrowdingObjectSignature b,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix)
        {
            if (a == null || b == null)
                return false;

            if (settings.usePairwiseDepthCompatibility)
            {
                float depthArcsec = CrowdingGeometryUtils.PairwiseDepthArcsec(settings.ipdMeters, a, b);
                if (depthArcsec > settings.maxPairwiseDepthArcsec)
                    return false;
            }

            if (settings.usePairwiseSimilarityCompatibility)
            {
                float similarity = Similarity(a, b, settings, semanticMatrix);
                if (similarity < settings.minSimilarityToJoin)
                    return false;
            }

            return true;
        }

        private static void SegmentDepthFirst(
            List<CrowdingObjectSignature> objects,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix,
            List<CrowdingGroupData> outputGroups)
        {
            List<CrowdingObjectSignature> sorted = new List<CrowdingObjectSignature>(objects);
            sorted.Sort((a, b) => a.depthMeters.CompareTo(b.depthMeters));

            HashSet<CrowdingObjectSignature> used = new HashSet<CrowdingObjectSignature>();
            int groupId = 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                CrowdingObjectSignature seed = sorted[i];
                if (used.Contains(seed)) continue;

                CrowdingGroupData group = new CrowdingGroupData();
                group.groupId = groupId++;
                group.members.Add(seed);
                used.Add(seed);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    CrowdingObjectSignature candidate = sorted[j];
                    if (used.Contains(candidate)) continue;

                    float spanArcsec = CrowdingGeometryUtils.DepthSpanArcsec(settings.ipdMeters, group.members, candidate);
                    if (spanArcsec > settings.maxDepthSpanArcsec)
                        continue;

                    float bestSimilarity = BestSimilarity(candidate, group.members, settings, semanticMatrix);
                    if (bestSimilarity >= settings.minSimilarityToJoin)
                    {
                        group.members.Add(candidate);
                        used.Add(candidate);
                    }
                }

                if (group.members.Count >= settings.minObjectsPerGroup)
                    outputGroups.Add(group);
            }
        }

        private static void ReassignGroupIds(List<CrowdingGroupData> groups)
        {
            if (groups == null)
                return;

            for (int i = 0; i < groups.Count; i++)
                groups[i].groupId = i;
        }

        public static float BestSimilarity(
            CrowdingObjectSignature candidate,
            IList<CrowdingObjectSignature> groupMembers,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix)
        {
            float best = 0f;
            for (int i = 0; i < groupMembers.Count; i++)
            {
                float s = Similarity(candidate, groupMembers[i], settings, semanticMatrix);
                if (s > best) best = s;
            }
            return best;
        }

        public static float Similarity(
            CrowdingObjectSignature a,
            CrowdingObjectSignature b,
            CrowdingSegmentationSettings settings,
            SemanticSimilarityMatrix semanticMatrix)
        {
            if (a == null || b == null) return 0f;
            if (settings == null) settings = new CrowdingSegmentationSettings();

            float sizeSimilarity = ComputeSizeSimilarity(a, b, settings);

            if (settings.rejectIfSizeRatioAboveLimit)
            {
                float minAllowedSimilarity = 1f / Mathf.Max(settings.maxSizeRatio, 1.0001f);
                if (sizeSimilarity < minAllowedSimilarity)
                    return 0f;
            }

            // Convert similarity in [0, 1] to a distance. This preserves the previous
            // log2-size-distance behavior for the legacy apparent-radius mode while
            // allowing angular-footprint similarity to penalize different projected shapes.
            float dSize = SizeSimilarityToDistance(sizeSimilarity);

            float dr = a.dominantColor.r - b.dominantColor.r;
            float dg = a.dominantColor.g - b.dominantColor.g;
            float db = a.dominantColor.b - b.dominantColor.b;
            float dColor = Mathf.Clamp01(Mathf.Sqrt(dr * dr + dg * dg + db * db) / Mathf.Sqrt(3f));

            float wSize = Mathf.Max(0f, settings.weightSize);
            float wColor = Mathf.Max(0f, settings.weightColor);
            float wSem = settings.useSemanticSimilarity ? Mathf.Max(0f, settings.weightSemantic) : 0f;

            float dSem = 0f;
            if (settings.useSemanticSimilarity)
            {
                float sSem = semanticMatrix != null ? semanticMatrix.GetSimilarity(a.semanticName, b.semanticName) : 0.5f;
                dSem = 1f - Mathf.Clamp01(sSem);
            }

            float sumW = Mathf.Max(0.0001f, wSize + wColor + wSem);
            float d = (wSize * dSize + wColor * dColor + wSem * dSem) / sumW;
            float tau = Mathf.Max(0.0001f, settings.tau);
            return Mathf.Exp(-d / tau);
        }

        private static float ComputeSizeSimilarity(
            CrowdingObjectSignature a,
            CrowdingObjectSignature b,
            CrowdingSegmentationSettings settings)
        {
            if (a == null || b == null)
                return 0f;

            if (settings != null &&
                settings.sizeSimilarityMode == CrowdingSizeSimilarityMode.AngularFootprintWidthHeight)
            {
                Camera referenceCamera = settings.angularFootprintReferenceCamera != null
                    ? settings.angularFootprintReferenceCamera
                    : Camera.main;

                if (referenceCamera != null)
                {
                    float footprintSimilarity = CrowdingAngularFootprintUtility.ComputeSimilarityFromRoots(
                        a.root,
                        b.root,
                        referenceCamera,
                        settings.angularFootprintReferenceFixationPoint,
                        includeInactiveChildren: true,
                        ignoreCornersBehindCamera: settings.angularFootprintIgnoreCornersBehindCamera,
                        useStrictProduct: settings.angularFootprintUseStrictProduct);

                    if (footprintSimilarity > 0f)
                        return Mathf.Clamp01(footprintSimilarity);
                }

                // Fallback is intentional: if no camera/root can be resolved, old scenes must
                // continue to work exactly as before instead of producing empty groups.
            }

            return LegacyApparentRadiusSimilarity(a, b);
        }

        private static float LegacyApparentRadiusSimilarity(
            CrowdingObjectSignature a,
            CrowdingObjectSignature b)
        {
            float ra = Mathf.Max(0.0001f, a.apparentRadiusDeg);
            float rb = Mathf.Max(0.0001f, b.apparentRadiusDeg);
            return Mathf.Min(ra, rb) / Mathf.Max(ra, rb);
        }

        private static float SizeSimilarityToDistance(float similarity)
        {
            similarity = Mathf.Clamp(similarity, 0.0001f, 1f);
            return Mathf.Clamp01(Mathf.Abs(Mathf.Log(similarity) / Mathf.Log(2f)));
        }
    }
}
