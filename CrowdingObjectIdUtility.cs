using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using UnityEngine;

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Shared utilities for stable Unity <-> Blender object matching.
    /// The important convention is: exported object names must contain "__CID_<32 hex chars>".
    /// Blender and Unity validators both recover the same objectId from that suffix.
    /// </summary>
    public static class CrowdingObjectIdUtility
    {
        public const string CidPrefix = "__CID_";
        private static readonly Regex CidRegex = new Regex("__CID_([a-fA-F0-9]{32})", RegexOptions.Compiled);

        public static string NewGuidId()
        {
            return System.Guid.NewGuid().ToString("N");
        }

        public static bool IsValidObjectId(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return false;
            objectId = objectId.Trim();
            if (objectId.Length != 32) return false;

            for (int i = 0; i < objectId.Length; i++)
            {
                char c = objectId[i];
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }

            return true;
        }

        public static string NormalizeObjectId(string objectId)
        {
            return string.IsNullOrWhiteSpace(objectId) ? string.Empty : objectId.Trim().ToLowerInvariant();
        }

        public static string HashToObjectId(string text)
        {
            if (string.IsNullOrEmpty(text)) text = "empty";
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                byte[] hash = md5.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(32);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string ExtractObjectIdFromName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            Match match = CidRegex.Match(objectName);
            return match.Success ? NormalizeObjectId(match.Groups[1].Value) : string.Empty;
        }

        public static bool NameContainsObjectId(string objectName, string objectId)
        {
            string extracted = ExtractObjectIdFromName(objectName);
            string normalized = NormalizeObjectId(objectId);
            return !string.IsNullOrEmpty(extracted) && extracted == normalized;
        }

        public static string RemoveCidSuffix(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return objectName;
            return CidRegex.Replace(objectName, string.Empty);
        }

        public static string BuildExportName(string originalName, string objectId)
        {
            string clean = RemoveCidSuffix(originalName);
            string id = NormalizeObjectId(objectId);
            if (!IsValidObjectId(id)) id = NewGuidId();
            return SanitizeName(clean) + CidPrefix + id;
        }

        public static string SanitizeName(string raw)
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

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
