# Collider Modules

Thả prefab collider tùy biến vào thư mục này, rồi gán thư mục vào ô
**Module Folder** trong cửa sổ `Tools ▸ Collider Auto Fitter`.

Tool quét mọi prefab trong folder và đăng ký theo **tên file**.

## Module được hỗ trợ

| Tên prefab | Dùng cho |
|------------|----------|
| `Cylinder` | các part hình trụ (tròn, không quá thuôn → không thành Capsule) |

Thiếu module `Cylinder` → các part trụ tự fallback về **Box** ôm khít.

## Quy ước hình học của một module

- **Trục Y local = chiều dài/cao** của hình.
- **X/Z = mặt cắt tròn** (đường kính).
- Collider **đặt ở gốc** (tâm ≈ origin).
- Kích thước **không cần chuẩn hóa**: tool tự đọc bounds thật của mesh và
  scale lại cho khít (Unity Cylinder cao 2, Ø1 vẫn dùng được).
- Nên là **MeshCollider (Convex = true)**, prefab chỉ chứa collider
  (tắt/bỏ MeshRenderer để khỏi hiện hình trong scene).

## Cách tạo prefab `Cylinder` nhanh

1. `GameObject ▸ 3D Object ▸ Cylinder`.
2. Bỏ component **MeshRenderer** (giữ MeshFilter + MeshCollider).
3. Trên **MeshCollider** bật **Convex**.
4. Đổi tên GameObject thành `Cylinder`, reset Transform (Position 0, Rotation 0, Scale 1).
5. Kéo vào thư mục này để tạo prefab. Xong — xóa object trong scene.
