#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    internal sealed class AtlasRendererVisibilityAnalyzer
        : IAtlasRendererVisibilityAnalyzer
    {
        private readonly AnimationIndex _animationIndex;
        private readonly ObjectPathRemapper _pathRemapper;
        private readonly Transform _avatarRoot;

        internal AtlasRendererVisibilityAnalyzer(BuildContext context)
        {
            var services = context.Extension<AnimatorServicesContext>();
            _animationIndex = services.AnimationIndex;
            _pathRemapper = services.ObjectPathRemapper;
            _avatarRoot = context.AvatarRootTransform;
        }

        public IReadOnlyDictionary<Renderer, AtlasRendererVisibilityState>
            Analyze(IEnumerable<Renderer> renderers)
        {
            return Analyze(
                _animationIndex,
                _avatarRoot,
                renderers,
                transform =>
                {
                    _pathRemapper.GetVirtualPathForObject(transform);
                    return _pathRemapper.GetAllPathsForObject(transform);
                });
        }

        internal static IReadOnlyDictionary<
            Renderer, AtlasRendererVisibilityState> Analyze(
            AnimationIndex animationIndex,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers,
            Func<Transform, IEnumerable<string>> pathsForObject)
        {
            var result =
                new Dictionary<Renderer, AtlasRendererVisibilityState>();
            var gameObjectGates =
                new Dictionary<Transform, VisibilityGateState>();

            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                var transform = renderer.transform;
                if (!IsUnderAvatarRoot(transform, avatarRoot))
                    continue;

                var rendererGate = AnalyzeGate(
                    animationIndex,
                    pathsForObject(transform),
                    renderer.GetType(),
                    "m_Enabled",
                    renderer.enabled);

                var canBecomeVisible = rendererGate.CanBeTrue;
                var canBecomeHidden = rendererGate.CanBeFalse;

                for (var cursor = transform;
                     cursor != null;
                     cursor = cursor.parent)
                {
                    // Match TexTransBehaviorSearch.CheckIsActive: this marker
                    // intentionally stops inherited active-state checks at the
                    // marked object, including that object's own activeSelf.
                    if (cursor.GetComponent<IsActiveInheritBreaker>() != null)
                        break;

                    if (!gameObjectGates.TryGetValue(
                            cursor, out var gameObjectGate))
                    {
                        gameObjectGate = AnalyzeGate(
                            animationIndex,
                            pathsForObject(cursor),
                            typeof(GameObject),
                            "m_IsActive",
                            cursor.gameObject.activeSelf);
                        gameObjectGates.Add(cursor, gameObjectGate);
                    }

                    canBecomeVisible &= gameObjectGate.CanBeTrue;
                    canBecomeHidden |= gameObjectGate.CanBeFalse;

                    if (cursor == avatarRoot)
                        break;
                }

                result[renderer] = new AtlasRendererVisibilityState(
                    canBecomeVisible,
                    canBecomeHidden);
            }

            return result;
        }

        private static bool IsUnderAvatarRoot(
            Transform transform,
            Transform avatarRoot)
        {
            for (var cursor = transform;
                 cursor != null;
                 cursor = cursor.parent)
            {
                if (cursor == avatarRoot)
                    return true;
            }

            return false;
        }

        private static VisibilityGateState AnalyzeGate(
            AnimationIndex index,
            IEnumerable<string> paths,
            Type componentType,
            string propertyName,
            bool currentValue)
        {
            var canBeTrue = currentValue;
            var canBeFalse = !currentValue;

            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                foreach (var clip in index.GetClipsForObjectPath(path))
                {
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (!TargetsGate(
                                binding,
                                path,
                                componentType,
                                propertyName))
                            continue;

                        var curve = clip.GetFloatCurve(binding);
                        if (curve == null)
                            continue;

                        foreach (var key in curve.keys)
                        {
                            if (float.IsNaN(key.value)
                                || float.IsInfinity(key.value))
                                continue;

                            if (key.value >= 0.5f)
                                canBeTrue = true;
                            else
                                canBeFalse = true;

                            if (canBeTrue && canBeFalse)
                                break;
                        }

                        if (canBeTrue && canBeFalse)
                            break;
                    }

                    if (canBeTrue && canBeFalse)
                        break;
                }

                if (canBeTrue && canBeFalse)
                    break;
            }

            return new VisibilityGateState(canBeTrue, canBeFalse);
        }

        private static bool TargetsGate(
            EditorCurveBinding binding,
            string path,
            Type componentType,
            string propertyName)
        {
            if (binding.path != path
                || binding.propertyName != propertyName
                || binding.type == null)
                return false;

            if (propertyName == "m_IsActive")
                return binding.type == typeof(GameObject);

            return typeof(Renderer).IsAssignableFrom(binding.type)
                && binding.type.IsAssignableFrom(componentType);
        }

        private readonly struct VisibilityGateState
        {
            internal readonly bool CanBeTrue;
            internal readonly bool CanBeFalse;

            internal VisibilityGateState(
                bool canBeTrue,
                bool canBeFalse)
            {
                CanBeTrue = canBeTrue;
                CanBeFalse = canBeFalse;
            }
        }
    }
}
