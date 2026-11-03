using System.Text;
using UnityEngine;

namespace CrowdingAwareLOD
{
    public static class CrowdingMeshSlotUtility
    {
        public static string GetRelativePath(Transform root, Transform child)
        {
            if (root == null || child == null) return string.Empty;
            if (root == child) return ".";

            string path = child.name;
            Transform current = child.parent;
            while (current != null && current != root)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        public static string BuildSlotId(string objectId, string childPath, string rendererType)
        {
            string raw = objectId + "__" + childPath + "__" + rendererType;
            return Sanitize(raw);
        }

        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unnamed";
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }
    }
}
