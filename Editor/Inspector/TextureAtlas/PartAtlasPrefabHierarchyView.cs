#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class PartAtlasPrefabHierarchyView : TreeView
    {
        private sealed class TransformItem : TreeViewItem
        {
            internal readonly Transform Transform;
            internal readonly string Path;

            internal TransformItem(int id, int depth, string displayName, Transform transform, string path)
                : base(id, depth, displayName)
            {
                Transform = transform;
                Path = path;
            }
        }

        private static readonly Color KeepColor = new(0.25f, 0.75f, 0.40f, 0.22f);
        private static readonly Color DeleteColor = new(0.90f, 0.25f, 0.25f, 0.22f);
        private static readonly Color ProtectColor = new(0.95f, 0.75f, 0.15f, 0.25f);
        private static readonly Color LinkFlashColor = new(0.30f, 0.60f, 1.00f, 0.30f);

        private readonly SearchField _searchField = new();
        private readonly Dictionary<int, TransformItem> _items = new();
        private readonly HashSet<int> _keep = new();
        private readonly HashSet<int> _delete = new();
        private readonly HashSet<int> _protected = new();

        private GameObject? _prefabRoot;
        private int _flashId;
        private double _flashUntil;

        internal event Action<Transform?>? SelectionChangedTransform;

        internal PartAtlasPrefabHierarchyView(TreeViewState state)
            : base(state)
        {
            rowHeight = 18f;
            showAlternatingRowBackgrounds = false;
            showBorder = true;
            Reload();
        }

        internal Transform? SelectedTransform
        {
            get
            {
                var selected = GetSelection();
                if (selected == null || selected.Count == 0)
                    return null;

                return _items.TryGetValue(selected[0], out var item)
                    ? item.Transform
                    : null;
            }
        }

        internal bool HasActiveFlash =>
            _flashId != 0 && EditorApplication.timeSinceStartup < _flashUntil;

        internal void SetRoot(GameObject? prefabRoot)
        {
            _prefabRoot = prefabRoot;
            searchString = "";
            _flashId = 0;
            _flashUntil = 0d;
            _keep.Clear();
            _delete.Clear();
            _protected.Clear();
            SetSelection(Array.Empty<int>());
            Reload();

            if (_prefabRoot != null)
                SetExpanded(_prefabRoot.transform.GetInstanceID(), true);
        }

        internal void SetAnalysis(MatsukawaAnalysis? analysis)
        {
            _keep.Clear();
            _delete.Clear();
            _protected.Clear();

            if (analysis != null)
            {
                foreach (var transform in analysis.KeepTransforms)
                    if (transform != null)
                        _keep.Add(transform.GetInstanceID());

                foreach (var transform in analysis.DeleteTransforms)
                    if (transform != null)
                        _delete.Add(transform.GetInstanceID());

                foreach (var gameObject in analysis.ProtectedObjects)
                    if (gameObject != null)
                        _protected.Add(gameObject.transform.GetInstanceID());
            }

            Repaint();
        }

        internal void SelectAndReveal(Transform transform, bool flash)
        {
            if (transform == null || _items.ContainsKey(transform.GetInstanceID()) is false)
                return;

            var current = transform.parent;
            while (current != null)
            {
                if (_items.ContainsKey(current.GetInstanceID()))
                    SetExpanded(current.GetInstanceID(), true);

                if (_prefabRoot != null && current == _prefabRoot.transform)
                    break;

                current = current.parent;
            }

            SetSelection(
                new[] { transform.GetInstanceID() },
                TreeViewSelectionOptions.RevealAndFrame
            );

            if (flash)
            {
                _flashId = transform.GetInstanceID();
                _flashUntil = EditorApplication.timeSinceStartup + 0.75d;
            }

            Repaint();
        }

        internal void Draw(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            var titleRect = new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, 18f);
            GUI.Label(titleRect, "Prefab Hierarchy", EditorStyles.boldLabel);

            var searchRect = new Rect(rect.x + 6f, titleRect.yMax + 3f, rect.width - 12f, 20f);
            var nextSearch = _searchField.OnGUI(searchRect, searchString);
            if (!string.Equals(nextSearch, searchString, StringComparison.Ordinal))
            {
                searchString = nextSearch;
                Reload();
            }

            var legendHeight = HasAnalysis ? 22f : 0f;
            var treeRect = new Rect(
                rect.x + 4f,
                searchRect.yMax + 4f,
                rect.width - 8f,
                Mathf.Max(0f, rect.yMax - searchRect.yMax - 8f - legendHeight)
            );

            if (_prefabRoot == null)
            {
                GUI.Label(
                    treeRect,
                    "Prefab Assetを指定するとHierarchyを表示します。",
                    EditorStyles.centeredGreyMiniLabel
                );
            }
            else
            {
                base.OnGUI(treeRect);
            }

            if (HasAnalysis)
            {
                var legendRect = new Rect(
                    rect.x + 6f,
                    rect.yMax - 20f,
                    rect.width - 12f,
                    18f
                );
                GUI.Label(
                    legendRect,
                    "緑=残る / 赤=消える / 黄=保護",
                    EditorStyles.miniLabel
                );
            }
        }

        private bool HasAnalysis =>
            _keep.Count > 0 || _delete.Count > 0 || _protected.Count > 0;

        protected override TreeViewItem BuildRoot()
        {
            _items.Clear();

            var root = new TreeViewItem
            {
                id = int.MinValue,
                depth = -1,
                displayName = "Root",
                children = new List<TreeViewItem>(),
            };

            if (_prefabRoot == null)
                return root;

            var item = BuildItem(_prefabRoot.transform, 0, _prefabRoot.transform);
            root.AddChild(item);
            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        private TransformItem BuildItem(Transform transform, int depth, Transform root)
        {
            var path = RelativePath(root, transform);
            var item = new TransformItem(
                transform.GetInstanceID(),
                depth,
                transform.name,
                transform,
                path
            );
            _items[item.id] = item;

            for (var i = 0; i < transform.childCount; i++)
                item.AddChild(BuildItem(transform.GetChild(i), depth + 1, root));

            return item;
        }

        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            if (string.IsNullOrWhiteSpace(searchString))
                return base.BuildRows(root);

            var rows = new List<TreeViewItem>();
            var memo = new Dictionary<int, bool>();

            if (root.children != null)
            {
                foreach (var child in root.children.OfType<TransformItem>())
                    AddSearchRows(child, rows, memo);
            }

            return rows;
        }

        private bool AddSearchRows(
            TransformItem item,
            List<TreeViewItem> rows,
            Dictionary<int, bool> memo)
        {
            if (HasSearchMatch(item, memo) is false)
                return false;

            rows.Add(item);

            if (item.children != null)
            {
                foreach (var child in item.children.OfType<TransformItem>())
                    AddSearchRows(child, rows, memo);
            }

            return true;
        }

        private bool HasSearchMatch(
            TransformItem item,
            Dictionary<int, bool> memo)
        {
            if (memo.TryGetValue(item.id, out var cached))
                return cached;

            var matched = IsDirectSearchMatch(item);

            if (item.children != null)
            {
                foreach (var child in item.children.OfType<TransformItem>())
                {
                    if (HasSearchMatch(child, memo))
                        matched = true;
                }
            }

            memo[item.id] = matched;
            return matched;
        }

        private bool IsDirectSearchMatch(TransformItem item)
        {
            if (string.IsNullOrWhiteSpace(searchString))
                return true;

            var query = searchString.Trim();
            return item.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || item.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            if (args.item is not TransformItem item)
            {
                base.RowGUI(args);
                return;
            }

            if (Event.current.type == EventType.Repaint)
            {
                Color? background = null;
                if (_delete.Contains(item.id))
                    background = DeleteColor;
                else if (_protected.Contains(item.id))
                    background = ProtectColor;
                else if (_keep.Contains(item.id))
                    background = KeepColor;

                if (background.HasValue)
                {
                    EditorGUI.DrawRect(args.rowRect, background.Value);
                    EditorGUI.DrawRect(
                        new Rect(args.rowRect.x, args.rowRect.y, 3f, args.rowRect.height),
                        new Color(
                            background.Value.r,
                            background.Value.g,
                            background.Value.b,
                            0.95f
                        )
                    );
                }

                if (_flashId == item.id
                    && EditorApplication.timeSinceStartup < _flashUntil)
                {
                    EditorGUI.DrawRect(args.rowRect, LinkFlashColor);
                }
            }

            var previousColor = GUI.color;
            if (string.IsNullOrWhiteSpace(searchString) is false
                && IsDirectSearchMatch(item) is false)
            {
                GUI.color = new Color(
                    previousColor.r,
                    previousColor.g,
                    previousColor.b,
                    previousColor.a * 0.55f
                );
            }

            base.RowGUI(args);
            GUI.color = previousColor;
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            Transform? selected = null;
            if (selectedIds != null
                && selectedIds.Count > 0
                && _items.TryGetValue(selectedIds[0], out var item))
            {
                selected = item.Transform;
            }

            SelectionChangedTransform?.Invoke(selected);
        }

        protected override bool CanMultiSelect(TreeViewItem item) => false;

        protected override bool CanRename(TreeViewItem item) => false;

        private static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return "";

            var parts = new List<string>();
            var current = target;

            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
