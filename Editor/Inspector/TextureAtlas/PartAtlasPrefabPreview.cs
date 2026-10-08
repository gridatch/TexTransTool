#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Renders selected extraction meshes without running their GameObjects,
    /// Animator, components, Matsukawa analysis, or the atlas pipeline.
    /// </summary>
    internal sealed class PartAtlasPrefabPreview : IDisposable
    {
        private sealed class DrawItem
        {
            internal readonly Mesh Mesh;
            internal readonly Matrix4x4 Matrix;
            internal readonly Material[] Materials;
            internal readonly bool OwnsMesh;

            internal DrawItem(Mesh mesh, Matrix4x4 matrix, Material[] materials, bool ownsMesh)
            {
                Mesh = mesh;
                Matrix = matrix;
                Materials = materials;
                OwnsMesh = ownsMesh;
            }
        }

        private readonly List<DrawItem> _items = new();
        private PreviewRenderUtility? _utility;
        private Bounds _bounds;
        private bool _hasBounds;
        // Fitting depends on the geometry and the preview aspect ratio,
        // never on the current azimuth. Recompute only when either changes.
        private float _fitAspect = -1f;
        private float _fitOrthographicSize;
        private const float CameraElevationDegrees = 18f;
        private const float FitMargin = 1.2f;
        private const float InitialYawDegrees = 145f;
        private const float DegreesPerCanvasWidth = 360f;
        private readonly Action _requestRepaint;
        private float _yawDegrees = InitialYawDegrees;
        private int _dragControlId;

        internal PartAtlasPrefabPreview(Action requestRepaint)
        {
            _requestRepaint = requestRepaint ?? throw new ArgumentNullException(nameof(requestRepaint));
        }

        internal void ResetView()
        {
            _yawDegrees = InitialYawDegrees;
            _dragControlId = 0;
        }

        internal void SetRenderers(IEnumerable<Renderer> renderers)
        {
            Clear();
            if (renderers == null)
                return;

            // The supplied list is already filtered by GameObject target selection
            // and the independent Renderer.Keep setting in the EditorWindow.
            foreach (var renderer in renderers.Where(renderer => renderer != null).Distinct())
            {
                Mesh? mesh = null;
                bool ownsMesh = false;

                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null)
                        continue;

                    var snapshot = new Mesh
                    {
                        name = "WDT Preview Mesh",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    try
                    {
                        // Snapshot local-space skinned geometry (including blend shapes).
                        // The source Prefab hierarchy is never changed.
                        skinned.BakeMesh(snapshot);
                        mesh = snapshot;
                        ownsMesh = true;
                    }
                    catch (Exception exception)
                    {
                        UnityEngine.Object.DestroyImmediate(snapshot);
                        Debug.LogWarning("WDT 3D preview: SkinnedMesh snapshot failed for "
                            + renderer.name + ": " + exception.Message);
                        continue;
                    }
                }
                else if (renderer is MeshRenderer staticRenderer)
                {
                    mesh = staticRenderer.GetComponent<MeshFilter>()?.sharedMesh;
                }

                if (mesh == null)
                    continue;

                var materials = renderer.sharedMaterials;
                if (mesh.subMeshCount == 0 || materials == null
                    || !materials.Take(mesh.subMeshCount).Any(material => material != null))
                {
                    if (ownsMesh)
                        UnityEngine.Object.DestroyImmediate(mesh);
                    continue;
                }

                var item = new DrawItem(mesh, renderer.localToWorldMatrix, materials, ownsMesh);
                _items.Add(item);
                EncapsulateBounds(item);
            }
        }

        internal void Clear()
        {
            foreach (var item in _items)
            {
                if (item.OwnsMesh && item.Mesh != null)
                    UnityEngine.Object.DestroyImmediate(item.Mesh);
            }

            _items.Clear();
            _hasBounds = false;
            _fitAspect = -1f;
        }

        internal void Draw(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            if (rect.width < 20f || rect.height < 50f)
                return;

            var title = new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, 18f);
            GUI.Label(title, "抽出対象の3Dプレビュー", EditorStyles.boldLabel);
            var canvas = new Rect(rect.x + 4f, title.yMax + 4f,
                rect.width - 8f, Mathf.Max(0f, rect.yMax - title.yMax - 8f));

            // Only horizontal left-button drags inside the canvas rotate the camera.
            // The preview never changes source Transforms or rebuilds mesh snapshots
            // during a drag.
            HandleOrbitInput(canvas);

            // Empty selections have no messages or extra UI: just the canvas.
            if (Event.current.type != EventType.Repaint)
                return;

            var background = EditorGUIUtility.isProSkin
                ? new Color(0.13f, 0.13f, 0.13f)
                : new Color(0.34f, 0.34f, 0.34f);
            EditorGUI.DrawRect(canvas, background);
            if (_items.Count == 0 || !_hasBounds || canvas.width < 1f || canvas.height < 1f)
                return;

            _utility ??= new PreviewRenderUtility();
            var camera = _utility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.orthographic = true;
            ConfigureCamera(camera, canvas.width / canvas.height);
            _utility.ambientColor = new Color(0.62f, 0.62f, 0.62f);
            var lights = _utility.lights;
            lights[0].intensity = 1.1f;
            lights[0].transform.rotation = Quaternion.Euler(40f, 25f, 0f);
            lights[1].intensity = 0.65f;
            lights[1].transform.rotation = Quaternion.Euler(340f, 200f, 0f);

            try
            {
                _utility.BeginPreview(canvas, GUIStyle.none);
                foreach (var item in _items)
                {
                    var limit = Mathf.Min(item.Mesh.subMeshCount, item.Materials.Length);
                    for (int submesh = 0; submesh < limit; submesh++)
                    {
                        var material = item.Materials[submesh];
                        if (material == null)
                            continue;

                        // PreviewRenderUtility.DrawMesh(matrix) decomposes matrices
                        // and loses mirrored/sheared transforms. Graphics.DrawMesh
                        // preserves the full source world matrix.
                        Graphics.DrawMesh(item.Mesh, item.Matrix, material, 0, camera,
                            submesh, null, ShadowCastingMode.Off, false);
                    }
                }

                _utility.Render(allowScriptableRenderPipeline: true);
                _utility.EndAndDrawPreview(canvas);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _utility.Cleanup();
                _utility = null;
            }
        }

        private void HandleOrbitInput(Rect canvas)
        {
            // Stable IMGUI control ID across Layout/MouseDrag/Repaint events.
            var controlId = GUIUtility.GetControlID(FocusType.Passive, canvas);
            var evt = Event.current;
            var eventType = evt.GetTypeForControl(controlId);

            if (eventType == EventType.MouseDown
                && evt.button == 0
                && GUIUtility.hotControl == 0
                && _items.Count > 0
                && canvas.Contains(evt.mousePosition))
            {
                GUIUtility.hotControl = controlId;
                _dragControlId = controlId;
                evt.Use();
            }
            else if (eventType == EventType.MouseDrag
                     && GUIUtility.hotControl == controlId
                     && _dragControlId == controlId)
            {
                // A drag across the full canvas width makes one full revolution.
                // Mathf.Repeat wraps continuously in either direction.
                var deltaDegrees = evt.delta.x * DegreesPerCanvasWidth / Mathf.Max(1f, canvas.width);
                if (!Mathf.Approximately(deltaDegrees, 0f))
                {
                    _yawDegrees = Mathf.Repeat(_yawDegrees + deltaDegrees, 360f);
                    _requestRepaint();
                }
                evt.Use();
            }
            else if ((eventType == EventType.MouseUp && evt.button == 0)
                     && GUIUtility.hotControl == controlId
                     && _dragControlId == controlId)
            {
                GUIUtility.hotControl = 0;
                _dragControlId = 0;
                evt.Use();
            }
            else if (eventType == EventType.Ignore
                     && GUIUtility.hotControl == controlId
                     && _dragControlId == controlId)
            {
                // The OS/editor can cancel a captured drag without MouseUp.
                GUIUtility.hotControl = 0;
                _dragControlId = 0;
            }
        }

        private void EncapsulateBounds(DrawItem item)
        {
            var meshBounds = item.Mesh.bounds;
            var min = meshBounds.min;
            var max = meshBounds.max;

            for (int i = 0; i < 8; i++)
            {
                var point = item.Matrix.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z));
                if (!_hasBounds)
                {
                    _bounds = new Bounds(point, Vector3.zero);
                    _hasBounds = true;
                }
                else
                {
                    _bounds.Encapsulate(point);
                }
            }
        }

        private void ConfigureCamera(Camera camera, float aspect)
        {
            // A complete revolution must not change the apparent zoom.
            // A world-space AABB has a maximum horizontal half-extent of
            // sqrt(ex² + ez²) over all possible azimuths. At the fixed
            // elevation, its maximum vertical half-extent is
            // ey*cos(elevation) + sqrt(ex² + ez²)*sin(elevation).
            // Both limits are independent of _yawDegrees and safely enclose
            // the source meshes throughout the full orbit.
            if (_fitAspect != aspect)
            {
                var extents = _bounds.extents;
                var horizontalRadius = Mathf.Sqrt(
                    extents.x * extents.x + extents.z * extents.z);
                var elevation = CameraElevationDegrees * Mathf.Deg2Rad;
                var maxHalfWidth = horizontalRadius;
                var maxHalfHeight = extents.y * Mathf.Cos(elevation)
                    + horizontalRadius * Mathf.Sin(elevation);

                _fitOrthographicSize = Mathf.Max(0.1f,
                    Mathf.Max(maxHalfHeight, maxHalfWidth / Mathf.Max(0.01f, aspect))
                    * FitMargin);
                _fitAspect = aspect;
            }

            var rotation = Quaternion.Euler(CameraElevationDegrees, _yawDegrees, 0f);
            var center = _bounds.center;
            var forward = rotation * Vector3.forward;
            var radius = Mathf.Max(_bounds.extents.magnitude, 0.01f);
            var distance = radius * 3f + 1f;

            camera.transform.rotation = rotation;
            camera.transform.position = center - forward * distance;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + radius * 3f + 1f;
            camera.orthographicSize = _fitOrthographicSize;
            camera.aspect = aspect;
        }

        public void Dispose()
        {
            Clear();
            if (_utility != null)
            {
                _utility.Cleanup();
                _utility = null;
            }
        }
    }
}
