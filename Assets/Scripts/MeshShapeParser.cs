using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 3D Shape Parsing — axis-first decomposition.
///
/// Pipeline for cylindrical objects (barrels, cans, pipes):
///   1. Area-weighted PCA on face normals → detect barrel axis.
///      normals of wall faces are all perpendicular to the axis
///      → axis = smallest-eigenvalue direction of normal covariance.
///   2. Separate faces: cap (|dot(n, axis)| > capDot) vs wall.
///   3. Caps  → BFS connected components → FitFlat per cap
///              (circular cap → thin Cylinder disc, else Box).
///   4. Wall  → ONE FitCylinder for ALL wall vertices together.
///              Radius = max radial distance → collider matches outer surface exactly.
///
/// For non-cylindrical objects: region-grow by normal angle → Box per flat panel.
/// </summary>
public static class MeshShapeParser
{
    // ─── Public types ────────────────────────────────────────────────────────

    public enum PrimitiveShape { Box, Sphere, Capsule, Cylinder }

    public class ShapeResult
    {
        public PrimitiveShape Shape;
        public Vector3        LocalCenter;
        public Quaternion     LocalRotation;
        public float HalfHeight;
        public float HalfRadiusA;
        public float HalfRadiusB;
        public float FullHeight => HalfHeight * 2f;
        public float AvgRadius  => (HalfRadiusA + HalfRadiusB) * 0.5f;
        public Vector3 BoxSize  => new Vector3(HalfRadiusA * 2f, FullHeight, HalfRadiusB * 2f);
    }

    // ─── Private types ───────────────────────────────────────────────────────

    private struct TriData
    {
        public Vector3 Normal;
        public Vector3 Centroid;
        public float   Area;
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public static List<ShapeResult> AnalyzeDecomposed(
        Mesh  mesh,
        float normalAngleDeg  = 35f,
        float minAreaFraction = 0.02f,
        float capDotThreshold = 0.65f)
    {
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;

        if (verts == null || tris == null || tris.Length < 9)
            return new List<ShapeResult> { Analyze(mesh) };

        int      triCount = tris.Length / 3;
        TriData[] td      = BuildTriData(verts, tris, triCount);
        float     totalArea = 0f;
        foreach (var t in td) totalArea += t.Area;
        if (totalArea < 1e-9f) return new List<ShapeResult> { Analyze(mesh) };

        // ── Step 1: detect barrel axis via face-normal PCA ───────────────────
        // For a barrel, wall normals spread in a plane perpendicular to the axis.
        // The eigenvector with the SMALLEST eigenvalue = the axis all wall normals
        // are perpendicular to = the barrel height axis.
        FindNormalPCA(td, totalArea, out Vector3[] normAxes, out float[] normEvals);
        // normAxes sorted: [0] largest spread, [2] smallest spread (axis candidate)
        float r01 = normEvals[0] > 1e-7f ? normEvals[1] / normEvals[0] : 0f;
        float r12 = normEvals[1] > 1e-7f ? normEvals[2] / normEvals[1] : 0f;
        bool  isCylindrical = r01 > 0.15f && r12 < 0.35f;

        if (!isCylindrical)
            return RegionGrowFallback(verts, tris, td, totalArea, normalAngleDeg, minAreaFraction);

        Vector3 mainAxis = normAxes[2];
        float   minArea  = totalArea * minAreaFraction;

        var edgeMap = BuildEdgeMap(tris, triCount);
        var triAdj  = BuildTriAdjacency(tris, triCount, edgeMap);
        var results = new List<ShapeResult>();

        // ── Step 2: classify triangles ───────────────────────────────────────
        // Cap  : face normal is roughly PARALLEL to mainAxis
        // Wall : face normal is roughly PERPENDICULAR to mainAxis
        var capSet = new HashSet<int>();
        var wallList = new List<int>();
        for (int i = 0; i < triCount; i++)
        {
            if (Mathf.Abs(Vector3.Dot(td[i].Normal, mainAxis)) > capDotThreshold)
                capSet.Add(i);
            else
                wallList.Add(i);
        }

        // ── Step 3: Caps → connectivity BFS → FitFlat each cap ───────────────
        var capVisited = new bool[triCount];
        foreach (int seed in capSet)
        {
            if (capVisited[seed]) continue;

            var comp = new List<int>();
            var bfsQ = new Queue<int>();
            bfsQ.Enqueue(seed); capVisited[seed] = true;

            while (bfsQ.Count > 0)
            {
                int t = bfsQ.Dequeue(); comp.Add(t);
                foreach (int nb in triAdj[t])
                {
                    if (!capVisited[nb] && capSet.Contains(nb))
                    { capVisited[nb] = true; bfsQ.Enqueue(nb); }
                }
            }

            float compArea = 0f; Vector3 avgN = Vector3.zero;
            foreach (int ti in comp) { compArea += td[ti].Area; avgN += td[ti].Normal * td[ti].Area; }
            if (compArea < minArea) continue;

            var capPts = GetVertsFromTris(comp, verts, tris);
            if (capPts.Count < 3) continue;

            ShapeResult sr = FitFlat(capPts, avgN.normalized);
            if (sr != null) results.Add(sr);
        }

        // ── Step 4: Wall → ONE cylinder collider ─────────────────────────────
        // All wall vertices fed into FitCylinder with the detected mainAxis.
        // Radius = max radial distance → matches the outer envelope exactly.
        // This avoids horizontal slicing and correctly represents the barrel wall.
        if (wallList.Count > 0)
        {
            float wallArea = 0f;
            foreach (int ti in wallList) wallArea += td[ti].Area;

            if (wallArea >= minArea)
            {
                var wallPts = GetVertsFromTris(wallList, verts, tris);
                if (wallPts.Count >= 3)
                    results.Add(FitCylinder(wallPts, mainAxis));
            }
        }

        if (results.Count == 0) results.Add(Analyze(mesh));
        return results;
    }

    /// <summary>Single-primitive fallback: fits one shape to the whole mesh via vertex PCA.</summary>
    public static ShapeResult Analyze(Mesh mesh)
    {
        Vector3[] verts = mesh.vertices;
        if (verts == null || verts.Length < 4) return FallbackBox(mesh);

        Vector3  centroid = ComputeCentroid(verts);
        float[,] cov      = ComputeCovariance(verts, centroid);
        JacobiEigen3x3(cov, out Vector3[] axes, out float[] evals);
        SortAxesByEigenvalue(ref axes, ref evals);
        EnsureRightHanded(ref axes);

        float minP = float.MaxValue, maxP = float.MinValue;
        float minQ = float.MaxValue, maxQ = float.MinValue;
        float minR = float.MaxValue, maxR = float.MinValue;
        foreach (Vector3 v in verts)
        {
            Vector3 d = v - centroid;
            float p = Vector3.Dot(d, axes[0]);
            float q = Vector3.Dot(d, axes[1]);
            float r = Vector3.Dot(d, axes[2]);
            if (p < minP) minP = p; if (p > maxP) maxP = p;
            if (q < minQ) minQ = q; if (q > maxQ) maxQ = q;
            if (r < minR) minR = r; if (r > maxR) maxR = r;
        }

        float d0 = maxP - minP, d1 = maxQ - minQ, d2 = maxR - minR;
        Vector3 obbCenter = centroid
            + axes[0] * ((minP + maxP) * 0.5f)
            + axes[1] * ((minQ + maxQ) * 0.5f)
            + axes[2] * ((minR + maxR) * 0.5f);

        float circularity = ComputeCircularity(verts, centroid, axes[1], axes[2]);
        float endFlatness = ComputeEndFlatness(verts, centroid, axes[0], minP, maxP, axes[1], axes[2]);
        float uniformity  = Mathf.Max(d0, d1, d2) > 0f ? Mathf.Min(d0, d1, d2) / Mathf.Max(d0, d1, d2) : 1f;
        float elongation  = d1 > 0f ? d0 / d1 : 1f;
        float minorRatio  = d2 > 0f ? d1 / d2 : 1f;

        PrimitiveShape shape;
        if (uniformity > 0.82f && circularity > 0.78f)
            shape = PrimitiveShape.Sphere;
        else if (elongation > 1.35f && circularity > 0.72f && minorRatio < 1.35f)
            shape = endFlatness > 0.65f ? PrimitiveShape.Cylinder : PrimitiveShape.Capsule;
        else
            shape = PrimitiveShape.Box;

        return new ShapeResult
        {
            Shape         = shape,
            LocalCenter   = obbCenter,
            LocalRotation = Quaternion.LookRotation(axes[2], axes[0]),
            HalfHeight    = d0 * 0.5f,
            HalfRadiusA   = d1 * 0.5f,
            HalfRadiusB   = d2 * 0.5f,
        };
    }

    // ─── Build helpers ───────────────────────────────────────────────────────

    private static TriData[] BuildTriData(Vector3[] verts, int[] tris, int triCount)
    {
        var td = new TriData[triCount];
        for (int i = 0; i < triCount; i++)
        {
            Vector3 a = verts[tris[i*3]], b = verts[tris[i*3+1]], c = verts[tris[i*3+2]];
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float   mag   = cross.magnitude;
            td[i].Area     = mag * 0.5f;
            td[i].Normal   = mag > 1e-9f ? cross / mag : Vector3.up;
            td[i].Centroid = (a + b + c) * (1f / 3f);
        }
        return td;
    }

    private static Dictionary<long, List<int>> BuildEdgeMap(int[] tris, int triCount)
    {
        var map = new Dictionary<long, List<int>>(triCount * 3);
        for (int i = 0; i < triCount; i++)
        for (int e = 0; e < 3; e++)
        {
            int v0 = tris[i*3+e], v1 = tris[i*3+(e+1)%3];
            long key = EdgeKey(v0, v1);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>(2);
            list.Add(i);
        }
        return map;
    }

    private static List<int>[] BuildTriAdjacency(int[] tris, int triCount,
        Dictionary<long, List<int>> edgeMap)
    {
        var adj = new List<int>[triCount];
        for (int i = 0; i < triCount; i++) adj[i] = new List<int>(3);
        foreach (var kv in edgeMap)
        {
            if (kv.Value.Count != 2) continue;
            adj[kv.Value[0]].Add(kv.Value[1]);
            adj[kv.Value[1]].Add(kv.Value[0]);
        }
        return adj;
    }

    private static long EdgeKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    // ─── Normal PCA ──────────────────────────────────────────────────────────

    private static void FindNormalPCA(TriData[] td, float totalArea,
        out Vector3[] axes, out float[] evals)
    {
        float invA = 1f / totalArea;
        Vector3 mn = Vector3.zero;
        foreach (var t in td) mn += t.Normal * (t.Area * invA);

        float c00=0,c01=0,c02=0,c11=0,c12=0,c22=0;
        foreach (var t in td)
        {
            float w = t.Area * invA;
            Vector3 d = t.Normal - mn;
            c00+=w*d.x*d.x; c01+=w*d.x*d.y; c02+=w*d.x*d.z;
            c11+=w*d.y*d.y; c12+=w*d.y*d.z;
            c22+=w*d.z*d.z;
        }
        float[,] cov = { {c00,c01,c02}, {c01,c11,c12}, {c02,c12,c22} };
        JacobiEigen3x3(cov, out axes, out evals);
        SortAxesByEigenvalue(ref axes, ref evals);
    }

    // ─── Vertex helpers ──────────────────────────────────────────────────────

    private static List<Vector3> GetVertsFromTris(IEnumerable<int> triIndices,
        Vector3[] verts, int[] tris)
    {
        var set = new HashSet<int>();
        foreach (int ti in triIndices)
        { set.Add(tris[ti*3]); set.Add(tris[ti*3+1]); set.Add(tris[ti*3+2]); }
        var pts = new List<Vector3>(set.Count);
        foreach (int vi in set) pts.Add(verts[vi]);
        return pts;
    }

    // ─── Fallback for non-cylindrical objects ────────────────────────────────

    private static List<ShapeResult> RegionGrowFallback(
        Vector3[] verts, int[] tris, TriData[] td,
        float totalArea, float normalAngleDeg, float minAreaFraction)
    {
        int   triCount  = tris.Length / 3;
        var   edgeMap   = BuildEdgeMap(tris, triCount);
        var   triAdj    = BuildTriAdjacency(tris, triCount, edgeMap);
        float cos       = Mathf.Cos(normalAngleDeg * Mathf.Deg2Rad);
        float minArea   = totalArea * minAreaFraction;
        var   visited   = new bool[triCount];
        var   results   = new List<ShapeResult>();

        for (int seed = 0; seed < triCount; seed++)
        {
            if (visited[seed]) continue;
            var region = new List<int>();
            var q      = new Queue<int>();
            q.Enqueue(seed); visited[seed] = true;
            while (q.Count > 0)
            {
                int t = q.Dequeue(); region.Add(t);
                foreach (int nb in triAdj[t])
                    if (!visited[nb] && Vector3.Dot(td[t].Normal, td[nb].Normal) >= cos)
                    { visited[nb] = true; q.Enqueue(nb); }
            }
            float rArea = 0f; Vector3 avgN = Vector3.zero;
            foreach (int ti in region) { rArea += td[ti].Area; avgN += td[ti].Normal * td[ti].Area; }
            if (rArea < minArea) continue;
            var pts = GetVertsFromTris(region, verts, tris);
            if (pts.Count < 3) continue;
            ShapeResult sr = FitFlat(pts, avgN.normalized);
            if (sr != null) results.Add(sr);
        }
        return results;
    }

    // ─── Primitive fitting ───────────────────────────────────────────────────

    /// <summary>
    /// Fits a Cylinder to a set of surface points around a known axis.
    /// Center and radius are derived from the vertices so the collider
    /// matches the outer envelope exactly — no sticking out.
    /// </summary>
    private static ShapeResult FitCylinder(List<Vector3> pts, Vector3 axis)
    {
        axis = axis.normalized;

        // Find centroid
        Vector3 centroid = Vector3.zero;
        foreach (Vector3 p in pts) centroid += p;
        centroid /= pts.Count;

        // Height span along axis
        float minProj = float.MaxValue, maxProj = float.MinValue;
        foreach (Vector3 p in pts)
        {
            float proj = Vector3.Dot(p - centroid, axis);
            if (proj < minProj) minProj = proj;
            if (proj > maxProj) maxProj = proj;
        }

        // Shift centre to the midpoint of the height span
        Vector3 axisCenter = centroid + axis * ((minProj + maxProj) * 0.5f);

        // Outer radius = max radial distance from the axis line through axisCenter
        float maxR = 0f;
        foreach (Vector3 p in pts)
        {
            Vector3 d      = p - axisCenter;
            Vector3 radial = d - axis * Vector3.Dot(d, axis);
            float   r      = radial.magnitude;
            if (r > maxR) maxR = r;
        }

        return new ShapeResult
        {
            Shape         = PrimitiveShape.Cylinder,
            LocalCenter   = axisCenter,
            // child Y-axis = cylinder axis (height direction)
            LocalRotation = Quaternion.FromToRotation(Vector3.up, axis),
            HalfHeight    = (maxProj - minProj) * 0.5f,
            HalfRadiusA   = maxR,
            HalfRadiusB   = maxR,
        };
    }

    /// <summary>
    /// Fits a Box (or thin Cylinder disc for round caps) to a flat surface region.
    /// The OBB is built in the plane defined by the region's average normal.
    /// The box is purely 2D (very thin in the normal direction) so it lies
    /// flush with the mesh surface and never protrudes outside.
    /// Round caps (circularity > 0.75) are returned as Cylinder discs.
    /// </summary>
    private static ShapeResult FitFlat(List<Vector3> pts, Vector3 normal)
    {
        normal = normal.normalized;

        // Build an orthonormal basis in the surface plane
        Vector3 u = Vector3.Cross(normal,
            Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(normal, u).normalized;

        // Centroid
        Vector3 centroid = Vector3.zero;
        foreach (Vector3 p in pts) centroid += p;
        centroid /= pts.Count;

        float minU = float.MaxValue, maxU = float.MinValue;
        float minV = float.MaxValue, maxV = float.MinValue;
        float minN = float.MaxValue, maxN = float.MinValue;

        foreach (Vector3 p in pts)
        {
            Vector3 d  = p - centroid;
            float   pu = Vector3.Dot(d, u);
            float   pv = Vector3.Dot(d, v);
            float   pn = Vector3.Dot(d, normal);
            if (pu < minU) minU = pu; if (pu > maxU) maxU = pu;
            if (pv < minV) minV = pv; if (pv > maxV) maxV = pv;
            if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
        }

        float extU = (maxU - minU) * 0.5f;
        float extV = (maxV - minV) * 0.5f;
        // Use the actual mesh thickness; floor at a small value so the collider
        // is never degenerate, but keep it very thin (≤ actual surface depth)
        // so the collider stays within the mesh bounds.
        float meshThick = (maxN - minN) * 0.5f;
        float extN      = Mathf.Max(meshThick, 0.005f);

        Vector3 obbCenter = centroid
            + u      * ((minU + maxU) * 0.5f)
            + v      * ((minV + maxV) * 0.5f)
            + normal * ((minN + maxN) * 0.5f);

        // Round cross-section → treat as a Cylinder disc (e.g. barrel bottom cap)
        float maxExt      = Mathf.Max(extU, extV);
        float minExt2     = Mathf.Min(extU, extV);
        float circularity = maxExt > 1e-6f ? minExt2 / maxExt : 0f;
        bool  isDisc      = circularity > 0.75f && maxExt > extN * 4f;

        // Rotation: child Y = normal (thickness direction)
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, normal);

        return new ShapeResult
        {
            Shape         = isDisc ? PrimitiveShape.Cylinder : PrimitiveShape.Box,
            LocalCenter   = obbCenter,
            LocalRotation = rot,
            HalfHeight    = extN,    // thin in normal direction → stays inside mesh
            HalfRadiusA   = extU,
            HalfRadiusB   = extV,
        };
    }

    // ─── Math helpers ────────────────────────────────────────────────────────

    private static ShapeResult FallbackBox(Mesh mesh)
    {
        Bounds b = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
        return new ShapeResult
        {
            Shape         = PrimitiveShape.Box,
            LocalCenter   = b.center,
            LocalRotation = Quaternion.identity,
            HalfHeight    = b.extents.y,
            HalfRadiusA   = b.extents.x,
            HalfRadiusB   = b.extents.z,
        };
    }

    private static Vector3 ComputeCentroid(Vector3[] verts)
    {
        Vector3 s = Vector3.zero;
        foreach (Vector3 v in verts) s += v;
        return s / verts.Length;
    }

    private static float[,] ComputeCovariance(Vector3[] verts, Vector3 centroid)
    {
        double c00=0,c01=0,c02=0,c11=0,c12=0,c22=0;
        double invN = 1.0 / verts.Length;
        foreach (Vector3 v in verts)
        {
            double dx = v.x-centroid.x, dy = v.y-centroid.y, dz = v.z-centroid.z;
            c00+=dx*dx; c01+=dx*dy; c02+=dx*dz;
            c11+=dy*dy; c12+=dy*dz; c22+=dz*dz;
        }
        return new float[3,3]
        {
            {(float)(c00*invN),(float)(c01*invN),(float)(c02*invN)},
            {(float)(c01*invN),(float)(c11*invN),(float)(c12*invN)},
            {(float)(c02*invN),(float)(c12*invN),(float)(c22*invN)},
        };
    }

    private static void JacobiEigen3x3(float[,] A, out Vector3[] vecs, out float[] vals)
    {
        float[,] a  = (float[,])A.Clone();
        float[,] vv = { {1,0,0},{0,1,0},{0,0,1} };
        const int   MaxIter = 60;
        const float Eps     = 1e-7f;
        for (int iter = 0; iter < MaxIter; iter++)
        {
            int p=0,q=1; float maxOff = Mathf.Abs(a[0,1]);
            if (Mathf.Abs(a[0,2]) > maxOff) { p=0; q=2; maxOff = Mathf.Abs(a[0,2]); }
            if (Mathf.Abs(a[1,2]) > maxOff) { p=1; q=2; }
            if (Mathf.Abs(a[p,q]) < Eps) break;
            float tau = (a[q,q]-a[p,p]) / (2f*a[p,q]);
            float t   = (tau>=0f?1f:-1f) / (Mathf.Abs(tau)+Mathf.Sqrt(tau*tau+1f));
            float c   = 1f/Mathf.Sqrt(t*t+1f), s = t*c;
            a[p,p]-=t*a[p,q]; a[q,q]+=t*a[p,q]; a[p,q]=a[q,p]=0f;
            for (int r=0; r<3; r++)
            {
                if (r==p||r==q) continue;
                float arp=a[r,p],arq=a[r,q];
                a[r,p]=a[p,r]=c*arp-s*arq; a[r,q]=a[q,r]=s*arp+c*arq;
            }
            for (int r=0; r<3; r++)
            {
                float vrp=vv[r,p],vrq=vv[r,q];
                vv[r,p]=c*vrp-s*vrq; vv[r,q]=s*vrp+c*vrq;
            }
        }
        vals = new float[]{a[0,0],a[1,1],a[2,2]};
        vecs = new Vector3[3];
        for (int i=0;i<3;i++) vecs[i] = new Vector3(vv[0,i],vv[1,i],vv[2,i]).normalized;
    }

    private static void SortAxesByEigenvalue(ref Vector3[] axes, ref float[] vals)
    {
        for (int i=0;i<2;i++) for (int j=0;j<2-i;j++)
            if (vals[j] < vals[j+1])
            {
                (vals[j],vals[j+1])=(vals[j+1],vals[j]);
                (axes[j],axes[j+1])=(axes[j+1],axes[j]);
            }
    }

    private static void EnsureRightHanded(ref Vector3[] axes)
    {
        if (Vector3.Dot(Vector3.Cross(axes[0],axes[1]),axes[2]) < 0f) axes[2]=-axes[2];
    }

    private static float ComputeCircularity(
        Vector3[] verts, Vector3 centroid, Vector3 ax1, Vector3 ax2)
    {
        float sumR=0,sumR2=0;
        foreach (Vector3 v in verts)
        {
            Vector3 d=v-centroid;
            float r1=Vector3.Dot(d,ax1), r2=Vector3.Dot(d,ax2);
            float r=Mathf.Sqrt(r1*r1+r2*r2);
            sumR+=r; sumR2+=r*r;
        }
        float mean=sumR/verts.Length;
        if (mean<1e-6f) return 0f;
        return Mathf.Clamp01(1f-Mathf.Sqrt(Mathf.Max(0f,sumR2/verts.Length-mean*mean))/mean);
    }

    private static float ComputeEndFlatness(
        Vector3[] verts, Vector3 centroid,
        Vector3 mainAxis, float minExt, float maxExt, Vector3 ax1, Vector3 ax2)
    {
        float range=maxExt-minExt, capZone=range*0.20f;
        float midLo=minExt+range*0.30f, midHi=maxExt-range*0.30f;
        float capR=0,midR=0; int capN=0,midN=0;
        foreach (Vector3 v in verts)
        {
            Vector3 d=v-centroid;
            float proj=Vector3.Dot(d,mainAxis);
            float r=Mathf.Sqrt(Mathf.Pow(Vector3.Dot(d,ax1),2)+Mathf.Pow(Vector3.Dot(d,ax2),2));
            if (proj<minExt+capZone||proj>maxExt-capZone){capR+=r;capN++;}
            if (proj>midLo&&proj<midHi){midR+=r;midN++;}
        }
        if (midN==0||capN==0) return 0.5f;
        float avgMid=midR/midN;
        return avgMid>0f ? Mathf.Clamp01(capR/capN/avgMid) : 0.5f;
    }
}
