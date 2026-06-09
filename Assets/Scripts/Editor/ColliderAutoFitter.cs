using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically fits Unity primitive colliders (Box / Sphere / Capsule) or
/// compound wall-box colliders to a mesh, using a pipeline inspired by VHACD.
///
/// AUTO mode routing:
///   • Open mesh (has boundary edges, e.g. barrel/bucket/bowl with open top)
///       → CONTAINER: N wall BoxColliders + 1 bottom BoxCollider.
///         Wall dimensions are derived from the actual mesh geometry (inner/outer radius).
///   • Closed mesh or forced primitive:
///       1. VOXELIZATION  — SAT triangle-AABB fills a 3-D voxel grid.
///          Interior scanline-fill captures solid volume.
///       2. DECOMPOSITION — recursively bisects voxel set into convex clusters.
///       3. PRIMITIVE FIT — picks Sphere / Capsule / Box by smallest volume.
/// </summary>
public static class ColliderAutoFitter
{
    // ── Constants ────────────────────────────────────────────────────────────

    public const int DEFAULT_RESOLUTION = 32;

    /// Boundary-edge fraction above this → treat as open hollow shell.
    /// 0.12 filters out small holes (pepper stem, UV cuts) while catching
    /// true containers (barrel, bucket, bowl) whose open top contributes ~20%.
    const float BOUNDARY_THRESHOLD = 0.12f;

    /// Percentile used when measuring primitive dimensions.
    /// The top (1 - OUTLIER_TRIM) fraction of extremal points is ignored so
    /// small protrusions — spines (gai), stems, bumps — do not inflate the
    /// collider beyond the main body shape.
    const float OUTLIER_TRIM  = 0.95f;

    const int MIN_VOXELS      = 8;
    const int CONTAINER_WALLS = 8;
    const string PREFIX       = "Collider_";

    public enum ShapeOverride { Auto, Sphere, Capsule, Box, Container }

    // ── Public API ───────────────────────────────────────────────────────────

    public static void FitCollider(GameObject root,
                                   ShapeOverride shapeOverride = ShapeOverride.Auto,
                                   int resolution = DEFAULT_RESOLUTION)
    {
        MeshFilter mf = root.GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning($"[AutoFit] No mesh found under '{root.name}'.");
            return;
        }

        RemoveAutoColliders(root);

        Mesh mesh = mf.sharedMesh;
        Vector3[] mv = mesh.vertices;
        var localVerts = new Vector3[mv.Length];
        for (int i = 0; i < mv.Length; i++)
            localVerts[i] = root.transform.InverseTransformPoint(
                                mf.transform.TransformPoint(mv[i]));
        int[] tris = mesh.triangles;

        ComputePCA(localVerts, out Vector3 centroid, out Vector3[] axes, out float[] extents);

        // ── Shell / container detection ────────────────────────────────────
        float boundaryFrac = BoundaryEdgeFraction(tris);
        // A genuine container (barrel, bucket, bowl) has its boundary edges
        // forming ONE large connected loop (the open rim) whose radius is
        // significant relative to the object (>= 25% of outerR).
        // UV-seam artifacts produce many tiny disconnected boundary segments
        // with small radii — they fail the loop-size test.
        bool isShell = boundaryFrac > 0.03f &&
                       HasLargeOpeningLoop(localVerts, tris, centroid);

        if (shapeOverride == ShapeOverride.Container ||
            (shapeOverride == ShapeOverride.Auto && isShell))
        {
            FitContainer(root, localVerts, centroid, axes, extents);
            Debug.Log($"[AutoFit] '{root.name}' → Container " +
                      $"(boundaryFrac={boundaryFrac:P0})");
            EditorUtility.SetDirty(root);
            return;
        }

        // ── Voxelize ────────────────────────────────────────────────────────
        Bounds bounds = new Bounds(localVerts[0], Vector3.zero);
        foreach (var v in localVerts) bounds.Encapsulate(v);

        HashSet<Vector3Int> voxels = Voxelize(localVerts, tris, bounds, resolution,
                                              out float voxelSize);
        if (voxels.Count == 0)
        {
            Debug.LogWarning($"[AutoFit] Voxelization produced no voxels for '{root.name}'.");
            return;
        }

        // ── Forced shape or segment by constriction ──────────────────────────
        ShapeType? forced = null;
        if      (shapeOverride == ShapeOverride.Sphere)  forced = ShapeType.Sphere;
        else if (shapeOverride == ShapeOverride.Capsule) forced = ShapeType.Capsule;
        else if (shapeOverride == ShapeOverride.Box)     forced = ShapeType.Box;

        List<List<Vector3Int>> clusters;
        if (forced.HasValue)
        {
            clusters = new List<List<Vector3Int>> { new List<Vector3Int>(voxels) };
            Debug.Log($"[AutoFit] '{root.name}' → 1 primitive (forced {forced})");
        }
        else
        {
            // Segment by cross-sectional constriction along the principal axis.
            // Each natural "pinch point" (stem-body junction, joint, waist) becomes
            // a segment boundary, letting each part pick its own best primitive.
            clusters = SegmentByConstriction(voxels, axes, bounds, voxelSize);
            Debug.Log($"[AutoFit] '{root.name}' → {voxels.Count} voxels → {clusters.Count} segment(s)");
        }

        // ── Fit primitive to each segment ─────────────────────────────────────
        // padding = 0 so collider never exceeds mesh surface.
        // Single segment → use actual mesh vertices for an exact fit.
        // Multi segment  → use voxel centres from that segment.
        const float padding = 0f;
        int idx = 0;
        foreach (var cluster in clusters)
        {
            if (cluster.Count < MIN_VOXELS) continue;
            float regionVol = cluster.Count * voxelSize * voxelSize * voxelSize;
            // Always use voxel centres: they represent the object bulk at the
            // current resolution and are unaffected by UV-split duplicate verts.
            Vector3[] pts = VoxelsToPoints(cluster, bounds.min, voxelSize);
            ComputePCA(pts, out Vector3 cc, out Vector3[] ax, out float[] ex);
            ShapeType shape = forced ?? BestShape(pts, cc, ax, ex, padding, regionVol);
            string cName = clusters.Count == 1 ? PREFIX + "Main" : PREFIX + idx;
            MakeColliderChild(root, cName, pts, cc, ax, shape, padding);
            idx++;
        }

        EditorUtility.SetDirty(root);
    }

    // ── Remove existing auto-colliders ───────────────────────────────────────

    static void RemoveAutoColliders(GameObject root)
    {
        var toDelete = new List<Transform>();
        foreach (Transform child in root.transform)
            if (child.name.StartsWith(PREFIX))
                toDelete.Add(child);
        foreach (var t in toDelete)
            Undo.DestroyObjectImmediate(t.gameObject);
    }

    // =========================================================================
    // SHELL / CONTAINER DETECTION
    // =========================================================================

    /// A genuine open container (barrel, bucket, bowl) has its boundary edges
    /// arranged as one or two large connected loops (the open rim/top).
    /// Solid objects with UV seam splits have many tiny, scattered boundary
    /// edge fragments whose individual radii are small relative to the object.
    ///
    /// Algorithm:
    ///   1. Collect all boundary edges (shared by exactly 1 triangle).
    ///   2. Build a vertex-adjacency graph from those edges.
    ///   3. Find connected components (BFS/DFS).
    ///   4. Measure the radius of the LARGEST component.
    ///   5. If that radius ≥ 25% of outerR → genuine opening → Container.
    static bool HasLargeOpeningLoop(Vector3[] verts, int[] tris, Vector3 centroid)
    {
        // Build edge-count map (same logic as BoundaryEdgeFraction)
        var edgeCount = new Dictionary<long, int>(tris.Length);
        for (int i = 0; i < tris.Length; i += 3)
        {
            for (int j = 0; j < 3; j++)
            {
                int a  = tris[i + j], b = tris[i + (j + 1) % 3];
                int lo = System.Math.Min(a, b), hi = System.Math.Max(a, b);
                long key = ((long)lo << 32) | (uint)hi;
                edgeCount.TryGetValue(key, out int cnt);
                edgeCount[key] = cnt + 1;
            }
        }

        // Build adjacency of boundary vertices
        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1) continue;
            int lo = (int)(kv.Key >> 32);
            int hi = (int)(kv.Key & 0xFFFFFFFFU);
            if (!adj.ContainsKey(lo)) adj[lo] = new List<int>();
            if (!adj.ContainsKey(hi)) adj[hi] = new List<int>();
            adj[lo].Add(hi);
            adj[hi].Add(lo);
        }
        if (adj.Count == 0) return false;

        // Object outer radius for normalization
        float outerR = 0f;
        foreach (var v in verts)
            outerR = Mathf.Max(outerR, Vector3.Distance(v, centroid));
        if (outerR < 1e-5f) return false;

        // Find connected components; measure radius of the largest one
        var visited      = new HashSet<int>();
        float largestRadius = 0f;

        foreach (int start in adj.Keys)
        {
            if (visited.Contains(start)) continue;

            var component = new List<int>();
            var stack     = new Stack<int>();
            stack.Push(start); visited.Add(start);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                component.Add(cur);
                foreach (int nb in adj[cur])
                    if (!visited.Contains(nb)) { visited.Add(nb); stack.Push(nb); }
            }

            // Centroid + max-radius of this loop
            Vector3 cen = Vector3.zero;
            foreach (int vi in component) cen += verts[vi];
            cen /= component.Count;
            float r = 0f;
            foreach (int vi in component)
                r = Mathf.Max(r, Vector3.Distance(verts[vi], cen));
            if (r > largestRadius) largestRadius = r;
        }

        // Container opening must span ≥ 25% of the object
        return largestRadius / outerR >= 0.25f;
    }

    static float BoundaryEdgeFraction(int[] tris)
    {
        if (tris.Length == 0) return 0f;
        var edgeCount = new Dictionary<long, int>(tris.Length);
        for (int i = 0; i < tris.Length; i += 3)
        {
            for (int j = 0; j < 3; j++)
            {
                int a = tris[i + j];
                int b = tris[i + (j + 1) % 3];
                int lo = System.Math.Min(a, b);
                int hi = System.Math.Max(a, b);
                long key = ((long)lo << 32) | (uint)hi;
                edgeCount.TryGetValue(key, out int cnt);
                edgeCount[key] = cnt + 1;
            }
        }
        int boundary = 0;
        foreach (var kv in edgeCount)
            if (kv.Value == 1) boundary++;
        return (float)boundary / edgeCount.Count;
    }

    // =========================================================================
    // CONTAINER MODE — hollow open-top objects (barrels, buckets, bowls)
    // =========================================================================
    //
    // Generates CONTAINER_WALLS BoxColliders arranged radially around the
    // height axis, plus one flat BoxCollider for the bottom plate.
    //
    // Wall dimensions come from the actual mesh geometry:
    //   • outerR = max radial distance from height axis
    //   • innerR = min radial distance in the upper band (avoids bottom plate)
    //   • wallThick = outerR - innerR
    //   • wallH     = full height minus bottom thickness

    static int FindSymmetryAxis(float[] ex)
    {
        // The "height axis" is the one whose cross-section is most circular.
        // Roundness when axis i is the height axis:
        //   = min(other two extents) / max(other two extents)
        // Higher score = more circular → better height axis candidate.
        float safe0 = Mathf.Max(ex[1], 1e-6f), safe1 = Mathf.Max(ex[0], 1e-6f);
        float r0 = Mathf.Min(ex[1], ex[2]) / safe0;        // axis 0 as height
        float r1 = Mathf.Min(ex[0], ex[2]) / safe1;        // axis 1 as height
        float r2 = Mathf.Min(ex[0], ex[1]) / safe1;        // axis 2 as height
        if (r0 >= r1 && r0 >= r2) return 0;
        if (r1 >= r2)             return 1;
        return 2;
    }

    static void FitContainer(GameObject root, Vector3[] verts,
                              Vector3 centroid, Vector3[] axes, float[] extents)
    {
        // 1. Height axis = axis with most circular cross-section
        int     symIdx = FindSymmetryAxis(extents);
        Vector3 upAxis = axes[symIdx];
        if (Vector3.Dot(upAxis, Vector3.up) < 0f) upAxis = -upAxis;

        // 2. Measure height range and radial distances for every vertex
        float minH = float.MaxValue, maxH = float.MinValue;
        float outerR = 0f;
        var hArr = new float[verts.Length];
        var rArr = new float[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 d = verts[i] - centroid;
            float h = Vector3.Dot(d, upAxis);
            float r = (d - h * upAxis).magnitude;
            hArr[i] = h; rArr[i] = r;
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
            if (r > outerR) outerR = r;
        }
        float totalH = maxH - minH;

        // 3. Inner radius: 10th-percentile of radii in the upper 80% band.
        //    Using percentile (not minimum) filters out stray center vertices
        //    (nails, hoops, UV seam verts) that would pull innerR to ~0.
        //    Only consider vertices that are >= 50% of outerR to avoid
        //    interior detail geometry.
        float bandLow  = minH + totalH * 0.20f;
        float minRadiusForWall = outerR * 0.50f;
        var bandRadii = new List<float>();
        for (int i = 0; i < verts.Length; i++)
            if (hArr[i] >= bandLow && rArr[i] >= minRadiusForWall)
                bandRadii.Add(rArr[i]);

        float innerR;
        if (bandRadii.Count >= 4)
        {
            bandRadii.Sort();
            // 10th percentile → stable estimate of where wall inner face begins
            innerR = bandRadii[Mathf.Max(0, Mathf.RoundToInt(bandRadii.Count * 0.10f) - 1)];
        }
        else
        {
            innerR = outerR * 0.75f;   // fallback: assume 25% wall thickness
        }

        // If inner ≈ outer (thin-shell or single-surface mesh), use 15% of outerR
        if (innerR >= outerR * 0.92f)
            innerR = outerR * 0.85f;

        float wallThick = Mathf.Max(outerR - innerR, outerR * 0.10f, 0.005f);
        // Outer face of each wall box sits exactly at outerR
        float midR = outerR - wallThick * 0.5f;

        // 4. Detect bottom plate: check if the bottom band contains vertices
        //    that are radially interior (r < outerR * 0.5), indicating a solid
        //    disc. Wall-only objects (open pipe, open bucket, etc.) have their
        //    bottom-band vertices only near outerR, so hasBottom stays false.
        float botBandMax = minH + totalH * 0.15f;
        float botThick   = 0f;
        bool  hasBottom  = false;
        for (int i = 0; i < verts.Length; i++)
        {
            if (hArr[i] > botBandMax) continue;
            if (rArr[i] < outerR * 0.5f) hasBottom = true;  // interior vertex = solid plate
            float span = hArr[i] - minH;
            if (span > botThick) botThick = span;
        }
        // Minimum bottom thickness = wall thickness, so wall and bottom connect
        botThick = Mathf.Max(botThick, wallThick, totalH * 0.04f, 0.005f);

        // 5. Perpendicular tangential basis
        Vector3 perpA = Vector3.Cross(upAxis, Vector3.up).normalized;
        if (perpA.sqrMagnitude < 0.01f)
            perpA = Vector3.Cross(upAxis, Vector3.right).normalized;
        Vector3 perpB = Vector3.Cross(upAxis, perpA).normalized;

        // 6. Wall dimensions
        //    Height: FULL totalH so walls always cover top-to-bottom
        float wallH    = totalH;
        float wallCtrH = (minH + maxH) * 0.5f;

        // Arc width: chord at midR that covers each sector with 30% margin.
        // This guarantees no gaps between adjacent boxes even when viewed from outerR.
        // chord = 2 * midR * tan(π/N), × 1.30 safety margin
        float arcWidth = 2f * midR * Mathf.Tan(Mathf.PI / CONTAINER_WALLS) * 1.30f;

        // 7. Generate wall BoxColliders
        for (int i = 0; i < CONTAINER_WALLS; i++)
        {
            float   angle  = i * (2f * Mathf.PI / CONTAINER_WALLS);
            Vector3 outDir = (Mathf.Cos(angle) * perpA +
                              Mathf.Sin(angle) * perpB).normalized;

            var go = new GameObject(string.Format("{0}Wall_{1:D2}", PREFIX, i));
            Undo.RegisterCreatedObjectUndo(go, "AutoFit Wall");
            go.layer = root.layer;
            go.transform.SetParent(root.transform, false);
            // Radial centre = midR so outer face aligns to outerR exactly
            go.transform.localPosition =
                centroid + upAxis * wallCtrH + outDir * midR;
            // local Z = outDir (radial/thin)  local Y = upAxis  local X = tangential
            go.transform.localRotation = Quaternion.LookRotation(outDir, upAxis);

            var bc    = go.AddComponent<BoxCollider>();
            bc.center = Vector3.zero;
            bc.size   = new Vector3(arcWidth, wallH, wallThick);
        }

        // 8. Bottom BoxCollider — only if a solid plate was detected
        if (hasBottom)
        {
            var bot = new GameObject(PREFIX + "Bottom");
            Undo.RegisterCreatedObjectUndo(bot, "AutoFit Bottom");
            bot.layer = root.layer;
            bot.transform.SetParent(root.transform, false);
            bot.transform.localPosition =
                centroid + upAxis * (minH + botThick * 0.5f);
            Vector3 fwd = Vector3.Cross(perpA, upAxis).normalized;
            bot.transform.localRotation = Quaternion.LookRotation(fwd, upAxis);

            var bc    = bot.AddComponent<BoxCollider>();
            bc.center = Vector3.zero;
            // Width = outerR * 2 so bottom plate fills the inner disc
            bc.size   = new Vector3(outerR * 2f, botThick, outerR * 2f);
        }
    }

    // =========================================================================
    // VOXELIZATION — SAT triangle-AABB + scanline interior fill
    // =========================================================================

    static HashSet<Vector3Int> Voxelize(Vector3[] verts, int[] tris,
                                        Bounds bounds, int resolution,
                                        out float voxelSize)
    {
        float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        voxelSize = maxExtent / Mathf.Max(resolution, 1);
        if (voxelSize < 1e-6f) { voxelSize = 0.01f; return new HashSet<Vector3Int>(); }

        Vector3 origin = bounds.min;
        float   half   = voxelSize * 0.5f;
        int nx = Mathf.CeilToInt(bounds.size.x / voxelSize) + 1;
        int ny = Mathf.CeilToInt(bounds.size.y / voxelSize) + 1;
        int nz = Mathf.CeilToInt(bounds.size.z / voxelSize) + 1;

        var result = new HashSet<Vector3Int>();

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            Vector3 v0 = verts[tris[ti]];
            Vector3 v1 = verts[tris[ti + 1]];
            Vector3 v2 = verts[tris[ti + 2]];

            Vector3 tMin = Vector3.Min(Vector3.Min(v0, v1), v2) - origin;
            Vector3 tMax = Vector3.Max(Vector3.Max(v0, v1), v2) - origin;

            int x0 = Mathf.Max(0, Mathf.FloorToInt(tMin.x / voxelSize));
            int x1 = Mathf.Min(nx - 1, Mathf.CeilToInt(tMax.x / voxelSize));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(tMin.y / voxelSize));
            int y1 = Mathf.Min(ny - 1, Mathf.CeilToInt(tMax.y / voxelSize));
            int z0 = Mathf.Max(0, Mathf.FloorToInt(tMin.z / voxelSize));
            int z1 = Mathf.Min(nz - 1, Mathf.CeilToInt(tMax.z / voxelSize));

            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                Vector3 center = origin + new Vector3(
                    (x + 0.5f) * voxelSize,
                    (y + 0.5f) * voxelSize,
                    (z + 0.5f) * voxelSize);
                if (TriAABBOverlap(v0, v1, v2, center, half))
                    result.Add(new Vector3Int(x, y, z));
            }
        }

        // Scanline interior fill (Y column)
        var cols = new Dictionary<(int, int), (int yMin, int yMax)>();
        foreach (var v in result)
        {
            var key = (v.x, v.z);
            if (!cols.TryGetValue(key, out var range))
                cols[key] = (v.y, v.y);
            else
                cols[key] = (System.Math.Min(range.yMin, v.y),
                             System.Math.Max(range.yMax, v.y));
        }
        foreach (var kv in cols)
            for (int y = kv.Value.yMin; y <= kv.Value.yMax; y++)
                result.Add(new Vector3Int(kv.Key.Item1, y, kv.Key.Item2));

        return result;
    }

    static bool TriAABBOverlap(Vector3 v0, Vector3 v1, Vector3 v2,
                                Vector3 boxCenter, float h)
    {
        Vector3 a = v0 - boxCenter, b = v1 - boxCenter, c = v2 - boxCenter;
        if (!OverlapOnAxis(Vector3.right,   a, b, c, h)) return false;
        if (!OverlapOnAxis(Vector3.up,      a, b, c, h)) return false;
        if (!OverlapOnAxis(Vector3.forward, a, b, c, h)) return false;

        Vector3 e0 = b - a, e1 = c - b;
        Vector3 n  = Vector3.Cross(e0, e1);
        float   rr = h * (Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z));
        if (Mathf.Abs(Vector3.Dot(n, a)) > rr) return false;

        Vector3[] edges = { e0, e1, a - c };
        Vector3[] tv    = { a, b, c };
        Vector3[] ba    = { Vector3.right, Vector3.up, Vector3.forward };
        foreach (var edge in edges)
        foreach (var axis in ba)
        {
            Vector3 sep = Vector3.Cross(edge, axis);
            if (sep.sqrMagnitude < 1e-10f) continue;
            float p0 = Vector3.Dot(sep, tv[0]);
            float p1 = Vector3.Dot(sep, tv[1]);
            float p2 = Vector3.Dot(sep, tv[2]);
            float rs = h * (Mathf.Abs(sep.x) + Mathf.Abs(sep.y) + Mathf.Abs(sep.z));
            if (Mathf.Min(p0, Mathf.Min(p1, p2)) >  rs) return false;
            if (Mathf.Max(p0, Mathf.Max(p1, p2)) < -rs) return false;
        }
        return true;
    }

    static bool OverlapOnAxis(Vector3 axis, Vector3 a, Vector3 b, Vector3 c, float h)
    {
        float p0 = Vector3.Dot(axis, a);
        float p1 = Vector3.Dot(axis, b);
        float p2 = Vector3.Dot(axis, c);
        return Mathf.Min(p0, Mathf.Min(p1, p2)) <= h &&
               Mathf.Max(p0, Mathf.Max(p1, p2)) >= -h;
    }

    // =========================================================================
    // CONSTRICTION-PROFILE SEGMENTATION  (Medial-Axis inspired)
    //
    // Full pipeline (theo đúng thuật toán được mô tả):
    //
    //  1. VOXELIZE                  — done before calling this method.
    //
    //  2. MEDIAL AXIS (approximate) — for each 1-voxel-wide slice perpendicular
    //     to the principal axis, find the centroid of voxels in that slice.
    //     The sequence of centroids {C_s} is the discrete medial axis:
    //     the centerline threading through the object.
    //
    //  3. RADIUS PROFILE (curvature proxy)
    //     For each slice, compute max perpendicular distance from C_s to any
    //     voxel in that slice → R_s (= inscribed-sphere radius at that slice).
    //     This profile captures "how wide the object is" at every point along
    //     the medial axis, which is the right quantity for pinch-point detection.
    //
    //  4. SMOOTH  — 5-point average suppresses single-voxel noise.
    //
    //  5. CONSTRICTION DETECTION (curvature test)
    //     A slice is a constriction (điểm thắt) when ALL THREE hold:
    //       a) Local minimum of R (first-derivative test).
    //       b) Positive curvature: R[s-1] - 2R[s] + R[s+1] > 0 (second-
    //          derivative test) — rules out flat valleys on boxy objects.
    //       c) Both flanking peaks (within PEAK_WINDOW) are ≥ 1/CONSTRICTION_RATIO
    //          times R[s] — rules out endpoints and gentle tapers.
    //     Requiring all three prevents false cuts on spheres, boxes, and
    //     complex-geometry objects like cabbage.
    //
    //  6. CUT + GROUP — voxels between cuts form independent segments.
    //
    //  7. VOLUME-RATIO FIT — each segment picks Sphere / Capsule / Box whose
    //     volume best matches the segment's voxel volume.
    // =========================================================================

    static List<List<Vector3Int>> SegmentByConstriction(
        HashSet<Vector3Int> voxels, Vector3[] axes, Bounds bounds, float voxelSize)
    {
        var voxelList = new List<Vector3Int>(voxels);
        int n = voxelList.Count;
        if (n == 0) return new List<List<Vector3Int>>();

        // ── Pre-compute world-space position for every voxel ─────────────────
        var positions = new Vector3[n];
        for (int i = 0; i < n; i++)
            positions[i] = bounds.min + new Vector3(
                (voxelList[i].x + 0.5f) * voxelSize,
                (voxelList[i].y + 0.5f) * voxelSize,
                (voxelList[i].z + 0.5f) * voxelSize);

        // ── Step 1 — Project onto principal axis, assign slice indices ────────
        float minP = float.MaxValue, maxP = float.MinValue;
        float[] proj = new float[n];
        for (int i = 0; i < n; i++)
        {
            float p = Vector3.Dot(positions[i], axes[0]);
            proj[i] = p;
            if (p < minP) minP = p;
            if (p > maxP) maxP = p;
        }

        int slices = Mathf.CeilToInt((maxP - minP) / voxelSize) + 1;
        if (slices < 4) return new List<List<Vector3Int>> { voxelList };

        int[] sliceOf   = new int[n];
        var   sliceSum  = new Vector3[slices];   // sum of positions per slice
        var   sliceCnt  = new int[slices];        // voxel count per slice

        for (int i = 0; i < n; i++)
        {
            int s = Mathf.Clamp(
                Mathf.FloorToInt((proj[i] - minP) / voxelSize), 0, slices - 1);
            sliceOf[i]  = s;
            sliceSum[s] += positions[i];
            sliceCnt[s]++;
        }

        // ── Step 2 — Medial axis: centroid of each slice ──────────────────────
        var sliceCen = new Vector3[slices];
        for (int s = 0; s < slices; s++)
            if (sliceCnt[s] > 0)
                sliceCen[s] = sliceSum[s] / sliceCnt[s];

        // ── Step 3 — Radius profile: max perpendicular distance from centroid ─
        // This is the inscribed-sphere radius at each cross-section.
        float[] profile = new float[slices];
        for (int i = 0; i < n; i++)
        {
            int s = sliceOf[i];
            if (sliceCnt[s] < 3) continue;   // ignore degenerate slices
            Vector3 d = positions[i] - sliceCen[s];
            d -= Vector3.Dot(d, axes[0]) * axes[0];  // keep only perpendicular part
            float r = d.magnitude;
            if (r > profile[s]) profile[s] = r;
        }

        // ── Step 4a — Smooth RADIUS profile (5-point MA) ─────────────────────
        float[] smoothR = new float[slices];
        for (int s = 0; s < slices; s++)
        {
            float sum = 0f; int cnt = 0;
            for (int k = -2; k <= 2; k++)
            {
                int t = s + k;
                if (t >= 0 && t < slices && sliceCnt[t] >= 3)
                    { sum += profile[t]; cnt++; }
            }
            smoothR[s] = cnt > 0 ? sum / cnt : 0f;
        }

        // ── Step 4b — Smooth VOXEL COUNT profile (5-point MA) ────────────────
        // Count = how many voxels in each slice. Thin stems have low count even
        // when their max-radius (spread of shoots) is deceptively large.
        float[] smoothC    = new float[slices];
        float   globalPeak = 0f;
        for (int s = 0; s < slices; s++)
        {
            float sum = 0f; int cnt = 0;
            for (int k = -2; k <= 2; k++)
            {
                int t = s + k;
                if (t >= 0 && t < slices) { sum += sliceCnt[t]; cnt++; }
            }
            smoothC[s] = cnt > 0 ? sum / cnt : 0f;
            if (smoothC[s] > globalPeak) globalPeak = smoothC[s];
        }

        // ── Step 5 — Constriction detection (two-signal OR logic) ────────────
        //
        // Signal A — Radius pinch (works for objects like pepper, sausage):
        //   a) local minimum of smoothed radius
        //   b) positive curvature (2nd derivative > 0)
        //   c) BOTH flanking radius peaks are significantly larger
        //
        // Signal B — Count drop (works for stem/shoot junctions like onion):
        //   a) local minimum of smoothed count
        //   b) count < COUNT_DROP × global peak   (much thinner than main body)
        //   c) LEFT flank count is significantly larger  (leaving the main body)
        //      Right side need not recover — the shoot stays thin.
        //
        // A cut fires when EITHER signal fires.
        const float RADIUS_RATIO  = 0.60f;  // radius pinch: < 60% of each flank
        const float COUNT_DROP    = 0.38f;  // count drop:   < 38% of global peak
        const float COUNT_FLANK   = 0.60f;  // count flank:  left must be > this × left peak
        const int   PEAK_WINDOW   = 8;
        var cutSlices = new HashSet<int>();

        for (int s = 1; s < slices - 1; s++)
        {
            // ── Signal A: radius pinch ──────────────────────────────────────
            bool signalA = false;
            if (smoothR[s] > 0f &&
                smoothR[s] <= smoothR[s - 1] && smoothR[s] <= smoothR[s + 1])
            {
                float curv = smoothR[s - 1] - 2f * smoothR[s] + smoothR[s + 1];
                if (curv > 0f)
                {
                    float lR = 0f, rR = 0f;
                    for (int k = 1; k <= PEAK_WINDOW; k++)
                    {
                        if (s - k >= 0)     lR = Mathf.Max(lR, smoothR[s - k]);
                        if (s + k < slices) rR = Mathf.Max(rR, smoothR[s + k]);
                    }
                    signalA = lR > 0f && smoothR[s] < RADIUS_RATIO * lR
                           && rR > 0f && smoothR[s] < RADIUS_RATIO * rR;
                }
            }

            // ── Signal B: count drop ────────────────────────────────────────
            bool signalB = false;
            if (globalPeak > 0f &&
                smoothC[s] < COUNT_DROP * globalPeak &&
                smoothC[s] <= smoothC[s - 1] && smoothC[s] <= smoothC[s + 1])
            {
                float lC = 0f;
                for (int k = 1; k <= PEAK_WINDOW; k++)
                    if (s - k >= 0) lC = Mathf.Max(lC, smoothC[s - k]);
                signalB = lC > 0f && smoothC[s] < COUNT_FLANK * lC;
            }

            if (signalA || signalB)
                cutSlices.Add(s);
        }

        if (cutSlices.Count == 0)
            return new List<List<Vector3Int>> { voxelList };

        // ── Step 6 — Label bands between cuts; cut slices are discarded ───────
        int[] bandOf = new int[slices];
        int   band   = 0;
        for (int s = 0; s < slices; s++)
        {
            if (cutSlices.Contains(s)) { bandOf[s] = -1; continue; }
            if (s == 0 || bandOf[s - 1] < 0) band++;
            bandOf[s] = band;
        }

        // ── Step 7 — Group voxels by band ────────────────────────────────────
        var segMap = new Dictionary<int, List<Vector3Int>>();
        for (int i = 0; i < n; i++)
        {
            int l = bandOf[sliceOf[i]];
            if (l < 0) continue;
            if (!segMap.ContainsKey(l)) segMap[l] = new List<Vector3Int>();
            segMap[l].Add(voxelList[i]);
        }

        return new List<List<Vector3Int>>(segMap.Values);
    }

    // =========================================================================
    // PRIMITIVE FIT
    // =========================================================================

    static Vector3[] VoxelsToPoints(List<Vector3Int> cluster, Vector3 origin, float vs)
    {
        var pts = new Vector3[cluster.Count];
        for (int i = 0; i < cluster.Count; i++)
        {
            var v = cluster[i];
            pts[i] = origin + new Vector3(
                (v.x + 0.5f) * vs, (v.y + 0.5f) * vs, (v.z + 0.5f) * vs);
        }
        return pts;
    }

    // Returns the value at percentile pct (0–1) of vals after sorting.
    // Calling with OUTLIER_TRIM ignores the top (1-OUTLIER_TRIM) extremal
    // points so thin protrusions (spines, gai, bumps) are filtered out.
    static float TrimmedMax(float[] vals, float pct)
    {
        System.Array.Sort(vals);
        int idx = Mathf.Clamp(
            Mathf.RoundToInt(vals.Length * pct) - 1, 0, vals.Length - 1);
        return Mathf.Max(0f, vals[idx]);
    }

    enum ShapeType { Sphere, Capsule, Box }

    // regionVol = voxel count × voxelSize³ ≈ actual volume of the mesh region.
    // Score = regionVol / primitiveVol: highest score = best shape match.
    // All measurements use TrimmedMax so small protrusions do not inflate the
    // primitive type selection beyond the main body shape.
    static ShapeType BestShape(Vector3[] pts, Vector3 centroid,
                                Vector3[] axes, float[] extents,
                                float pad, float regionVol = 0f)
    {
        int n = pts.Length;
        if (n == 0) return ShapeType.Sphere;

        // ── Trimmed Sphere ────────────────────────────────────────────────────
        var dists = new float[n];
        for (int i = 0; i < n; i++) dists[i] = Vector3.Distance(pts[i], centroid);
        float r = TrimmedMax(dists, OUTLIER_TRIM) + pad;
        float volSphere = (4f / 3f) * Mathf.PI * r * r * r;

        // ── Trimmed Capsule (along axes[0] = longest axis) ────────────────────
        var projArr = new float[n];
        var perpArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 d = pts[i] - centroid;
            float   p = Vector3.Dot(d, axes[0]);
            projArr[i] = p;
            perpArr[i] = (d - p * axes[0]).magnitude;
        }
        float capR = TrimmedMax(perpArr, OUTLIER_TRIM) + pad;
        // Trim both ends of the length axis: spines can protrude either way
        var sortProj = (float[])projArr.Clone();
        System.Array.Sort(sortProj);
        int trimN  = Mathf.Clamp(Mathf.RoundToInt(n * (1f - OUTLIER_TRIM)), 0, n / 2 - 1);
        float capLen = sortProj[n - 1 - trimN] - sortProj[trimN];
        float capH   = Mathf.Max(capLen + 2f * pad, 2f * capR);
        float cylH   = Mathf.Max(0f, capH - 2f * capR);
        float volCap = (4f / 3f) * Mathf.PI * capR * capR * capR
                     + Mathf.PI * capR * capR * cylH;

        // ── Trimmed Box ───────────────────────────────────────────────────────
        var abs0 = new float[n]; var abs1 = new float[n]; var abs2 = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 d = pts[i] - centroid;
            abs0[i] = Mathf.Abs(Vector3.Dot(d, axes[0]));
            abs1[i] = Mathf.Abs(Vector3.Dot(d, axes[1]));
            abs2[i] = Mathf.Abs(Vector3.Dot(d, axes[2]));
        }
        float bh0 = TrimmedMax(abs0, OUTLIER_TRIM) + pad;
        float bh1 = TrimmedMax(abs1, OUTLIER_TRIM) + pad;
        float bh2 = TrimmedMax(abs2, OUTLIER_TRIM) + pad;
        float volBox = 8f * bh0 * bh1 * bh2;

        if (regionVol <= 0f)
        {
            if (volSphere <= volCap && volSphere <= volBox) return ShapeType.Sphere;
            if (volCap    <= volBox)                        return ShapeType.Capsule;
            return ShapeType.Box;
        }

        // Volume-ratio scoring: highest score = best match = least wasted space
        float sS = regionVol / volSphere;
        float sC = regionVol / volCap;
        float sB = regionVol / volBox;
        if (sS >= sC && sS >= sB) return ShapeType.Sphere;
        if (sC >= sB)             return ShapeType.Capsule;
        return ShapeType.Box;
    }

    static void MakeColliderChild(GameObject root, string name,
                                  Vector3[] pts, Vector3 centroid,
                                  Vector3[] axes, ShapeType shape, float pad)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "AutoFit " + name);
        go.layer = root.layer;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = centroid;
        go.transform.localRotation = BuildPCARot(axes);
        switch (shape)
        {
            case ShapeType.Sphere:  FitSphere (go, pts, centroid, pad);        break;
            case ShapeType.Capsule: FitCapsule(go, pts, centroid, axes, pad);  break;
            default:                FitBox    (go, pts, centroid, axes, pad);  break;
        }
    }

    static void FitSphere(GameObject go, Vector3[] pts, Vector3 centroid, float pad)
    {
        var dists = new float[pts.Length];
        for (int i = 0; i < pts.Length; i++) dists[i] = Vector3.Distance(pts[i], centroid);
        var sc = go.AddComponent<SphereCollider>();
        sc.center = Vector3.zero;
        sc.radius = TrimmedMax(dists, OUTLIER_TRIM) + pad;
    }

    static void FitCapsule(GameObject go, Vector3[] pts, Vector3 centroid,
                            Vector3[] axes, float pad)
    {
        int n = pts.Length;
        var projArr = new float[n];
        var perpArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 d = pts[i] - centroid;
            float   p = Vector3.Dot(d, axes[0]);
            projArr[i] = p;
            perpArr[i] = (d - p * axes[0]).magnitude;
        }
        float r = TrimmedMax(perpArr, OUTLIER_TRIM) + pad;
        System.Array.Sort(projArr);
        int trimN    = Mathf.Clamp(Mathf.RoundToInt(n * (1f - OUTLIER_TRIM)), 0, n / 2 - 1);
        float capLen = projArr[n - 1 - trimN] - projArr[trimN];
        var cc = go.AddComponent<CapsuleCollider>();
        cc.center    = Vector3.zero;
        cc.direction = 1;
        cc.radius    = r;
        cc.height    = Mathf.Max(capLen + 2f * pad, 2f * r);
    }

    static void FitBox(GameObject go, Vector3[] pts, Vector3 centroid,
                       Vector3[] axes, float pad)
    {
        int n = pts.Length;
        var abs0 = new float[n]; var abs1 = new float[n]; var abs2 = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 d = pts[i] - centroid;
            abs0[i] = Mathf.Abs(Vector3.Dot(d, axes[0]));
            abs1[i] = Mathf.Abs(Vector3.Dot(d, axes[1]));
            abs2[i] = Mathf.Abs(Vector3.Dot(d, axes[2]));
        }
        float hy = TrimmedMax(abs0, OUTLIER_TRIM) + pad;
        float hx = TrimmedMax(abs1, OUTLIER_TRIM) + pad;
        float hz = TrimmedMax(abs2, OUTLIER_TRIM) + pad;
        var bc = go.AddComponent<BoxCollider>();
        bc.center = Vector3.zero;
        bc.size   = new Vector3(hx * 2f, hy * 2f, hz * 2f);
    }

    // ── PCA rotation ─────────────────────────────────────────────────────────

    static Quaternion BuildPCARot(Vector3[] axes)
    {
        Vector3 up    = axes[0];
        Vector3 right = (axes[1] - Vector3.Dot(axes[1], up) * up).normalized;
        if (right.sqrMagnitude < 0.01f) right = Vector3.right;
        Vector3 fwd   = Vector3.Cross(right, up).normalized;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        return Quaternion.LookRotation(fwd, up);
    }

    // ── PCA ──────────────────────────────────────────────────────────────────

    static void ComputePCA(Vector3[] pts,
                           out Vector3   centroid,
                           out Vector3[] axes,
                           out float[]   halfExtents)
    {
        centroid = Vector3.zero;
        foreach (var p in pts) centroid += p;
        centroid /= pts.Length;

        double cxx=0,cxy=0,cxz=0,cyy=0,cyz=0,czz=0;
        foreach (var p in pts)
        {
            double dx=p.x-centroid.x, dy=p.y-centroid.y, dz=p.z-centroid.z;
            cxx+=dx*dx; cxy+=dx*dy; cxz+=dx*dz;
            cyy+=dy*dy; cyz+=dy*dz; czz+=dz*dz;
        }
        double n = pts.Length;
        var cov = new Mat3((float)(cxx/n),(float)(cxy/n),(float)(cxz/n),
                           (float)(cxy/n),(float)(cyy/n),(float)(cyz/n),
                           (float)(cxz/n),(float)(cyz/n),(float)(czz/n));

        Vector3 e0 = PowerIter(cov,              new Vector3(1f,  0.1f, 0.05f));
        Vector3 e1 = PowerIter(Deflate(cov, e0), new Vector3(0.05f, 1f,  0.1f));
        Vector3 e2 = Vector3.Cross(e0, e1).normalized;

        float h0=0, h1=0, h2=0;
        foreach (var p in pts)
        {
            Vector3 d = p - centroid;
            h0 = Mathf.Max(h0, Mathf.Abs(Vector3.Dot(d, e0)));
            h1 = Mathf.Max(h1, Mathf.Abs(Vector3.Dot(d, e1)));
            h2 = Mathf.Max(h2, Mathf.Abs(Vector3.Dot(d, e2)));
        }
        var pairs = new (float h, Vector3 e)[] { (h0,e0),(h1,e1),(h2,e2) };
        System.Array.Sort(pairs, (a,b) => b.h.CompareTo(a.h));
        axes        = new[] { pairs[0].e, pairs[1].e, pairs[2].e };
        halfExtents = new[] { pairs[0].h, pairs[1].h, pairs[2].h };
    }

    struct Mat3
    {
        public float m00,m01,m02, m10,m11,m12, m20,m21,m22;
        public Mat3(float a,float b,float c, float d,float e,float f, float g,float h,float k)
        { m00=a;m01=b;m02=c; m10=d;m11=e;m12=f; m20=g;m21=h;m22=k; }
        public Vector3 Mul(Vector3 v) => new Vector3(
            m00*v.x+m01*v.y+m02*v.z,
            m10*v.x+m11*v.y+m12*v.z,
            m20*v.x+m21*v.y+m22*v.z);
    }

    static Vector3 PowerIter(Mat3 M, Vector3 seed, int iters = 32)
    {
        Vector3 v = seed.normalized;
        for (int i = 0; i < iters; i++)
        {
            v = M.Mul(v);
            float mag = v.magnitude;
            if (mag < 1e-10f) break;
            v /= mag;
        }
        return v.normalized;
    }

    static Mat3 Deflate(Mat3 M, Vector3 v)
    {
        Vector3 Mv     = M.Mul(v);
        float   lambda = Vector3.Dot(v, Mv);
        return new Mat3(
            M.m00-lambda*v.x*v.x, M.m01-lambda*v.x*v.y, M.m02-lambda*v.x*v.z,
            M.m10-lambda*v.y*v.x, M.m11-lambda*v.y*v.y, M.m12-lambda*v.y*v.z,
            M.m20-lambda*v.z*v.x, M.m21-lambda*v.z*v.y, M.m22-lambda*v.z*v.z);
    }
}
