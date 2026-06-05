using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace HoleMaster.Editor
{
    public class LODGroupSetupWindow : EditorWindow
    {
        private const string LODMeshRootPath = "Assets/0_Game/Prefabs/LOD";
        private const string ShadowKeyword = "shadow";
        private const int LodCount = 4;

        private readonly int[] _qualities    = { 100, 80, 50, 30 };
        private readonly float[] _transitions = { 0.40f, 0.20f, 0.10f, 0.0f };

        private List<GameObject> _prefabs = new List<GameObject>();
        private ReorderableList _reorderableList;
        private DefaultAsset _sourceFolder;
        private Vector2 _scrollPos;
        private string _statusMessage = string.Empty;

        [MenuItem("Window/Game Tools/LOD Group Setup")]
        private static void Open()
        {
            LODGroupSetupWindow window = GetWindow<LODGroupSetupWindow>("LOD Group Setup");
            window.minSize = new Vector2(420f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            _reorderableList = new ReorderableList(_prefabs, typeof(GameObject), true, true, true, true);
            _reorderableList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, $"Prefabs  (drag here)  [{_prefabs.Count}]");
            _reorderableList.drawElementCallback = DrawPrefabElement;
            _reorderableList.onAddCallback = list => list.list.Add(null);
        }

        private void DrawPrefabElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            rect.y += 2f;
            rect.height = EditorGUIUtility.singleLineHeight;
            _prefabs[index] = (GameObject)EditorGUI.ObjectField(rect, _prefabs[index], typeof(GameObject), false);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("LOD Group Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            DrawLODConfig();
            EditorGUILayout.Space(4f);
            DrawFolderSection();
            EditorGUILayout.Space(8f);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));
            _reorderableList.DoLayoutList();
            Rect listRect = GUILayoutUtility.GetLastRect();
            HandleDragAndDrop(listRect);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            DrawActionButtons();

            if (!string.IsNullOrEmpty(_statusMessage))
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);

            EditorGUILayout.Space(4f);
        }

        private void DrawLODConfig()
        {
            EditorGUILayout.LabelField("LOD Configuration", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Level", GUILayout.Width(100f));
            EditorGUILayout.LabelField("Quality", GUILayout.Width(55f));
            EditorGUILayout.LabelField("Transition %", GUILayout.Width(90f));
            EditorGUILayout.LabelField("Mesh Folder", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < LodCount; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(i == 0 ? "LOD 0 (original)" : $"LOD {i}", GUILayout.Width(100f));

                GUI.enabled = i > 0;
                _qualities[i] = Mathf.Clamp(EditorGUILayout.IntField(_qualities[i], GUILayout.Width(50f)), 1, 100);
                GUI.enabled = true;

                float pct = _transitions[i] * 100f;
                pct = EditorGUILayout.FloatField((float)Math.Round(pct, 1), GUILayout.Width(50f));
                _transitions[i] = Mathf.Clamp(pct / 100f, 0f, 1f);

                string hint = i == 0 ? "(original mesh)" : $"LOD_{_qualities[i]}/";
                EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawFolderSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Source Folder", GUILayout.Width(90f));
            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(_sourceFolder, typeof(DefaultAsset), false);
            GUI.enabled = _sourceFolder != null;
            if (GUILayout.Button("Get Prefabs", GUILayout.Width(90f)))
                GetPrefabsFromFolder();
            if (GUILayout.Button("Clear & Get", GUILayout.Width(80f)))
            {
                _prefabs.Clear();
                GetPrefabsFromFolder();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void GetPrefabsFromFolder()
        {
            if (_sourceFolder == null) return;

            string folderPath = AssetDatabase.GetAssetPath(_sourceFolder);
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });

            int added = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && !_prefabs.Contains(prefab))
                {
                    _prefabs.Add(prefab);
                    added++;
                }
            }

            _statusMessage = added > 0
                ? $"Added {added} prefab(s) from {folderPath}"
                : "No new prefabs found in selected folder.";
            Repaint();
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!dropArea.Contains(evt.mousePosition)) return;

            bool hasPrefab = DragAndDrop.objectReferences.OfType<GameObject>().Any();
            DragAndDrop.visualMode = hasPrefab ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (GameObject go in DragAndDrop.objectReferences.OfType<GameObject>())
                {
                    if (!_prefabs.Contains(go))
                        _prefabs.Add(go);
                }
                Repaint();
            }
            evt.Use();
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.BeginHorizontal();

            bool hasValidPrefabs = _prefabs.Any(p => p != null);
            GUI.enabled = hasValidPrefabs;
            if (GUILayout.Button("Setup LOD Groups", GUILayout.Height(32f)))
                SetupLODGroups();

            GUI.enabled = _prefabs.Count > 0;
            if (GUILayout.Button("Clear List", GUILayout.Height(32f), GUILayout.Width(90f)))
            {
                _prefabs.Clear();
                _statusMessage = string.Empty;
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
        }

        private void SetupLODGroups()
        {
            List<GameObject> validPrefabs = _prefabs.Where(p => p != null).ToList();
            if (validPrefabs.Count == 0) return;

            int successCount = 0;
            int skipCount = 0;

            try
            {
                for (int i = 0; i < validPrefabs.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(
                        "Setting up LOD Groups",
                        $"{validPrefabs[i].name} ({i + 1}/{validPrefabs.Count})",
                        (float)i / validPrefabs.Count);

                    bool ok = ProcessPrefab(validPrefabs[i]);
                    if (ok) successCount++;
                    else    skipCount++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            _statusMessage = $"Done. {successCount} prefab(s) set up, {skipCount} skipped.";
            Debug.Log($"[LODGroupSetup] {_statusMessage}");
        }

        private bool ProcessPrefab(GameObject prefabAsset)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                MeshFilter[] validFilters = root.GetComponentsInChildren<MeshFilter>(true)
                    .Where(mf => mf.sharedMesh != null
                        && mf.gameObject.name.IndexOf(ShadowKeyword, StringComparison.OrdinalIgnoreCase) < 0
                        && !mf.gameObject.name.Contains("_LOD"))
                    .ToArray();

                if (validFilters.Length == 0) return false;

                LODGroup existingGroup = root.GetComponent<LODGroup>();
                if (existingGroup != null)
                    UnityEngine.Object.DestroyImmediate(existingGroup);

                RemoveExistingLODChildren(root);

                LODGroup lodGroup = root.AddComponent<LODGroup>();
                if (lodGroup == null)
                {
                    Debug.LogError($"[LODGroupSetup] Failed to add LODGroup to {prefabAsset.name}");
                    return false;
                }

                LOD[] lods = new LOD[LodCount];

                // LOD 0 — original renderers
                Renderer[] lod0Renderers = validFilters
                    .Select(mf => mf.GetComponent<Renderer>())
                    .Where(r => r != null)
                    .ToArray();
                lods[0] = new LOD(_transitions[0], lod0Renderers);

                // LOD 1, 2, 3 — simplified mesh child GameObjects
                for (int lodIndex = 1; lodIndex < LodCount; lodIndex++)
                {
                    int quality = _qualities[lodIndex];
                    List<Renderer> lodRenderers = new List<Renderer>();

                    foreach (MeshFilter mf in validFilters)
                    {
                        Mesh simplifiedMesh = FindSimplifiedMesh(prefabAsset.name, mf.sharedMesh.name, quality);
                        if (simplifiedMesh == null)
                        {
                            Debug.LogWarning($"[LODGroupSetup] Mesh not found for {prefabAsset.name} / {mf.sharedMesh.name} at quality {quality}. Run LOD Mesh Generator first.");
                            continue;
                        }

                        GameObject lodGO = CreateLODChild(mf, simplifiedMesh, lodIndex);
                        lodGO.transform.SetParent(mf.transform.parent, false);
                        lodRenderers.Add(lodGO.GetComponent<MeshRenderer>());
                    }

                    lods[lodIndex] = new LOD(_transitions[lodIndex], lodRenderers.ToArray());
                }

                lodGroup.SetLODs(lods);
                lodGroup.RecalculateBounds();

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void RemoveExistingLODChildren(GameObject root)
        {
            List<GameObject> toRemove = root.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.EndsWith("_LOD1") || t.name.EndsWith("_LOD2") || t.name.EndsWith("_LOD3"))
                .Select(t => t.gameObject)
                .ToList();

            foreach (GameObject go in toRemove)
                UnityEngine.Object.DestroyImmediate(go);
        }

        private Mesh FindSimplifiedMesh(string prefabName, string meshName, int quality)
        {
            string fileName = SanitizeFileName($"{prefabName}_{meshName}_LOD{quality}");
            string searchFolder = $"{LODMeshRootPath}/LOD_{quality}";
            string[] guids = AssetDatabase.FindAssets(fileName, new[] { searchFolder });
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private static GameObject CreateLODChild(MeshFilter sourceMF, Mesh simplifiedMesh, int lodIndex)
        {
            GameObject go = new GameObject($"{sourceMF.gameObject.name}_LOD{lodIndex}");
            go.transform.localPosition = sourceMF.transform.localPosition;
            go.transform.localRotation = sourceMF.transform.localRotation;
            go.transform.localScale    = sourceMF.transform.localScale;

            go.AddComponent<MeshFilter>().sharedMesh = simplifiedMesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            MeshRenderer sourceMR = sourceMF.GetComponent<MeshRenderer>();
            if (sourceMR != null)
                mr.sharedMaterials = sourceMR.sharedMaterials;

            return go;
        }

        private static string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            return fileName;
        }
    }
}
