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
            // Fixed three-quarter view. No camera controls in the initial version.
            var rotation = Quaternion.Euler(18f, 145f, 0f);
            var center = _bounds.center;
            var forward = rotation * Vector3.forward;
            var right = rotation * Vector3.right;
            var up = rotation * Vector3.up;
            var extents = _bounds.extents;

            float halfWidth = 0f;
            float halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                var offset = new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(Vector3.Dot(offset, right)));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(Vector3.Dot(offset, up)));
            }

            var radius = Mathf.Max(extents.magnitude, 0.01f);
            var distance = radius * 3f + 1f;
            camera.transform.rotation = rotation;
            camera.transform.position = center - forward * distance;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + radius * 3f + 1f;
            camera.orthographicSize = Mathf.Max(0.1f,
                Mathf.Max(halfHeight, halfWidth / Mathf.Max(0.01f, aspect)) * 1.2f);
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
