using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Simple semantic similarity lookup loaded from a JSON TextAsset.
    /// Expected schema:
    /// {
    ///   "entries": [
    ///     {"a":"wooden chair", "b":"wooden stool", "similarity":0.82},
    ///     {"a":"red apple", "b":"orange", "similarity":0.64}
    ///   ]
    /// }
    /// Similarity is interpreted as cosine-like similarity in [0,1].
    /// </summary>
    [Serializable]
    public class SemanticSimilarityMatrix
    {
        [Serializable]
        public class Entry
        {
            public string a;
            public string b;
            public float similarity;
        }

        [Serializable]
        private class Wrapper
        {
            public Entry[] entries;
        }

        private readonly Dictionary<string, float> _similarities = new Dictionary<string, float>();
        private readonly float _fallbackSimilarity;

        public SemanticSimilarityMatrix(float fallbackSimilarity = 0.5f)
        {
            _fallbackSimilarity = Mathf.Clamp01(fallbackSimilarity);
        }

        public static SemanticSimilarityMatrix FromJson(TextAsset textAsset, float fallbackSimilarity = 0.5f)
        {
            SemanticSimilarityMatrix matrix = new SemanticSimilarityMatrix(fallbackSimilarity);
            if (textAsset == null || string.IsNullOrWhiteSpace(textAsset.text)) return matrix;

            try
            {
                Wrapper wrapper = JsonUtility.FromJson<Wrapper>(textAsset.text);
                if (wrapper == null || wrapper.entries == null) return matrix;

                for (int i = 0; i < wrapper.entries.Length; i++)
                {
                    Entry e = wrapper.entries[i];
                    if (e == null || string.IsNullOrWhiteSpace(e.a) || string.IsNullOrWhiteSpace(e.b)) continue;
                    matrix.SetSimilarity(e.a, e.b, e.similarity);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Could not parse semantic similarity JSON: " + ex.Message);
            }

            return matrix;
        }

        public void SetSimilarity(string a, string b, float similarity)
        {
            string key = MakeKey(a, b);
            if (string.IsNullOrEmpty(key)) return;
            _similarities[key] = Mathf.Clamp01(similarity);
        }

        public float GetSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return _fallbackSimilarity;
            if (Normalize(a) == Normalize(b)) return 1f;

            string key = MakeKey(a, b);
            float value;
            if (_similarities.TryGetValue(key, out value)) return value;
            return _fallbackSimilarity;
        }

        private static string MakeKey(string a, string b)
        {
            string na = Normalize(a);
            string nb = Normalize(b);
            if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb)) return string.Empty;
            return string.CompareOrdinal(na, nb) <= 0 ? na + "||" + nb : nb + "||" + na;
        }

        private static string Normalize(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();
        }
    }
}
