#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransCore;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public sealed partial class AtlasTexture
    {
        internal static HashSet<Texture> CollectSourceTextures(
            AtlasContext atlasContext)
        {
            return atlasContext.MaterialGroupingCtx.ContainsTextureDictionaries
                .Values
                .SelectMany(properties => properties.Values)
                .Where(texture => texture != null)
                .ToHashSet();
        }

        internal static bool TryCalculateSourceTextureMemory(
            IDomain domain,
            IEnumerable<Texture> textures,
            out long bytes)
        {
            bytes = 0;
            var alphaCache = new Dictionary<Texture, bool>();

            foreach (var texture in textures.Distinct())
            {
                if (!TryGetOriginalTextureCostInfo(
                        domain,
                        texture,
                        alphaCache,
                        out var block,
                        out var mipCount,
                        out _))
                {
                    return false;
                }

                var width = Math.Max(1, texture.width);
                var height = Math.Max(1, texture.height);
                for (var mip = 0; mip < Math.Max(1, mipCount); mip += 1)
                {
                    var blocksX =
                        ((long)width + block.Width - 1) / block.Width;
                    var blocksY =
                        ((long)height + block.Height - 1) / block.Height;
                    bytes += blocksX * blocksY * block.Size;

                    width = Math.Max(1, width >> 1);
                    height = Math.Max(1, height >> 1);
                }
            }

            return true;
        }

        internal static bool TryCalculateFinalTextureMemory(
            AtlasContext atlasContext,
            TexFineTuningResult tuningResult,
            out long bytes)
        {
            bytes = 0;
            var alphaCache = new Dictionary<Texture, bool>();
            var activeGroupIds = atlasContext.AtlasSubMeshIndexSetCtx
                .AtlasSubMeshIndexIDHash
                .Select(subMesh => subMesh.MaterialGroupID)
                .Distinct()
                .ToArray();
            var materialGroups = atlasContext.MaterialGroupingCtx.GroupMaterials;

            foreach (var output in tuningResult.TextureDescriptors)
            {
                if (output.Value.TextureFormat
                    is not TextureCompressionData compression)
                {
                    return false;
                }

                var sourceProperties = tuningResult.RenderTextures
                    .Where(pair => ReferenceEquals(pair.Value, output.Key))
                    .SelectMany(pair =>
                        tuningResult.SourceProperties.TryGetValue(
                            pair.Key, out var properties)
                            ? properties
                            : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var sourceTextures = new HashSet<Texture>();
                foreach (var property in sourceProperties)
                {
                    foreach (var groupId in activeGroupIds)
                    {
                        var materialGroup = materialGroups[groupId];
                        if (materialGroup.GroupedTexture.TryGetValue(
                                property, out var sourceTexture)
                            && sourceTexture != null)
                        {
                            sourceTextures.Add(sourceTexture);
                        }
                    }
                }

                var hasAlpha = sourceTextures.Count == 0;
                foreach (var sourceTexture in sourceTextures)
                {
                    if (!TryHasAlpha(
                            sourceTexture, alphaCache,
                            out var sourceHasAlpha))
                    {
                        return false;
                    }

                    if (!sourceHasAlpha) continue;
                    hasAlpha = true;
                    break;
                }

                var format = compression.UseOverride
                    ? compression.OverrideTextureFormat
                    : TextureCompressionData.GetQuality2TextureFormat(
                        compression.FormatQualityValue, hasAlpha);
                var mipCount = output.Value.UseMipMap
                    ? CalculateMemoryMipCount(output.Key.Width, output.Key.Hight)
                    : 1;

                if (!TryCalculateTextureMemory(
                        output.Key.Width,
                        output.Key.Hight,
                        format,
                        mipCount,
                        out var outputBytes))
                {
                    return false;
                }

                bytes += outputBytes;
            }

            return true;
        }

        internal static bool TryGetOriginalTextureCostInfo(
            IDomain domain,
            Texture texture,
            Dictionary<Texture, bool> alphaCache,
            out (uint Width, uint Height, uint Size) block,
            out int mipCount,
            out bool streaming)
        {
            block = default;
            mipCount = 1;
            streaming = false;

            switch (texture)
            {
                case Texture2D texture2D:
                {
                    TexTransToolTextureDescriptor descriptor;
                    try
                    {
                        descriptor = domain.GetTextureDescriptor(texture2D);
                    }
                    catch
                    {
                        return false;
                    }

                    TextureFormat format;
                    try
                    {
                        format = descriptor.TextureFormat
                            .Get(texture2D).CompressFormat;
                    }
                    catch
                    {
                        return false;
                    }

                    block = GetMemoryBlockLayout(format);
                    mipCount = descriptor.UseMipMap
                        ? CalculateMemoryMipCount(
                            texture2D.width, texture2D.height)
                        : 1;
                    streaming = mipCount > 1;
                    return true;
                }

                case RenderTexture renderTexture:
                {
                    TexTransToolTextureDescriptor? descriptor = null;
                    try
                    {
                        descriptor = domain.GetTextureDescriptor(renderTexture);
                    }
                    catch
                    {
                        // Unregistered RenderTextures are represented by their
                        // current graphics format.
                    }

                    if (descriptor == null)
                    {
                        block = GetMemoryBlockLayout(renderTexture.graphicsFormat);
                        mipCount = renderTexture.useMipMap
                            ? CalculateMemoryMipCount(
                                renderTexture.width, renderTexture.height)
                            : 1;
                        streaming = false;
                        return true;
                    }

                    if (descriptor.TextureFormat
                            is not ITexTransToolTextureFormatMemoryEstimate resolver
                        || !resolver.TryGetMemoryEstimateFormat(
                            renderTexture,
                            hasAlpha: false,
                            out var format))
                    {
                        return false;
                    }

                    block = GetMemoryBlockLayout(format);
                    mipCount = descriptor.UseMipMap
                        ? CalculateMemoryMipCount(
                            renderTexture.width, renderTexture.height)
                        : 1;
                    streaming = mipCount > 1;
                    return true;
                }

                default:
                    return false;
            }
        }

        internal static string FormatTextureMemory(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024d).ToString("0.0") + " KiB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0") + " MiB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.0") + " GiB";
        }

        private static bool TryHasAlpha(
            Texture texture,
            Dictionary<Texture, bool> cache,
            out bool hasAlpha)
        {
            if (cache.TryGetValue(texture, out hasAlpha))
                return true;

            if (texture is RenderTexture renderTexture)
            {
                hasAlpha = GraphicsFormatUtility.HasAlphaChannel(
                    renderTexture.graphicsFormat);
                cache[texture] = hasAlpha;
                return true;
            }

            if (texture is not Texture2D texture2D)
            {
                hasAlpha = false;
                return false;
            }

            try
            {
                using var result =
                    TextureCompressionData.HasAlphaChannel(texture2D);
                hasAlpha = result.GetResult();
                cache[texture] = hasAlpha;
                return true;
            }
            catch
            {
                hasAlpha = false;
                return false;
            }
        }

        private static int CalculateMemoryMipCount(int width, int height)
        {
            var count = 1;
            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
                count += 1;
            }

            return count;
        }

        private static bool TryCalculateTextureMemory(
            int width,
            int height,
            TextureFormat format,
            int mipCount,
            out long bytes)
        {
            bytes = 0;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            mipCount = Math.Max(1, mipCount);

            try
            {
                var blockWidth = Math.Max(
                    1u, GraphicsFormatUtility.GetBlockWidth(format));
                var blockHeight = Math.Max(
                    1u, GraphicsFormatUtility.GetBlockHeight(format));
                var blockSize = Math.Max(
                    1u, GraphicsFormatUtility.GetBlockSize(format));

                for (var mip = 0; mip < mipCount; mip += 1)
                {
                    var blocksX =
                        ((long)width + blockWidth - 1) / blockWidth;
                    var blocksY =
                        ((long)height + blockHeight - 1) / blockHeight;
                    bytes += blocksX * blocksY * blockSize;

                    width = Math.Max(1, width >> 1);
                    height = Math.Max(1, height >> 1);
                }

                return true;
            }
            catch
            {
                bytes = 0;
                return false;
            }
        }

        private static (uint Width, uint Height, uint Size)
            GetMemoryBlockLayout(TextureFormat format)
        {
            try
            {
                return (
                    Math.Max(1u, GraphicsFormatUtility.GetBlockWidth(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockHeight(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockSize(format)));
            }
            catch
            {
                return (1u, 1u, 4u);
            }
        }

        private static (uint Width, uint Height, uint Size)
            GetMemoryBlockLayout(GraphicsFormat format)
        {
            try
            {
                return (
                    Math.Max(1u, GraphicsFormatUtility.GetBlockWidth(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockHeight(format)),
                    Math.Max(1u, GraphicsFormatUtility.GetBlockSize(format)));
            }
            catch
            {
                return (1u, 1u, 4u);
            }
        }
    }
}
