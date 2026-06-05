using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ColliderAutoFitterWindow : EditorWindow
{
    [MenuItem("Tools/Collider Auto Fitter")]
    static void Open() => GetWindow<ColliderAutoFitterWindow>("Collider Auto Fitter");

    ColliderAutoFitter.ShapeOverride _shapeOverride = ColliderAutoFitter.ShapeOverride.Auto;
    readonly List<GameObject> _dropTargets = new List<GameObject>();
    Vector2 _scroll;
    bool _showHelp;

    // Repaint while the mouse is inside the window so drop highlight updates.
    void OnGUI()
    {
        EditorGUILayout.LabelField("Auto-Fit Collider to Selection", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // ── Shape override ────────────────────────────────────────────────
        EditorGUILayout.LabelField("Shape Override", EditorStyles.boldLabel);
        _shapeOverride = (ColliderAutoFitter.ShapeOverride)EditorGUILayout.EnumPopup(
            new GUIContent("Shape",
                "Auto       — detect from mesh (convexity + PCA)\n" +
                "Container  — hollow open-top object: N wall boxes + bottom box\n" +
                "Sphere / Capsule / Box — force that primitive, size fit to mesh bounds"),
            _shapeOverride);

        if (_shapeOverride == ColliderAutoFitter.ShapeOverride.Container)
        {
            EditorGUILayout.HelpBox(
                "Container mode: generates " +
                "8 wall BoxColliders arranged radially\n" +
                "+ 1 flat BoxCollider for the bottom.\n" +
                "Sized automatically from the mesh bounds.",
                MessageType.Info);
        }
        else if (_shapeOverride != ColliderAutoFitter.ShapeOverride.Auto)
        {
            EditorGUILayout.HelpBox(
                $"Forces {_shapeOverride} collider.\n" +
                "Size is always fit tightly to the mesh bounds.",
                MessageType.Info);
        }

        EditorGUILayout.Space();

        // ── Drop zone ─────────────────────────────────────────────────────
        DrawDropZone();

        EditorGUILayout.Space();

        // ── Target list ───────────────────────────────────────────────────
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

        // ── Fit button ────────────────────────────────────────────────────
        GUI.enabled = allTargets.Count > 0;
        if (GUILayout.Button("Fit Collider(s)", GUILayout.Height(36)))
        {
            Undo.SetCurrentGroupName("Auto Fit Colliders");
            int group = Undo.GetCurrentGroup();
            foreach (var go in allTargets)
                ColliderAutoFitter.FitCollider(go, _shapeOverride);
            Undo.CollapseUndoOperations(group);
            Debug.Log($"[ColliderAutoFitter] Processed {allTargets.Count} object(s).");
            _dropTargets.Clear();
        }
        GUI.enabled = true;

        EditorGUILayout.Space();
        _showHelp = EditorGUILayout.Foldout(_showHelp, "How it works", true);
        if (_showHelp)
        {
            EditorGUILayout.HelpBox(
                "AUTO:\n" +
                "  Convexity = |signed vol| / OBB vol\n" +
                "  \u2265 35%  \u2192  1 primitive\n" +
                "  < 35%  \u2192  recursive PCA bisection \u2192 compound\n\n" +
                "  Shape (PCA extents e0 \u2265 e1 \u2265 e2):\n" +
                "    e0/e2 < 1.35                    \u2192 Sphere\n" +
                "    e0/e2 \u2265 2.0, round cross-section \u2192 Capsule\n" +
                "    otherwise                       \u2192 Box (OBB)\n\n" +
                "CONTAINER:\n" +
                "  Skips convexity check.\n" +
                "  Generates 8 wall slabs (BoxCollider) at equal angles\n" +
                "  around the rim + 1 flat BoxCollider at the bottom.\n" +
                "  Use for: buckets, baskets, bowls, crates.",
                MessageType.None);
        }
    }

    // Returns union of scene-selection + manually dropped objects (deduped).
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
        // Reserve a fixed-height rect for the drop zone.
        Rect dropRect = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));

        bool isHover = dropRect.Contains(Event.current.mousePosition);
        bool isDragging = DragAndDrop.objectReferences != null &&
                          DragAndDrop.objectReferences.Length > 0;

        Color boxColor = (isHover && isDragging)
            ? new Color(0.3f, 0.7f, 1f, 0.35f)
            : new Color(0.5f, 0.5f, 0.5f, 0.15f);

        EditorGUI.DrawRect(dropRect, boxColor);

        // Dashed border
        Color borderColor = (isHover && isDragging)
            ? new Color(0.3f, 0.7f, 1f, 0.9f)
            : new Color(0.6f, 0.6f, 0.6f, 0.6f);
        DrawBorder(dropRect, borderColor);

        string label = _dropTargets.Count > 0
            ? $"{_dropTargets.Count} object(s) dropped  (drag more or clear below)"
            : "Drag GameObjects here";
        GUIStyle labelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };
        GUI.Label(dropRect, label, labelStyle);

        // Clear button (top-right corner)
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
                {
                    if (obj is GameObject go && !_dropTargets.Contains(go))
                        _dropTargets.Add(go);
                }
                evt.Use();
                Repaint();
                break;

            case EventType.DragExited:
                Repaint();
                break;
        }
    }

    static bool HasGameObjects(Object[] refs)
    {
        foreach (var o in refs)
            if (o is GameObject) return true;
        return false;
    }

    static void DrawBorder(Rect r, Color c)
    {
        EditorGUI.DrawRect(new Rect(r.x,            r.y,            r.width, 1),      c);
        EditorGUI.DrawRect(new Rect(r.x,            r.yMax - 1,     r.width, 1),      c);
        EditorGUI.DrawRect(new Rect(r.x,            r.y,            1,       r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - 1,     r.y,            1,       r.height), c);
    }
}
