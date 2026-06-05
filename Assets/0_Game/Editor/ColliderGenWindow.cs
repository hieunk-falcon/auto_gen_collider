using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HoleMaster.Editor
{
    /// <summary>
    /// Editor window: drag a GameObject, click Generate → auto-fits primitive colliders
    /// using a PCA-based 3D Shape Parsing algorithm.
    /// Box / Sphere / Capsule are Unity built-in primitives.
    /// Cylinder uses a custom MeshCollider with the mesh you supply.
    /// </summary>
    public class ColliderGenWindow : EditorWindow
    {
        // ── Settings ────────────────────────────────────────────────────────
        private GameObject      _target;
        private Mesh            _cylinderMesh;
        private PhysicsMaterial _physicsMaterial;

        private bool _allowBox        = true;
        private bool _allowSphere     = true;
        private bool _allowCapsule    = true;
        private bool _allowCylinder   = true;
        private bool _removeExisting  = true;
        private bool _processChildren = true;

        // Decomposition settings
        private float _normalAngleDeg   = 35f;
        private float _minAreaFraction  = 0.02f;

        private Vector2 _scroll;
        private string  _status = string.Empty;

        // ── GUIStyles (lazy) ────────────────────────────────────────────────
        private GUIStyle _headerStyle;
        private GUIStyle _sectionStyle;

        // ── Entry ────────────────────────────────────────────────────────────

        [MenuItem("Window/Game Tools/Collider Shape Generator")]
        private static void Open()
        {
            var w = GetWindow<ColliderGenWindow>("Collider Generator");
            w.minSize = new Vector2(330f, 420f);
            w.Show();
        }

        // ── GUI ──────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // ── Header ──────────────────────────────────────────────────────
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Collider Shape Generator", _headerStyle);
            EditorGUILayout.Space(4f);
            DrawDivider();

            // ── Target & meshes ─────────────────────────────────────────────
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Target", _sectionStyle);
            EditorGUI.BeginChangeCheck();
            _target = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Object", "Drag a prefab or scene object here"),
                _target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) _status = string.Empty;

            EditorGUILayout.Space(4f);
            _cylinderMesh = (Mesh)EditorGUILayout.ObjectField(
                new GUIContent("Cylinder Mesh",
                    "Custom mesh for cylinder colliders (assumed unit height=1, radius=0.5 along Y).\n" +
                    "Leave empty to fall back to BoxCollider for cylinder shapes."),
                _cylinderMesh, typeof(Mesh), false);

            _physicsMaterial = (PhysicsMaterial)EditorGUILayout.ObjectField(
                new GUIContent("Physics Material", "Assigned to every generated collider"),
                _physicsMaterial, typeof(PhysicsMaterial), false);

            // ── Allowed shapes ───────────────────────────────────────────────
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Allowed Shapes", _sectionStyle);

            EditorGUILayout.BeginHorizontal();
            _allowBox      = EditorGUILayout.ToggleLeft("Box",      _allowBox,      GUILayout.Width(70f));
            _allowSphere   = EditorGUILayout.ToggleLeft("Sphere",   _allowSphere,   GUILayout.Width(70f));
            _allowCapsule  = EditorGUILayout.ToggleLeft("Capsule",  _allowCapsule,  GUILayout.Width(70f));
            _allowCylinder = EditorGUILayout.ToggleLeft("Cylinder", _allowCylinder, GUILayout.Width(80f));
            EditorGUILayout.EndHorizontal();

            if (!_allowBox && !_allowSphere && !_allowCapsule && !_allowCylinder)
            {
                EditorGUILayout.HelpBox("At least one shape must be allowed.", MessageType.Error);
                _allowBox = true;
            }

            // ── Decomposition settings ───────────────────────────────────────
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Decomposition", _sectionStyle);
            _normalAngleDeg = EditorGUILayout.Slider(
                new GUIContent("Normal Angle Threshold",
                    "Max angle (°) between adjacent face normals for region growing.\n" +
                    "35° = smooth cylinders (16+ sides). 50° = low-poly (8 sides).\n" +
                    "Too high may merge unrelated faces."),
                _normalAngleDeg, 10f, 60f);
            _minAreaFraction = EditorGUILayout.Slider(
                new GUIContent("Min Area Fraction",
                    "Regions smaller than this fraction of total mesh area are ignored."),
                _minAreaFraction, 0.005f, 0.10f);

            // ── Options ──────────────────────────────────────────────────────
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Options", _sectionStyle);
            _removeExisting   = EditorGUILayout.ToggleLeft(
                new GUIContent("Remove existing colliders",
                    "Deletes all Collider components and previously generated _Col_ children before generating"),
                _removeExisting);
            _processChildren = EditorGUILayout.ToggleLeft(
                new GUIContent("Process children recursively",
                    "Analyze every MeshFilter in the hierarchy, not only the root object"),
                _processChildren);

            // ── Generate button ──────────────────────────────────────────────
            EditorGUILayout.Space(10f);
            DrawDivider();
            EditorGUILayout.Space(4f);

            GUI.enabled = _target != null;
            if (GUILayout.Button("Generate Colliders", GUILayout.Height(34f)))
                Generate();
            GUI.enabled = true;

            // ── Status / hints ───────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);

            if (_target == null)
                EditorGUILayout.HelpBox(
                    "Drag a GameObject into \"Target Object\" then press Generate.",
                    MessageType.Warning);
            else if (_allowCylinder && _cylinderMesh == null)
                EditorGUILayout.HelpBox(
                    "Cylinder Mesh is not set — cylinder shapes will fall back to BoxCollider.",
                    MessageType.Warning);

            EditorGUILayout.EndScrollView();
        }

        // ── Generation logic ─────────────────────────────────────────────────

        private void Generate()
        {
            if (_target == null) return;

            Undo.SetCurrentGroupName("Generate Primitive Colliders");
            int undoGroup = Undo.GetCurrentGroup();

            MeshFilter[] meshFilters = _processChildren
                ? _target.GetComponentsInChildren<MeshFilter>(true)
                : _target.GetComponents<MeshFilter>();

            if (meshFilters.Length == 0)
            {
                _status = "No MeshFilter found on the target (or its children).";
                return;
            }

            int processed = 0;
            foreach (MeshFilter mf in meshFilters)
            {
                if (mf.sharedMesh == null) continue;
                ProcessMeshFilter(mf);
                processed++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            _status = $"Done — processed {processed} mesh(es). Each mesh split into surface regions.";
        }

        private void ProcessMeshFilter(MeshFilter mf)
        {
            GameObject go = mf.gameObject;

            if (_removeExisting)
            {
                // Remove existing Collider components
                foreach (Collider col in go.GetComponents<Collider>())
                    Undo.DestroyObjectImmediate(col);

                // Remove previously generated collider children
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = go.transform.GetChild(i);
                    if (child.name.StartsWith("_Col_"))
                        Undo.DestroyObjectImmediate(child.gameObject);
                }
            }

            var results = MeshShapeParser.AnalyzeDecomposed(
                mf.sharedMesh, _normalAngleDeg, _minAreaFraction);

            foreach (var result in results)
            {
                MeshShapeParser.PrimitiveShape resolved = ResolveAllowed(result.Shape);
                ApplyCollider(go, result, resolved);
            }

            EditorUtility.SetDirty(go);
        }

        /// <summary>If the detected shape is disabled, fall back to Box.</summary>
        private MeshShapeParser.PrimitiveShape ResolveAllowed(MeshShapeParser.PrimitiveShape shape)
        {
            bool allowed = shape switch
            {
                MeshShapeParser.PrimitiveShape.Box      => _allowBox,
                MeshShapeParser.PrimitiveShape.Sphere   => _allowSphere,
                MeshShapeParser.PrimitiveShape.Capsule  => _allowCapsule,
                MeshShapeParser.PrimitiveShape.Cylinder => _allowCylinder,
                _                                       => true,
            };
            return allowed ? shape : MeshShapeParser.PrimitiveShape.Box;
        }

        /// <summary>
        /// Creates a child "_Col_XXX" GameObject with the correct rotation so that
        /// Unity's axis-aligned colliders match the OBB orientation.
        /// </summary>
        private void ApplyCollider(GameObject parent,
            MeshShapeParser.ShapeResult r,
            MeshShapeParser.PrimitiveShape shape)
        {
            // ── Create oriented child ────────────────────────────────────────
            var child = new GameObject($"_Col_{shape}");
            Undo.RegisterCreatedObjectUndo(child, "Create Collider Child");

            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = r.LocalCenter;
            child.transform.localRotation = r.LocalRotation;
            child.transform.localScale    = Vector3.one;

            // ── Add the appropriate collider ─────────────────────────────────
            switch (shape)
            {
                case MeshShapeParser.PrimitiveShape.Box:
                {
                    var col    = child.AddComponent<BoxCollider>();
                    col.center = Vector3.zero;
                    col.size   = r.BoxSize;
                    if (_physicsMaterial) col.material = _physicsMaterial;
                    break;
                }

                case MeshShapeParser.PrimitiveShape.Sphere:
                {
                    var col    = child.AddComponent<SphereCollider>();
                    col.center = Vector3.zero;
                    col.radius = r.AvgRadius;
                    if (_physicsMaterial) col.material = _physicsMaterial;
                    break;
                }

                case MeshShapeParser.PrimitiveShape.Capsule:
                {
                    // direction = 1 (Y-axis) matches our rotation where child Y = major axis
                    var col       = child.AddComponent<CapsuleCollider>();
                    col.center    = Vector3.zero;
                    col.direction = 1;
                    col.height    = r.FullHeight;
                    col.radius    = r.AvgRadius;
                    if (_physicsMaterial) col.material = _physicsMaterial;
                    break;
                }

                case MeshShapeParser.PrimitiveShape.Cylinder:
                {
                    if (_cylinderMesh != null)
                    {
                        // Scale child to stretch unit cylinder (height=1, radius=0.5) to fit OBB.
                        // Child Y = major axis (height direction).
                        float scaleR = r.AvgRadius * 2f;   // 0.5 * scale = AvgRadius → scale = AvgRadius*2
                        child.transform.localScale = new Vector3(scaleR, r.FullHeight, scaleR);

                        var col        = child.AddComponent<MeshCollider>();
                        col.sharedMesh = _cylinderMesh;
                        col.convex     = true;
                        if (_physicsMaterial) col.material = _physicsMaterial;
                    }
                    else
                    {
                        // No cylinder mesh supplied → fall back to BoxCollider
                        var col    = child.AddComponent<BoxCollider>();
                        col.center = Vector3.zero;
                        col.size   = r.BoxSize;
                        if (_physicsMaterial) col.material = _physicsMaterial;
                    }
                    break;
                }
            }
        }

        // ── Style helpers ────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_headerStyle != null) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 13,
                alignment = TextAnchor.MiddleLeft,
            };

            _sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
            };
        }

        private static void DrawDivider()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, new Color(0.3f, 0.3f, 0.3f, 1f));
        }
    }
}
