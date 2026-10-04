#nullable enable
#if CONTAINS_MA

using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using nadena.dev.ndmf;
using nadena.dev.modular_avatar.core;

namespace net.rs64.TexTransTool.NDMF.AdditionalMaterials
{
    internal class MAMaterialsProvider : IAdditionalMaterialsProvider
    {
        private readonly ModularAvatarMaterialSetter[] _setters;

        public MAMaterialsProvider(BuildContext context)
        {
            _setters = context.AvatarRootObject
                .GetComponentsInChildren<ModularAvatarMaterialSetter>(true);
        }

        public HashSet<Material> GetReferencedMaterials()
        {
            return _setters
                .SelectMany(setter => setter.Objects)
                .Select(obj => obj?.Material)
                .UOfType<Material>()
                .ToHashSet();
        }

        public void ReplaceReferencedMaterials(
            Dictionary<Material, Material> mapping,
            IReadOnlyCollection<Renderer>? targetRenderers = null)
        {
            var targetGameObjects = targetRenderers?
                .Select(renderer => renderer.gameObject)
                .ToHashSet();

            foreach (var setter in _setters)
            {
                foreach (var obj in setter.Objects)
                {
                    if (obj == null || obj.Material == null) { continue; }

                    if (targetGameObjects != null)
                    {
                        var targetObject = obj.Object?.Get(setter);
                        if (targetObject == null || targetGameObjects.Contains(targetObject) is false)
                        {
                            continue;
                        }
                    }

                    if (mapping.TryGetValue(obj.Material, out var newMaterial))
                    {
                        obj.Material = newMaterial;
                    }
                }
            }
        }
    }   
}

#endif
