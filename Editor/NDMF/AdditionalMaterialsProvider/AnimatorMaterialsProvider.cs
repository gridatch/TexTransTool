#nullable enable

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using net.rs64.TexTransTool.Utils;

namespace net.rs64.TexTransTool.NDMF.AdditionalMaterials
{
    internal class AnimatorMaterialsProvider : IAdditionalMaterialsProvider
    {
        private readonly AnimatorServicesContext _animatorServicesContext;
        private readonly GameObject _avatarRoot;

        public AnimatorMaterialsProvider(BuildContext context)
        {
            _animatorServicesContext = context.Extension<AnimatorServicesContext>();
            _avatarRoot = context.AvatarRootObject;
        }

        public HashSet<Material> GetReferencedMaterials()
        {
            return _animatorServicesContext.AnimationIndex
                .GetPPtrReferencedObjects
                .UOfType<Material>()
                .ToHashSet();
        }

        public void ReplaceReferencedMaterials(
            Dictionary<Material, Material> mapping,
            IReadOnlyCollection<Renderer>? targetRenderers = null)
        {
            if (targetRenderers == null)
            {
                _animatorServicesContext.AnimationIndex.RewriteObjectCurves(obj => {
                    if (obj is Material oldMat && mapping.TryGetValue(oldMat, out var newMat)) {
                        return newMat;
                    }
                    return obj;
                });
                return;
            }

#if NDMF_1_8_0_OR_NEWER
            var targetRendererSet = targetRenderers.ToHashSet();

            _animatorServicesContext.AnimationIndex.RewriteObjectCurves((binding, obj) => {
                if (obj is not Material oldMat || mapping.TryGetValue(oldMat, out var newMat) is false)
                {
                    return obj;
                }

                return BindingTargetsRenderer(binding, targetRendererSet) ? newMat : obj;
            });
#else
            var hasAmbiguousMaterialCurve = _animatorServicesContext.AnimationIndex
                .GetPPtrReferencedObjects
                .OfType<Material>()
                .Any(mapping.ContainsKey);

            if (hasAmbiguousMaterialCurve)
            {
                throw new InvalidOperationException(
                    "TexTransTool: Renderer-scoped AtlasTexture targeting requires NDMF 1.8.0 or newer when target materials are referenced by animation object curves."
                );
            }
#endif
        }

#if NDMF_1_8_0_OR_NEWER
        private bool BindingTargetsRenderer(
            EditorCurveBinding binding,
            HashSet<Renderer> targetRenderers)
        {
            if (typeof(Renderer).IsAssignableFrom(binding.type) is false) { return false; }

            var targetTransform = string.IsNullOrEmpty(binding.path)
                ? _avatarRoot.transform
                : _avatarRoot.transform.Find(binding.path);

            if (targetTransform == null) { return false; }

            return targetTransform
                .GetComponents(binding.type)
                .OfType<Renderer>()
                .Any(targetRenderers.Contains);
        }
#endif
    }
}
