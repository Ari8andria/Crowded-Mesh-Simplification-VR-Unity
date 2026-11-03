"""
Command-line Blender generator for crowding-aware LOD variants.

Typical call from Unity:
    blender --background --python blender_generate_crowding_lods_cli.py -- \
        --input-scene C:/Project/CrowdingExports/UnitySceneExport/Scene_CID.fbx \
        --manifest C:/Project/CrowdingExports/crowding_batch_manifest.json \
        --output-dir C:/Project/Assets/CrowdingLODGenerated/GeneratedFBXVariants \
        --variant-prefix SceneVariant \
        --strict

What it does:
1. Imports a Unity-exported FBX/GLB scene whose logical object roots contain __CID_<objectId>.
2. Reads the Unity manifest containing keep ratios for each variant.
3. Duplicates and decimates each logical object for each combination.
4. Exports one FBX per combination.
5. Writes CSV reports for achieved triangle ratios and group-budget errors.
"""

import argparse
import csv
import json
import os
import re
import sys
from typing import Dict, List, Optional, Set, Tuple

import bpy

CID_RE = re.compile(r"__CID_([a-fA-F0-9]{32})")
DEFAULT_MIN_KEEP_RATIO = 0.01
DEFAULT_MAX_KEEP_RATIO = 1.0


def parse_args():
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []

    parser = argparse.ArgumentParser(description="Generate crowding-aware LOD FBX variants from a Unity manifest.")
    parser.add_argument("--input-scene", required=True, help="FBX or GLB/GLTF scene exported from Unity with __CID_ suffixes.")
    parser.add_argument("--manifest", required=True, help="Manifest JSON exported by CrowdingBlenderBatchExporter.")
    parser.add_argument("--output-dir", required=True, help="Output directory for generated FBX variants and reports.")
    parser.add_argument("--variant-prefix", default="SceneVariant", help="Prefix used for exported FBX filenames.")
    parser.add_argument("--strict", action="store_true", help="Abort if any manifest object cannot be matched exactly once.")
    parser.add_argument("--min-keep-ratio", type=float, default=DEFAULT_MIN_KEEP_RATIO)
    parser.add_argument("--max-keep-ratio", type=float, default=DEFAULT_MAX_KEEP_RATIO)
    parser.add_argument("--keep-blender-file", action="store_true", help="Save a .blend file containing generated collections.")
    return parser.parse_args(argv)


def sanitize_name(name: str) -> str:
    safe = []
    for c in str(name):
        safe.append(c if (c.isalnum() or c in ["_", "-", "."]) else "_")
    return "".join(safe) or "unnamed"


def extract_cid(name: str) -> Optional[str]:
    match = CID_RE.search(name or "")
    if not match:
        return None
    return match.group(1).lower()


def load_manifest(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()


def import_scene(path: str):
    ext = os.path.splitext(path)[1].lower()
    if ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in [".glb", ".gltf"]:
        bpy.ops.import_scene.gltf(filepath=path)
    else:
        raise RuntimeError(f"Unsupported input scene extension: {ext}. Use FBX, GLB, or GLTF.")


def iter_descendants(root):
    yield root
    stack = list(root.children)
    while stack:
        obj = stack.pop()
        yield obj
        stack.extend(list(obj.children))


def collect_mesh_descendants(root) -> List[bpy.types.Object]:
    return [obj for obj in iter_descendants(root) if obj.type == "MESH"]


def is_descendant_of(obj, possible_parent) -> bool:
    p = obj.parent
    while p is not None:
        if p == possible_parent:
            return True
        p = p.parent
    return False


def build_cid_root_map() -> Dict[str, List[bpy.types.Object]]:
    candidates_by_id: Dict[str, List[bpy.types.Object]] = {}
    for obj in bpy.context.scene.objects:
        cid = extract_cid(obj.name)
        if not cid:
            continue
        candidates_by_id.setdefault(cid, []).append(obj)

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


def expected_ids_from_manifest(manifest: dict) -> Set[str]:
    ids = set()
    for obj_info in manifest.get("objects", []):
        object_id = str(obj_info.get("objectId", "")).lower()
        if object_id:
            ids.add(object_id)
    return ids


def validate_manifest_objects(manifest: dict, cid_map: Dict[str, List[bpy.types.Object]], strict: bool) -> bool:
    expected_ids = expected_ids_from_manifest(manifest)
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
        if strict:
            raise RuntimeError(message)
        print("WARNING: " + message)
        return False

    print(f"Crowding manifest validation passed. Objects: {len(expected_ids)}")
    return True


def count_triangles_for_mesh_object(obj) -> int:
    if obj is None or obj.type != "MESH" or obj.data is None:
        return 0
    mesh = obj.data
    # After import, mesh.polygons is reliable. Assumes triangulated FBX; otherwise n-gons are counted as loop_total-2.
    tris = 0
    for poly in mesh.polygons:
        tris += max(1, len(poly.vertices) - 2)
    return tris


def count_triangles_under_root(root) -> int:
    return sum(count_triangles_for_mesh_object(obj) for obj in collect_mesh_descendants(root))


def duplicate_hierarchy(root, collection, name_suffix: str):
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


def apply_decimate_to_root(root, keep_ratio: float, min_keep_ratio: float, max_keep_ratio: float):
    keep_ratio = max(min_keep_ratio, min(max_keep_ratio, float(keep_ratio)))

    if keep_ratio >= 0.999:
        return

    for mesh_obj in collect_mesh_descendants(root):
        if mesh_obj.data is None:
            continue
        mod = mesh_obj.modifiers.new(name="CrowdingDecimate", type="DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = keep_ratio

        bpy.ops.object.select_all(action="DESELECT")
        bpy.context.view_layer.objects.active = mesh_obj
        mesh_obj.select_set(True)
        try:
            bpy.ops.object.modifier_apply(modifier=mod.name)
        except Exception as exc:
            print(f"WARNING: could not apply decimation on {mesh_obj.name}: {exc}")
        mesh_obj.select_set(False)


def make_variant_collection(name: str):
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


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
        path_mode="AUTO",
    )
    bpy.ops.object.select_all(action="DESELECT")


def build_group_budget_report_rows(combo: dict, object_rows: List[dict]) -> List[dict]:
    groups: Dict[int, List[dict]] = {}
    for row in object_rows:
        groups.setdefault(int(row["groupId"]), []).append(row)

    rows = []
    for group_id, records in sorted(groups.items()):
        initial = sum(int(r["initialTrianglesManifest"]) for r in records)
        requested = sum(float(r["requestedKeepRatio"]) * int(r["initialTrianglesManifest"]) for r in records)
        achieved = sum(int(r["finalTrianglesBlender"]) for r in records)
        requested_k = float(combo.get("groupKeepRatioK", 1.0))
        ideal_group_budget = requested_k * initial
        err_vs_ideal = (achieved - ideal_group_budget) / ideal_group_budget * 100.0 if ideal_group_budget > 0 else 0.0
        err_vs_requested_sum = (achieved - requested) / requested * 100.0 if requested > 0 else 0.0
        rows.append({
            "variantKey": combo.get("variantKey", ""),
            "groupId": group_id,
            "K": requested_k,
            "initialTrianglesManifest": initial,
            "idealGroupBudgetTriangles": round(ideal_group_budget),
            "requestedByObjectTriangles": round(requested),
            "achievedTrianglesBlender": achieved,
            "errorPercentVsIdealK": round(err_vs_ideal, 4),
            "errorPercentVsRequestedObjectSum": round(err_vs_requested_sum, 4),
        })
    return rows


def generate_variants(args, manifest: dict):
    os.makedirs(args.output_dir, exist_ok=True)

    cid_map = build_cid_root_map()
    validate_manifest_objects(manifest, cid_map, args.strict)

    triangle_report_rows = []
    group_report_rows = []

    for combo in manifest.get("combinations", []):
        key = combo.get("variantKey", "variant")
        scene_variant_name = sanitize_name(combo.get("expectedSceneVariantName", args.variant_prefix + "_" + key))
        print(f"\nGenerating {scene_variant_name}...")

        collection = make_variant_collection(scene_variant_name)
        combo_object_rows = []

        for record in combo.get("objectRecords", []):
            object_id = str(record.get("objectId", "")).lower()
            keep_ratio = float(record.get("keepRatio", 1.0))
            roots = cid_map.get(object_id, [])

            if len(roots) != 1:
                msg = f"Cannot uniquely find objectId={object_id}; candidates={len(roots)}"
                if args.strict:
                    raise RuntimeError(msg)
                print("WARNING: " + msg)
                continue

            root = roots[0]
            object_variant_name = sanitize_name(record.get("expectedObjectVariantName", root.name + "__" + key))

            original_triangles_blender = count_triangles_under_root(root)
            duplicated_root = duplicate_hierarchy(root, collection, key)
            duplicated_root.name = object_variant_name

            apply_decimate_to_root(duplicated_root, keep_ratio, args.min_keep_ratio, args.max_keep_ratio)
            final_triangles_blender = count_triangles_under_root(duplicated_root)
            achieved_ratio = final_triangles_blender / original_triangles_blender if original_triangles_blender > 0 else 0.0

            row = {
                "variantKey": key,
                "objectId": object_id,
                "objectName": record.get("rootName", ""),
                "groupId": int(record.get("groupId", -1)),
                "role": record.get("role", ""),
                "requestedKeepRatio": keep_ratio,
                "initialTrianglesManifest": int(record.get("initialTriangles", 0)),
                "initialTrianglesBlender": original_triangles_blender,
                "finalTrianglesBlender": final_triangles_blender,
                "achievedKeepRatioBlender": round(achieved_ratio, 6),
                "absoluteRatioError": round(achieved_ratio - keep_ratio, 6),
            }
            triangle_report_rows.append(row)
            combo_object_rows.append(row)

        group_report_rows.extend(build_group_budget_report_rows(combo, combo_object_rows))

        out_path = os.path.join(args.output_dir, scene_variant_name + ".fbx")
        export_collection_to_fbx(collection, out_path)
        print(f"Exported {out_path}")

    write_csv_report(
        os.path.join(args.output_dir, "crowding_triangle_report.csv"),
        triangle_report_rows,
        [
            "variantKey", "objectId", "objectName", "groupId", "role", "requestedKeepRatio",
            "initialTrianglesManifest", "initialTrianglesBlender", "finalTrianglesBlender",
            "achievedKeepRatioBlender", "absoluteRatioError",
        ],
    )

    write_csv_report(
        os.path.join(args.output_dir, "crowding_group_budget_report.csv"),
        group_report_rows,
        [
            "variantKey", "groupId", "K", "initialTrianglesManifest", "idealGroupBudgetTriangles",
            "requestedByObjectTriangles", "achievedTrianglesBlender", "errorPercentVsIdealK", "errorPercentVsRequestedObjectSum",
        ],
    )

    if args.keep_blender_file:
        blend_path = os.path.join(args.output_dir, "crowding_generated_variants.blend")
        bpy.ops.wm.save_as_mainfile(filepath=blend_path)
        print(f"Saved Blender file: {blend_path}")


def write_csv_report(path: str, rows: List[dict], fieldnames: List[str]):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)
    print(f"Wrote report: {path}")


def main():
    args = parse_args()
    print("Crowding Blender generator started.")
    print(f"Input scene: {args.input_scene}")
    print(f"Manifest: {args.manifest}")
    print(f"Output: {args.output_dir}")

    clear_scene()
    import_scene(args.input_scene)
    manifest = load_manifest(args.manifest)
    generate_variants(args, manifest)
    print("Crowding Blender generator done.")


if __name__ == "__main__":
    main()
