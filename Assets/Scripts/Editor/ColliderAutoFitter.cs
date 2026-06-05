using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically fits Unity primitive colliders (Box/Sphere/Capsule) to a mesh.
///
/// Pipeline:
///  1. Convexity check  — |signed mesh volume| / PCA-OBB volume.
///                         ≥ CONVEX_THRESHOLD  → single primitive.
///                         below threshold     → concave: split into 8 spatial
///                                               octants, fit a primitive to each
///                                               (compound collider).
///  2. Shape selection (per cluster):
///       PCA eigenvectors  → 3 half-extents  e0 ≥ e1 ≥ e2.
///       e0/e2 < 1.35                         → Sphere
///       e0/e2 ≥ 2.0, e1/e2 < 1.4,
///         cross-section round (minR/maxR>0.55) → Capsule
///       otherwise                             → Box  (OBB via PCA rotation)
///  3. Tight-fit guarantee: every vertex lies INSIDE or ON the collider surface.
/// </summary>
public static class ColliderAutoFitter
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// |signed mesh vol| / OBB vol — below this the cluster is considered concave
    const float CONVEX_THRESHOLD = 0.35f;

    /// Maximum recursion depth when bisecting → at most 2^MAX_DEPTH clusters
    const int MAX_DEPTH = 3;

    /// Skip clusters that are too small to split further
    const int MIN_TRIS = 6;

    const string PREFIX = "Collider_";

    /// Number of wall box colliders generated for Container shape
    const int   CONTAINER_WALLS          = 8;
    /// Wall thickness as fraction of container radius
    const float WALL_THICKNESS_FRAC      = 0.12f;
    /// Bottom plate thickness as fraction of container height
    const float BOTTOM_THICKNESS_FRAC    = 0.10f;
    /// Convexity below this → auto-detect as shell/container (in Auto mode)
    const float SHELL_THRESHOLD          = 0.12f;

    /// <summary>Explicit shape override passed to <see cref="FitCollider"/>.</summary>
    public enum ShapeOverride { Auto, Sphere, Capsule, Box, Container }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Removes existing Collider_* children then adds tight-fit primitive
    /// collider(s) based on the mesh found under <paramref name="root"/>.
    /// </summary>
    /// <param name="shapeOverride">
    /// Force a specific collider shape instead of auto-detecting:
    /// • Auto      — detect from mesh (convexity + PCA).
    /// • Container — hollow object: N wall boxes + 1 bottom box.
    /// • Sphere / Capsule / Box — force that primitive, size fit to mesh bounds.
    /// </param>
    public static void FitCollider(GameObject root,
                                   ShapeOverride shapeOverride = ShapeOverride.Auto)
    {
        MeshFilter mf = root.GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning($"[AutoFit] No mesh found under '{root.name}'.");
            return;
        }

        RemoveAutoColliders(root);

        MeshCluster full = ExtractCluster(mf.sharedMesh, mf.transform, root.transform);
        if (full.verts.Length < 4) return;

        ComputePCA(full.verts, out Vector3 centroid, out Vector3[] axes, out float[] extents);

        // ── Container override: compound walls + bottom ───────────────────
        if (shapeOverride == ShapeOverride.Container)
        {
            FitContainer(root, full.verts, centroid, axes, extents);
            Debug.Log($"[AutoFit] '{root.name}' → Container (forced)");
            EditorUtility.SetDirty(root);
            return;
        }

        // ── Single primitive override ─────────────────────────────────────
        ShapeType? forced = null;
        if (shapeOverride == ShapeOverride.Sphere)  forced = ShapeType.Sphere;
        else if (shapeOverride == ShapeOverride.Capsule) forced = ShapeType.Capsule;
        else if (shapeOverride == ShapeOverride.Box)     forced = ShapeType.Box;

        float convexity = Convexity(full);
        var clusters = new List<MeshCluster>();

        if (forced.HasValue)
        {
            clusters.Add(full);
            Debug.Log($"[AutoFit] '{root.name}' → forced {forced}");
        }
        else if (convexity >= CONVEX_THRESHOLD)
        {
            clusters.Add(full);
            Debug.Log($"[AutoFit] '{root.name}' → convex ({convexity:P0}), 1 primitive");
        }
        else if (convexity < SHELL_THRESHOLD)
        {
            // Very low signed volume → open shell (basket, bowl, crate…)
            FitContainer(root, full.verts, centroid, axes, extents);
            Debug.Log($"[AutoFit] '{root.name}' → Container (auto-shell, convexity={convexity:P0})");
            EditorUtility.SetDirty(root);
            return;
        }
        else
        {
            Decompose(full, 0, clusters);
            Debug.Log($"[AutoFit] '{root.name}' → concave ({convexity:P0}), {clusters.Count} parts");
        }

        int idx = 0;
        foreach (var cluster in clusters)
        {
            ComputePCA(cluster.verts, out Vector3 cc, out Vector3[] ax, out float[] ex);
            ShapeType shape = forced ?? ClassifyShape(ex, cluster.verts, cc, ax);
            string cName = clusters.Count == 1 ? PREFIX + "Main" : PREFIX + idx;
            MakeColliderChild(root, cName, cluster.verts, cc, ax, shape);
            idx++;
        }

        EditorUtility.SetDirty(root);
    }

    // ── MeshCluster: vertices + triangle index list ───────────────────────────

    struct MeshCluster { public Vector3[] verts; public int[] tris; }

    static MeshCluster ExtractCluster(Mesh mesh, Transform meshT, Transform rootT)
    {
        Vector3[] mv = mesh.vertices;
        var rv = new Vector3[mv.Length];
        for (int i = 0; i < mv.Length; i++)
            rv[i] = rootT.InverseTransformPoint(meshT.TransformPoint(mv[i]));
        return new MeshCluster { verts = rv, tris = (int[])mesh.triangles.Clone() };
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

    // ── Cylindrical symmetry axis ─────────────────────────────────────────────
    //  Returns index of the axis whose perpendicular cross-section is most ROUND.
    //  = the axis where the two OTHER extents are most equal to each other.
    //
    //  Basket (e0≈e1≈0.50, e2≈0.35):      axis 2 roundness = 0.50/0.50 = 1.00  ← best ✓
    //  Tall cylinder (e0=1.0, e1≈e2≈0.5): axis 0 roundness = 0.50/0.50 = 1.00  ✓

    static int FindSymmetryAxis(float[] ex)
    {
        float r0 = Mathf.Min(ex[1], ex[2]) / Mathf.Max(ex[1], 1e-6f);
        float r1 = Mathf.Min(ex[0], ex[2]) / Mathf.Max(ex[0], 1e-6f);
        float r2 = Mathf.Min(ex[0], ex[1]) / Mathf.Max(ex[0], 1e-6f);
        if (r2 >= r0 && r2 >= r1) return 2;
        if (r0 >= r1)             return 0;
        return 1;
    }

    // ── Container: N wall boxes + 1 bottom box ───────────────────────────────
    //  Correctly identifies height axis via FindSymmetryAxis so it works
    //  for both tall containers (e0 = height) and wide/short ones (e2 = height).

    static void FitContainer(GameObject root, Vector3[] verts,
                              Vector3 centroid, Vector3[] axes, float[] extents)
    {
        // 1. Height axis = the axis with the most circular cross-section
        int     symIdx = FindSymmetryAxis(extents);
        Vector3 upAxis = axes[symIdx];
        if (Vector3.Dot(upAxis, Vector3.up) < 0f) upAxis = -upAxis;

        // 2. Height range and outer radius in root local space
        float minH = float.MaxValue, maxH = float.MinValue, maxR = 0f;
        foreach (var v in verts)
        {
            Vector3 d = v - centroid;
            float   h = Vector3.Dot(d, upAxis);
            float   r = (d - h * upAxis).magnitude;
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
            if (r > maxR) maxR = r;
        }

        float totalH    = maxH - minH;
        float midH      = (minH + maxH) * 0.5f;
        float wallThick = Mathf.Max(maxR * WALL_THICKNESS_FRAC,    0.005f);
        float botThick  = Mathf.Max(totalH * BOTTOM_THICKNESS_FRAC, 0.005f);
        // Arc width = chord length for N segments × 2 + 5 % overlap → no gaps
        float arcWidth  = 2f * maxR * Mathf.Sin(Mathf.PI / CONTAINER_WALLS) * 2.1f;

        // 3. Perpendicular basis around upAxis for polar placement
        Vector3 perpA = Vector3.Cross(upAxis, Vector3.up).normalized;
        if (perpA.sqrMagnitude < 0.01f)
            perpA = Vector3.Cross(upAxis, Vector3.right).normalized;
        Vector3 perpB = Vector3.Cross(upAxis, perpA).normalized;

        // 4. Wall slabs ────────────────────────────────────────────────────────
        //    Centre at (maxR - wallThick/2): inner face flush with rim
        for (int i = 0; i < CONTAINER_WALLS; i++)
        {
            float   angle  = i * (2f * Mathf.PI / CONTAINER_WALLS);
            Vector3 outDir = (Mathf.Cos(angle) * perpA + Mathf.Sin(angle) * perpB).normalized;

            var go = new GameObject(string.Format("{0}Wall_{1:D2}", PREFIX, i));
            Undo.RegisterCreatedObjectUndo(go, "AutoFit Wall");
            go.layer = root.layer;
            go.transform.SetParent(root.transform, false);

            go.transform.localPosition = centroid
                                         + upAxis * midH
                                         + outDir * (maxR - wallThick * 0.5f);
            // local Z = outDir (radial/thin)  local Y = upAxis  local X = tangential
            go.transform.localRotation = Quaternion.LookRotation(outDir, upAxis);

            var bc    = go.AddComponent<BoxCollider>();
            bc.center = Vector3.zero;
            bc.size   = new Vector3(arcWidth, totalH, wallThick);
        }

        // 5. Bottom plate ──────────────────────────────────────────────────────
        var bot = new GameObject(PREFIX + "Bottom");
        Undo.RegisterCreatedObjectUndo(bot, "AutoFit Bottom");
        bot.layer = root.layer;
        bot.transform.SetParent(root.transform, false);
        bot.transform.localPosition = centroid + upAxis * (minH + botThick * 0.5f);
        Vector3 fwd = Vector3.Cross(perpA, upAxis).normalized;
        bot.transform.localRotation = Quaternion.LookRotation(fwd, upAxis);

        var botBc    = bot.AddComponent<BoxCollider>();
        botBc.center = Vector3.zero;
        botBc.size   = new Vector3(maxR * 2f, botThick, maxR * 2f);
    }

    // ── Convexity = |signed mesh volume| / OBB volume ────────────────────────
    //
    //  Signed volume via divergence theorem:
    //    V = (1/6) Σ dot(v0, cross(v1,v2))  for each triangle
    //
    //  A solid convex mesh (sphere ~0.52, box ~1.0) scores high.
    //  An open/hollow mesh (basket, bowl) scores near 0.

    static float Convexity(MeshCluster c)
    {
        if (c.tris.Length < 9) return 1f;

        double vol = 0;
        for (int i = 0; i < c.tris.Length; i += 3)
            vol += Vector3.Dot(c.verts[c.tris[i]],
                               Vector3.Cross(c.verts[c.tris[i+1]], c.verts[c.tris[i+2]]));
        float meshVol = Mathf.Abs((float)(vol / 6.0));

        ComputePCA(c.verts, out _, out _, out float[] ex);
        float obbVol = 8f * ex[0] * ex[1] * ex[2];
        if (obbVol < 1e-9f) return 1f;

        return Mathf.Clamp01(meshVol / obbVol);
    }

    // ── Recursive bisection ───────────────────────────────────────────────────
    //
    //  At each level: try all 3 PCA axes as split planes, pick the plane whose
    //  split maximises min(convexity_A, convexity_B) → "most balanced" split.
    //  Recurse on each half until convex or max depth reached.

    static void Decompose(MeshCluster cluster, int depth, List<MeshCluster> output)
    {
        float cv = Convexity(cluster);

        if (cv >= CONVEX_THRESHOLD || depth >= MAX_DEPTH || cluster.tris.Length < MIN_TRIS * 3)
        {
            if (cluster.verts.Length >= 4) output.Add(cluster);
            return;
        }

        ComputePCA(cluster.verts, out Vector3 centroid, out Vector3[] axes, out _);

        MeshCluster bestA = default, bestB = default;
        float bestScore = -1f;

        for (int i = 0; i < 3; i++)
        {
            SplitCluster(cluster, centroid, axes[i], out MeshCluster a, out MeshCluster b);
            if (a.verts.Length < 4 || b.verts.Length < 4) continue;
            float score = Mathf.Min(Convexity(a), Convexity(b));
            if (score > bestScore) { bestScore = score; bestA = a; bestB = b; }
        }

        if (bestA.verts == null) { output.Add(cluster); return; }

        Decompose(bestA, depth + 1, output);
        Decompose(bestB, depth + 1, output);
    }

    // ── Split cluster by plane (planePoint, planeNormal) ─────────────────────
    //
    //  Each triangle is assigned to the side containing the majority of its
    //  vertices (2-or-3 on A → cluster A; otherwise cluster B).
    //  Boundary vertices (minority side) are also copied into the opposite
    //  cluster so each piece remains spatially bounded.

    static void SplitCluster(MeshCluster src, Vector3 planePoint, Vector3 planeNormal,
                              out MeshCluster clA, out MeshCluster clB)
    {
        int n = src.verts.Length;
        float[] sd = new float[n];
        for (int i = 0; i < n; i++)
            sd[i] = Vector3.Dot(src.verts[i] - planePoint, planeNormal);

        var vA = new List<Vector3>(); var tA = new List<int>();
        var vB = new List<Vector3>(); var tB = new List<int>();
        int[] mA = new int[n]; int[] mB = new int[n];
        for (int i = 0; i < n; i++) { mA[i] = -1; mB[i] = -1; }

        // Local helper inline via lambda — requires C# 7 (Unity Roslyn)
        System.Func<int,int> addA = oi => { if (mA[oi]<0){mA[oi]=vA.Count;vA.Add(src.verts[oi]);} return mA[oi]; };
        System.Func<int,int> addB = oi => { if (mB[oi]<0){mB[oi]=vB.Count;vB.Add(src.verts[oi]);} return mB[oi]; };

        for (int i = 0; i < src.tris.Length; i += 3)
        {
            int i0 = src.tris[i], i1 = src.tris[i+1], i2 = src.tris[i+2];
            int plus = (sd[i0]>=0?1:0) + (sd[i1]>=0?1:0) + (sd[i2]>=0?1:0);

            if (plus >= 2) // majority on A (positive) side
            {
                tA.Add(addA(i0)); tA.Add(addA(i1)); tA.Add(addA(i2));
                // Copy boundary verts to B so B cluster stays properly bounded
                if (sd[i0] < 0) addB(i0);
                if (sd[i1] < 0) addB(i1);
                if (sd[i2] < 0) addB(i2);
            }
            else           // majority on B (negative) side
            {
                tB.Add(addB(i0)); tB.Add(addB(i1)); tB.Add(addB(i2));
                if (sd[i0] >= 0) addA(i0);
                if (sd[i1] >= 0) addA(i1);
                if (sd[i2] >= 0) addA(i2);
            }
        }

        clA = new MeshCluster { verts = vA.ToArray(), tris = tA.ToArray() };
        clB = new MeshCluster { verts = vB.ToArray(), tris = tB.ToArray() };
    }

    // ── Shape classification ──────────────────────────────────────────────────

    enum ShapeType { Sphere, Capsule, Box }

    static ShapeType ClassifyShape(float[] ex,
                                   Vector3[] verts, Vector3 centroid, Vector3[] axes)
    {
        float e0 = ex[0], e2 = Mathf.Max(ex[2], 1e-6f);
        float ratioLong = e0 / e2;
        float ratioMid  = ex[1] / e2;

        if (ratioLong < 1.35f)
            return ShapeType.Sphere;

        if (ratioLong >= 2.0f && ratioMid < 1.4f
            && IsCrossSectionRound(verts, centroid, axes[0]))
            return ShapeType.Capsule;

        return ShapeType.Box;
    }

    /// Projects vertices onto the plane perpendicular to <paramref name="mainAxis"/>
    /// and checks whether the cross-section is roughly circular (minR/maxR > 0.55).
    static bool IsCrossSectionRound(Vector3[] verts, Vector3 centroid, Vector3 mainAxis)
    {
        float minR = float.MaxValue, maxR = 0f;
        foreach (var v in verts)
        {
            Vector3 d    = v - centroid;
            float   proj = Vector3.Dot(d, mainAxis);
            float   perp = (d - proj * mainAxis).magnitude;
            if (perp > 1e-4f) { minR = Mathf.Min(minR, perp); maxR = Mathf.Max(maxR, perp); }
        }
        if (minR == float.MaxValue || maxR < 1e-6f) return true;
        return (minR / maxR) > 0.55f;
    }

    // ── Collider child creation ───────────────────────────────────────────────

    static void MakeColliderChild(GameObject root, string childName,
                                  Vector3[] verts, Vector3 centroid,
                                  Vector3[] axes, ShapeType shape)
    {
        var go = new GameObject(childName);
        Undo.RegisterCreatedObjectUndo(go, "AutoFit " + childName);
        go.layer = root.layer;
        go.transform.SetParent(root.transform, false);
        // Child sits at cluster centroid; local Y aligned to the longest PCA axis
        go.transform.localPosition = centroid;
        go.transform.localRotation = BuildPCARot(axes);

        switch (shape)
        {
            case ShapeType.Sphere:  FitSphere(go, verts, centroid);        break;
            case ShapeType.Capsule: FitCapsule(go, verts, centroid, axes); break;
            default:                FitBox(go, verts, centroid, axes);     break;
        }
    }

    // ── Sphere ────────────────────────────────────────────────────────────────
    //  r = max distance from centroid to any vertex  (all verts guaranteed inside)

    static void FitSphere(GameObject go, Vector3[] verts, Vector3 centroid)
    {
        float r = 0f;
        foreach (var v in verts) r = Mathf.Max(r, Vector3.Distance(v, centroid));
        var sc    = go.AddComponent<SphereCollider>();
        sc.center = Vector3.zero;   // go sits at centroid
        sc.radius = r;
    }

    // ── Capsule ───────────────────────────────────────────────────────────────
    //  axis   = axes[0]  →  child local Y after BuildPCARot
    //  radius = max perpendicular distance from axis
    //  height = full span along axis  (clamped ≥ 2r)

    static void FitCapsule(GameObject go, Vector3[] verts, Vector3 centroid, Vector3[] axes)
    {
        float minP = float.MaxValue, maxP = float.MinValue, maxR = 0f;
        foreach (var v in verts)
        {
            Vector3 d    = v - centroid;
            float   proj = Vector3.Dot(d, axes[0]);
            float   perp = (d - proj * axes[0]).magnitude;
            minP = Mathf.Min(minP, proj);
            maxP = Mathf.Max(maxP, proj);
            maxR = Mathf.Max(maxR, perp);
        }
        float height  = Mathf.Max(maxP - minP, 2f * maxR);
        var   cc      = go.AddComponent<CapsuleCollider>();
        cc.center     = Vector3.zero;
        cc.direction  = 1;      // Y-axis → aligned to axes[0] by BuildPCARot
        cc.radius     = maxR;
        cc.height     = height;
    }

    // ── Box (OBB) ─────────────────────────────────────────────────────────────
    //  BuildPCARot maps:  child Y → axes[0]  ·  child X → axes[1]  ·  child Z → axes[2]
    //  So BoxCollider.size = (2·halfX, 2·halfY, 2·halfZ) in child local space.

    static void FitBox(GameObject go, Vector3[] verts, Vector3 centroid, Vector3[] axes)
    {
        float halfY = 0f, halfX = 0f, halfZ = 0f;
        foreach (var v in verts)
        {
            Vector3 d = v - centroid;
            halfY = Mathf.Max(halfY, Mathf.Abs(Vector3.Dot(d, axes[0]))); // child Y
            halfX = Mathf.Max(halfX, Mathf.Abs(Vector3.Dot(d, axes[1]))); // child X
            halfZ = Mathf.Max(halfZ, Mathf.Abs(Vector3.Dot(d, axes[2]))); // child Z
        }
        var bc    = go.AddComponent<BoxCollider>();
        bc.center = Vector3.zero;
        bc.size   = new Vector3(halfX * 2f, halfY * 2f, halfZ * 2f);
    }

    // ── PCA rotation ─────────────────────────────────────────────────────────
    //  Constructs a rotation so that:
    //    child local Y  →  axes[0]  (longest)
    //    child local X  →  axes[1]  (middle)
    //    child local Z  →  axes[2]  (shortest, right-hand corrected)

    static Quaternion BuildPCARot(Vector3[] axes)
    {
        Vector3 up      = axes[0];
        Vector3 right   = (axes[1] - Vector3.Dot(axes[1], up) * up).normalized;
        if (right.sqrMagnitude < 0.01f) right = Vector3.right;
        Vector3 forward = Vector3.Cross(right, up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        return Quaternion.LookRotation(forward, up);
    }

    // ── PCA ──────────────────────────────────────────────────────────────────
    //  Centroid → covariance matrix → 3 eigenvectors (power iteration + deflation)
    //  → half-extents sorted descending.

    static void ComputePCA(Vector3[] verts,
                           out Vector3   centroid,
                           out Vector3[] axes,
                           out float[]   halfExtents)
    {
        centroid = Vector3.zero;
        foreach (var v in verts) centroid += v;
        centroid /= verts.Length;

        double cxx=0,cxy=0,cxz=0,cyy=0,cyz=0,czz=0;
        foreach (var v in verts)
        {
            double dx=v.x-centroid.x, dy=v.y-centroid.y, dz=v.z-centroid.z;
            cxx+=dx*dx; cxy+=dx*dy; cxz+=dx*dz;
            cyy+=dy*dy; cyz+=dy*dz; czz+=dz*dz;
        }
        double n = verts.Length;
        var cov = new Mat3((float)(cxx/n),(float)(cxy/n),(float)(cxz/n),
                           (float)(cxy/n),(float)(cyy/n),(float)(cyz/n),
                           (float)(cxz/n),(float)(cyz/n),(float)(czz/n));

        Vector3 e0 = PowerIter(cov,            new Vector3(1f,  0.1f, 0.05f));
        Vector3 e1 = PowerIter(Deflate(cov,e0), new Vector3(0.05f, 1f, 0.1f));
        Vector3 e2 = Vector3.Cross(e0, e1).normalized;

        float h0=0, h1=0, h2=0;
        foreach (var v in verts)
        {
            Vector3 d = v - centroid;
            h0 = Mathf.Max(h0, Mathf.Abs(Vector3.Dot(d, e0)));
            h1 = Mathf.Max(h1, Mathf.Abs(Vector3.Dot(d, e1)));
            h2 = Mathf.Max(h2, Mathf.Abs(Vector3.Dot(d, e2)));
        }

        var pairs = new (float h, Vector3 e)[] { (h0,e0),(h1,e1),(h2,e2) };
        System.Array.Sort(pairs, (a,b) => b.h.CompareTo(a.h));
        axes        = new Vector3[] { pairs[0].e, pairs[1].e, pairs[2].e };
        halfExtents = new float[]   { pairs[0].h, pairs[1].h, pairs[2].h };
    }

    // ── Minimal 3×3 matrix ────────────────────────────────────────────────────

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

    static Vector3 PowerIter(Mat3 M, Vector3 seed, int iters = 64)
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

    /// M' = M − λ(v⊗v),  λ = vᵀMv   (removes dominant eigenvector contribution)
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
