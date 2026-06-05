using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ScriptableObject chứa danh sách mesh cơ bản do người dùng cung cấp.
/// Dùng làm collider shape đơn giản ở Mức 1 và Mức 2.
/// Tạo qua: Assets > Create > Collider Mesh Library
/// </summary>
[CreateAssetMenu(fileName = "ColliderMeshLibrary", menuName = "Collider Auto Assigner/Collider Mesh Library")]
public class ColliderMeshLibrary : ScriptableObject
{
    [Tooltip("Danh sách các mesh cơ bản dùng làm collider (hình trụ, hình nón, v.v.)")]
    public List<Mesh> meshes = new List<Mesh>();
}
