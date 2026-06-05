using UnityEditor;
using UnityEngine;

namespace MantisLODEditor
{
    [CustomEditor(typeof(ProgressiveMeshRuntime))]
    public class ProgressiveMeshRuntimeEditor : Editor
    {
        private ProgressiveMesh save_progressiveMesh = null;
        private bool show_advanced_options = true;
        private bool[] mesh_lod_foldouts = null;

        private static string FormatLodTrianglesVsLod0(int trisAtLod, int lod0Tris)
        {
            if (lod0Tris <= 0)
                return $"{trisAtLod} tris (—)";
            float pct = 100f * trisAtLod / lod0Tris;
            return $"{trisAtLod} tris ({pct:F1}%)";
        }

        static void PersistInspectorChanges(Object obj)
        {
            if (obj == null)
                return;
            EditorUtility.SetDirty(obj);
            if (PrefabUtility.IsPartOfPrefabInstance(obj))
                PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
        }

        override public void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            var runtime = target as ProgressiveMeshRuntime;
            if (runtime)
            {
                // when the property changed
                if (runtime.progressiveMesh != save_progressiveMesh)
                {
                    // clear the property
                    if (runtime.progressiveMesh == null)
                    {
                        Undo.RecordObject(runtime, "Progressive Mesh Reference Cleared");
                        runtime.reset_all_parameters();
                        PersistInspectorChanges(runtime);
                    }
                    else
                    {
                        // diffent property or mesh lod range not exists
                        if (save_progressiveMesh != null || runtime.mesh_lod_range == null || runtime.mesh_lod_range.Length == 0)
                        {
                            Undo.RecordObject(runtime, "Progressive Mesh Reference Changed");
                            runtime.reset_all_parameters();
                            int max_lod_count = runtime.progressiveMesh.triangles[0];
                            int mesh_count = runtime.progressiveMesh.triangles[1];
                            runtime.mesh_lod_range = new int[mesh_count * 2];
                            for (int i = 0; i < mesh_count; i++)
                            {
                                runtime.mesh_lod_range[i * 2] = 0;
                                runtime.mesh_lod_range[i * 2 + 1] = max_lod_count - 1;
                            }
                            PersistInspectorChanges(runtime);
                        }
                    }
                    save_progressiveMesh = runtime.progressiveMesh;
                }
                // show advanced options
                show_advanced_options = EditorGUILayout.Foldout(show_advanced_options, show_advanced_options ? "Hide Advanced Options" : "Show Advanced Options");
                if (show_advanced_options)
                {
                    GUIStyle helpStyle = new GUIStyle(GUI.skin.box);
                    helpStyle.wordWrap = true;
                    helpStyle.alignment = TextAnchor.UpperLeft;
                    helpStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                    GUILayout.Label(
                        "If the gameObject is an instance of a prefab, you must drag the gameObject from the hierarchy window onto the source prefab to save the changes when finished editing. Otherwise, all the changes will lose after reloading the scene!"
                        , helpStyle
                        , GUILayout.ExpandWidth(true));
                    runtime.optimize_on_the_fly = EditorGUILayout.Toggle("Optimize On The Fly", runtime.optimize_on_the_fly);
                    GUILayout.Label(
                        "You should always turn on this option in most cases, but when running in editor XR (Mock HMD Loader), the character may blink when switching LODs when this option is enabled. I don't know if it is a Unity bug or it also happens on real XR devices. If this happens, please turn off this option."
                        , helpStyle
                        , GUILayout.ExpandWidth(true));
                    runtime.updateInterval = EditorGUILayout.FloatField("Update Interval", runtime.updateInterval);
                    GUILayout.Label(
                        "How often to check LOD changes."
                        , helpStyle
                        , GUILayout.ExpandWidth(true));
                    // clamp to valid range
                    if (runtime.updateInterval < 0.1f) runtime.updateInterval = 0.1f;
                    if (runtime.updateInterval > 5.0f) runtime.updateInterval = 5.0f;
                    EditorGUILayout.Space();
                    runtime.never_cull = EditorGUILayout.Toggle("Never Cull", runtime.never_cull);
                    GUILayout.Label(
                        "The gameObject is alway visible or be culled when far away."
                        , helpStyle
                        , GUILayout.ExpandWidth(true));
                    runtime.cull_ratio = EditorGUILayout.FloatField("Cull Ratio", runtime.cull_ratio);
                    if (!runtime.never_cull)
                    {
                        GUILayout.Label(
                            "How far away will the gameObject be culled."
                            , helpStyle
                            , GUILayout.ExpandWidth(true));
                    }
                    // clamp to valid range
                    if (runtime.cull_ratio < 0.0f) runtime.cull_ratio = 0.0f;
                    if (runtime.cull_ratio > 1.0f) runtime.cull_ratio = 1.0f;
                    string[] options = new string[] { "Cull By Size", "Cull By Distance" };
                    runtime.lod_strategy = GUILayout.SelectionGrid(runtime.lod_strategy, options, 1, EditorStyles.radioButton);
                    if (runtime.lod_strategy == 1)
                    {
                        runtime.disappear_distance = EditorGUILayout.FloatField("Disappear Distance", runtime.disappear_distance);
                        GUILayout.Label(
                            "How far away will the gameObject look like a tiny point."
                            , helpStyle
                            , GUILayout.ExpandWidth(true));
                        // clamp to valid range
                        if (runtime.disappear_distance < 0.0f) runtime.disappear_distance = 0.0f;
                    }
                    // mesh lod range exists
                    if (runtime.mesh_lod_range != null && runtime.mesh_lod_range.Length != 0)
                    {
                        int mesh_count = runtime.mesh_lod_range.Length / 2;
                        if (mesh_lod_foldouts == null || mesh_lod_foldouts.Length != mesh_count)
                        {
                            var next = new bool[mesh_count];
                            for (int m = 0; m < mesh_count; m++)
                                next[m] = mesh_lod_foldouts != null && m < mesh_lod_foldouts.Length ? mesh_lod_foldouts[m] : false;
                            mesh_lod_foldouts = next;
                        }
                        int max_lod_count = runtime.progressiveMesh.triangles[0];
                        for (int m = 0; m < mesh_count; m++)
                        {
                            EditorGUILayout.Space();
                            int lod0_tris = MantisLODEditorUtility.get_triangles_count_from_progressive_mesh(runtime.progressiveMesh, 0, m);
                            string foldout_label = runtime.progressiveMesh.uuids[m] + "  (" + lod0_tris + " tris)";
                            mesh_lod_foldouts[m] = EditorGUILayout.Foldout(mesh_lod_foldouts[m], foldout_label, true);
                            if (!mesh_lod_foldouts[m])
                                continue;

                            EditorGUI.indentLevel++;
                            int i_min = m * 2;
                            int i_max = m * 2 + 1;

                            EditorGUILayout.BeginHorizontal();
                            runtime.mesh_lod_range[i_min] = EditorGUILayout.IntField("Min LOD", runtime.mesh_lod_range[i_min]);
                            if (runtime.mesh_lod_range[i_min] < 0) runtime.mesh_lod_range[i_min] = 0;
                            if (runtime.mesh_lod_range[i_min] > max_lod_count - 1) runtime.mesh_lod_range[i_min] = max_lod_count - 1;
                            int min_tris = MantisLODEditorUtility.get_triangles_count_from_progressive_mesh(runtime.progressiveMesh, runtime.mesh_lod_range[i_min], m);
                            EditorGUILayout.LabelField(FormatLodTrianglesVsLod0(min_tris, lod0_tris), GUILayout.MinWidth(100f));
                            EditorGUILayout.EndHorizontal();

                            EditorGUILayout.BeginHorizontal();
                            runtime.mesh_lod_range[i_max] = EditorGUILayout.IntField("Max LOD", runtime.mesh_lod_range[i_max]);
                            if (runtime.mesh_lod_range[i_max] < 0) runtime.mesh_lod_range[i_max] = 0;
                            if (runtime.mesh_lod_range[i_max] > max_lod_count - 1) runtime.mesh_lod_range[i_max] = max_lod_count - 1;
                            int tris_at_max_lod = MantisLODEditorUtility.get_triangles_count_from_progressive_mesh(runtime.progressiveMesh, runtime.mesh_lod_range[i_max], m);
                            EditorGUILayout.LabelField(FormatLodTrianglesVsLod0(tris_at_max_lod, lod0_tris), GUILayout.MinWidth(100f));
                            EditorGUILayout.EndHorizontal();
                            EditorGUI.indentLevel--;
                        }
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                if (runtime)
                    PersistInspectorChanges(runtime);
            }
        }
    }
}
