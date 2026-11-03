using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CrowdingAwareLOD
{
    /// <summary>
    /// Put this component on the prefab root / logical parent object.
    /// The objectId is generated once and then used as the stable Unity <-> Blender key.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrowdingObjectMarker : MonoBehaviour
    {
        [Tooltip("Human-readable semantic name, e.g. 'wooden chair', 'red apple'. If empty, the GameObject name is used.")]
        public string semanticName;

        [SerializeField, Tooltip("Stable persistent ID used for Unity <-> Blender matching. Do not edit manually unless necessary.")]
        private string objectId;

        [Tooltip("Disable this if the object should be ignored by the crowding-aware pipeline.")]
        public bool includeInCrowdingPipeline = true;

        [SerializeField, HideInInspector]
        private string originalNameBeforeCidBake;

        public string ObjectId
        {
            get { return CrowdingObjectIdUtility.NormalizeObjectId(objectId); }
        }

        public string OriginalNameBeforeCidBake
        {
            get { return originalNameBeforeCidBake; }
        }

        public string EffectiveSemanticName
        {
            get { return string.IsNullOrWhiteSpace(semanticName) ? CrowdingObjectIdUtility.RemoveCidSuffix(gameObject.name) : semanticName.Trim(); }
        }

        public string ExportNameWithCid
        {
            get { return CrowdingObjectIdUtility.BuildExportName(gameObject.name, ObjectId); }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            EnsureObjectId();
        }

        private void Reset()
        {
            EnsureObjectId();
            if (string.IsNullOrWhiteSpace(semanticName))
                semanticName = CrowdingObjectIdUtility.RemoveCidSuffix(gameObject.name);
        }

        public void EnsureObjectId()
        {
            if (!CrowdingObjectIdUtility.IsValidObjectId(objectId))
            {
                objectId = CrowdingObjectIdUtility.NewGuidId();
                EditorUtility.SetDirty(this);
            }
            else
            {
                string normalized = CrowdingObjectIdUtility.NormalizeObjectId(objectId);
                if (objectId != normalized)
                {
                    objectId = normalized;
                    EditorUtility.SetDirty(this);
                }
            }
        }

        public void RegenerateObjectId()
        {
            Undo.RecordObject(this, "Regenerate Crowding Object ID");
            objectId = CrowdingObjectIdUtility.NewGuidId();
            EditorUtility.SetDirty(this);
        }

        public void SetObjectIdEditorOnly(string newObjectId)
        {
            Undo.RecordObject(this, "Set Crowding Object ID");
            objectId = CrowdingObjectIdUtility.IsValidObjectId(newObjectId)
                ? CrowdingObjectIdUtility.NormalizeObjectId(newObjectId)
                : CrowdingObjectIdUtility.NewGuidId();
            EditorUtility.SetDirty(this);
        }

        public void StoreOriginalNameBeforeBake()
        {
            Undo.RecordObject(this, "Store Original Crowding Object Name");
            if (string.IsNullOrEmpty(originalNameBeforeCidBake))
                originalNameBeforeCidBake = CrowdingObjectIdUtility.RemoveCidSuffix(gameObject.name);
            EditorUtility.SetDirty(this);
        }

        public void ClearStoredOriginalName()
        {
            Undo.RecordObject(this, "Clear Stored Crowding Object Name");
            originalNameBeforeCidBake = string.Empty;
            EditorUtility.SetDirty(this);
        }

        [ContextMenu("Regenerate Object ID")]
        private void RegenerateObjectIdContextMenu()
        {
            RegenerateObjectId();
        }

        [ContextMenu("Bake CID In Name")]
        private void BakeCidInNameContextMenu()
        {
            EnsureObjectId();
            StoreOriginalNameBeforeBake();
            Undo.RecordObject(gameObject, "Bake Crowding CID In Name");
            gameObject.name = ExportNameWithCid;
            EditorUtility.SetDirty(gameObject);
        }

        [ContextMenu("Restore Name Before CID Bake")]
        private void RestoreNameContextMenu()
        {
            if (string.IsNullOrEmpty(originalNameBeforeCidBake)) return;
            Undo.RecordObject(gameObject, "Restore Crowding Object Name");
            gameObject.name = originalNameBeforeCidBake;
            ClearStoredOriginalName();
            EditorUtility.SetDirty(gameObject);
        }
#endif
    }
}
