using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically fits primitive (and optional custom-mesh) colliders to a mesh.
///
/// Three modes (chosen by the user — the one bit that is impossible to detect
/// reliably is "hollow vs solid", so we let the user supply it):
///
///   • Solid     — never makes walls. Connected-components → recursive
///                 "valley-cut" decomposition → one primitive per part
///                 (Sphere / Capsule / Cylinder / Box).
///   • Container — always hollow: N wall boxes + 1 bottom box.
///   • Auto      — fits Solid first; if the fit is good it keeps it; only if the
///                 fit is poor AND the shape is round + open does it fall back to
///                 Container. Biased to Solid (a wrong Solid is far less harmful
///                 than walls wrapped around a solid object).
///
/// Valley-cut: at each node the cluster is projected onto its 3 PCA axes and the
/// strongest "cut" is taken from three signals:
///   - density gap     (empty band in the point histogram → separate branches)
///   - radius valley   (a neck where the radial profile dips)
///   - radius shoulder (a sharp step in the radial profile)
/// Recurse until no significant cut remains.
///
/// Confidence gate: fill = |meshVolume| / Σ colliderVolume. Below the threshold
/// the result is flagged (and optionally removed) so the user fits it by hand.
/// </summary>
public static class ColliderAutoFitter
{
    // ── Naming ────────────────────────────────────────────────────────────────
    const string PREFIX = "Collider_";

    // ── Container ─────────────────────────────────────────────────────────────
    const int   CONTAINER_WALLS       = 8;
    const float WALL_THICKNESS_FRAC   = 0.12f;
    const float BOTTOM_THICKNESS_FRAC = 0.10f;

    // ── Valley-cut decomposition ──────────────────────────────────────────────
    const int   MAX_DEPTH      = 5;     // recursion cap → up to 2^5 before guards
    const int   MAX_PARTS      = 30;    // hard cap on primitives per object
    const int   MIN_CLUSTER_V  = 8;     // stop splitting tiny clusters
    const int   PROFILE_BINS   = 24;    // histogram resolution along an axis
    const float MIN_CUT_SCORE  = 0.30f; // relative valley/step depth to justify a cut
    const float EDGE_MARGIN    = 0.12f; // ignore cuts within 12% of either end

    // ── Uniform point-cloud sampling ──────────────────────────────────────────
    const int   SAMPLE_POINTS       = 4096;   // uniform surface samples for PCA / cut analysis

    // ── Small-part pruning ────────────────────────────────────────────────────
    const float MIN_PART_SIZE_FRAC  = 0.1f;  // skip parts whose largest half-extent < 3% of mesh diagonal

    // ── Collider sizing ───────────────────────────────────────────────────────
    const float RADIUS_PERCENTILE   = 0.95f;  // use 95th-percentile radius to ignore outlier vertices

    // ── Shape classification ──────────────────────────────────────────────────
    const float SPHERE_RATIO   = 1.35f; // e0/e2 below → Sphere
    const float ROUND_RATIO    = 1.6f;  // perp-extent ratio below → round section
    const float CAPSULE_ASPECT = 2.5f;  // e0/e1 above → elongated tube (capsule chain)
    const float TUBE_FLATNESS  = 2.2f;  // e1/e2 below → cross-section not a flat ribbon
    const int   CAP_CHAIN_MAX  = 5;     // max capsules in a chain

    // ── Auto mode ─────────────────────────────────────────────────────────────
    const float AUTO_SOLID_OK   = 0.45f; // Solid fill at/above this → keep Solid
    const float OPEN_EDGE_STRONG = 0.10f; // boundary-edge ratio above → "open"

    // ── Symmetry ──────────────────────────────────────────────────────────────
    const float SYM_THRESHOLD   = 0.80f; // fraction of mirrored verts that must match

    // ── Gate ──────────────────────────────────────────────────────────────────
    public const float DEFAULT_MIN_FILL = 0.35f;

    /// <summary>Name the tool looks for in the module folder for a cylinder.</summary>
    public const string CYLINDER_MODULE = "Cylinder";

    public enum FitMode { Auto, Container, Solid }
    enum ShapeType { Sphere, Capsule, Cylinder, Box }

    // ── Options & result ──────────────────────────────────────────────────────

    public struct FitOptions
    {
        public FitMode        mode;
        public ModuleLibrary  modules;
        public bool           skipLowConfidence;
        public float          minFill;
        public bool           showSymmetryPlane;

        public static FitOptions Default => new FitOptions
        {
            mode = FitMode.Auto, modules = null,
            skipLowConfidence = true, minFill = DEFAULT_MIN_FILL,
            showSymmetryPlane = false
        };
    }

    public struct FitResult
    {
        public GameObject root;
        public bool       created;   // colliders are on the object
        public bool       flagged;   // low confidence (review by hand)
        public float      fill;      // 0..1 confidence
        public int        parts;
        public string     mode;      // human readable for the log
    }

    // ── Custom collider module library (prefabs in a folder) ──────────────────

    public class ModuleLibrary
    {
        readonly Dictionary<string, GameObject> _byName =
            new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);

        public int  Count       => _byName.Count;
        public bool Has(string n) => _byName.ContainsKey(n);
        public GameObject Get(string n) => _byName.TryGetValue(n, out var g) ? g : null;

        /// Scans <paramref name="assetFolderPath"/> (e.g. "Assets/Foo/Modules")
        /// for prefabs and registers them by file name.
        public static ModuleLibrary FromFolder(string assetFolderPath)
        {
            var lib = new ModuleLibrary();
            if (string.IsNullOrEmpty(assetFolderPath))            return lib;
            if (!AssetDatabase.IsValidFolder(assetFolderPath))    return lib;

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { assetFolderPath }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var    go   = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) lib._byName[go.name] = go;
            }
            return lib;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static FitResult FitCollider(GameObject root, FitOptions opt)
    {
        var result = new FitResult { root = root, mode = opt.mode.ToString() };

        MeshFilter mf = root.GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning($"[AutoFit] No mesh found under '{root.name}'.");
            return result;
        }

        RemoveAutoColliders(root);

        MeshCluster full = ExtractCluster(mf.sharedMesh, mf.transform, root.transform);
        if (full.verts.Length < 4) return result;

        // Sample a uniform point cloud for analysis (PCA, symmetry, cuts).
        // Raw vertices are kept for final collider bounds sizing.
        Vector3[] fullCloud = SamplePointCloud(full, SAMPLE_POINTS);
        ComputePCA(fullCloud, out Vector3 centroid, out Vector3[] axes, out float[] extents);

        // ── Container (forced) ────────────────────────────────────────────────
        if (opt.mode == FitMode.Container)
        {
            FitContainer(root, full.verts, centroid, axes, extents);
            EditorUtility.SetDirty(root);
            result.created = true; result.parts = CONTAINER_WALLS + 1; result.mode = "Container";
            Debug.Log($"[AutoFit] '{root.name}' → Container ({result.parts} parts)");
            return result;
        }

        GameObject debugContainer = null;
        if (opt.mode == FitMode.Solid || opt.mode == FitMode.Auto)
        {
            var existingDebug = root.transform.Find("__DebugSegments__");
            if (existingDebug != null)
                Undo.DestroyObjectImmediate(existingDebug.gameObject);

            debugContainer = new GameObject("__DebugSegments__");
            Undo.RegisterCreatedObjectUndo(debugContainer, "Auto Fit Debug Segments");
            debugContainer.transform.SetParent(root.transform, false);
        }

        // Minimum part size: skip fragments < 3% of mesh diagonal.
        float meshDiag   = 2f * Mathf.Sqrt(extents[0] * extents[0] + extents[1] * extents[1] + extents[2] * extents[2]);
        float minPartSize = meshDiag * MIN_PART_SIZE_FRAC;

        // ── Solid decomposition (used by Solid and Auto) ──────────────────────
        var build = BuildSolid(root, full, fullCloud, opt.modules, centroid, axes, debugContainer, minPartSize, opt.showSymmetryPlane);
        double meshVol = MeshVolume(full);
        float  fill    = (float)Clamp01(meshVol / System.Math.Max(build.vol, 1e-9));
        result.parts = build.count;

        // Calculate single primitive fit as a fallback/alternative.
        ShapeType singleShape = Classify(extents, opt.modules);
        Extents(full.verts, centroid, axes, out float[] _, out float[] singleHalf);
        double singleVol = GetSinglePrimitiveVolume(full.verts, centroid, axes, singleShape, singleHalf, opt.modules);

        // If decomposition has 0 parts, or if it occupies MORE volume than a single primitive collider
        // (which indicates over-segmentation and bad overlapping), fallback to a single primitive.
        if (build.count == 0 || build.vol > singleVol)
        {
            // Remove the decomposed colliders (starting with PREFIX) but keep the debug container.
            var collidersToDelete = new List<GameObject>();
            foreach (Transform child in root.transform)
            {
                if (child.name.StartsWith(PREFIX))
                    collidersToDelete.Add(child.gameObject);
            }
            foreach (var go in collidersToDelete)
                Undo.DestroyObjectImmediate(go);

            // Clean up segment meshes inside the debug container but keep the symmetry plane.
            if (debugContainer != null)
            {
                var segmentsToDelete = new List<GameObject>();
                foreach (Transform child in debugContainer.transform)
                {
                    if (child.name.StartsWith("DebugSegment_"))
                        segmentsToDelete.Add(child.gameObject);
                }
                foreach (var go in segmentsToDelete)
                    Undo.DestroyObjectImmediate(go);
            }

            double finalVol = MakeColliderChild(root, PREFIX + "0", full.verts, centroid, axes, singleShape, opt.modules);
            build = (1, finalVol);
            fill  = (float)Clamp01(meshVol / System.Math.Max(finalVol, 1e-9));
            result.parts = 1;
        }

        if (opt.mode == FitMode.Solid)
        {
            result.created = build.count > 0;
            ApplyGate(ref result, root, fill, opt);
            Debug.Log($"[AutoFit] '{root.name}' → Solid ({result.parts} parts, fill {fill:P0})"
                      + (result.flagged ? "  ⚠ low confidence" : ""));
            EditorUtility.SetDirty(root);
            return result;
        }

        // ── Auto ──────────────────────────────────────────────────────────────
        if (fill >= AUTO_SOLID_OK)
        {
            result.created = build.count > 0; result.mode = "Auto→Solid";
            Debug.Log($"[AutoFit] '{root.name}' → Auto→Solid ({result.parts} parts, fill {fill:P0})");
            EditorUtility.SetDirty(root);
            return result;
        }

        // Poor solid fit → maybe a hollow container.
        bool round = IsRound(extents);
        bool open  = OpenEdgeRatio(full) > OPEN_EDGE_STRONG;
        if (round && open)
        {
            RemoveAutoColliders(root);                       // drop the solid attempt
            FitContainer(root, full.verts, centroid, axes, extents);
            result.created = true; result.parts = CONTAINER_WALLS + 1; result.mode = "Auto→Container";
            Debug.Log($"[AutoFit] '{root.name}' → Auto→Container (solid fill {fill:P0}, round+open)");
            EditorUtility.SetDirty(root);
            return result;
        }

        // Not clearly a container → keep best-effort solid, let the gate decide.
        result.created = build.count > 0; result.mode = "Auto→Solid";
        ApplyGate(ref result, root, fill, opt);
        Debug.Log($"[AutoFit] '{root.name}' → Auto→Solid ({result.parts} parts, fill {fill:P0})"
                  + (result.flagged ? "  ⚠ low confidence" : ""));
        EditorUtility.SetDirty(root);
        return result;
    }

    static void ApplyGate(ref FitResult r, GameObject root, float fill, FitOptions opt)
    {
        r.fill = fill;
        if (fill >= opt.minFill) return;

        r.flagged = true;
        if (opt.skipLowConfidence)
        {
            RemoveAutoColliders(root);
            r.created = false; r.parts = 0;
        }
    }

    // ── Solid build: components → valley-cut leaves → colliders ────────────────

    // ── Debug Mesh Helpers ───────────────────────────────────────────────────

    static MeshCluster MirrorCompleteCluster(MeshCluster leaf, Vector3 centroid, Vector3 normal)
    {
        Vector3[] mv = MirrorComplete(leaf.verts, centroid, normal);
        int originalVertCount = leaf.verts.Length;
        int[] mirroredTris = new int[leaf.tris.Length * 2];
        System.Array.Copy(leaf.tris, 0, mirroredTris, 0, leaf.tris.Length);
        for (int i = 0; i < leaf.tris.Length; i += 3)
        {
            mirroredTris[leaf.tris.Length + i] = leaf.tris[i] + originalVertCount;
            mirroredTris[leaf.tris.Length + i + 1] = leaf.tris[i + 2] + originalVertCount;
            mirroredTris[leaf.tris.Length + i + 2] = leaf.tris[i + 1] + originalVertCount;
        }
        return new MeshCluster { verts = mv, tris = mirroredTris };
    }

    static MeshCluster ReflectCluster(MeshCluster leaf, Vector3 centroid, Vector3 normal)
    {
        Vector3[] mv = Reflect(leaf.verts, centroid, normal);
        int[] reflectedTris = new int[leaf.tris.Length];
        for (int i = 0; i < leaf.tris.Length; i += 3)
        {
            reflectedTris[i] = leaf.tris[i];
            reflectedTris[i + 1] = leaf.tris[i + 2];
            reflectedTris[i + 2] = leaf.tris[i + 1];
        }
        return new MeshCluster { verts = mv, tris = reflectedTris };
    }

    static MeshCluster SliceCluster(MeshCluster src, float lo, float hi, Vector3 c, Vector3 axis)
    {
        int n = src.verts.Length;
        var subVerts = new List<Vector3>();
        var remap = new Dictionary<int, int>();

        for (int i = 0; i < n; i++)
        {
            float p = Vector3.Dot(src.verts[i] - c, axis);
            if (p >= lo && p <= hi)
            {
                remap[i] = subVerts.Count;
                subVerts.Add(src.verts[i]);
            }
        }

        var subTris = new List<int>();
        for (int i = 0; i < src.tris.Length; i += 3)
        {
            int i0 = src.tris[i], i1 = src.tris[i + 1], i2 = src.tris[i + 2];
            if (remap.ContainsKey(i0) && remap.ContainsKey(i1) && remap.ContainsKey(i2))
            {
                subTris.Add(remap[i0]);
                subTris.Add(remap[i1]);
                subTris.Add(remap[i2]);
            }
        }

        return new MeshCluster { verts = subVerts.ToArray(), tris = subTris.ToArray() };
    }

    static void CreateDebugSymmetryPlane(GameObject parent, MeshCluster full, Vector3 centroid, Vector3 normal)
    {
        if (parent == null || full.verts == null || full.verts.Length == 0)
            return;

        // Calculate size based on mesh bounds
        Vector3 mn = full.verts[0], mx = full.verts[0];
        foreach (var v in full.verts)
        {
            mn = Vector3.Min(mn, v);
            mx = Vector3.Max(mx, v);
        }
        float size = (mx - mn).magnitude * 1.2f;

        GameObject planeGO = new GameObject("__DebugSymmetryPlane__");
        Undo.RegisterCreatedObjectUndo(planeGO, "Auto Fit Debug Symmetry Plane");
        planeGO.transform.SetParent(parent.transform, false);

        MeshFilter mf = planeGO.AddComponent<MeshFilter>();
        Mesh mesh = new Mesh();
        mesh.name = "SymmetryPlane_Mesh";

        float half = size * 0.5f;
        Vector3[] vertices = new Vector3[4];
        Vector3 u = Vector3.Cross(normal, Vector3.up).normalized;
        if (u.sqrMagnitude < 0.01f)
            u = Vector3.Cross(normal, Vector3.right).normalized;
        Vector3 vDir = Vector3.Cross(normal, u).normalized;

        vertices[0] = centroid - u * half - vDir * half;
        vertices[1] = centroid + u * half - vDir * half;
        vertices[2] = centroid + u * half + vDir * half;
        vertices[3] = centroid - u * half + vDir * half;

        mesh.vertices = vertices;
        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2, 1, 2, 0, 2, 3, 0 }; // Double-sided
        mesh.RecalculateNormals();
        mf.sharedMesh = mesh;

        MeshRenderer mr = planeGO.AddComponent<MeshRenderer>();
        Color color = new Color(0.9f, 0.1f, 0.9f, 0.25f); // Semi-transparent magenta/pink

        Shader shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        Material mat = new Material(shader);
        mat.name = "SymmetryPlane_Mat";
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);

        if (shader.name == "Standard")
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        else if (shader.name.Contains("Universal Render Pipeline/Lit") || shader.name.Contains("URP/Lit"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
        }

        mr.sharedMaterial = mat;
    }

    static void CreateDebugMeshForSegment(GameObject parent, MeshCluster cluster, int idx)
    {
        if (parent == null || cluster.verts == null || cluster.verts.Length < 3 || cluster.tris == null || cluster.tris.Length < 3)
            return;

        GameObject segmentGO = new GameObject($"DebugSegment_{idx}");
        Undo.RegisterCreatedObjectUndo(segmentGO, "Auto Fit Debug Segments");
        segmentGO.transform.SetParent(parent.transform, false);

        MeshFilter mf = segmentGO.AddComponent<MeshFilter>();
        Mesh mesh = new Mesh();
        mesh.name = $"Segment_{idx}_Mesh";
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(cluster.verts);
        mesh.SetTriangles(cluster.tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;

        MeshRenderer mr = segmentGO.AddComponent<MeshRenderer>();
        Color color = Color.HSVToRGB((float)(idx % 12) / 12f, 0.85f, 0.9f);
        color.a = 0.5f;

        Shader shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        Material mat = new Material(shader);
        mat.name = $"Segment_{idx}_Mat";
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);

        if (shader.name == "Standard")
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        else if (shader.name.Contains("Universal Render Pipeline/Lit") || shader.name.Contains("URP/Lit"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
        }

        mr.sharedMaterial = mat;
    }

    // ── Solid build: components → valley-cut leaves → colliders ────────────────

    static (int count, double vol) BuildSolid(GameObject root, MeshCluster full,
                                              Vector3[] fullCloud,
                                              ModuleLibrary modules, Vector3 centroid, Vector3[] axes,
                                              GameObject debugParent, float minPartSize, bool showSymmetryPlane)
    {
        // Symmetric objects (aircraft, characters…) → fit one half + mirror, so
        // mirror-image parts get identical colliders instead of drifting apart.
        // Use uniform cloud (not raw verts) to avoid density-biased symmetry detection.
        if (TryFindSymmetryPlane(fullCloud, centroid, axes, out Vector3 symN))
            return BuildSolidSymmetric(root, full, modules, centroid, symN, debugParent, minPartSize, showSymmetryPlane);

        return BuildSolidPlain(root, full, modules, debugParent, minPartSize);
    }

    static List<MeshCluster> DecomposeToLeaves(GameObject root, MeshCluster cluster)
    {
        var leaves = new List<MeshCluster>();
        foreach (var comp in ConnectedComponents(cluster))
            ValleyDecompose(comp, 0, leaves);

        if (leaves.Count > MAX_PARTS)
        {
            leaves.Sort((a, b) => b.verts.Length.CompareTo(a.verts.Length));
            leaves.RemoveRange(MAX_PARTS, leaves.Count - MAX_PARTS);
            Debug.LogWarning($"[AutoFit] '{root.name}' produced many parts; capped at {MAX_PARTS}.");
        }
        return leaves;
    }

    // ── Symmetric build: fit the + half, mirror to the − half ─────────────────
    //   • A part that touches the plane (fuselage, body) is "central": mirror-
    //     complete it and fit ONE collider centred on the plane.
    //   • A part fully on one side (wing) is fitted, then an exact mirror copy is
    //     created on the other side → guaranteed identical colliders.

    static (int count, double vol) BuildSolidSymmetric(GameObject root, MeshCluster full,
                                                       ModuleLibrary modules,
                                                       Vector3 centroid, Vector3 normal,
                                                       GameObject debugParent, float minPartSize, bool showSymmetryPlane)
    {
        SplitCluster(full, centroid, normal, out MeshCluster half, out _);  // keep + side
        if (half.verts.Length < 4) return BuildSolidPlain(root, full, modules, debugParent, minPartSize);

        var leaves = DecomposeToLeaves(root, half);
        if (leaves.Count == 0) return BuildSolidPlain(root, full, modules, debugParent, minPartSize);

        // Create the symmetry plane visualizer in the debug container if option is enabled
        if (showSymmetryPlane)
            CreateDebugSymmetryPlane(debugParent, full, centroid, normal);

        // Band (in plane-normal units) that counts a part as "central".
        float span = 0f;
        foreach (var v in full.verts) span = Mathf.Max(span, Mathf.Abs(Vector3.Dot(v - centroid, normal)));
        float band = Mathf.Max(span * 0.06f, 1e-4f);

        int    idx = 0;
        double vol = 0;
        foreach (var leaf in leaves)
        {
            if (leaf.verts.Length < 4) continue;

            float dmin = float.MaxValue;
            foreach (var v in leaf.verts)
                dmin = Mathf.Min(dmin, Vector3.Dot(v - centroid, normal));

            if (dmin <= band)
            {
                // Central part → mirror-complete → one centred fit.
                MeshCluster mc = MirrorCompleteCluster(leaf, centroid, normal);
                vol += FitLeaf(root, mc, modules, ref idx, debugParent, minPartSize);
            }
            else
            {
                // Side part + its exact mirror (same deterministic fit → identical).
                vol += FitLeaf(root, leaf, modules, ref idx, debugParent, minPartSize);
                MeshCluster reflectedCluster = ReflectCluster(leaf, centroid, normal);
                vol += FitLeaf(root, reflectedCluster, modules, ref idx, debugParent, minPartSize);
            }
        }
        Debug.Log($"[AutoFit] '{root.name}' → symmetric ({idx} parts, mirror plane normal {normal})");
        return (idx, vol);
    }

    // Plain (no symmetry) build — used as a safe fallback inside the symmetric path.
    static (int count, double vol) BuildSolidPlain(GameObject root, MeshCluster full, ModuleLibrary modules, GameObject debugParent, float minPartSize)
    {
        var leaves = DecomposeToLeaves(root, full);
        if (leaves.Count == 0) return (0, 0);
        int idx = 0; double vol = 0;
        foreach (var leaf in leaves)
        {
            if (leaf.verts.Length < 4) continue;
            vol += FitLeaf(root, leaf, modules, ref idx, debugParent, minPartSize);
        }
        return (idx, vol);
    }

    // Fits one vertex cluster: an elongated (possibly curved) tube → capsule chain
    // that follows the centreline; anything else → a single best-fit primitive.
    static double FitLeaf(GameObject root, MeshCluster leaf, ModuleLibrary modules, ref int idx, GameObject debugParent, float minPartSize)
    {
        // Use sampled cloud for PCA/shape classification, raw verts for bounds sizing.
        Vector3[] cloud = SamplePointCloud(leaf, SAMPLE_POINTS);
        ComputePCA(cloud, out Vector3 c, out Vector3[] ax, out float[] ex);

        // Skip fragments that are too small to contribute meaningful collision coverage.
        if (ex[0] < minPartSize)
            return 0;

        if (IsTube(ex))
            return FitCapsuleChain(root, leaf, cloud, ChainSegments(ex), modules, ref idx, debugParent);

        double v = MakeColliderChild(root, PREFIX + idx, leaf.verts, c, ax, Classify(ex, modules), modules);
        CreateDebugMeshForSegment(debugParent, leaf, idx);
        idx++;
        return v;
    }

    // Elongated AND not a flat ribbon → a (possibly curved) round tube.
    static bool IsTube(float[] ex) =>
        ex[0] / Mathf.Max(ex[1], 1e-6f) >= CAPSULE_ASPECT &&
        ex[1] / Mathf.Max(ex[2], 1e-6f) <  TUBE_FLATNESS;

    static int ChainSegments(float[] ex)
    {
        int n = Mathf.RoundToInt(ex[0] / Mathf.Max(ex[2], 1e-6f) / 1.5f);
        return Mathf.Clamp(n, 2, CAP_CHAIN_MAX);
    }

    // Capsule chain: slice along the long axis, run PCA per slice so each capsule
    // tilts to follow the local tangent of a curved tube (chili, stem, finger…).
    static double FitCapsuleChain(GameObject root, MeshCluster leaf, Vector3[] leafCloud, int segments,
                                  ModuleLibrary modules, ref int idx, GameObject debugParent)
    {
        // Axis from the uniform cloud for correct orientation regardless of vertex density.
        ComputePCA(leafCloud, out Vector3 c, out Vector3[] ax, out _);
        Vector3 axis = ax[0];

        // Range computed from raw verts for tight spatial coverage.
        float minP = float.MaxValue, maxP = float.MinValue;
        foreach (var v in leaf.verts)
        {
            float p = Vector3.Dot(v - c, axis);
            if (p < minP) minP = p;
            if (p > maxP) maxP = p;
        }

        float binLen  = (maxP - minP) / segments;
        float overlap = binLen * 0.25f;
        double vol = 0; int made = 0;

        for (int s = 0; s < segments; s++)
        {
            float lo = minP + s * binLen - overlap;
            float hi = minP + (s + 1) * binLen + overlap;

            MeshCluster subCluster = SliceCluster(leaf, lo, hi, c, axis);
            if (subCluster.verts.Length < 4) continue;

            // Use sampled cloud for sub-slice orientation.
            Vector3[] subCloud = SamplePointCloud(subCluster, Mathf.Max(64, SAMPLE_POINTS / segments));
            ComputePCA(subCloud, out Vector3 cc, out Vector3[] sax, out _);
            vol += MakeColliderChild(root, PREFIX + idx, subCluster.verts, cc, sax, ShapeType.Capsule, modules);
            CreateDebugMeshForSegment(debugParent, subCluster, idx);
            idx++; made++;
        }

        if (made == 0)
        {
            vol += MakeColliderChild(root, PREFIX + idx, leaf.verts, c, ax, ShapeType.Capsule, modules);
            CreateDebugMeshForSegment(debugParent, leaf, idx);
            idx++;
        }
        return vol;
    }

    // ── Symmetry plane detection (best of the 3 PCA axes as mirror normal) ─────

    static bool TryFindSymmetryPlane(Vector3[] verts, Vector3 centroid, Vector3[] axes, out Vector3 normal)
    {
        normal = axes[0];
        if (verts.Length < 16) return false;

        Vector3 mn = verts[0], mx = verts[0];
        foreach (var v in verts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        float q = Mathf.Max((mx - mn).magnitude * 0.01f, 1e-5f);

        var grid = new HashSet<Vector3Int>();
        foreach (var v in verts) grid.Add(Quantize(v, q));

        float best = 0f; int bestK = -1;
        for (int k = 0; k < 3; k++)
        {
            int matched = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 r = ReflectPoint(verts[i], centroid, axes[k]);
                if (HasNeighbor(grid, r, q)) matched++;
            }
            float s = matched / (float)verts.Length;
            if (s > best) { best = s; bestK = k; }
        }

        if (bestK >= 0 && best >= SYM_THRESHOLD) { normal = axes[bestK]; return true; }
        return false;
    }

    static Vector3Int Quantize(Vector3 v, float q) =>
        new Vector3Int(Mathf.RoundToInt(v.x / q), Mathf.RoundToInt(v.y / q), Mathf.RoundToInt(v.z / q));

    static bool HasNeighbor(HashSet<Vector3Int> grid, Vector3 p, float q)
    {
        Vector3Int b = Quantize(p, q);
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
            if (grid.Contains(new Vector3Int(b.x + x, b.y + y, b.z + z))) return true;
        return false;
    }

    static Vector3 ReflectPoint(Vector3 v, Vector3 p0, Vector3 n) =>
        v - 2f * Vector3.Dot(v - p0, n) * n;

    static Vector3[] Reflect(Vector3[] verts, Vector3 p0, Vector3 n)
    {
        var r = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++) r[i] = ReflectPoint(verts[i], p0, n);
        return r;
    }

    static Vector3[] MirrorComplete(Vector3[] verts, Vector3 p0, Vector3 n)
    {
        var r = new Vector3[verts.Length * 2];
        for (int i = 0; i < verts.Length; i++)
        {
            r[i] = verts[i];
            r[verts.Length + i] = ReflectPoint(verts[i], p0, n);
        }
        return r;
    }

    // ── Valley-cut recursion ──────────────────────────────────────────────────

    static void ValleyDecompose(MeshCluster cluster, int depth, List<MeshCluster> output)
    {
        if (output.Count >= MAX_PARTS ||
            cluster.verts.Length < MIN_CLUSTER_V ||
            depth >= MAX_DEPTH)
        {
            output.Add(cluster);
            return;
        }

        // Sample a uniform cloud for structural analysis; raw verts for bounds later.
        Vector3[] cloud = SamplePointCloud(cluster, SAMPLE_POINTS);

        // Elongated round tube → don't cut. Radius variations along a curved tube
        // are chord artifacts, not real parts; FitLeaf handles it as a centreline-
        // following capsule chain instead of being chopped into boxes. (Flat wings
        // fail IsTube, so symmetric aircraft halves still get split + mirrored.)
        ComputePCA(cloud, out _, out _, out float[] gex);
        if (IsTube(gex))
        {
            output.Add(cluster);
            return;
        }

        if (!FindBestCut(cloud, cluster, out Vector3 pp, out Vector3 pn, out float score) ||
            score < MIN_CUT_SCORE)
        {
            output.Add(cluster);
            return;
        }

        SplitCluster(cluster, pp, pn, out MeshCluster a, out MeshCluster b);
        if (a.verts.Length < 4 || b.verts.Length < 4)
        {
            output.Add(cluster);
            return;
        }

        ValleyDecompose(a, depth + 1, output);
        ValleyDecompose(b, depth + 1, output);
    }

    // Searches all 3 PCA axes for the strongest density-gap / radius-valley /
    // radius-shoulder. Uses the uniform point cloud for histogram analysis and the
    // raw cluster verts for the actual plane position only.
    static bool FindBestCut(Vector3[] cloud, MeshCluster cluster,
                            out Vector3 planePoint, out Vector3 planeNormal, out float score)
    {
        planePoint = Vector3.zero; planeNormal = Vector3.up; score = -1f;

        ComputePCA(cloud, out Vector3 centroid, out Vector3[] axes, out float[] extents);
        int n = cloud.Length;
        float[] proj = new float[n];

        for (int k = 0; k < 3; k++)
        {
            Vector3 axis = axes[k];
            float weight = extents[k] / Mathf.Max(extents[0], 1e-6f);
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                proj[i] = Vector3.Dot(cloud[i] - centroid, axis);
                if (proj[i] < mn) mn = proj[i];
                if (proj[i] > mx) mx = proj[i];
            }
            float span = mx - mn;
            if (span < 1e-5f) continue;

            // Histograms: point count + max radius per bin.
            int[]   cnt = new int[PROFILE_BINS];
            float[] rad = new float[PROFILE_BINS];
            for (int i = 0; i < n; i++)
            {
                int b = Mathf.Clamp((int)((proj[i] - mn) / span * PROFILE_BINS), 0, PROFILE_BINS - 1);
                cnt[b]++;
                Vector3 d  = cloud[i] - centroid;
                float   pe = (d - proj[i] * axis).magnitude;
                if (pe > rad[b]) rad[b] = pe;
            }

            int maxC = 0; float maxR = 0f;
            for (int b = 0; b < PROFILE_BINS; b++) { if (cnt[b] > maxC) maxC = cnt[b]; if (rad[b] > maxR) maxR = rad[b]; }
            if (maxC == 0) continue;

            int lo = Mathf.Max(1, Mathf.RoundToInt(PROFILE_BINS * EDGE_MARGIN));
            int hi = PROFILE_BINS - 1 - lo;

            // 1+2. Density gap & radius valley at interior bin i.
            for (int i = lo; i <= hi; i++)
            {
                int   leftC = 0, rightC = 0;
                float leftR = 0f, rightR = 0f;
                for (int j = 0; j < i; j++) { if (cnt[j] > leftC) leftC = cnt[j];  if (cnt[j] > 0 && rad[j] > leftR) leftR = rad[j]; }
                for (int j = i + 1; j < PROFILE_BINS; j++) { if (cnt[j] > rightC) rightC = cnt[j]; if (cnt[j] > 0 && rad[j] > rightR) rightR = rad[j]; }

                float pos = mn + (i + 0.5f) / PROFILE_BINS * span;

                if (leftC > 0 && rightC > 0)
                {
                    float dens = (Mathf.Min(leftC, rightC) - cnt[i]) / (float)maxC * weight;
                    if (dens > score) { score = dens; planePoint = centroid + axis * pos; planeNormal = axis; }
                }
                if (maxR > 1e-6f && leftR > 0f && rightR > 0f)
                {
                    float val = (Mathf.Min(leftR, rightR) - rad[i]) / maxR * weight;
                    if (val > score) { score = val; planePoint = centroid + axis * pos; planeNormal = axis; }
                }
            }

            // 3. Radius shoulder between adjacent populated bins.
            if (maxR > 1e-6f)
            {
                for (int i = lo; i < hi; i++)
                {
                    if (cnt[i] == 0 || cnt[i + 1] == 0) continue;
                    float step = Mathf.Abs(rad[i + 1] - rad[i]) / maxR * weight;
                    if (step > score)
                    {
                        float pos = mn + (i + 1f) / PROFILE_BINS * span;
                        score = step; planePoint = centroid + axis * pos; planeNormal = axis;
                    }
                }
            }
        }

        return score >= 0f;
    }

    // ── Connected components (weld by position, union-find over triangles) ─────

    static List<MeshCluster> ConnectedComponents(MeshCluster full)
    {
        int[] weld = WeldByPosition(full.verts, out int wc);
        int[] uf   = new int[wc];
        for (int i = 0; i < wc; i++) uf[i] = i;

        for (int i = 0; i < full.tris.Length; i += 3)
        {
            int a = weld[full.tris[i]], b = weld[full.tris[i + 1]], c = weld[full.tris[i + 2]];
            Union(uf, a, b); Union(uf, b, c);
        }

        var compTris = new Dictionary<int, List<int>>();
        for (int i = 0; i < full.tris.Length; i += 3)
        {
            int rootId = Find(uf, weld[full.tris[i]]);
            if (!compTris.TryGetValue(rootId, out var list)) { list = new List<int>(); compTris[rootId] = list; }
            list.Add(full.tris[i]); list.Add(full.tris[i + 1]); list.Add(full.tris[i + 2]);
        }

        if (compTris.Count <= 1) return new List<MeshCluster> { full };

        var result = new List<MeshCluster>();
        foreach (var kv in compTris)
        {
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var tris  = new List<int>();
            foreach (int oi in kv.Value)
            {
                if (!remap.TryGetValue(oi, out int li)) { li = verts.Count; remap[oi] = li; verts.Add(full.verts[oi]); }
                tris.Add(li);
            }
            result.Add(new MeshCluster { verts = verts.ToArray(), tris = tris.ToArray() });
        }
        return result;
    }

    static int  Find(int[] uf, int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
    static void Union(int[] uf, int a, int b) { a = Find(uf, a); b = Find(uf, b); if (a != b) uf[a] = b; }

    // ── Shape classification ──────────────────────────────────────────────────

    static ShapeType Classify(float[] ex, ModuleLibrary modules)
    {
        float e2 = Mathf.Max(ex[2], 1e-6f);
        if (ex[0] / e2 < SPHERE_RATIO) return ShapeType.Sphere;

        int   sym = FindSymmetryAxis(ex);
        int   o1  = (sym + 1) % 3, o2 = (sym + 2) % 3;
        float rO  = Mathf.Max(ex[o1], ex[o2]);
        float rI  = Mathf.Max(Mathf.Min(ex[o1], ex[o2]), 1e-6f);
        bool  round = (rO / rI) < ROUND_RATIO;

        if (!round) return ShapeType.Box;

        float aspect = ex[sym] / Mathf.Max(rO, 1e-6f);
        if (aspect >= CAPSULE_ASPECT) return ShapeType.Capsule;
        return ShapeType.Cylinder; // module if present, else Box fallback
    }

    static bool IsRound(float[] ex)
    {
        int   sym = FindSymmetryAxis(ex);
        int   o1  = (sym + 1) % 3, o2 = (sym + 2) % 3;
        float rO  = Mathf.Max(ex[o1], ex[o2]);
        float rI  = Mathf.Max(Mathf.Min(ex[o1], ex[o2]), 1e-6f);
        return (rO / rI) < ROUND_RATIO;
    }

    static double GetSinglePrimitiveVolume(Vector3[] verts, Vector3 centroid, Vector3[] axes, ShapeType shape, float[] half, ModuleLibrary modules)
    {
        switch (shape)
        {
            case ShapeType.Sphere:
            {
                float r = (half[0] + half[1] + half[2]) / 3f;
                return 4.0 / 3.0 * Mathf.PI * r * r * r;
            }
            case ShapeType.Capsule:
            {
                Vector3 axis = axes[0];
                float r = PercentileRadius(verts, centroid, axis, RADIUS_PERCENTILE);
                float h = Mathf.Max(2f * half[0], 2f * r);
                double cyl = Mathf.PI * r * r * Mathf.Max(h - 2f * r, 0f);
                double cap = 4.0 / 3.0 * Mathf.PI * r * r * r;
                return cyl + cap;
            }
            case ShapeType.Cylinder:
            {
                bool hasPrefab = modules != null && modules.Has(CYLINDER_MODULE);
                if (hasPrefab)
                {
                    Extents(verts, centroid, axes, out float[] mid, out float[] halfEx);
                    Vector3 center = centroid + axes[0] * mid[0] + axes[1] * mid[1] + axes[2] * mid[2];
                    int sym = FindSymmetryAxis(halfEx);
                    Vector3 up = axes[sym];
                    float height = 2f * halfEx[sym];
                    float rad = 0f;
                    foreach (var v in verts)
                    {
                        Vector3 d = v - center;
                        float pe = (d - Vector3.Dot(d, up) * up).magnitude;
                        if (pe > rad) rad = pe;
                    }
                    return Mathf.PI * rad * rad * height;
                }
                else
                {
                    return 8.0 * half[0] * half[1] * half[2];
                }
            }
            default: // Box
            {
                return 8.0 * half[0] * half[1] * half[2];
            }
        }
    }

    // ── Collider creation (returns the collider's volume for the fill metric) ─

    static double MakeColliderChild(GameObject root, string name, Vector3[] verts,
                                    Vector3 centroid, Vector3[] axes, ShapeType shape,
                                    ModuleLibrary modules)
    {
        // Tight oriented extents: midpoint + half-size along each PCA axis.
        Extents(verts, centroid, axes, out float[] mid, out float[] half);
        Vector3 center = centroid + axes[0] * mid[0] + axes[1] * mid[1] + axes[2] * mid[2];

        switch (shape)
        {
            case ShapeType.Sphere:
            {
                // Use OBB center and the average of OBB half-extents for a balanced, tight sphere.
                float r = (half[0] + half[1] + half[2]) / 3f;
                var go = NewChild(root, name, center, Quaternion.identity);
                var sc = go.AddComponent<SphereCollider>();
                sc.center = Vector3.zero; sc.radius = r;
                return 4.0 / 3.0 * Mathf.PI * r * r * r;
            }

            case ShapeType.Capsule:
            {
                // Length axis = axes[0]; 95th-percentile perpendicular radius.
                Vector3 axis = axes[0];
                float r = PercentileRadius(verts, centroid, axis, RADIUS_PERCENTILE);
                // Height: tight extent along axis + two hemisphere end-caps.
                float h = Mathf.Max(2f * half[0], 2f * r);
                var   go = NewChild(root, name, centroid + axis * mid[0], BuildPCARot(axes));
                var   cc = go.AddComponent<CapsuleCollider>();
                cc.center = Vector3.zero; cc.direction = 1; cc.radius = r; cc.height = h;
                double cyl = Mathf.PI * r * r * Mathf.Max(h - 2f * r, 0f);
                double cap = 4.0 / 3.0 * Mathf.PI * r * r * r;
                return cyl + cap;
            }

            case ShapeType.Cylinder:
            {
                int     sym = FindSymmetryAxis(half);
                Vector3 up  = axes[sym];
                float   height = 2f * half[sym];
                float   rad = 0f;
                foreach (var v in verts)
                {
                    Vector3 d  = v - center;
                    float   pe = (d - Vector3.Dot(d, up) * up).magnitude;
                    if (pe > rad) rad = pe;
                }
                Vector3 pos = center;

                GameObject prefab = modules != null ? modules.Get(CYLINDER_MODULE) : null;
                if (prefab != null)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    Undo.RegisterCreatedObjectUndo(go, "AutoFit " + name);
                    go.name  = name;
                    go.layer = root.layer;
                    go.transform.SetParent(root.transform, false);

                    Quaternion rot = BuildAxisRot(up, axes[(sym + 1) % 3]);
                    Vector3 nat = ModuleLocalSize(prefab);
                    Vector3 scale = new Vector3(
                        2f * rad / Mathf.Max(nat.x, 1e-4f),
                        height   / Mathf.Max(nat.y, 1e-4f),
                        2f * rad / Mathf.Max(nat.z, 1e-4f));

                    Vector3 centerOffset = ModuleLocalCenter(prefab);
                    Vector3 rotatedOffset = rot * Vector3.Scale(centerOffset, scale);
                    Vector3 localPos = pos - rotatedOffset;

                    go.transform.localPosition = localPos;
                    go.transform.localRotation = rot;
                    go.transform.localScale    = scale;
                    return Mathf.PI * rad * rad * height;
                }

                // Fallback: tight OBB (Box) — no cylinder module supplied.
                var goB = NewChild(root, name, center, BuildPCARot(axes));
                var bcB = goB.AddComponent<BoxCollider>();
                bcB.center = Vector3.zero;
                bcB.size   = new Vector3(2f * half[1], 2f * half[0], 2f * half[2]);
                return 8.0 * half[0] * half[1] * half[2];
            }

            default: // Box (OBB)
            {
                var go = NewChild(root, name, center, BuildPCARot(axes));
                var bc = go.AddComponent<BoxCollider>();
                bc.center = Vector3.zero;
                bc.size   = new Vector3(2f * half[1], 2f * half[0], 2f * half[2]);
                return 8.0 * half[0] * half[1] * half[2];
            }
        }
    }

    // Native local-space size of a module prefab (for scale normalisation).
    static Vector3 ModuleLocalSize(GameObject prefab)
    {
        var mc = prefab.GetComponentInChildren<MeshCollider>();
        if (mc != null && mc.sharedMesh != null) return mc.sharedMesh.bounds.size;

        var mf = prefab.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh.bounds.size;

        var col = prefab.GetComponentInChildren<Collider>();
        if (col != null) return col.bounds.size;   // approximate

        return Vector3.one;
    }

    // Native local-space center of a module prefab (for pivot/offset normalisation).
    static Vector3 ModuleLocalCenter(GameObject prefab)
    {
        var mc = prefab.GetComponentInChildren<MeshCollider>();
        if (mc != null && mc.sharedMesh != null) return mc.sharedMesh.bounds.center;

        var mf = prefab.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh.bounds.center;

        var col = prefab.GetComponentInChildren<Collider>();
        if (col != null) return col.bounds.center;

        return Vector3.zero;
    }

    // Returns the p-th percentile (0..1) of perpendicular distances from 'verts'
    // to the line through 'centre' along 'axis'. When axis == Vector3.zero the
    // plain distance to centre is used (sphere case).
    static float PercentileRadius(Vector3[] verts, Vector3 centre, Vector3 axis, float p)
    {
        bool usePlain = axis.sqrMagnitude < 1e-8f;
        var  dists    = new float[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            if (usePlain)
            {
                dists[i] = Vector3.Distance(verts[i], centre);
            }
            else
            {
                Vector3 d = verts[i] - centre;
                dists[i] = (d - Vector3.Dot(d, axis) * axis).magnitude;
            }
        }
        System.Array.Sort(dists);
        int idx = Mathf.Clamp(Mathf.FloorToInt(p * verts.Length), 0, verts.Length - 1);
        return dists[idx];
    }

    static GameObject NewChild(GameObject root, string name, Vector3 localPos, Quaternion localRot)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "AutoFit " + name);
        go.layer = root.layer;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        return go;
    }

    // min/max projection per PCA axis → midpoint & half-size.
    static void Extents(Vector3[] verts, Vector3 centroid, Vector3[] axes,
                        out float[] mid, out float[] half)
    {
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue };
        float[] mx = { float.MinValue, float.MinValue, float.MinValue };
        foreach (var v in verts)
        {
            Vector3 d = v - centroid;
            for (int k = 0; k < 3; k++)
            {
                float p = Vector3.Dot(d, axes[k]);
                if (p < mn[k]) mn[k] = p;
                if (p > mx[k]) mx[k] = p;
            }
        }
        mid = new float[3]; half = new float[3];
        for (int k = 0; k < 3; k++)
        {
            mid[k]  = (mn[k] + mx[k]) * 0.5f;
            half[k] = (mx[k] - mn[k]) * 0.5f;
        }
    }

    // ── Uniform surface point sampling ────────────────────────────────────────
    // Samples numPoints points uniformly across the mesh surface by area-weighted
    // barycentric sampling. Deterministic seed (42) so symmetric meshes always
    // yield the same cloud. Falls back to raw verts if the mesh has no triangles.

    static Vector3[] SamplePointCloud(MeshCluster cluster, int numPoints)
    {
        int numTris = cluster.tris != null ? cluster.tris.Length / 3 : 0;
        if (numTris == 0 || cluster.verts.Length < 3)
            return cluster.verts;

        // Build cumulative-area table.
        float[] cumArea = new float[numTris];
        float totalArea = 0f;
        for (int t = 0; t < numTris; t++)
        {
            Vector3 a = cluster.verts[cluster.tris[t * 3]];
            Vector3 b = cluster.verts[cluster.tris[t * 3 + 1]];
            Vector3 c = cluster.verts[cluster.tris[t * 3 + 2]];
            totalArea += 0.5f * Vector3.Cross(b - a, c - a).magnitude;
            cumArea[t] = totalArea;
        }

        if (totalArea < 1e-9f)
            return cluster.verts;

        var pts = new Vector3[numPoints];
        var rng = new System.Random(42); // fixed seed → deterministic for symmetry

        for (int i = 0; i < numPoints; i++)
        {
            // Binary-search cumulative area to pick a triangle proportional to area.
            float target = (float)(rng.NextDouble() * totalArea);
            int lo = 0, hi = numTris - 1, triIdx = numTris - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cumArea[mid] >= target) { triIdx = mid; hi = mid - 1; }
                else                        lo = mid + 1;
            }

            Vector3 va = cluster.verts[cluster.tris[triIdx * 3]];
            Vector3 vb = cluster.verts[cluster.tris[triIdx * 3 + 1]];
            Vector3 vc = cluster.verts[cluster.tris[triIdx * 3 + 2]];

            // Uniform barycentric point: fold square sample into triangle.
            float r1 = (float)rng.NextDouble();
            float r2 = (float)rng.NextDouble();
            float sqrtR1 = Mathf.Sqrt(r1);
            pts[i] = (1f - sqrtR1) * va + sqrtR1 * (1f - r2) * vb + sqrtR1 * r2 * vc;
        }
        return pts;
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

    static void RemoveAutoColliders(GameObject root)
    {
        var toDelete = new List<Transform>();
        foreach (Transform child in root.transform)
            if (child.name.StartsWith(PREFIX) || child.name == "__DebugSegments__")
                toDelete.Add(child);
        foreach (var t in toDelete)
            Undo.DestroyObjectImmediate(t.gameObject);
    }

    // ── Split cluster by plane (majority side; boundary verts copied) ─────────

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

        System.Func<int, int> addA = oi => { if (mA[oi] < 0) { mA[oi] = vA.Count; vA.Add(src.verts[oi]); } return mA[oi]; };
        System.Func<int, int> addB = oi => { if (mB[oi] < 0) { mB[oi] = vB.Count; vB.Add(src.verts[oi]); } return mB[oi]; };

        for (int i = 0; i < src.tris.Length; i += 3)
        {
            int i0 = src.tris[i], i1 = src.tris[i + 1], i2 = src.tris[i + 2];
            int plus = (sd[i0] >= 0 ? 1 : 0) + (sd[i1] >= 0 ? 1 : 0) + (sd[i2] >= 0 ? 1 : 0);

            if (plus >= 2)
            {
                tA.Add(addA(i0)); tA.Add(addA(i1)); tA.Add(addA(i2));
            }
            else
            {
                tB.Add(addB(i0)); tB.Add(addB(i1)); tB.Add(addB(i2));
            }
        }

        clA = new MeshCluster { verts = vA.ToArray(), tris = tA.ToArray() };
        clB = new MeshCluster { verts = vB.ToArray(), tris = tB.ToArray() };
    }

    // ── Container: N wall boxes + 1 bottom box ───────────────────────────────

    static void FitContainer(GameObject root, Vector3[] verts,
                              Vector3 centroid, Vector3[] axes, float[] extents)
    {
        int     symIdx = FindSymmetryAxis(extents);
        Vector3 upAxis = axes[symIdx];
        if (Vector3.Dot(upAxis, Vector3.up) < 0f) upAxis = -upAxis;

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
        float wallThick = Mathf.Max(maxR  * WALL_THICKNESS_FRAC,   0.005f);
        float botThick  = Mathf.Max(totalH * BOTTOM_THICKNESS_FRAC, 0.005f);
        float arcWidth  = 2f * maxR * Mathf.Sin(Mathf.PI / CONTAINER_WALLS) * 2.1f;

        Vector3 perpA = Vector3.Cross(upAxis, Vector3.up).normalized;
        if (perpA.sqrMagnitude < 0.01f)
            perpA = Vector3.Cross(upAxis, Vector3.right).normalized;
        Vector3 perpB = Vector3.Cross(upAxis, perpA).normalized;

        for (int i = 0; i < CONTAINER_WALLS; i++)
        {
            float   angle  = i * (2f * Mathf.PI / CONTAINER_WALLS);
            Vector3 outDir = (Mathf.Cos(angle) * perpA + Mathf.Sin(angle) * perpB).normalized;

            var go = NewChild(root, string.Format("{0}Wall_{1:D2}", PREFIX, i),
                              centroid + upAxis * midH + outDir * (maxR - wallThick * 0.5f),
                              Quaternion.LookRotation(outDir, upAxis));
            var bc = go.AddComponent<BoxCollider>();
            bc.center = Vector3.zero;
            bc.size   = new Vector3(arcWidth, totalH, wallThick);
        }

        Vector3 fwd = Vector3.Cross(perpA, upAxis).normalized;
        var bot = NewChild(root, PREFIX + "Bottom",
                           centroid + upAxis * (minH + botThick * 0.5f),
                           Quaternion.LookRotation(fwd, upAxis));
        var botBc = bot.AddComponent<BoxCollider>();
        botBc.center = Vector3.zero;
        botBc.size   = new Vector3(maxR * 2f, botThick, maxR * 2f);
    }

    // ── |signed mesh volume| (divergence theorem) ─────────────────────────────

    static double MeshVolume(MeshCluster c)
    {
        if (c.tris.Length < 9) return 0;
        double vol = 0;
        for (int i = 0; i < c.tris.Length; i += 3)
            vol += Vector3.Dot(c.verts[c.tris[i]],
                               Vector3.Cross(c.verts[c.tris[i + 1]], c.verts[c.tris[i + 2]]));
        return System.Math.Abs(vol / 6.0);
    }

    // ── Openness = fraction of boundary edges (welded by position) ─────────────

    static float OpenEdgeRatio(MeshCluster c)
    {
        if (c.tris.Length < 9) return 0f;
        int[] weld = WeldByPosition(c.verts, out _);

        var edges = new Dictionary<long, int>();
        for (int i = 0; i < c.tris.Length; i += 3)
        {
            int a = weld[c.tris[i]], b = weld[c.tris[i + 1]], d = weld[c.tris[i + 2]];
            CountEdge(edges, a, b);
            CountEdge(edges, b, d);
            CountEdge(edges, d, a);
        }
        if (edges.Count == 0) return 0f;

        int boundary = 0;
        foreach (var kv in edges) if (kv.Value == 1) boundary++;
        return (float)boundary / edges.Count;
    }

    static void CountEdge(Dictionary<long, int> edges, int a, int b)
    {
        if (a == b) return;
        if (a > b) { int t = a; a = b; b = t; }
        long key = ((long)a << 32) | (uint)b;
        edges.TryGetValue(key, out int n);
        edges[key] = n + 1;
    }

    // Weld vertices by quantised position (ignores UV/normal seam duplicates).
    static int[] WeldByPosition(Vector3[] verts, out int weldCount)
    {
        Vector3 mn = verts[0], mx = verts[0];
        foreach (var v in verts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        float q = Mathf.Max((mx - mn).magnitude * 1e-4f, 1e-6f);

        var map  = new Dictionary<Vector3Int, int>();
        var weld = new int[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 v = verts[i];
            var key = new Vector3Int(Mathf.RoundToInt(v.x / q),
                                     Mathf.RoundToInt(v.y / q),
                                     Mathf.RoundToInt(v.z / q));
            if (!map.TryGetValue(key, out int id)) { id = map.Count; map[key] = id; }
            weld[i] = id;
        }
        weldCount = map.Count;
        return weld;
    }

    // ── Cylindrical symmetry axis (roundest perpendicular cross-section) ──────

    static int FindSymmetryAxis(float[] ex)
    {
        float r0 = Mathf.Min(ex[1], ex[2]) / Mathf.Max(ex[1], 1e-6f);
        float r1 = Mathf.Min(ex[0], ex[2]) / Mathf.Max(ex[0], 1e-6f);
        float r2 = Mathf.Min(ex[0], ex[1]) / Mathf.Max(ex[0], 1e-6f);
        if (r2 >= r0 && r2 >= r1) return 2;
        if (r0 >= r1)             return 0;
        return 1;
    }

    // ── Rotations ─────────────────────────────────────────────────────────────
    //  BuildPCARot:  child Y → axes[0], X → axes[1], Z → axes[2].
    //  BuildAxisRot: child Y → up,      X/Z spanning the perpendicular plane.

    static Quaternion BuildPCARot(Vector3[] axes)
    {
        Vector3 up      = axes[0];
        Vector3 right   = (axes[1] - Vector3.Dot(axes[1], up) * up).normalized;
        if (right.sqrMagnitude < 0.01f) right = Vector3.right;
        Vector3 forward = Vector3.Cross(right, up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        return Quaternion.LookRotation(forward, up);
    }

    static Quaternion BuildAxisRot(Vector3 up, Vector3 refRight)
    {
        up = up.normalized;
        Vector3 right = refRight - Vector3.Dot(refRight, up) * up;
        if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(up, Vector3.right);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(up, Vector3.forward);
        right.Normalize();
        Vector3 forward = Vector3.Cross(right, up).normalized;
        return Quaternion.LookRotation(forward, up);
    }

    // ── PCA (covariance → eigenvectors via power iteration + deflation) ───────

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

        Vector3 e0 = PowerIter(cov,             new Vector3(1f,  0.1f, 0.05f));
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

    static Mat3 Deflate(Mat3 M, Vector3 v)
    {
        Vector3 Mv     = M.Mul(v);
        float   lambda = Vector3.Dot(v, Mv);
        return new Mat3(
            M.m00-lambda*v.x*v.x, M.m01-lambda*v.x*v.y, M.m02-lambda*v.x*v.z,
            M.m10-lambda*v.y*v.x, M.m11-lambda*v.y*v.y, M.m12-lambda*v.y*v.z,
            M.m20-lambda*v.z*v.x, M.m21-lambda*v.z*v.y, M.m22-lambda*v.z*v.z);
    }

    static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
}