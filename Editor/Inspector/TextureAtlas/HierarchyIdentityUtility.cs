#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal static class HierarchyIdentityUtility
    {
        internal static int[] GetSiblingIndexPath(Transform root, Transform target)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target == root) return Array.Empty<int>();

            var indices = new List<int>();
            var current = target;
            while (current != null && current != root)
            {
                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }

            if (current != root)
                throw new ArgumentException("Target is not a child of root.", nameof(target));

            indices.Reverse();
            return indices.ToArray();
        }

        internal static Transform? ResolveSiblingIndexPath(
            Transform root,
            IReadOnlyList<int> indices)
        {
            var current = root;
            for (var i = 0; i < indices.Count; i++)
            {
                var index = indices[i];
                if (index < 0 || index >= current.childCount)
                    return null;

                current = current.GetChild(index);
            }

            return current;
        }

        internal static string GetRendererKey(GameObject root, Renderer renderer)
        {
            var path = string.Join(
                "/",
                GetSiblingIndexPath(root.transform, renderer.transform)
            );
            var renderers = renderer.GetComponents<Renderer>();
            var componentIndex = Array.IndexOf(renderers, renderer);

            return path
                + "|"
                + renderer.GetType().FullName
                + "|"
                + componentIndex;
        }

        internal static bool TryResolveUniqueNamePath(
            Transform root,
            string path,
            out Transform? target,
            out string error)
        {
            target = null;
            error = "";

            if (root == null)
            {
                error = "Prefab Rootがありません。";
                return false;
            }

            if (string.IsNullOrEmpty(path))
            {
                target = root;
                return true;
            }

            var current = root;
            var resolvedSegments = new List<string>();
            foreach (var segment in path.Split('/'))
            {
                if (string.IsNullOrEmpty(segment))
                {
                    error = "空の階層名を含むPathは解決できません: " + path;
                    return false;
                }

                Transform? match = null;
                var matchCount = 0;
                for (var i = 0; i < current.childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (child.name != segment) continue;

                    match = child;
                    matchCount++;
                }

                resolvedSegments.Add(segment);
                var resolvedPath = string.Join("/", resolvedSegments);

                if (matchCount == 0)
                {
                    error = "指定されたGameObjectがPrefab内に見つかりません: " + resolvedPath;
                    return false;
                }

                if (matchCount > 1)
                {
                    error =
                        "同一階層に同名GameObjectが複数あるためPathを一意に解決できません: "
                        + resolvedPath;
                    return false;
                }

                current = match!;
            }

            target = current;
            return true;
        }
    }
}
