#nullable enable
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.Collections;
using System;
using net.rs64.TexTransTool.Utils;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal class MaterialGroupingContext
    {
        public readonly GroupMaterial[] GroupMaterials;
        public readonly IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> ContainsTextureDictionaries;

        public readonly string? PrimaryTexturePropertyOrMaximum;
        private readonly IReadOnlyDictionary<Material, Vector2Int?>?
            _primaryDensitySizesByMaterial;

        public MaterialGroupingContext(
            HashSet<Material> targetMaterials,
            UVChannel atlasingTargetUVChannel,
            string? primaryTexturePropertyOrMaximum)
            : this(
                BuildContainsTextureDictionaries(
                    targetMaterials,
                    atlasingTargetUVChannel),
                primaryTexturePropertyOrMaximum,
                null)
        {
        }

        internal MaterialGroupingContext(
            HashSet<Material> targetMaterials,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>>
                precomputedContainsTextureDictionaries,
            string? primaryTexturePropertyOrMaximum,
            IReadOnlyDictionary<Material, Vector2Int?>?
                primaryDensitySizesByMaterial = null)
            : this(
                targetMaterials.ToDictionary(
                    material => material,
                    material =>
                        precomputedContainsTextureDictionaries.TryGetValue(
                            material, out var textures)
                            ? textures
                            : (IReadOnlyDictionary<string, Texture>)
                                new Dictionary<string, Texture>()),
                primaryTexturePropertyOrMaximum,
                primaryDensitySizesByMaterial)
        {
        }

        private MaterialGroupingContext(
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>>
                containsTextureDictionaries,
            string? primaryTexturePropertyOrMaximum,
            IReadOnlyDictionary<Material, Vector2Int?>?
                primaryDensitySizesByMaterial)
        {
            ContainsTextureDictionaries = containsTextureDictionaries;
            GroupMaterials = BuildGroups(containsTextureDictionaries);
            PrimaryTexturePropertyOrMaximum =
                primaryTexturePropertyOrMaximum;
            _primaryDensitySizesByMaterial =
                primaryDensitySizesByMaterial;
        }

        internal static IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>>
            BuildContainsTextureDictionaries(
                HashSet<Material> targetMaterials,
                UVChannel atlasingTargetUVChannel)
        {
            return targetMaterials
                .Select(m => (
                    m,
                    Usage: TTShaderTextureUsageInformationUtil
                        .GetContainsUVTransformUsage(m),
                    TextureProperties: m.GetTexturePropertyNames().ToHashSet()
                ))
                .Select(kv => (
                    kv.m,
                    kv.Usage
                        .Where(u =>
                            (((int)u.Value.UVChannel) - 1)
                                == (int)atlasingTargetUVChannel)
                        .Where(u =>
                            TTShaderTextureUsageInformationUtil
                                .RequiresBump2ndOriginalUVPreservation(
                                    u.Key,
                                    u.Value,
                                    atlasingTargetUVChannel) is false)
                        .Where(u => kv.TextureProperties.Contains(u.Key))
                        .Select(u => (u.Key, kv.m.GetTexture(u.Key)))
                        .Where(u => u.Item2 != null)
                        .ToDictionary(
                            u => u.Key,
                            u => u.Item2
                        )
                    )
                )
                .ToDictionary(
                    kv => kv.m,
                    kv => kv.Item2
                        as IReadOnlyDictionary<string, Texture>);
        }

        private static GroupMaterial[] BuildGroups(
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>>
                containsTextureDictionaries)
        {
            var groupMaterials = new List<GroupMaterial>();
            foreach (var (keyMat, containsTextures)
                     in containsTextureDictionaries)
            {
                var added = false;
                foreach (var group in groupMaterials)
                {
                    if (group.Add(keyMat, containsTextures)
                        is GroupMaterial.AddResult.Success
                        or GroupMaterial.AddResult.Already)
                    {
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    groupMaterials.Add(
                        new GroupMaterial
                        {
                            { keyMat, containsTextures },
                        });
                }
            }

            return groupMaterials.ToArray();
        }


        public int GetMaterialGroupID(Material material)
        {
            return Array.FindIndex(GroupMaterials, i => i.Contains(material));
        }
        public int GetMaterialGroupID(GroupMaterial group)
        {
            return Array.IndexOf(GroupMaterials, group);
        }
        public Texture? GetPrimaryTexture(int groupID)
        {
            return GroupMaterials[groupID].GetPrimaryTexture(
                PrimaryTexturePropertyOrMaximum);
        }

        internal Vector2Int? GetPrimaryDensitySize(int groupID)
        {
            var primary = GetPrimaryTexture(groupID);
            Vector2Int? result = primary == null
                ? null
                : new Vector2Int(primary.width, primary.height);

            if (_primaryDensitySizesByMaterial == null)
                return result;

            foreach (var material in GroupMaterials[groupID])
            {
                if (!_primaryDensitySizesByMaterial.TryGetValue(
                        material, out var preserved)
                    || !preserved.HasValue)
                    continue;

                if (!result.HasValue)
                {
                    result = preserved;
                    continue;
                }

                result = new Vector2Int(
                    Mathf.Max(result.Value.x, preserved.Value.x),
                    Mathf.Max(result.Value.y, preserved.Value.y));
            }

            return result;
        }

        public HashSet<string> GetContainsAllProperties()
        {
            return GroupMaterials.SelectMany(i => i.GroupedTexture.Keys).ToHashSet();
        }


        public class GroupMaterial : IEnumerable<Material>
        {
            Dictionary<Material, IReadOnlyDictionary<string, Texture>> _containsTextures = new();
            Dictionary<string, Texture> _groupedTexture = new();

            public int Count => _containsTextures.Count;

            public AddResult Add(Material mat, IReadOnlyDictionary<string, Texture> textures)
            {
                if (_containsTextures.ContainsKey(mat)) { return AddResult.Already; } // already
                if (IsGroupCompatible(textures) is false) { return AddResult.NotCompatible; }

                _containsTextures[mat] = textures;
                foreach (var texKV in textures)
                {
                    if (texKV.Value == null) { continue; }
                    _groupedTexture[texKV.Key] = texKV.Value;
                }
                return AddResult.Success;
            }
            bool IsGroupCompatible(IReadOnlyDictionary<string, Texture> textures)
            {
                foreach (var propName in _groupedTexture.Keys)
                {
                    var groupTex = _groupedTexture[propName];
                    var addMaterialTex = textures.GetValueOrDefault(propName);

                    if (addMaterialTex == null) { continue; }
                    if (groupTex != addMaterialTex) { return false; }
                }
                return true;
            }

            public enum AddResult
            {
                Success,
                NotCompatible,
                Already,

            }
            public IEnumerator<Material> GetEnumerator() { return _containsTextures.Keys.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() { return _containsTextures.Keys.GetEnumerator(); }

            public bool Contains(Material? mat)
            {
                if (mat == null) { return false; }
                return _containsTextures.ContainsKey(mat);
            }

            public Texture? GetPrimaryTexture(string? primaryTexturePropertyOrMaximum)
            {
                if (primaryTexturePropertyOrMaximum is null) { return GetMaximum(); }
                if (_groupedTexture.TryGetValue(primaryTexturePropertyOrMaximum, out var tex)) { return tex; }
                return null;

                Texture? GetMaximum()
                {
                    Texture? tex = null;
                    int maxValue = 0;
                    foreach (var m in _groupedTexture.Values)
                    {
                        if (m == null) { continue; }
                        if (tex == null)
                        {
                            tex = m;
                            maxValue = tex.height * tex.width;
                            continue;
                        }

                        var mSize = m.height * m.width;
                        if (maxValue < mSize)
                        {
                            tex = m;
                            maxValue = mSize;
                        }
                    }
                    return tex;
                }

            }

            public IReadOnlyDictionary<string, Texture> GroupedTexture => _groupedTexture;
        }

    }
}
