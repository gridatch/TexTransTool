#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Native Unity component inspectors hosted in a separate, disposable Prefab
    /// contents copy. Never point custom Editors at the extraction staging root.
    ///
    /// The view uses InspectorElement so custom CreateInspectorGUI() (including
    /// UI Toolkit + IMGUI hybrid inspectors, e.g. Modular Avatar) is preserved.
    /// Arbitrary third-party Editors may have side effects on referenced assets,
    /// so the separate Prefab copy isolates the working hierarchy, not all assets.
    /// </summary>
    internal sealed class PartAtlasPrefabReadOnlyInspector : IDisposable
    {
        private const float ComponentGap = 4f;
        private readonly VisualElement _root = new();
        private readonly ScrollView _scroll = new(ScrollViewMode.Vertical);
        private readonly List<UnityEditor.Editor> _editors = new();

        private GameObject? _inspectionRoot;
        private Transform? _originalRoot;
        private Transform? _selectedOriginal;
        private int _selectionRevision;

        internal VisualElement Root => _root;

        internal PartAtlasPrefabReadOnlyInspector()
        {
            _root.name = "wdt-native-inspector";
            _root.style.flexDirection = FlexDirection.Column;
            _root.style.flexGrow = 1f;
            _root.style.minHeight = 0f;

            var header = new Label("Inspector（閲覧用コピー）");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginLeft = 8f;
            header.style.marginTop = 7f;
            header.style.marginBottom = 7f;
            _root.Add(header);

            _scroll.style.flexGrow = 1f;
            _scroll.style.minHeight = 0f;
            _root.Add(_scroll);
        }

        /// <summary>
        /// Maintains a second hierarchy with the same Prefab structure as the
        /// extraction staging object; no changes to it are ever saved.
        /// </summary>
        internal void SetSourcePrefab(GameObject? sourcePrefab)
        {
            _selectionRevision++;
            ClearEditors();
            _selectedOriginal = null;
            _originalRoot = null;

            if (_inspectionRoot != null)
            {
                try { PrefabUtility.UnloadPrefabContents(_inspectionRoot); }
                catch (Exception exception) { Debug.LogException(exception); }
                _inspectionRoot = null;
            }

            if (sourcePrefab == null)
                return;

            var assetPath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (string.IsNullOrEmpty(assetPath))
                return;

            try
            {
                _inspectionRoot = PrefabUtility.LoadPrefabContents(assetPath);
                // Match the original Window's staging representation.
                PartAtlasPrefabPipeline.UnpackPrefabInstancesForStaging(_inspectionRoot);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("WDT Inspector: cannot load inspection copy: "
                    + exception.Message);
                if (_inspectionRoot != null)
                {
                    try { PrefabUtility.UnloadPrefabContents(_inspectionRoot); }
                    catch (Exception cleanupException) { Debug.LogException(cleanupException); }
                    _inspectionRoot = null;
                }
            }
        }

        internal void SetSelection(Transform? selected, Transform? originalRoot)
        {
            if (selected == _selectedOriginal && originalRoot == _originalRoot)
                return;

            _selectedOriginal = selected;
            _originalRoot = originalRoot;
            var revision = ++_selectionRevision;
            ClearEditors();

            if (selected == null || originalRoot == null || _inspectionRoot == null)
                return;

            // TreeView selection changes occur inside an IMGUI event. Custom
            // Editors can build UI Toolkit/IMGUI hybrids; construct those
            // inspectors on the next UI Toolkit panel update instead.
            _root.schedule.Execute(() =>
            {
                if (revision != _selectionRevision
                    || _inspectionRoot == null
                    || selected == null
                    || originalRoot == null)
                    return;

                var counterpart = FindCounterpart(selected, originalRoot,
                    _inspectionRoot.transform);
                if (counterpart == null)
                    return;

                RenderSelectedObject(counterpart.gameObject);
                _scroll.scrollOffset = Vector2.zero;
            }).ExecuteLater(0);
        }

        private static Transform? FindCounterpart(
            Transform original, Transform originalRoot, Transform copyRoot)
        {
            var indices = new List<int>();
            var current = original;
            while (current != originalRoot)
            {
                if (current.parent == null)
                    return null;

                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }

            var target = copyRoot;
            for (var i = indices.Count - 1; i >= 0; i--)
            {
                var index = indices[i];
                if (index < 0 || index >= target.childCount)
                    return null;

                target = target.GetChild(index);
            }

            return target;
        }

        private void RenderSelectedObject(GameObject gameObject)
        {
            var name = new Label(gameObject.name);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginLeft = 8f;
            name.style.marginBottom = 3f;
            _scroll.Add(name);

            var components = gameObject.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    var missing = new Label("Missing Script");
                    missing.style.marginLeft = 8f;
                    _scroll.Add(missing);
                    continue;
                }

                // An explicit Editor is owned by this class. InspectorElement(editor)
                // does not own it, so destroy on selection/source changes.
                UnityEditor.Editor? editor = null;
                try
                {
                    editor = UnityEditor.Editor.CreateEditor(component);
                    if (editor == null)
                        continue;

                    var foldout = new Foldout
                    {
                        text = ObjectNames.GetInspectorTitle(component),
                        value = true,
                    };
                    foldout.style.marginTop = ComponentGap;
                    foldout.style.marginLeft = 4f;
                    foldout.style.marginRight = 4f;

                    var inspector = new InspectorElement(editor);
                    inspector.style.flexGrow = 1f;
                    foldout.Add(inspector);
                    _scroll.Add(foldout);
                    _editors.Add(editor);
                }
                catch (Exception exception)
                {
                    if (editor != null)
                        Object.DestroyImmediate(editor);
                    Debug.LogWarning("WDT Inspector: component inspector unavailable for "
                        + component.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private void ClearEditors()
        {
            // Remove UI Toolkit elements and their bindings before destroying
            // their Editor instances and the corresponding transient components.
            _scroll.Clear();
            foreach (var editor in _editors)
            {
                if (editor != null)
                    Object.DestroyImmediate(editor);
            }

            _editors.Clear();
        }

        public void Dispose()
        {
            SetSourcePrefab(null);
            _root.Clear();
        }
    }
}
