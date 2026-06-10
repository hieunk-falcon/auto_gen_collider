using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

public class ColliderAutoFitterWindow : EditorWindow
{
    [MenuItem("Tools/Collider Auto Fitter")]
    static void Open() => GetWindow<ColliderAutoFitterWindow>("Collider Auto Fitter");

    const string PREF_FOLDER = "CAF_ModuleFolderGUID";
    const string PREF_SKIP   = "CAF_SkipLowConfidence";
    const string PREF_FILL   = "CAF_MinFill";
    const string PREF_SYM    = "CAF_ShowSymmetryPlane";
    const string PREF_WALLS  = "CAF_ContainerWalls";

    ColliderAutoFitter.FitMode _mode = ColliderAutoFitter.FitMode.Auto;
    DefaultAsset _moduleFolder;
    bool  _skipLowConfidence = true;
    float _minFill = ColliderAutoFitter.DEFAULT_MIN_FILL;
    bool  _showSymmetryPlane = false;
    int   _containerWalls = ColliderAutoFitter.DEFAULT_CONTAINER_WALLS;

    readonly List<GameObject> _dropTargets = new List<GameObject>();
    Vector2 _scroll;
    bool _showHelp;

    void OnEnable()
    {
        string guid = EditorPrefs.GetString(PREF_FOLDER, "");
        if (!string.IsNullOrEmpty(guid))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
                _moduleFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
        }
        _skipLowConfidence = EditorPrefs.GetBool(PREF_SKIP, true);
        _minFill           = EditorPrefs.GetFloat(PREF_FILL, ColliderAutoFitter.DEFAULT_MIN_FILL);
        _showSymmetryPlane = EditorPrefs.GetBool(PREF_SYM, false);
        _containerWalls    = EditorPrefs.GetInt(PREF_WALLS, ColliderAutoFitter.DEFAULT_CONTAINER_WALLS);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Auto-Fit Collider to Selection", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // ── Mode ──────────────────────────────────────────────────────────────
        _mode = (ColliderAutoFitter.FitMode)EditorGUILayout.EnumPopup(
            new GUIContent("Mode",
                "Auto      — fit Solid; fall back to Container only if the fit is poor & the shape is round + open\n" +
                "Container — hollow: N wall boxes + 1 bottom box\n" +
                "Solid     — decompose into primitives (Sphere/Capsule/Cylinder/Box), never walls"),
            _mode);

        switch (_mode)
        {
            case ColliderAutoFitter.FitMode.Container:
                EditorGUILayout.HelpBox($"Generates {_containerWalls} wall BoxColliders + 1 bottom BoxCollider, "
                    + "sized from the mesh bounds. Use for buckets, baskets, bowls, crates.", MessageType.Info);
                break;
            case ColliderAutoFitter.FitMode.Solid:
                EditorGUILayout.HelpBox("Splits the mesh into parts (connected components + valley-cut) "
                    + "and fits one primitive per part. Never produces hollow walls.", MessageType.Info);
                break;
        }

        if (_mode == ColliderAutoFitter.FitMode.Container || _mode == ColliderAutoFitter.FitMode.Auto)
        {
            EditorGUI.BeginChangeCheck();
            _containerWalls = EditorGUILayout.IntSlider(
                new GUIContent("Container Walls", "Number of wall colliders to generate in Container mode."),
                _containerWalls, 3, 32);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PREF_WALLS, _containerWalls);
            }
        }

        EditorGUILayout.Space();

        // ── Custom collider modules ──────────────────────────────────────────
        EditorGUILayout.LabelField("Custom Collider Modules", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _moduleFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Module Folder",
                "Folder of prefabs used as custom collider shapes.\n" +
                "A prefab named 'Cylinder' (unit: diameter 1, height 1 along local Y, " +
                "collider at origin) is used for cylinder-shaped parts.\n" +
                "Empty → cylinder parts fall back to a tight Box."),
            _moduleFolder, typeof(DefaultAsset), false);
        if (EditorGUI.EndChangeCheck())
        {
            string guid = _moduleFolder != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_moduleFolder)) : "";
            EditorPrefs.SetString(PREF_FOLDER, guid);
        }

        DrawModuleStatus();

        EditorGUILayout.Space();

        // ── Confidence gate ──────────────────────────────────────────────────
        EditorGUILayout.LabelField("Confidence Gate", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _skipLowConfidence = EditorGUILayout.ToggleLeft(
            new GUIContent("Skip & warn on low confidence",
                "If the collider fill ratio is below the threshold, remove the colliders and "
                + "list the object for manual fitting. Off → keep them but still warn."),
            _skipLowConfidence);
        _minFill = EditorGUILayout.Slider(
            new GUIContent("Min fill", "meshVolume / colliderVolume below which a fit is flagged."),
            _minFill, 0.05f, 0.95f);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(PREF_SKIP, _skipLowConfidence);
            EditorPrefs.SetFloat(PREF_FILL, _minFill);
        }

        EditorGUILayout.Space();

        // ── Debug options ─────────────────────────────────────────────────────
        EditorGUILayout.LabelField("Debug Visualization", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _showSymmetryPlane = EditorGUILayout.ToggleLeft(
            new GUIContent("Show symmetry plane", "Visualize the detected symmetry plane in the editor."),
            _showSymmetryPlane);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(PREF_SYM, _showSymmetryPlane);
        }

        EditorGUILayout.Space();

        // ── Drop zone ─────────────────────────────────────────────────────────
        DrawDropZone();

        EditorGUILayout.Space();

        // ── Target list ───────────────────────────────────────────────────────
        var allTargets = BuildTargetList();
        if (allTargets.Count > 0)
        {
            EditorGUILayout.LabelField($"Will process {allTargets.Count} object(s):", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(80));
            foreach (var go in allTargets)
                EditorGUILayout.ObjectField(go, typeof(GameObject), true);
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.LabelField("No objects selected or dropped.", EditorStyles.centeredGreyMiniLabel);
        }

        EditorGUILayout.Space();

        // ── Fit button ────────────────────────────────────────────────────────
        GUI.enabled = allTargets.Count > 0;
        if (GUILayout.Button("Fit Collider(s)", GUILayout.Height(36)))
            Process(allTargets);
        GUI.enabled = true;

        EditorGUILayout.Space();
        DrawHelp();
    }

    void Process(List<GameObject> targets)
    {
        string folderPath = _moduleFolder != null ? AssetDatabase.GetAssetPath(_moduleFolder) : null;
        var modules = ColliderAutoFitter.ModuleLibrary.FromFolder(folderPath);

        var opt = ColliderAutoFitter.FitOptions.Default;
        opt.mode              = _mode;
        opt.modules           = modules;
        opt.skipLowConfidence = _skipLowConfidence;
        opt.minFill           = _minFill;
        opt.showSymmetryPlane = _showSymmetryPlane;
        opt.containerWalls    = _containerWalls;

        Undo.SetCurrentGroupName("Auto Fit Colliders");
        int group = Undo.GetCurrentGroup();

        int ok = 0;
        var flagged = new List<Object>();
        var sb = new StringBuilder();

        foreach (var go in targets)
        {
            var r = ColliderAutoFitter.FitCollider(go, opt);
            if (r.created && !r.flagged) ok++;
            if (r.flagged || !r.created)
            {
                flagged.Add(go);
                sb.AppendLine($"• {go.name}   (fill {r.fill:P0}, {r.parts} part(s))");
            }
        }

        Undo.CollapseUndoOperations(group);
        _dropTargets.Clear();

        Debug.Log($"[ColliderAutoFitter] {ok} OK, {flagged.Count} flagged of {targets.Count} object(s).");

        if (flagged.Count > 0)
        {
            Selection.objects = flagged.ToArray();
            EditorUtility.DisplayDialog("Collider Auto Fitter",
                $"{ok} object(s) fitted.\n\n{flagged.Count} object(s) need a manual collider "
                + (_skipLowConfidence ? "(low-confidence fits were removed):\n\n" : "(low-confidence, kept):\n\n")
                + sb,
                "OK");
        }
    }

    void DrawModuleStatus()
    {
        string path = _moduleFolder != null ? AssetDatabase.GetAssetPath(_moduleFolder) : null;
        if (string.IsNullOrEmpty(path))
        {
            EditorGUILayout.HelpBox("No module folder. Cylinder parts will fall back to Box.", MessageType.None);
            return;
        }
        var lib = ColliderAutoFitter.ModuleLibrary.FromFolder(path);
        string cyl = lib.Has(ColliderAutoFitter.CYLINDER_MODULE) ? "✓ Cylinder" : "✗ no 'Cylinder'";
        EditorGUILayout.HelpBox($"{lib.Count} module prefab(s) found.   {cyl}", MessageType.None);
    }

    List<GameObject> BuildTargetList()
    {
        var result = new List<GameObject>(Selection.gameObjects);
        foreach (var go in _dropTargets)
            if (go != null && !result.Contains(go))
                result.Add(go);
        return result;
    }

    void DrawDropZone()
    {
        Rect dropRect = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));

        bool isHover = dropRect.Contains(Event.current.mousePosition);
        bool isDragging = DragAndDrop.objectReferences != null &&
                          DragAndDrop.objectReferences.Length > 0;

        Color boxColor = (isHover && isDragging)
            ? new Color(0.3f, 0.7f, 1f, 0.35f)
            : new Color(0.5f, 0.5f, 0.5f, 0.15f);
        EditorGUI.DrawRect(dropRect, boxColor);

        Color borderColor = (isHover && isDragging)
            ? new Color(0.3f, 0.7f, 1f, 0.9f)
            : new Color(0.6f, 0.6f, 0.6f, 0.6f);
        DrawBorder(dropRect, borderColor);

        string label = _dropTargets.Count > 0
            ? $"{_dropTargets.Count} object(s) dropped  (drag more or clear below)"
            : "Drag GameObjects here";
        GUIStyle labelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };
        GUI.Label(dropRect, label, labelStyle);

        if (_dropTargets.Count > 0)
        {
            Rect clearBtn = new Rect(dropRect.xMax - 52, dropRect.y + 2, 50, 18);
            if (GUI.Button(clearBtn, "Clear", EditorStyles.miniButton))
                _dropTargets.Clear();
        }

        HandleDropEvents(dropRect);
    }

    void HandleDropEvents(Rect dropRect)
    {
        Event evt = Event.current;
        if (!dropRect.Contains(evt.mousePosition)) return;

        switch (evt.type)
        {
            case EventType.DragUpdated:
                DragAndDrop.visualMode = HasGameObjects(DragAndDrop.objectReferences)
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                evt.Use();
                Repaint();
                break;

            case EventType.DragPerform:
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                    if (obj is GameObject go && !_dropTargets.Contains(go))
                        _dropTargets.Add(go);
                evt.Use();
                Repaint();
                break;

            case EventType.DragExited:
                Repaint();
                break;
        }
    }

    void DrawHelp()
    {
        _showHelp = EditorGUILayout.Foldout(_showHelp, "How it works", true);
        if (!_showHelp) return;

        EditorGUILayout.HelpBox(
            "MODES\n" +
            "  Solid     → connected components + valley-cut decomposition;\n" +
            "              each part → Sphere / Capsule / Cylinder / Box.\n" +
            "  Container → 8 wall boxes + 1 bottom box (hollow).\n" +
            "  Auto      → Solid first; only switches to Container if the Solid\n" +
            "              fit is poor AND the shape is round + open.\n\n" +
            "VALLEY-CUT (Solid)\n" +
            "  Cuts at density gaps (separate branches), radius valleys (necks)\n" +
            "  and radius shoulders (steps) along the PCA axes.\n\n" +
            "PART SHAPE (extents e0 ≥ e1 ≥ e2)\n" +
            "  e0/e2 < 1.35              → Sphere\n" +
            "  round & length/r ≥ 2.5    → Capsule\n" +
            "  round & length/r < 2.5    → Cylinder (module) / Box fallback\n" +
            "  otherwise                 → Box (OBB)\n\n" +
            "CONFIDENCE\n" +
            "  fill = meshVolume / colliderVolume.\n" +
            "  Below 'Min fill' → flagged (and removed if 'Skip' is on); a dialog\n" +
            "  lists the objects and selects them for manual fitting.",
            MessageType.None);
    }

    static bool HasGameObjects(Object[] refs)
    {
        foreach (var o in refs)
            if (o is GameObject) return true;
        return false;
    }

    static void DrawBorder(Rect r, Color c)
    {
        EditorGUI.DrawRect(new Rect(r.x,        r.y,        r.width, 1),       c);
        EditorGUI.DrawRect(new Rect(r.x,        r.yMax - 1, r.width, 1),       c);
        EditorGUI.DrawRect(new Rect(r.x,        r.y,        1,       r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y,        1,       r.height), c);
    }
}