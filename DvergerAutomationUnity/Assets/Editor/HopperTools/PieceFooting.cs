using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The footing a build piece is set down by.
///
/// Vanilla places a ghost by whichever of its colliders comes closest to the point the player aims at, and
/// only counts boxes, spheres, capsules and convex meshes: a non-convex MeshCollider - every model's own -
/// is skipped (Player.UpdatePlacementGhost). A piece whose only primitive colliders sit up on it (a core
/// socket's sphere, a switch's box) is put down on those, buried to them and pushed sideways.
///
/// So each piece gets a thin box under its lowest geometry, measured from the model's own vertices: it is
/// always the collider nearest the ground, and the piece stands on the point aimed at. Measured rather than
/// typed in, so a re-exported model brings its footing with it. Needs the model imported Read/Write, which
/// the game wants anyway (its navmesh builder skips unreadable piece meshes).
/// </summary>
internal static class PieceFooting {
    // How far above the very lowest vertex still counts as touching the ground: a slab's bevel, feet of
    // slightly different lengths.
    private const float ContactBand = 0.05f;
    private const float Height = 0.1f;
    private const int PieceLayer = 10;

    /// <summary>
    /// Adds the footing as a child of <paramref name="body"/>, in its space - so if <paramref name="body"/>
    /// is scaled afterwards, the footing scales with it. Meshes <paramref name="skip"/> picks out (hidden
    /// cores, moving parts) are not measured.
    /// </summary>
    internal static BoxCollider Add(Transform body, GameObject model, Func<GameObject, bool> skip, List<string> notes) {
        List<Vector3> points = new List<Vector3>();
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(includeInactive: true)) {
            if (filter.sharedMesh == null || (skip != null && skip(filter.gameObject))) { continue; }
            if (!filter.sharedMesh.isReadable) {
                notes.Add("ERROR: " + filter.sharedMesh.name + " is not Read/Write - the footing cannot be measured from it.");
                continue;
            }
            foreach (Vector3 vertex in filter.sharedMesh.vertices) {
                points.Add(body.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
            }
        }
        if (points.Count == 0) {
            notes.Add("ERROR: no readable mesh to measure a footing from - the piece will be placed on whatever collider is nearest.");
            return null;
        }

        float lowest = float.MaxValue;
        foreach (Vector3 p in points) { lowest = Mathf.Min(lowest, p.y); }
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in points) {
            if (p.y > lowest + ContactBand) { continue; }
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }

        GameObject go = new GameObject("Footing");
        go.layer = PieceLayer;
        go.transform.SetParent(body, worldPositionStays: false);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = new Vector3((minX + maxX) / 2f, lowest + Height / 2f, (minZ + maxZ) / 2f);
        box.size = new Vector3(Mathf.Max(0.05f, maxX - minX), Height, Mathf.Max(0.05f, maxZ - minZ));
        box.isTrigger = false;
        notes.Add("Footing: " + box.size.x.ToString("0.00") + " x " + box.size.z.ToString("0.00") + " m at y " + lowest.ToString("0.000") + " (model size).");
        return box;
    }
}
