using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

using MantisLODEditor;

namespace HoleMaster.Editor
{
    public class LODMeshGeneratorWindow : EditorWindow
    {
        private const string OutputRootPath = "Assets/0_Game/Prefabs/LOD";
        private const string ShadowKeyword = "shadow";

        private List<GameObject> _prefabs = new List<GameObject>();
        private ReorderableList _reorderableList;
        private float _quality = 50f;
        private Vector2 _scrollPos;
        private string _statusMessage = string.Empty;
        private DefaultAsset _sourceFolder;

        [MenuItem("Window/Game Tools/LOD Mesh Generator")]
        private static void Open()
        {
            LODMeshGeneratorWindow window = GetWindow<LODMeshGeneratorWindow>("LOD Mesh Generator");
            window.minSize = new Vector2(360f, 400f);
            window.Show();
        }

        private void OnEnable()
        {
            _reorderableList = new ReorderableList(_prefabs, typeof(GameObject), true, true, true, true);
            _reorderableList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, $"Item Prefabs  (drag prefabs here)  [{_prefabs.Count}]");
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
            EditorGUILayout.LabelField("LOD Mesh Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            DrawQualityField();
            EditorGUILayout.Space(4f);
            DrawFolderSection();
            EditorGUILayout.Space(8f);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));
            _reorderableList.DoLayoutList();
            Rect listRect = GUILayoutUtility.GetLastRect();
            HandleDragAndDrop(listRect);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            DrawCreateButton();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
            }
            EditorGUILayout.Space(4f);
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
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;
            if (!dropArea.Contains(evt.mousePosition))
                return;

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

        private void DrawQualityField()
        {
            _quality = EditorGUILayout.Slider("Quality", _quality, 1f, 100f);
        }

        private void DrawCreateButton()
        {
            EditorGUILayout.BeginHorizontal();

            bool hasValidPrefabs = _prefabs.Any(p => p != null);
            GUI.enabled = hasValidPrefabs;
            if (GUILayout.Button("Create LOD Meshes", GUILayout.Height(32f)))
                GenerateLODMeshes();

            GUI.enabled = _prefabs.Count > 0;
            if (GUILayout.Button("Clear List", GUILayout.Height(32f), GUILayout.Width(90f)))
            {
                _prefabs.Clear();
                _statusMessage = string.Empty;
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
        }

        private void GenerateLODMeshes()
        {
            List<GameObject> validPrefabs = _prefabs.Where(p => p != null).ToList();
            if (validPrefabs.Count == 0)
            {
                _statusMessage = "No valid prefabs in list.";
                return;
            }

            EnsureOutputFolder((int)_quality);

            string outputFolder = $"{OutputRootPath}/LOD_{(int)_quality}";
            int totalSaved = 0;

            try
            {
                for (int i = 0; i < validPrefabs.Count; i++)
                {
                    GameObject prefab = validPrefabs[i];
                    EditorUtility.DisplayProgressBar(
                        "Generating LOD Meshes",
                        $"Processing: {prefab.name} ({i + 1}/{validPrefabs.Count})",
                        (float)i / validPrefabs.Count);

                    int saved = ProcessPrefab(prefab, outputFolder);
                    totalSaved += saved;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            _statusMessage = $"Done. Saved {totalSaved} mesh asset(s) to {outputFolder}";
            Debug.Log($"[LODMeshGenerator] {_statusMessage}");
        }

        private int ProcessPrefab(GameObject prefab, string outputFolder)
        {
            MeshFilter[] allMeshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter[] validMeshFilters = allMeshFilters
                .Where(mf => mf.sharedMesh != null
                    && mf.gameObject.name.IndexOf(ShadowKeyword, StringComparison.OrdinalIgnoreCase) < 0
                    && !mf.gameObject.name.Contains("_LOD"))
                .ToArray();

            if (validMeshFilters.Length == 0) return 0;

            Mantis_Mesh[] mantisMeshes = CreateMantisMeshCopies(validMeshFilters);

            MantisLODEditorUtility.PrepareSimplify(mantisMeshes, false);
            MantisLODEditorUtility.Simplify(
                mantisMeshes,
                protect_boundary: true,
                protect_detail: false,
                protect_symmetry: false,
                protect_normal: false,
                protect_shape: true,
                use_detail_map: false,
                detail_boost: 10);
            MantisLODEditorUtility.SetQuality(mantisMeshes, _quality);

            int savedCount = SaveMeshAssets(mantisMeshes, validMeshFilters, prefab.name, outputFolder);

            MantisLODEditorUtility.FinishSimplify(mantisMeshes, false);
            return savedCount;
        }

        private static Mantis_Mesh[] CreateMantisMeshCopies(MeshFilter[] meshFilters)
        {
            var mantisMeshes = new Mantis_Mesh[meshFilters.Length];
            for (int i = 0; i < meshFilters.Length; i++)
            {
                Mesh meshCopy = UnityEngine.Object.Instantiate(meshFilters[i].sharedMesh);
                meshCopy.name = meshFilters[i].sharedMesh.name;
                mantisMeshes[i] = new Mantis_Mesh { mesh = meshCopy };
            }
            return mantisMeshes;
        }

        private static int SaveMeshAssets(Mantis_Mesh[] mantisMeshes, MeshFilter[] meshFilters, string prefabName, string outputFolder)
        {
            int saved = 0;
            for (int i = 0; i < mantisMeshes.Length; i++)
            {
                Mesh mesh = mantisMeshes[i].mesh;
                if (mesh == null) continue;

                string meshName = string.IsNullOrEmpty(mesh.name) ? meshFilters[i].gameObject.name : mesh.name;
                string quality = Path.GetFileName(outputFolder).Replace("LOD_", "");
                string fileName = SanitizeFileName($"{prefabName}_{meshName}_LOD{quality}.asset");
                string filePath = $"{outputFolder}/{fileName}";

                if (AssetDatabase.LoadAssetAtPath<Mesh>(filePath) != null)
                    AssetDatabase.DeleteAsset(filePath);

                MantisLODEditorUtility.SaveSimplifiedMesh(mesh, filePath);
                saved++;
            }
            return saved;
        }
        private static string SanitizeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
                fileName = fileName.Replace(c, '_');
            return fileName;
        }

        private static void EnsureOutputFolder(int quality)
        {
            if (!AssetDatabase.IsValidFolder(OutputRootPath))
            {
                string[] parts = OutputRootPath.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            string lodFolder = $"{OutputRootPath}/LOD_{quality}";
            if (!AssetDatabase.IsValidFolder(lodFolder))
                AssetDatabase.CreateFolder(OutputRootPath, $"LOD_{quality}");
        }
    }
}
