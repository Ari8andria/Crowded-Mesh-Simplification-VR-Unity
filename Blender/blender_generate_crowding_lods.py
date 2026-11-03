"""
Offline Blender generator for crowding-aware LOD variants.

Unity side:
1. Add/validate CrowdingObjectMarker on every logical object root.
2. Bake CID suffixes in Unity object names before FBX/GLB export:
      Crowding Pipeline > Blender Export > Bake CID Suffixes Into Marked Object Names
3. Export the Unity scene/objects to FBX or GLB.
4. Export the crowding manifest:
      Crowding Pipeline > Export Blender Batch Manifest

Blender side:
1. Import the FBX/GLB exported from Unity.
2. Set MANIFEST_PATH and OUTPUT_DIR below.
3. Run this script.

The script matches objects only through the stable suffix:
    __CID_<32 hex chars>
This survives Blender renaming such as .001 and avoids relying on Unity display names.
"""

import bpy
import json
import os
import re
from typing import Dict, List, Optional, Set

# -------------------------------------------------------------------------
# User settings
# -------------------------------------------------------------------------
MANIFEST_PATH = r"C:/path/to/your/UnityProject/CrowdingExports/crowding_batch_manifest.json"
OUTPUT_DIR = r"C:/path/to/your/UnityProject/CrowdingExports/GeneratedFBXVariants"

EXPORT_ONE_FBX_PER_COMBINATION = True
DECIMATE_MODIFIER_TYPE = 'COLLAPSE'
MIN_KEEP_RATIO = 0.01
MAX_KEEP_RATIO = 1.0

# If True, missing/duplicated object IDs stop the script before any export.
STRICT_VALIDATION = True

CID_RE = re.compile(r"__CID_([a-fA-F0-9]{32})")

# -------------------------------------------------------------------------
# ID / naming helpers
# -------------------------------------------------------------------------

def sanitize_name(name: str) -> str:
    safe = []
    for c in str(name):
        safe.append(c if (c.isalnum() or c in ['_', '-', '.']) else '_')
    return ''.join(safe) or 'unnamed'


def extract_cid(name: str) -> Optional[str]:
    match = CID_RE.search(name or "")
    if not match:
        return None
    return match.group(1).lower()


def load_manifest(path: str) -> dict:
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def iter_descendants(root):
    yield root
    stack = list(root.children)
    while stack:
        obj = stack.pop()
        yield obj
        stack.extend(list(obj.children))


def collect_mesh_descendants(root) -> List[bpy.types.Object]:
    return [obj for obj in iter_descendants(root) if obj.type == 'MESH']


def is_descendant_of(obj, possible_parent) -> bool:
    p = obj.parent
    while p is not None:
        if p == possible_parent:
            return True
        p = p.parent
    return False

# -------------------------------------------------------------------------
# Scene object mapping
# -------------------------------------------------------------------------

def build_cid_root_map() -> Dict[str, List[bpy.types.Object]]:
    """
    Returns a map objectId -> list of candidate logical roots.
    If a parent has a CID, child CIDs are not promoted as independent roots.
    """
    candidates_by_id: Dict[str, List[bpy.types.Object]] = {}

    for obj in bpy.context.scene.objects:
        cid = extract_cid(obj.name)
        if not cid:
            continue
        candidates_by_id.setdefault(cid, []).append(obj)

    # Remove CID-bearing children if a CID-bearing parent with the same ID exists.
    roots_by_id: Dict[str, List[bpy.types.Object]] = {}
    for cid, candidates in candidates_by_id.items():
        roots = []
        for obj in candidates:
            has_same_id_parent = False
            for other in candidates:
                if other is obj:
                    continue
                if is_descendant_of(obj, other):
                    has_same_id_parent = True
                    break
            if not has_same_id_parent:
                roots.append(obj)
        roots_by_id[cid] = roots

    return roots_by_id


def validate_manifest_objects(manifest: dict, cid_map: Dict[str, List[bpy.types.Object]]) -> None:
    expected_ids: Set[str] = set()
    for obj_info in manifest.get('objects', []):
        object_id = str(obj_info.get('objectId', '')).lower()
        if object_id:
            expected_ids.add(object_id)

    errors = []
    for object_id in sorted(expected_ids):
        roots = cid_map.get(object_id, [])
        if len(roots) == 0:
            errors.append(f"Missing object in Blender scene: objectId={object_id}")
        elif len(roots) > 1:
            names = [obj.name for obj in roots]
            errors.append(f"Duplicate logical roots for objectId={object_id}: {names}")

    if errors:
        message = "Crowding manifest validation failed:\n" + "\n".join(errors)
        if STRICT_VALIDATION:
            raise RuntimeError(message)
        print("WARNING: " + message)
    else:
        print(f"Crowding manifest validation passed. Objects: {len(expected_ids)}")

# -------------------------------------------------------------------------
# Duplication / decimation / export
# -------------------------------------------------------------------------

def duplicate_hierarchy(root, collection, name_suffix: str):
    """Duplicate a logical root and all descendants, preserving hierarchy and transforms."""
    old_to_new = {}
    all_objects = list(iter_descendants(root))

    for obj in all_objects:
        dup = obj.copy()
        if obj.data is not None:
            dup.data = obj.data.copy()
        dup.animation_data_clear()
        dup.name = sanitize_name(obj.name + "__" + name_suffix)
        collection.objects.link(dup)
        old_to_new[obj] = dup

    for obj in all_objects:
        dup = old_to_new[obj]
        if obj.parent in old_to_new:
            dup.parent = old_to_new[obj.parent]
            dup.matrix_parent_inverse = obj.matrix_parent_inverse.copy()
        dup.matrix_world = obj.matrix_world.copy()

    return old_to_new[root]


def apply_decimate_to_root(root, keep_ratio: float):
    keep_ratio = max(MIN_KEEP_RATIO, min(MAX_KEEP_RATIO, float(keep_ratio)))

    for mesh_obj in collect_mesh_descendants(root):
        if mesh_obj.data is None:
            continue
        if keep_ratio >= 0.999:
            continue

        mod = mesh_obj.modifiers.new(name="CrowdingDecimate", type='DECIMATE')
        mod.decimate_type = DECIMATE_MODIFIER_TYPE
        mod.ratio = keep_ratio

        bpy.ops.object.select_all(action='DESELECT')
        bpy.context.view_layer.objects.active = mesh_obj
        mesh_obj.select_set(True)
        try:
            bpy.ops.object.modifier_apply(modifier=mod.name)
        except Exception as exc:
            print(f"WARNING: could not apply decimation on {mesh_obj.name}: {exc}")
        mesh_obj.select_set(False)


def hide_all_scene_objects():
    for obj in bpy.context.scene.objects:
        obj.select_set(False)
        obj.hide_set(True)


def select_collection_recursive(collection):
    for obj in collection.objects:
        obj.hide_set(False)
        obj.select_set(True)
    for child in collection.children:
        select_collection_recursive(child)


def export_collection_to_fbx(collection, output_path: str):
    hide_all_scene_objects()
    select_collection_recursive(collection)
    bpy.ops.export_scene.fbx(
        filepath=output_path,
        use_selection=True,
        apply_unit_scale=True,
        bake_space_transform=False,
        add_leaf_bones=False,
    )
    bpy.ops.object.select_all(action='DESELECT')


def make_variant_collection(name: str):
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection

# -------------------------------------------------------------------------
# Main generation
# -------------------------------------------------------------------------

def generate_variants(manifest: dict):
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    cid_map = build_cid_root_map()
    validate_manifest_objects(manifest, cid_map)

    for combo in manifest.get('combinations', []):
        key = combo.get('variantKey', 'variant')
        scene_variant_name = sanitize_name(combo.get('expectedSceneVariantName', 'SceneVariant_' + key))
        print(f"\nGenerating {scene_variant_name}...")

        collection = make_variant_collection(scene_variant_name)

        for record in combo.get('objectRecords', []):
            object_id = str(record.get('objectId', '')).lower()
            keep_ratio = float(record.get('keepRatio', 1.0))
            roots = cid_map.get(object_id, [])

            if len(roots) != 1:
                msg = f"Cannot uniquely find objectId={object_id}; candidates={len(roots)}"
                if STRICT_VALIDATION:
                    raise RuntimeError(msg)
                print("WARNING: " + msg)
                continue

            root = roots[0]
            object_variant_name = sanitize_name(record.get('expectedObjectVariantName', root.name + '__' + key))
            duplicated_root = duplicate_hierarchy(root, collection, key)
            duplicated_root.name = object_variant_name
            apply_decimate_to_root(duplicated_root, keep_ratio)

        if EXPORT_ONE_FBX_PER_COMBINATION:
            out_path = os.path.join(OUTPUT_DIR, scene_variant_name + ".fbx")
            export_collection_to_fbx(collection, out_path)
            print(f"Exported {out_path}")


if __name__ == "__main__":
    manifest = load_manifest(MANIFEST_PATH)
    generate_variants(manifest)
    print("\nDone.")
