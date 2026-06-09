// AutoColliderTool.cs
// Assets/Scripts/Editor/AutoColliderTool.cs
//
// Standalone Unity Editor window – "Auto Collider"
// Fits Box / Sphere / Capsule primitives onto mesh objects by
// segmenting them at geometric "pinch points".
//
// Pipeline
//   1. Voxelise mesh   (Möller–Trumbore ray-parity test)
//   2. Distance field  (BFS inward from surface voxels)
//   3. Medial axis     (voxels not dominated by any 26-neighbour in dist field)
//   4. Pinch points    (local minima of radius on the skeleton)
//   5. Segment         (6-connected flood-fill, walls at pinch voxels)
//   6. Fit collider    (Box / Sphere / Capsule — best volume-efficiency wins)
//
// All generated colliders are placed under a child GameObject named
// "__AutoColliders__".  Re-running the tool removes and replaces that child.
// The operation is fully undoable.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class AutoColliderTool : EditorWindow
{
    // ─────────────────────────── state ───────────────────────────────
    private readonly List<GameObject> _targets = new List<GameObject>();
    private int    _resolution   = 20;
    private float  _pinchRatio   = 0.60f;
    private float  _fitThreshold = 0.55f;
    private int    _sectorCount  = 4;
    private Vector2 _scroll;

    private const string AutoParentName = "__AutoColliders__";
    private const string DebugParentName = "__DebugSegments__";

    // ─────────────────────────── window ──────────────────────────────
    [MenuItem("Tools/Auto Collider")]
    public static void Open() => GetWindow<AutoColliderTool>("Auto Collider");

    // ─────────────────────────── GUI ─────────────────────────────────
    private void OnGUI()
    {
        EditorGUILayout.LabelField("Auto Collider Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        _resolution = EditorGUILayout.IntSlider(
            new GUIContent("Voxel Resolution",
                "Grid resolution per axis. 16–32 works well for most meshes.\n" +
                "Higher = more accurate segmentation, but slower."),
            _resolution, 8, 64);

        _pinchRatio = EditorGUILayout.Slider(
            new GUIContent("Pinch Sensitivity",
                "A skeleton voxel is treated as a cut-point when its radius is below\n" +
                "(this value) × (max neighbour radius).  0 = never cut, 1 = cut everywhere."),
            _pinchRatio, 0.10f, 0.95f);

        _fitThreshold = EditorGUILayout.Slider(
            new GUIContent("Fill Threshold",
                "Segments whose best primitive fill-ratio is below this value get ring-decomposed.\n" +
                "Lower = only very hollow shapes (e.g. barrel walls) trigger sector boxes."),
            _fitThreshold, 0.30f, 0.90f);

        _sectorCount = EditorGUILayout.IntSlider(
            new GUIContent("Ring Sectors",
                "Number of box colliders used to approximate a hollow ring / cylinder wall."),
            _sectorCount, 3, 8);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Target Objects", EditorStyles.boldLabel);

        // ── drop zone ────────────────────────────────────────────────
        Rect dropRect = EditorGUILayout.GetControlRect(false, 46);
        GUI.Box(dropRect, "↓  Drag GameObjects here  ↓", EditorStyles.helpBox);
        HandleDrop(dropRect);

        // ── object list ───────────────────────────────────────────────
        _scroll = EditorGUILayout.BeginScrollView(
            _scroll, GUILayout.MinHeight(60), GUILayout.MaxHeight(220));

        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            EditorGUILayout.BeginHorizontal();
            _targets[i] = (GameObject)EditorGUILayout.ObjectField(
                _targets[i], typeof(GameObject), true);
            if (GUILayout.Button("✕", GUILayout.Width(24)))
                _targets.RemoveAt(i);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(10);

        GUI.enabled = _targets.Any(t => t != null);
        if (GUILayout.Button("Generate Colliders", GUILayout.Height(36)))
            GenerateAll();
        GUI.enabled = true;
    }

    private void HandleDrop(Rect area)
    {
        Event e = Event.current;
        if (!area.Contains(e.mousePosition)) return;

        if (e.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            e.Use();
        }
        else if (e.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is GameObject go && !_targets.Contains(go))
                    _targets.Add(go);
            e.Use();
        }
    }

    // ─────────────────────────── entry ───────────────────────────────
    private void GenerateAll()
    {
        foreach (var go in _targets)
        {
            if (go == null) continue;
            try { ProcessObject(go); }
            catch (Exception ex)
            {
                Debug.LogError($"[AutoCollider] {go.name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    private void ProcessObject(GameObject root)
    {
        var mf = root.GetComponent<MeshFilter>()
              ?? root.GetComponentInChildren<MeshFilter>();

        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning($"[AutoCollider] {root.name}: No MeshFilter / mesh found.");
            return;
        }

        Mesh    mesh     = mf.sharedMesh;
        int     R        = _resolution;
        Bounds  bounds   = ExpandBounds(mesh.bounds, 0.04f);
        Vector3 step     = bounds.size / R;
        float   voxelVol = step.x * step.y * step.z;

        // ── pipeline ─────────────────────────────────────────────────
        bool[,,]  solid   = Voxelise(mesh, bounds, R);
        float[,,] dist    = DistanceField(solid, R);
        bool[,,]  skel    = MedialAxis(solid, dist, R);
        var       pinches = PinchPoints(skel, dist, R);
        int[,,]   labels  = Segment(solid, pinches, R);
        int       nSeg    = ArrayMax(labels);

        if (nSeg == 0)
        {
            Debug.LogWarning($"[AutoCollider] {root.name}: Voxelisation found no solid volume.");
            return;
        }

        // ── rebuild __AutoColliders__ child ───────────────────────────
        GameObject meshGO = mf.gameObject;

        Undo.SetCurrentGroupName("Auto Collider");
        int grp = Undo.GetCurrentGroup();

        var existing = meshGO.transform.Find(AutoParentName);
        if (existing != null)
            Undo.DestroyObjectImmediate(existing.gameObject);

        var container = new GameObject(AutoParentName);
        Undo.RegisterCreatedObjectUndo(container, "Auto Collider");
        container.transform.SetParent(meshGO.transform, false);

        var existingDebug = meshGO.transform.Find(DebugParentName);
        if (existingDebug != null)
            Undo.DestroyObjectImmediate(existingDebug.gameObject);

        var debugContainer = new GameObject(DebugParentName);
        Undo.RegisterCreatedObjectUndo(debugContainer, "Auto Collider");
        debugContainer.transform.SetParent(meshGO.transform, false);

        int created = 0;
        for (int seg = 1; seg <= nSeg; seg++)
        {
            var voxels = CollectSegment(labels, seg, R);
            if (voxels.Count < 6) continue;
            CreateDebugMeshForSegment(debugContainer, voxels, bounds, step, seg, nSeg);
            AddCollider(container, voxels, bounds, step, voxelVol, seg, _fitThreshold, _sectorCount);
            created++;
        }

        Undo.CollapseUndoOperations(grp);
        Debug.Log($"[AutoCollider] {root.name}: {nSeg} segment(s) → {created} collider(s) generated.");
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 1 – VOXELISE
    // ═════════════════════════════════════════════════════════════════

    private static bool[,,] Voxelise(Mesh mesh, Bounds bounds, int R)
    {
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;
        var       grid  = new bool[R, R, R];
        Vector3   step  = bounds.size / R;

        for (int x = 0; x < R; x++)
        for (int y = 0; y < R; y++)
        for (int z = 0; z < R; z++)
        {
            Vector3 p = bounds.min + new Vector3(
                (x + .5f) * step.x,
                (y + .5f) * step.y,
                (z + .5f) * step.z);
            grid[x, y, z] = InsideMesh(p, verts, tris);
        }
        return grid;
    }

    // Slightly off-axis ray to avoid coplanar edge ambiguities
    private static readonly Vector3 RayDir = new Vector3(1f, 0.003917f, 0.006173f);

    private static bool InsideMesh(Vector3 origin, Vector3[] verts, int[] tris)
    {
        int hits = 0;
        for (int i = 0; i < tris.Length; i += 3)
            if (RayTriangle(origin, RayDir,
                            verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]]))
                hits++;
        return (hits & 1) == 1;
    }

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float E = 1e-7f;
        Vector3 e1 = v1 - v0, e2 = v2 - v0, h = Vector3.Cross(d, e2);
        float   a  = Vector3.Dot(e1, h);
        if (a > -E && a < E) return false;
        float   f  = 1f / a;
        Vector3 s  = o - v0;
        float   u  = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;
        Vector3 q  = Vector3.Cross(s, e1);
        float   v  = f * Vector3.Dot(d, q);
        if (v < 0f || u + v > 1f) return false;
        return f * Vector3.Dot(e2, q) > E;
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 2 – DISTANCE FIELD  (BFS inward from surface voxels)
    // ═════════════════════════════════════════════════════════════════

    private static float[,,] DistanceField(bool[,,] solid, int R)
    {
        var d = new float[R, R, R];
        var q = new Queue<Vector3Int>();

        for (int x = 0; x < R; x++)
        for (int y = 0; y < R; y++)
        for (int z = 0; z < R; z++)
        {
            if (!solid[x, y, z]) continue;
            bool surface = false;
            foreach (var nb in N6(new Vector3Int(x, y, z), R))
                if (!solid[nb.x, nb.y, nb.z]) { surface = true; break; }
            d[x, y, z] = surface ? 1f : float.MaxValue;
            if (surface) q.Enqueue(new Vector3Int(x, y, z));
        }

        while (q.Count > 0)
        {
            var c  = q.Dequeue();
            float next = d[c.x, c.y, c.z] + 1f;
            foreach (var nb in N6(c, R))
            {
                if (!solid[nb.x, nb.y, nb.z] || d[nb.x, nb.y, nb.z] <= next) continue;
                d[nb.x, nb.y, nb.z] = next;
                q.Enqueue(nb);
            }
        }
        return d;
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 3 – MEDIAL AXIS
    //  A solid voxel belongs to the skeleton when no 26-neighbour
    //  has a strictly higher distance-field value (local maximum).
    // ═════════════════════════════════════════════════════════════════

    private static bool[,,] MedialAxis(bool[,,] solid, float[,,] dist, int R)
    {
        var sk = new bool[R, R, R];
        for (int x = 0; x < R; x++)
        for (int y = 0; y < R; y++)
        for (int z = 0; z < R; z++)
        {
            if (!solid[x, y, z] || dist[x, y, z] < 1.5f) continue;
            float dv = dist[x, y, z];
            bool dominated = false;
            foreach (var nb in N26(new Vector3Int(x, y, z), R))
            {
                if (solid[nb.x, nb.y, nb.z] && dist[nb.x, nb.y, nb.z] > dv + 0.5f)
                {
                    dominated = true;
                    break;
                }
            }
            sk[x, y, z] = !dominated;
        }
        return sk;
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 4 – PINCH POINTS
    //  Skeleton voxels whose radius is a local minimum relative to
    //  their skeleton neighbours (radius < ratio × max-neighbour-radius).
    // ═════════════════════════════════════════════════════════════════

    private HashSet<Vector3Int> PinchPoints(bool[,,] sk, float[,,] dist, int R)
    {
        var pinches = new HashSet<Vector3Int>();
        for (int x = 0; x < R; x++)
        for (int y = 0; y < R; y++)
        for (int z = 0; z < R; z++)
        {
            if (!sk[x, y, z]) continue;
            float dv = dist[x, y, z], maxNb = 0f;
            int   cnt = 0;
            foreach (var nb in N26(new Vector3Int(x, y, z), R))
            {
                if (!sk[nb.x, nb.y, nb.z]) continue;
                cnt++;
                if (dist[nb.x, nb.y, nb.z] > maxNb)
                    maxNb = dist[nb.x, nb.y, nb.z];
            }
            if (cnt > 0 && dv < maxNb * _pinchRatio)
                pinches.Add(new Vector3Int(x, y, z));
        }
        return pinches;
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 5 – SEGMENT  (6-connected flood-fill)
    // ═════════════════════════════════════════════════════════════════

    private static int[,,] Segment(bool[,,] solid, HashSet<Vector3Int> pinches, int R)
    {
        var labels = new int[R, R, R];
        int label  = 0;

        for (int sx = 0; sx < R; sx++)
        for (int sy = 0; sy < R; sy++)
        for (int sz = 0; sz < R; sz++)
        {
            var start = new Vector3Int(sx, sy, sz);
            if (!solid[sx, sy, sz] || labels[sx, sy, sz] != 0 || pinches.Contains(start))
                continue;

            label++;
            var q = new Queue<Vector3Int>();
            q.Enqueue(start);
            labels[sx, sy, sz] = label;

            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var nb in N6(c, R))
                {
                    if (!solid[nb.x, nb.y, nb.z]
                        || labels[nb.x, nb.y, nb.z] != 0
                        || pinches.Contains(nb))
                        continue;
                    labels[nb.x, nb.y, nb.z] = label;
                    q.Enqueue(nb);
                }
            }
        }
        return labels;
    }

    // ═════════════════════════════════════════════════════════════════
    //  STEP 6 – FIT COLLIDER
    //  For each segment, compute OBB via PCA, then pick the primitive
    //  (Box / Sphere / Capsule) with the highest volume efficiency
    //  (segment_voxel_volume / primitive_volume).
    // ═════════════════════════════════════════════════════════════════

    private static void AddCollider(
        GameObject container, List<Vector3Int> voxels,
        Bounds bounds, Vector3 step, float voxelVol, int idx,
        float fitThreshold, int sectorCount)
    {
        // Convert voxel grid coords → mesh-local space points
        var pts = new Vector3[voxels.Count];
        for (int i = 0; i < voxels.Count; i++)
        {
            var v = voxels[i];
            pts[i] = bounds.min + new Vector3(
                (v.x + .5f) * step.x,
                (v.y + .5f) * step.y,
                (v.z + .5f) * step.z);
        }

        float segVol = voxels.Count * voxelVol;

        // Centroid
        Vector3 centroid = Vector3.zero;
        foreach (var p in pts) centroid += p;
        centroid /= pts.Length;

        // Centroid marker – empty GameObject so the pivot is visible in the Scene view
        var centroidGO = new GameObject($"Centroid_{idx}");
        Undo.RegisterCreatedObjectUndo(centroidGO, "Auto Collider");
        centroidGO.transform.SetParent(container.transform, false);
        centroidGO.transform.localPosition = centroid;

        // OBB axes and extents via PCA
        PCA(pts, centroid, out Vector3 ax0, out Vector3 ax1, out Vector3 ax2);

        float mn0 = float.MaxValue, mx0 = float.MinValue;
        float mn1 = float.MaxValue, mx1 = float.MinValue;
        float mn2 = float.MaxValue, mx2 = float.MinValue;

        foreach (var p in pts)
        {
            Vector3 d = p - centroid;
            float d0 = Vector3.Dot(d, ax0);
            float d1 = Vector3.Dot(d, ax1);
            float d2 = Vector3.Dot(d, ax2);
            if (d0 < mn0) mn0 = d0; if (d0 > mx0) mx0 = d0;
            if (d1 < mn1) mn1 = d1; if (d1 > mx1) mx1 = d1;
            if (d2 < mn2) mn2 = d2; if (d2 > mx2) mx2 = d2;
        }

        float len0 = mx0 - mn0;   // primary   (longest)
        float len1 = mx1 - mn1;   // secondary
        float len2 = mx2 - mn2;   // tertiary  (shortest)

        // OBB centre in mesh-local space
        Vector3 obbCentre = centroid
            + ax0 * ((mn0 + mx0) * .5f)
            + ax1 * ((mn1 + mx1) * .5f)
            + ax2 * ((mn2 + mx2) * .5f);

        // ── Volume of each candidate primitive ────────────────────────

        // Box  (fits tightly around OBB)
        float boxVol  = len0 * len1 * len2;

        // Sphere  (min enclosing sphere = half OBB diagonal)
        float sphR   = 0.5f * Mathf.Sqrt(len0*len0 + len1*len1 + len2*len2);
        float sphVol = (4f / 3f) * Mathf.PI * sphR * sphR * sphR;

        // Capsule – try all 3 principal axes, keep smallest-volume fit
        float capVol = float.MaxValue, capR = 0f, capH = 0f;
        Vector3 capAxis = ax0;
        var capCandidates = new (float pri, float s1, float s2, Vector3 axis)[]
        {
            (len0, len1, len2, ax0),
            (len1, len0, len2, ax1),
            (len2, len0, len1, ax2),
        };
        foreach (var (pri, s1, s2, axis) in capCandidates)
        {
            float r   = Mathf.Max(s1, s2) * .5f;
            float h   = pri;
            float cyl = Mathf.Max(0f, h - 2f * r);
            float v   = Mathf.PI * r * r * cyl + (4f / 3f) * Mathf.PI * r * r * r;
            if (v < capVol) { capVol = v; capR = r; capH = h; capAxis = axis; }
        }

        // Efficiency: how tightly does the primitive wrap the segment?
        // (Higher ratio = better fit.  Clamp min volume to avoid ÷0.)
        float sBox = boxVol > 1e-9f ? segVol / boxVol : 0f;
        float sSph = sphVol > 1e-9f ? segVol / sphVol : 0f;
        float sCap = capVol > 1e-9f ? segVol / capVol : 0f;
        float best = Mathf.Max(sBox, sSph, sCap);

        // ── Ring / hollow-shape decomposition ─────────────────────────
        // ax2 = tertiary PCA axis = the axis perpendicular to the ring plane
        if (best < fitThreshold)
        {
            float ringOuterR = Mathf.Max(len0, len1) * .5f;
            if (IsHollowRing(pts, obbCentre, ax2, ringOuterR))
            {
                SplitRingWallAndDisc(pts, obbCentre, ax2, ringOuterR,
                    out Vector3[] wallPts, out Vector3[] discPts);
                if (wallPts.Length >= 4)
                    AddRingSectors(container, wallPts, obbCentre, ax2, sectorCount, idx);
                if (discPts.Length >= 4)
                    AddDiscBox(container, discPts, ax2, idx);
                return;
            }
        }

        // ── Create child GameObject ───────────────────────────────────
        var child = new GameObject($"Col_{idx}");
        Undo.RegisterCreatedObjectUndo(child, "Auto Collider");
        child.transform.SetParent(container.transform, false);
        child.transform.localPosition = obbCentre;

        if (best == sSph && sSph > 0f)
        {
            // ── Sphere ────────────────────────────────────────────────
            var sc    = Undo.AddComponent<SphereCollider>(child);
            sc.radius = sphR;
        }
        else if (best == sCap && sCap > 0f)
        {
            // ── Capsule  (direction = Y, so rotate child to align Y→capAxis)
            child.transform.localRotation = Quaternion.FromToRotation(Vector3.up, capAxis);
            var cc       = Undo.AddComponent<CapsuleCollider>(child);
            cc.direction = 1;    // Y-axis
            cc.radius    = capR;
            cc.height    = capH;
        }
        else
        {
            // ── Box  (local X→ax0, Y→ax1, Z→ax2)
            // LookRotation(forward=ax2, up=ax1) → right = cross(ax1,ax2) = ax0
            child.transform.localRotation = Quaternion.LookRotation(ax2, ax1);
            var bc  = Undo.AddComponent<BoxCollider>(child);
            bc.size = new Vector3(len0, len1, len2);
        }
    }

    private static void CreateDebugMeshForSegment(
        GameObject parent, List<Vector3Int> voxels, Bounds bounds, Vector3 step, int seg, int totalSegs)
    {
        var segmentGO = new GameObject($"DebugSegment_{seg}");
        Undo.RegisterCreatedObjectUndo(segmentGO, "Auto Collider");
        segmentGO.transform.SetParent(parent.transform, false);

        var mf = segmentGO.AddComponent<MeshFilter>();
        var mesh = CreateVoxelMesh(voxels, bounds, step);
        mf.sharedMesh = mesh;

        var mr = segmentGO.AddComponent<MeshRenderer>();
        Color color = Color.HSVToRGB((float)(seg - 1) / totalSegs, 0.85f, 0.9f);
        color.a = 0.5f;

        Shader shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        var mat = new Material(shader);
        mat.name = $"Segment_{seg}_Mat";
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

    private static Mesh CreateVoxelMesh(List<Vector3Int> voxels, Bounds bounds, Vector3 step)
    {
        var mesh = new Mesh();
        mesh.name = "SegmentMesh";
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        Vector3[] cubeVertices = {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f),
        };

        int[] cubeTriangles = {
            0, 2, 1,  0, 3, 2, // Front
            1, 6, 5,  1, 2, 6, // Right
            5, 7, 4,  5, 6, 7, // Back
            4, 3, 0,  4, 7, 3, // Left
            3, 6, 2,  3, 7, 6, // Top
            4, 1, 5,  4, 0, 1  // Bottom
        };

        int vCount = 0;
        foreach (var v in voxels)
        {
            Vector3 center = bounds.min + new Vector3(
                (v.x + .5f) * step.x,
                (v.y + .5f) * step.y,
                (v.z + .5f) * step.z);

            for (int i = 0; i < 8; i++)
            {
                vertices.Add(center + Vector3.Scale(cubeVertices[i], step));
            }

            for (int i = 0; i < 36; i++)
            {
                triangles.Add(vCount + cubeTriangles[i]);
            }

            vCount += 8;
        }

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    // ═════════════════════════════════════════════════════════════════
    //  RING HELPERS
    // ═════════════════════════════════════════════════════════════════

    // Returns true when < 15 % of points lie within the inner 45 % of
    // the ring's outer radius (i.e. the centre column is hollow).
    private static bool IsHollowRing(Vector3[] pts, Vector3 centre,
        Vector3 ringAxis, float outerR)
    {
        float innerR2 = (outerR * 0.45f) * (outerR * 0.45f);
        int inner = 0;
        foreach (var p in pts)
        {
            Vector3 d      = p - centre;
            Vector3 radial = d - Vector3.Dot(d, ringAxis) * ringAxis;
            if (radial.sqrMagnitude < innerR2) inner++;
        }
        return inner < pts.Length * 0.15f;
    }

    // Splits points into:
    //   wallPts  – radial distance >  50 % of outerR  (cylindrical shell)
    //   discPts  – radial distance <= 50 % of outerR  (end caps / bottom disc)
    private static void SplitRingWallAndDisc(Vector3[] pts, Vector3 centre,
        Vector3 ringAxis, float outerR,
        out Vector3[] wallPts, out Vector3[] discPts)
    {
        float splitR2 = (outerR * 0.5f) * (outerR * 0.5f);
        var wall = new List<Vector3>();
        var disc = new List<Vector3>();
        foreach (var p in pts)
        {
            Vector3 d      = p - centre;
            Vector3 radial = d - Vector3.Dot(d, ringAxis) * ringAxis;
            if (radial.sqrMagnitude > splitR2) wall.Add(p);
            else                               disc.Add(p);
        }
        wallPts = wall.ToArray();
        discPts = disc.ToArray();
    }

    // Decomposes a ring's wall points into N angular sector box colliders.
    private static void AddRingSectors(GameObject container, Vector3[] pts,
        Vector3 ringCentre, Vector3 ringAxis, int N, int idxBase)
    {
        Vector3 up  = ringAxis.normalized;
        Vector3 tmp = Mathf.Abs(Vector3.Dot(up, Vector3.up)) < 0.9f
                    ? Vector3.up : Vector3.right;
        Vector3 rgt = Vector3.Cross(up, tmp).normalized;
        Vector3 fwd = Vector3.Cross(rgt, up).normalized;

        float sectorAngle = 2f * Mathf.PI / N;
        var sectors = new List<Vector3>[N];
        for (int i = 0; i < N; i++) sectors[i] = new List<Vector3>();

        foreach (var p in pts)
        {
            Vector3 d      = p - ringCentre;
            Vector3 planar = d - Vector3.Dot(d, up) * up;
            float   angle  = Mathf.Atan2(Vector3.Dot(planar, fwd),
                                         Vector3.Dot(planar, rgt));
            if (angle < 0f) angle += 2f * Mathf.PI;
            int s = Mathf.Min(Mathf.FloorToInt(angle / sectorAngle), N - 1);
            sectors[s].Add(p);
        }

        for (int i = 0; i < N; i++)
        {
            if (sectors[i].Count < 4) continue;

            float   mid  = (i + 0.5f) * sectorAngle;
            Vector3 sRgt = (Mathf.Cos(mid) * rgt + Mathf.Sin(mid) * fwd).normalized;
            Vector3 sFwd = Vector3.Cross(up, sRgt).normalized;

            Vector3 sec = Vector3.zero;
            foreach (var p in sectors[i]) sec += p;
            sec /= sectors[i].Count;

            float mnR=float.MaxValue, mxR=float.MinValue;
            float mnU=float.MaxValue, mxU=float.MinValue;
            float mnT=float.MaxValue, mxT=float.MinValue;
            foreach (var p in sectors[i])
            {
                Vector3 d = p - sec;
                float r = Vector3.Dot(d, sRgt);
                float u = Vector3.Dot(d, up);
                float t = Vector3.Dot(d, sFwd);
                if (r < mnR) mnR = r; if (r > mxR) mxR = r;
                if (u < mnU) mnU = u; if (u > mxU) mxU = u;
                if (t < mnT) mnT = t; if (t > mxT) mxT = t;
            }

            Vector3 boxCentre = sec
                + sRgt * ((mnR + mxR) * .5f)
                + up   * ((mnU + mxU) * .5f)
                + sFwd * ((mnT + mxT) * .5f);

            var child = new GameObject($"Col_{idxBase}_W{i}");
            Undo.RegisterCreatedObjectUndo(child, "Auto Collider");
            child.transform.SetParent(container.transform, false);
            child.transform.localPosition = boxCentre;
            child.transform.localRotation = Quaternion.LookRotation(sFwd, up);
            var bc = Undo.AddComponent<BoxCollider>(child);
            bc.size = new Vector3(mxR - mnR, mxU - mnU, mxT - mnT);
        }
    }

    // Fits a single flat box to the bottom/top disc voxels.
    private static void AddDiscBox(GameObject container, Vector3[] pts,
        Vector3 discNormal, int idxBase)
    {
        Vector3 up  = discNormal.normalized;
        Vector3 tmp = Mathf.Abs(Vector3.Dot(up, Vector3.up)) < 0.9f
                    ? Vector3.up : Vector3.right;
        Vector3 rgt = Vector3.Cross(up, tmp).normalized;
        Vector3 fwd = Vector3.Cross(rgt, up).normalized;

        Vector3 cen = Vector3.zero;
        foreach (var p in pts) cen += p;
        cen /= pts.Length;

        float mnR=float.MaxValue, mxR=float.MinValue;
        float mnU=float.MaxValue, mxU=float.MinValue;
        float mnF=float.MaxValue, mxF=float.MinValue;
        foreach (var p in pts)
        {
            Vector3 d = p - cen;
            float r = Vector3.Dot(d, rgt);
            float u = Vector3.Dot(d, up);
            float f = Vector3.Dot(d, fwd);
            if (r < mnR) mnR = r; if (r > mxR) mxR = r;
            if (u < mnU) mnU = u; if (u > mxU) mxU = u;
            if (f < mnF) mnF = f; if (f > mxF) mxF = f;
        }

        Vector3 boxCentre = cen
            + rgt * ((mnR + mxR) * .5f)
            + up  * ((mnU + mxU) * .5f)
            + fwd * ((mnF + mxF) * .5f);

        var child = new GameObject($"Col_{idxBase}_D");
        Undo.RegisterCreatedObjectUndo(child, "Auto Collider");
        child.transform.SetParent(container.transform, false);
        child.transform.localPosition = boxCentre;
        child.transform.localRotation = Quaternion.LookRotation(fwd, up);
        var bc = Undo.AddComponent<BoxCollider>(child);
        bc.size = new Vector3(mxR - mnR, mxU - mnU, mxF - mnF);
    }

    // ═════════════════════════════════════════════════════════════════
    //  PCA – Jacobi eigenvalue decomposition for 3×3 symmetric matrix
    //  Returns eigenvectors sorted by descending eigenvalue.
    //  e0 = primary axis (max variance), e1 = secondary, e2 = tertiary.
    // ═════════════════════════════════════════════════════════════════

    private static void PCA(
        Vector3[] pts, Vector3 centroid,
        out Vector3 e0, out Vector3 e1, out Vector3 e2)
    {
        // Build covariance matrix
        double c00=0, c01=0, c02=0, c11=0, c12=0, c22=0;
        foreach (var p in pts)
        {
            double dx = p.x - centroid.x;
            double dy = p.y - centroid.y;
            double dz = p.z - centroid.z;
            c00 += dx * dx; c01 += dx * dy; c02 += dx * dz;
                            c11 += dy * dy; c12 += dy * dz;
                                            c22 += dz * dz;
        }
        int n = pts.Length;
        c00/=n; c01/=n; c02/=n; c11/=n; c12/=n; c22/=n;

        // Jacobi iteration on symmetric 3×3
        double[,] A = { { c00, c01, c02 },
                        { c01, c11, c12 },
                        { c02, c12, c22 } };
        double[,] V = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int iter = 0; iter < 60; iter++)
        {
            // Find the largest off-diagonal element
            int p = 0, q = 1;
            double mx = Math.Abs(A[0, 1]);
            if (Math.Abs(A[0, 2]) > mx) { mx = Math.Abs(A[0, 2]); p = 0; q = 2; }
            if (Math.Abs(A[1, 2]) > mx) {                          p = 1; q = 2; }
            if (Math.Abs(A[p, q]) < 1e-12) break;

            double theta = (A[q, q] - A[p, p]) / (2.0 * A[p, q]);
            double t     = (theta >= 0 ? 1 : -1) /
                           (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
            double cs    = 1.0 / Math.Sqrt(t * t + 1.0);
            double sn    = t * cs;

            double app = A[p, p], aqq = A[q, q], apq = A[p, q];
            A[p, p] = cs * cs * app - 2 * sn * cs * apq + sn * sn * aqq;
            A[q, q] = sn * sn * app + 2 * sn * cs * apq + cs * cs * aqq;
            A[p, q] = A[q, p] = 0;

            int r = 3 - p - q;   // the third index: {0,1,2} \ {p,q}
            double arp = A[r, p], arq = A[r, q];
            A[r, p] = A[p, r] = cs * arp - sn * arq;
            A[r, q] = A[q, r] = sn * arp + cs * arq;

            for (int k = 0; k < 3; k++)
            {
                double vkp = V[k, p], vkq = V[k, q];
                V[k, p] = cs * vkp - sn * vkq;
                V[k, q] = sn * vkp + cs * vkq;
            }
        }

        // Sort eigenvectors by descending eigenvalue
        double[] ev  = { A[0, 0], A[1, 1], A[2, 2] };
        int[]    ord = { 0, 1, 2 };
        Array.Sort(ev, ord);
        Array.Reverse(ord);   // ord[0] → largest eigenvalue index

        e0 = new Vector3((float)V[0, ord[0]], (float)V[1, ord[0]], (float)V[2, ord[0]]).normalized;
        e1 = new Vector3((float)V[0, ord[1]], (float)V[1, ord[1]], (float)V[2, ord[1]]).normalized;
        e2 = Vector3.Cross(e0, e1).normalized;   // ensure right-handed frame
    }

    // ─────────────────────────── helpers ─────────────────────────────

    private static Bounds ExpandBounds(Bounds b, float fraction)
    {
        b.Expand(b.size * fraction);
        return b;
    }

    private static List<Vector3Int> CollectSegment(int[,,] labels, int seg, int R)
    {
        var list = new List<Vector3Int>();
        for (int x = 0; x < R; x++)
        for (int y = 0; y < R; y++)
        for (int z = 0; z < R; z++)
            if (labels[x, y, z] == seg)
                list.Add(new Vector3Int(x, y, z));
        return list;
    }

    private static int ArrayMax(int[,,] a)
    {
        int m = 0;
        foreach (int v in a) if (v > m) m = v;
        return m;
    }

    // ── Neighbour iterators ───────────────────────────────────────────

    private static readonly (int dx, int dy, int dz)[] _d6 =
    {
        (1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1)
    };

    private static IEnumerable<Vector3Int> N6(Vector3Int v, int R)
    {
        foreach (var (dx, dy, dz) in _d6)
        {
            int nx = v.x + dx, ny = v.y + dy, nz = v.z + dz;
            // Unsigned comparison handles negative values (they become huge)
            if ((uint)nx < (uint)R && (uint)ny < (uint)R && (uint)nz < (uint)R)
                yield return new Vector3Int(nx, ny, nz);
        }
    }

    private static IEnumerable<Vector3Int> N26(Vector3Int v, int R)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            int nx = v.x + dx, ny = v.y + dy, nz = v.z + dz;
            if ((uint)nx < (uint)R && (uint)ny < (uint)R && (uint)nz < (uint)R)
                yield return new Vector3Int(nx, ny, nz);
        }
    }
}
