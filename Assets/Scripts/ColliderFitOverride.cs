using UnityEngine;

/// <summary>
/// Per-item collider override. Attach to an object that the auto-fitter handles.
/// When <see cref="forcedShape"/> is anything other than <c>Auto</c>, the fitter
/// skips classification entirely and places exactly that single collider shape.
///
/// This is a plain runtime MonoBehaviour (no UnityEditor dependency) so it can
/// live on prefabs that ship in a build. The enum is defined here — independent
/// of the editor-only <c>ColliderAutoFitter.ShapeType</c> — and the editor maps
/// between the two.
/// </summary>
[DisallowMultipleComponent]
public class ColliderFitOverride : MonoBehaviour
{
    public enum Shape
    {
        Auto,           // let the classifier decide (default)
        Sphere,
        Capsule,
        Cylinder,
        Box,
        FrustumCone,
        FrustumPyramid,
        Dome
    }

    [Tooltip("Auto = let the fitter classify. Anything else forces that collider shape.")]
    public Shape forcedShape = Shape.Auto;

    [Tooltip("Set by the fitter after a run: how well the chosen collider fills the mesh (0..1). Informational only.")]
    [Range(0f, 1f)]
    public float lastFill;

    [Tooltip("Set by the fitter when the chosen shape was a low-confidence guess and likely needs a manual override.")]
    public bool flaggedForReview;
}
