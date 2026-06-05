using UnityEditor;
using UnityEngine;

public class ColliderAutoFitterWindow : EditorWindow
{
    [MenuItem("Tools/Collider Auto Fitter")]
    static void Open() => GetWindow<ColliderAutoFitterWindow>("Collider Auto Fitter");

    ColliderAutoFitter.ShapeOverride _shapeOverride = ColliderAutoFitter.ShapeOverride.Auto;
    bool _showHelp;

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

        // ── Fit button ────────────────────────────────────────────────────
        GUI.enabled = Selection.gameObjects.Length > 0;
        if (GUILayout.Button("Fit Collider to Selected Object(s)", GUILayout.Height(36)))
        {
            Undo.SetCurrentGroupName("Auto Fit Colliders");
            int group = Undo.GetCurrentGroup();
            int count = 0;
            foreach (var go in Selection.gameObjects)
            {
                ColliderAutoFitter.FitCollider(go, _shapeOverride);
                count++;
            }
            Undo.CollapseUndoOperations(group);
            Debug.Log($"[ColliderAutoFitter] Processed {count} object(s).");
        }
        GUI.enabled = true;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Selected:", Selection.gameObjects.Length > 0
            ? string.Join(", ", System.Array.ConvertAll(Selection.gameObjects, g => g.name))
            : "(none)", EditorStyles.miniLabel);

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
}
