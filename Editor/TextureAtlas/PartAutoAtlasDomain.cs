#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Owns temporary objects created by standalone AutoAtlas execution until
    /// the Part workflow persists the final reachable result.
    /// </summary>
    internal sealed class PartAutoAtlasAssetStore : IAssetSaver, IDisposable
    {
        private readonly HashSet<Object> _temporaryAssets = new();

        internal readonly HashSet<Mesh> GeneratedMeshes = new();
        internal readonly HashSet<Material> GeneratedMaterials = new();
        internal readonly HashSet<Texture2D> GeneratedTextures = new();

        public bool IsTemporaryAsset(Object asset)
        {
            return asset != null && _temporaryAssets.Contains(asset);
        }

        public void TransferAsset(Object asset)
        {
            if (asset == null || AssetDatabase.Contains(asset))
                return;

            Track(asset);
        }

        internal void TrackGeneratedMaterials(IEnumerable<Material> materials)
        {
            foreach (var material in materials)
            {
                if (material == null || AssetDatabase.Contains(material))
                    continue;

                Track(material);
            }
        }

        private void Track(Object asset)
        {
            if (!_temporaryAssets.Add(asset))
                return;

            switch (asset)
            {
                case Mesh mesh:
                    GeneratedMeshes.Add(mesh);
                    break;
                case Material material:
                    GeneratedMaterials.Add(material);
                    break;
                case Texture2D texture:
                    GeneratedTextures.Add(texture);
                    break;
            }
        }

        public void Dispose()
        {
            foreach (var asset in _temporaryAssets.ToArray())
            {
                if (asset != null && !AssetDatabase.Contains(asset))
                    Object.DestroyImmediate(asset);
            }

            _temporaryAssets.Clear();
            GeneratedMeshes.Clear();
            GeneratedMaterials.Clear();
            GeneratedTextures.Clear();
        }
    }

    /// <summary>
    /// Real-mutation domain used by standalone Part AutoAtlas. Unlike
    /// NotWorkDomain it applies Mesh/Material replacements to the temporary
    /// hierarchy and delays texture compression until after PNG persistence.
    /// </summary>
    internal sealed class PartAutoAtlasDomain : RenderersDomain
    {
        private readonly PartAutoAtlasAssetStore _assetStore;
        private readonly Dictionary<Material, HashSet<Material>>
            _materialOrigins = new();
        private bool _disposed;

        internal PartAutoAtlasDomain(
            GameObject root,
            PartAutoAtlasAssetStore assetStore)
            : base(
                root.GetComponentsInChildren<Renderer>(true).ToList(),
                assetStore)
        {
            _assetStore = assetStore;
        }

        protected override bool CompressDownloadedTexturesOnDispose => false;

        public override void ReplaceMaterials(
            Dictionary<Material, Material> mapping)
        {
            var incoming = mapping
                .Where(pair =>
                    pair.Key != null
                    && pair.Value != null
                    && pair.Key != pair.Value)
                .GroupBy(pair => pair.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(pair => pair.Key)
                        .Distinct()
                        .ToArray());

            HashSet<Material> ResolveOrigins(
                Material material,
                HashSet<Material> visiting)
            {
                if (!visiting.Add(material))
                    return new HashSet<Material> { material };

                var resolved = new HashSet<Material>();
                if (_materialOrigins.TryGetValue(
                        material, out var knownOrigins))
                {
                    resolved.UnionWith(knownOrigins);
                }

                if (incoming.TryGetValue(
                        material, out var sourceMaterials))
                {
                    foreach (var source in sourceMaterials)
                    {
                        resolved.UnionWith(
                            ResolveOrigins(
                                source,
                                new HashSet<Material>(visiting)));
                    }
                }

                if (resolved.Count == 0)
                    resolved.Add(material);

                return resolved;
            }

            foreach (var pair in mapping)
            {
                if (pair.Key == null || pair.Value == null)
                    continue;

                if (!_materialOrigins.TryGetValue(
                        pair.Value, out var destinationOrigins))
                {
                    destinationOrigins = new HashSet<Material>();
                    _materialOrigins[pair.Value] = destinationOrigins;
                }

                destinationOrigins.UnionWith(
                    ResolveOrigins(
                        pair.Key,
                        new HashSet<Material>()));
            }

            _assetStore.TrackGeneratedMaterials(mapping.Values);
            base.ReplaceMaterials(mapping);
        }

        internal IReadOnlyCollection<Material> GetOriginMaterials(
            Material material)
        {
            if (material != null
                && _materialOrigins.TryGetValue(
                    material, out var origins)
                && origins.Count != 0)
            {
                return origins;
            }

            return material == null
                ? Array.Empty<Material>()
                : new[] { material };
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            base.Dispose();
        }
    }
}
