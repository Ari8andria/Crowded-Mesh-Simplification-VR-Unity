"""
Mesh-only Blender decimation script for the Unity crowding-aware LOD pipeline.

Usage:
    blender --background --python blender_generate_mesh_lods_cli.py -- manifest.json output_dir import_map.json

This version is designed for low-poly scenes where you do NOT want artificial lower bounds
such as MIN_KEEP_RATIO or MIN_FINAL_TRIANGLES.

Main idea:
    - Keep the requested ratio exactly as the applied ratio.
    - Reduce holes/topology damage by preserving boundaries and by decimating each connected
      component separately, then joining the components back into a single mesh.

What this script does NOT do:
    - It does not enforce a minimum keep ratio.
    - It does not enforce a minimum number of final triangles.
    - It cannot guarantee zero holes if the requested ratio is extremely aggressive.

What it does to reduce holes:
    - Marks open boundary edges as SEAM and SHARP before decimation.
    - Uses Decimate delimit flags: SEAM, SHARP, MATERIAL, UV, NORMAL.
    - Separates loose connected components and applies the same requested ratio to each component.
    - Recalculates normals after decimation.
    - Triangulates the final mesh before exporting JSON to Unity.
"""

import bpy
import json
import csv
import os
import sys
import hashlib
import traceback
from datetime import datetime, timezone


# -----------------------------------------------------------------------------
# Hole/topology safety options.
# -----------------------------------------------------------------------------
# No minimum ratio is applied. This is intentional for low-poly experiments.
CLAMP_REQUESTED_RATIO_TO_VALID_RANGE = True
LOWER_RATIO_EPSILON = 0.001

# Boundary/topology protection.
MARK_OPEN_BOUNDARIES_AS_SEAM_AND_SHARP = True
DECIMATE_DELIMIT_FLAGS = {"SEAM", "SHARP", "MATERIAL", "UV", "NORMAL"}
USE_COLLAPSE_TRIANGULATE = True

# Component-safe decimation.
# If True, the mesh is separated into loose connected components. Each component is decimated
# with the same requested ratio, then all components are joined again.
DECIMATE_CONNECTED_COMPONENTS_SEPARATELY = True

# Final cleanup.
RECALCULATE_NORMALS_AFTER_DECIMATION = True
TRIANGULATE_FINAL_MESH = True


# -----------------------------------------------------------------------------
# CLI / utility.
# -----------------------------------------------------------------------------
def parse_args():
    argv = sys.argv
    if "--" not in argv:
        raise RuntimeError("Expected arguments after --: manifest.json output_dir import_map.json")
    args = argv[argv.index("--") + 1:]
    if len(args) < 3:
        raise RuntimeError(
            "Usage: blender --background --python blender_generate_mesh_lods_cli.py -- "
            "manifest.json output_dir import_map.json"
        )
    return os.path.abspath(args[0]), os.path.abspath(args[1]), os.path.abspath(args[2])


def ensure_dir(path):
    if path:
        os.makedirs(path, exist_ok=True)


def clear_scene():
    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()


def sanitize(name):
    out = []
    for ch in str(name):
        out.append(ch if ch.isalnum() or ch in ["_", "-", "."] else "_")
    return "".join(out) or "unnamed"


def stable_hash(text, length=12):
    h = hashlib.sha1(str(text).encode("utf-8", errors="ignore")).hexdigest()
    return h[:max(6, int(length))]


def shorten_middle(text, max_len):
    text = sanitize(text)
    max_len = int(max_len)
    if len(text) <= max_len:
        return text
    if max_len <= 16:
        return text[:max_len]
    keep_left = max_len // 2 - 2
    keep_right = max_len - keep_left - 3
    return text[:keep_left] + "___" + text[-keep_right:]


def compact_slot_name(slot_id, max_len=90):
    """
    Create a deterministic but short filename for Windows-safe output paths.

    The previous version used the full slotId as the JSON filename. On Windows,
    complex scene/object names can push paths beyond MAX_PATH and Blender may stop
    before writing the import map. The hash preserves uniqueness while the tail
    keeps the file human-readable.
    """
    s = sanitize(slot_id)
    h = stable_hash(s, 12)
    tail = s[-48:] if len(s) > 48 else s
    return shorten_middle(f"{h}__{tail}", max_len)


def compact_mesh_name(variant_key, slot_id, max_len=120):
    vk = shorten_middle(variant_key, 36)
    slot = compact_slot_name(slot_id, 70)
    return shorten_middle(f"{vk}__{slot}", max_len)


def safe_join_json_path(directory, filename_without_ext, max_total_len=240):
    """Return a JSON path short enough for old Windows path handling."""
    filename_without_ext = shorten_middle(filename_without_ext, 96)
    path = os.path.join(directory, filename_without_ext + ".json")

    # Keep a safety margin below 260 because Blender/Python/Unity may still hit
    # legacy path limits depending on project settings and OS policy.
    if os.name == "nt" and len(path) > max_total_len:
        short = stable_hash(path, 16)
        path = os.path.join(directory, short + ".json")

    return path


def clamp_ratio(ratio):
    ratio = float(ratio)
    if not CLAMP_REQUESTED_RATIO_TO_VALID_RANGE:
        return ratio
    return max(LOWER_RATIO_EPSILON, min(1.0, ratio))


# -----------------------------------------------------------------------------
# OBJ import.
# -----------------------------------------------------------------------------
def import_obj(filepath):
    clear_scene()

    if not os.path.exists(filepath):
        raise FileNotFoundError(filepath)

    # Blender 4.x/5.x has wm.obj_import; Blender 3.x commonly uses import_scene.obj.
    if hasattr(bpy.ops.wm, "obj_import"):
        bpy.ops.wm.obj_import(
            filepath=filepath,
            forward_axis="NEGATIVE_Y",
            up_axis="Z"
        )
    else:
        try:
            bpy.ops.preferences.addon_enable(module="io_scene_obj")
        except Exception:
            pass

        if not hasattr(bpy.ops, "import_scene") or not hasattr(bpy.ops.import_scene, "obj"):
            raise RuntimeError(
                "OBJ importer not available. Enable the io_scene_obj add-on or use a newer Blender version."
            )

        bpy.ops.import_scene.obj(
            filepath=filepath,
            axis_forward="-Y",
            axis_up="Z"
        )

    mesh_objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not mesh_objects:
        raise RuntimeError(f"No mesh object imported from {filepath}")

    if len(mesh_objects) == 1:
        bpy.ops.object.select_all(action="DESELECT")
        mesh_objects[0].select_set(True)
        bpy.context.view_layer.objects.active = mesh_objects[0]
        return mesh_objects[0]

    # If OBJ created multiple objects, join them into one mesh while preserving material slots.
    bpy.ops.object.select_all(action="DESELECT")
    for obj in mesh_objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = mesh_objects[0]
    bpy.ops.object.join()
    return bpy.context.view_layer.objects.active


# -----------------------------------------------------------------------------
# Topology helpers.
# -----------------------------------------------------------------------------
def count_triangles(obj):
    mesh = obj.data
    mesh.update()
    mesh.calc_loop_triangles()
    return len(mesh.loop_triangles)


def mark_open_boundaries_as_seam_and_sharp(obj):
    """
    Mark edges used by only one polygon as seam+sharp.
    With Decimate delimit flags, this helps preserve existing open borders.
    """
    if obj is None or obj.type != "MESH" or obj.data is None:
        return 0

    mesh = obj.data
    mesh.update()

    edge_use_count = [0 for _ in mesh.edges]
    edge_key_to_index = {edge.key: i for i, edge in enumerate(mesh.edges)}

    for poly in mesh.polygons:
        for edge_key in poly.edge_keys:
            idx = edge_key_to_index.get(edge_key)
            if idx is not None:
                edge_use_count[idx] += 1

    marked = 0
    for idx, edge in enumerate(mesh.edges):
        if edge_use_count[idx] <= 1:
            edge.use_seam = True
            try:
                edge.use_edge_sharp = True
            except Exception:
                pass
            marked += 1

    mesh.update()
    return marked


def recalculate_normals(obj):
    if obj is None or obj.type != "MESH":
        return

    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    try:
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.normals_make_consistent(inside=False)
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass


def triangulate_object(obj):
    if obj is None or obj.type != "MESH":
        return

    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    tri = obj.modifiers.new(name="CrowdingTriangulate", type="TRIANGULATE")
    bpy.ops.object.modifier_apply(modifier=tri.name)


def separate_loose_components(obj):
    """
    Separate obj into loose connected components and return the resulting mesh objects.
    If separation creates only one object, returns [obj].
    """
    if obj is None or obj.type != "MESH":
        return []

    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    # Need edit mode for mesh.separate(type='LOOSE').
    try:
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.separate(type="LOOSE")
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass
        return [obj]

    components = [o for o in bpy.context.selected_objects if o.type == "MESH"]

    if not components:
        components = [o for o in bpy.context.scene.objects if o.type == "MESH"]

    # Stable order for deterministic output.
    components.sort(key=lambda o: o.name)
    return components


def join_components(components, final_name):
    components = [o for o in components if o is not None and o.type == "MESH"]

    if not components:
        raise RuntimeError("Cannot join zero components")

    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="DESELECT")

    for obj in components:
        obj.select_set(True)

    bpy.context.view_layer.objects.active = components[0]

    if len(components) > 1:
        bpy.ops.object.join()

    final_obj = bpy.context.view_layer.objects.active
    final_obj.name = final_name
    final_obj.data.name = final_name
    return final_obj


# -----------------------------------------------------------------------------
# Decimation.
# -----------------------------------------------------------------------------
def apply_decimate_modifier(obj, requested_ratio):
    """
    Apply Blender Decimate Collapse using exactly requested_ratio, with boundary delimiters.
    No minimum ratio or minimum triangle count is imposed.
    """
    ratio = clamp_ratio(requested_ratio)

    bpy.ops.object.mode_set(mode="OBJECT") if bpy.ops.object.mode_set.poll() else None
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    if MARK_OPEN_BOUNDARIES_AS_SEAM_AND_SHARP:
        mark_open_boundaries_as_seam_and_sharp(obj)

    if ratio < 0.999:
        mod = obj.modifiers.new(name="CrowdingDecimate", type="DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = ratio

        # Preserve important discontinuities as much as Blender allows.
        try:
            mod.delimit = DECIMATE_DELIMIT_FLAGS
        except Exception:
            pass

        try:
            mod.use_collapse_triangulate = USE_COLLAPSE_TRIANGULATE
        except Exception:
            pass

        bpy.ops.object.modifier_apply(modifier=mod.name)

    if RECALCULATE_NORMALS_AFTER_DECIMATION:
        recalculate_normals(obj)

    return ratio


def apply_decimate_component_safe(obj, requested_ratio, final_name):
    """
    Decimate a mesh while preserving disconnected parts better:
      1. Separate loose connected components.
      2. Apply the same requested ratio to each component.
      3. Join components back.
      4. Triangulate final mesh.

    This avoids a global decimation pass from sacrificing small disconnected parts more aggressively
    than large parts. It still respects the requested ratio; no lower bound is used.
    """
    ratio = clamp_ratio(requested_ratio)

    if not DECIMATE_CONNECTED_COMPONENTS_SEPARATELY:
        applied_ratio = apply_decimate_modifier(obj, ratio)
        obj.name = final_name
        obj.data.name = final_name
        if TRIANGULATE_FINAL_MESH:
            triangulate_object(obj)
        return obj, applied_ratio, 1

    components = separate_loose_components(obj)

    if not components:
        raise RuntimeError("No connected components after separation")

    for comp in components:
        apply_decimate_modifier(comp, ratio)

    final_obj = join_components(components, final_name)

    if RECALCULATE_NORMALS_AFTER_DECIMATION:
        recalculate_normals(final_obj)

    if TRIANGULATE_FINAL_MESH:
        triangulate_object(final_obj)

    return final_obj, ratio, len(components)


# -----------------------------------------------------------------------------
# Unity JSON mesh export.
# -----------------------------------------------------------------------------
def blender_to_unity(v):
    # Inverse of Unity->Blender used by the Unity OBJ exporter: Blender(x,z,-y) -> Unity(x,-z,y)
    return (float(v.x), float(-v.z), float(v.y))


def export_mesh_json(obj, json_path, mesh_name):
    ensure_dir(os.path.dirname(json_path))

    mesh = obj.data
    mesh.update()
    mesh.calc_loop_triangles()

    uv_layer = mesh.uv_layers.active.data if mesh.uv_layers and mesh.uv_layers.active else None

    vertices = []
    uvs = []
    vertex_map = {}
    submesh_tris = {}

    def get_corner_vertex(loop_index):
        loop = mesh.loops[loop_index]
        vertex = mesh.vertices[loop.vertex_index]
        co = blender_to_unity(vertex.co)

        if uv_layer is not None:
            uv = uv_layer[loop_index].uv
            uv_tuple = (float(uv.x), float(uv.y))
        else:
            uv_tuple = (0.0, 0.0)

        # Quantize slightly to keep JSON deterministic and reduce duplicate vertices.
        key = (
            round(co[0], 8), round(co[1], 8), round(co[2], 8),
            round(uv_tuple[0], 8), round(uv_tuple[1], 8)
        )

        if key in vertex_map:
            return vertex_map[key]

        index = len(vertices) // 3
        vertices.extend([co[0], co[1], co[2]])
        uvs.extend([uv_tuple[0], uv_tuple[1]])
        vertex_map[key] = index
        return index

    tri_count = 0
    for tri in mesh.loop_triangles:
        mat_index = int(tri.material_index)
        if mat_index not in submesh_tris:
            submesh_tris[mat_index] = []

        indices = [get_corner_vertex(li) for li in tri.loops]
        submesh_tris[mat_index].extend(indices)
        tri_count += 1

    submeshes = []
    for mat_index in sorted(submesh_tris.keys()):
        submeshes.append({
            "materialIndex": mat_index,
            "triangles": submesh_tris[mat_index]
        })

    payload = {
        "meshName": mesh_name,
        "vertexCount": len(vertices) // 3,
        "triangleCount": tri_count,
        "vertices": vertices,
        "uv": uvs,
        "subMeshes": submeshes
    }

    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, separators=(",", ":"))

    return payload["vertexCount"], tri_count


# -----------------------------------------------------------------------------
# Main.
# -----------------------------------------------------------------------------
def main():
    manifest_path, output_dir, import_map_path = parse_args()
    ensure_dir(output_dir)
    ensure_dir(os.path.dirname(import_map_path))

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    slots = {slot["slotId"]: slot for slot in manifest.get("meshSlots", [])}
    variants = manifest.get("variants", [])

    records = []
    triangle_csv_rows = []
    group_budget = {}

    total_jobs = sum(len(v.get("meshRecords", [])) for v in variants)
    done = 0

    for variant in variants:
        variant_key = variant["variantKey"]
        variant_dir = os.path.join(output_dir, variant_key)
        ensure_dir(variant_dir)

        for rec in variant.get("meshRecords", []):
            slot_id = rec["slotId"]
            slot = slots.get(slot_id)

            if slot is None:
                print(f"WARNING: slot not found in manifest: {slot_id}")
                continue

            if not slot.get("supported", True):
                print(f"SKIP unsupported slot {slot_id}: {slot.get('warning', '')}")
                continue

            source_obj = slot.get("sourceObjPath", "")
            if not os.path.isabs(source_obj):
                source_obj = os.path.abspath(os.path.join(os.path.dirname(manifest_path), source_obj))

            requested_ratio = clamp_ratio(float(rec.get("keepRatio", 1.0)))
            safe_slot = compact_slot_name(slot_id)
            mesh_name = compact_mesh_name(variant_key, slot_id)
            json_path = safe_join_json_path(variant_dir, safe_slot)

            try:
                obj = import_obj(source_obj)
                obj.name = mesh_name
                obj.data.name = mesh_name

                component_count_before = 1
                if DECIMATE_CONNECTED_COMPONENTS_SEPARATELY:
                    # component count is returned after separation.
                    obj, applied_ratio, component_count_before = apply_decimate_component_safe(
                        obj,
                        requested_ratio,
                        mesh_name
                    )
                else:
                    obj, applied_ratio, component_count_before = apply_decimate_component_safe(
                        obj,
                        requested_ratio,
                        mesh_name
                    )

                vertex_count, final_triangles = export_mesh_json(obj, json_path, mesh_name)

            except Exception as ex:
                print(f"ERROR while processing {variant_key}/{slot_id}: {ex}")
                raise

            initial_triangles = int(rec.get("initialTriangles", 0))
            achieved = float(final_triangles) / float(initial_triangles) if initial_triangles > 0 else 0.0

            records.append({
                "variantKey": variant_key,
                "slotId": slot_id,
                "objectId": rec.get("objectId", ""),
                "rootName": rec.get("rootName", ""),
                "childPath": rec.get("childPath", ""),
                "rendererType": rec.get("rendererType", ""),
                # Backward-compatible field name: this is a decimated mesh JSON path in v5.
                "exportedFbxPath": json_path,
                "exportedFbxAssetPathHint": "",
                "exportedObjectName": mesh_name,
                "requestedKeepRatio": requested_ratio,
                "appliedKeepRatio": applied_ratio,
                "initialTriangles": initial_triangles,
                "finalTriangles": int(final_triangles),
                "achievedKeepRatio": achieved,
                "componentCount": int(component_count_before)
            })

            triangle_csv_rows.append({
                "variantKey": variant_key,
                "slotId": slot_id,
                "objectId": rec.get("objectId", ""),
                "childPath": rec.get("childPath", ""),
                "requestedKeepRatio": requested_ratio,
                "appliedKeepRatio": applied_ratio,
                "initialTriangles": initial_triangles,
                "finalTriangles": int(final_triangles),
                "achievedKeepRatio": achieved,
                "componentCount": int(component_count_before)
            })

            group_key = (variant_key, int(rec.get("groupId", -1)))
            if group_key not in group_budget:
                group_budget[group_key] = {
                    "initial": 0,
                    "final": 0,
                    "requestedK": float(variant.get("groupKeepRatioK", 0.0))
                }
            group_budget[group_key]["initial"] += initial_triangles
            group_budget[group_key]["final"] += int(final_triangles)

            done += 1
            if done % 25 == 0 or done == total_jobs:
                print(f"Processed {done}/{total_jobs} mesh variant jobs")

    import_map = {
        "manifestVersion": "2.0-mesh-only-import-map",
        "generatedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sourceManifestPath": manifest_path,
        "blenderOutputDirectory": output_dir,
        "recordCount": len(records),
        "records": records
    }

    with open(import_map_path, "w", encoding="utf-8") as f:
        json.dump(import_map, f, indent=2)

    triangle_report_path = os.path.join(output_dir, "crowding_triangle_report.csv")
    with open(triangle_report_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "variantKey", "slotId", "objectId", "childPath",
            "requestedKeepRatio", "appliedKeepRatio",
            "initialTriangles", "finalTriangles", "achievedKeepRatio", "componentCount"
        ])
        writer.writeheader()
        writer.writerows(triangle_csv_rows)

    budget_report_path = os.path.join(output_dir, "crowding_group_budget_report.csv")
    with open(budget_report_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=[
            "variantKey", "groupId", "requestedK", "initialTriangles", "requestedTriangles",
            "finalTriangles", "achievedK", "errorPercent"
        ])
        writer.writeheader()
        for (variant_key, group_id), data in sorted(group_budget.items()):
            initial = data["initial"]
            final = data["final"]
            requested_k = data["requestedK"]
            requested_triangles = requested_k * initial
            achieved_k = final / initial if initial > 0 else 0.0
            err = 100.0 * (final - requested_triangles) / requested_triangles if requested_triangles > 0 else 0.0
            writer.writerow({
                "variantKey": variant_key,
                "groupId": group_id,
                "requestedK": requested_k,
                "initialTriangles": initial,
                "requestedTriangles": requested_triangles,
                "finalTriangles": final,
                "achievedK": achieved_k,
                "errorPercent": err
            })

    print("Mesh-only LOD generation complete.")
    print("No MIN_KEEP_RATIO / MIN_FINAL_TRIANGLES constraints were applied.")
    print("Component-safe decimation:", DECIMATE_CONNECTED_COMPONENTS_SEPARATELY)
    print("Delimit flags:", sorted(list(DECIMATE_DELIMIT_FLAGS)))
    print(f"Import map: {import_map_path}")
    print(f"Triangle report: {triangle_report_path}")
    print(f"Group budget report: {budget_report_path}")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("FATAL: Blender mesh LOD generation failed before import map was written.")
        traceback.print_exc()
        sys.exit(1)
