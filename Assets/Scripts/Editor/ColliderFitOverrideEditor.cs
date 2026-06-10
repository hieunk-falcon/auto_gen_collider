using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="ColliderFitOverride"/>. Shows the forced-shape
/// dropdown, the last fill confidence, a review warning, and a one-click
/// "Re-fit now" button that re-runs the auto-fitter on this object using the
/// chosen override (or full auto when the shape is left on Auto).
///
/// The module folder is remembered in EditorPrefs so you only set it once.
/// </summary>
[CustomEditor(typeof(ColliderFitOverride))]
public class ColliderFitOverrideEditor : Editor
{
    const string PREF_MODULE_FOLDER = "ColliderTool.ModuleFolder";
    const string DEFAULT_MODULE_FOLDER = "Assets/0_Game/ColliderModule";

    SerializedProperty _forced, _lastFill, _flagged;
    string _moduleFolder;

    void OnEnable()
    {
        _forced   = serializedObject.FindProperty("forcedShape");
        _lastFill = serializedObject.FindProperty("lastFill");
        _flagged  = serializedObject.FindProperty("flaggedForReview");
        _moduleFolder = EditorPrefs.GetString(PREF_MODULE_FOLDER, DEFAULT_MODULE_FOLDER);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(_forced,
            new GUIContent("Forced shape", "Auto = let the fitter classify. Anything else forces that collider."));

        // Confidence read-out + review flag.
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.Slider("Last fill", _lastFill.floatValue, 0f, 1f);

        if (_flagged.boolValue)
            EditorGUILayout.HelpBox(
                "Low-confidence fit — the classifier wasn't sure. Pick a shape above and re-fit.",
                MessageType.Warning);

        EditorGUILayout.Space();

        // Module folder (remembered across sessions).
        EditorGUI.BeginChangeCheck();
        _moduleFolder = EditorGUILayout.TextField(
            new GUIContent("Module folder", "Folder holding Cylinder / FrustumCone / FrustumPyramid / Dome prefabs."),
            _moduleFolder);
        if (EditorGUI.EndChangeCheck())
            EditorPrefs.SetString(PREF_MODULE_FOLDER, _moduleFolder);

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Re-fit now", GUILayout.Height(28)))
                RunFit((ColliderFitOverride)target);

            if (GUILayout.Button("Reset to Auto", GUILayout.Width(110), GUILayout.Height(28)))
            {
                _forced.enumValueIndex = (int)ColliderFitOverride.Shape.Auto;
                serializedObject.ApplyModifiedProperties();
                RunFit((ColliderFitOverride)target);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    void RunFit(ColliderFitOverride ovr)
    {
        var opt = ColliderAutoFitter.FitOptions.Default;
        opt.modules = ColliderAutoFitter.ModuleLibrary.FromFolder(_moduleFolder);
        opt.containerWalls = EditorPrefs.GetInt("CAF_ContainerWalls", ColliderAutoFitter.DEFAULT_CONTAINER_WALLS);
        if (opt.modules.Count == 0)
            Debug.LogWarning($"[AutoFit] No module prefabs found in '{_moduleFolder}'. " +
                             "Mesh-module shapes will fall back to Box.");

        Undo.SetCurrentGroupName("Re-fit collider");
        ColliderAutoFitter.FitCollider(ovr.gameObject, opt);
    }
}
