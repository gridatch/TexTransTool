#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            foreach (var texture in textures.Distinct())
            {
                if (!TryGetOriginalTextureCostInfo(
                        domain,
                        texture,
                        out var block,
                        out var mipCount))
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
            out (uint Width, uint Height, uint Size) block,
            out int mipCount)
        {
            block = default;
            mipCount = 1;

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

                    if (!TryGetMemoryBlockLayout(format, out block))
                        return false;

                    // A source Texture2D already has a concrete mip chain.
                    // Using a synthetic full chain can overestimate the
                    // standalone baseline and make an additional merge look
                    // beneficial when it is not.
                    mipCount = GetSourceTextureMipCount(
                        texture2D, descriptor);
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
                        if (!TryGetMemoryBlockLayout(
                                renderTexture.graphicsFormat,
                                out block))
                        {
                            return false;
                        }

                        mipCount = renderTexture.useMipMap
                            ? CalculateMemoryMipCount(
                                renderTexture.width, renderTexture.height)
                            : 1;
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

                    if (!TryGetMemoryBlockLayout(format, out block))
                        return false;

                    mipCount = descriptor.UseMipMap
                        ? CalculateMemoryMipCount(
                            renderTexture.width, renderTexture.height)
                        : 1;
                    return true;
                }

                default:
                    return false;
            }
        }

        internal static int GetSourceTextureMipCount(
            Texture2D texture,
            TexTransToolTextureDescriptor descriptor)
        {
            return descriptor.UseMipMap
                ? Math.Max(1, texture.mipmapCount)
                : 1;
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

            if (!TryGetMemoryBlockLayout(format, out var block))
                return false;

            for (var mip = 0; mip < mipCount; mip += 1)
            {
                var blocksX =
                    ((long)width + block.Width - 1) / block.Width;
                var blocksY =
                    ((long)height + block.Height - 1) / block.Height;
                bytes += blocksX * blocksY * block.Size;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
            }

            return true;
        }

        internal static string CreateTextureMemoryIncreaseDiagnostic(
            IDomain domain,
            AtlasContext atlasContext,
            TexFineTuningResult tuningResult,
            long sourceBytes,
            long outputBytes,
            IEnumerable<string> targetMaterialNames)
        {
            var builder = new StringBuilder();
            builder.AppendLine("AtlasTexture texture-memory increase diagnostic");
            builder.Append("Target materials: ");
            builder.AppendLine(string.Join(", ", targetMaterialNames));
            builder.Append("Total texture memory: ");
            builder.Append(FormatTextureMemory(sourceBytes));
            builder.Append(" (");
            builder.Append(sourceBytes);
            builder.Append(" B) -> ");
            builder.Append(FormatTextureMemory(outputBytes));
            builder.Append(" (");
            builder.Append(outputBytes);
            builder.AppendLine(" B)");

            var sourceTextures = CollectSourceTextures(atlasContext)
                .OrderBy(texture => texture.name, StringComparer.Ordinal)
                .ThenBy(texture => texture.GetInstanceID())
                .ToArray();

            builder.Append("Physical texture count: ");
            builder.Append(sourceTextures.Length);
            builder.Append(" -> ");
            builder.AppendLine(tuningResult.TextureDescriptors.Count.ToString());

            var activeGroupIds = atlasContext.AtlasSubMeshIndexSetCtx
                .AtlasSubMeshIndexIDHash
                .Select(subMesh => subMesh.MaterialGroupID)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var materialGroups = atlasContext.MaterialGroupingCtx.GroupMaterials;

            var sourceUsages = new Dictionary<Texture, HashSet<string>>();
            foreach (var groupId in activeGroupIds)
            {
                var group = materialGroups[groupId];
                foreach (var property in group.GroupedTexture)
                {
                    if (property.Value == null) continue;
                    if (!sourceUsages.TryGetValue(
                            property.Value, out var usages))
                    {
                        usages = new HashSet<string>(StringComparer.Ordinal);
                        sourceUsages.Add(property.Value, usages);
                    }

                    usages.Add("g" + groupId + ":" + property.Key);
                }
            }

            builder.AppendLine();
            builder.AppendLine("[source physical textures]");
            foreach (var sourceTexture in sourceTextures)
            {
                builder.Append("- ");
                builder.Append(sourceTexture.name);
                builder.Append(" [instanceID=");
                builder.Append(sourceTexture.GetInstanceID());
                builder.AppendLine("]");
                builder.Append("  resolution: ");
                builder.Append(sourceTexture.width);
                builder.Append("x");
                builder.AppendLine(sourceTexture.height.ToString());

                if (TryGetSourceTextureDiagnosticDetail(
                        domain,
                        sourceTexture,
                        out var sourceFormat,
                        out var sourceMipCount,
                        out var sourceTextureBytes))
                {
                    builder.Append("  format: ");
                    builder.AppendLine(sourceFormat);
                    builder.Append("  mip count: ");
                    builder.AppendLine(sourceMipCount.ToString());
                    builder.Append("  resident memory: ");
                    builder.Append(FormatTextureMemory(sourceTextureBytes));
                    builder.Append(" (");
                    builder.Append(sourceTextureBytes);
                    builder.AppendLine(" B)");
                }
                else
                {
                    builder.AppendLine("  format/mip/memory: unknown");
                }

                if (sourceUsages.TryGetValue(sourceTexture, out var usages)
                    && usages.Count != 0)
                {
                    builder.Append("  usages: ");
                    builder.AppendLine(string.Join(", ",
                        usages.OrderBy(value => value, StringComparer.Ordinal)));
                    builder.Append("  distinct properties: ");
                    builder.AppendLine(
                        usages.Select(usage =>
                                usage.Substring(usage.IndexOf(':') + 1))
                            .Distinct(StringComparer.Ordinal)
                            .Count()
                            .ToString());
                }
                else
                {
                    builder.AppendLine("  usages: unknown");
                }
            }

            builder.AppendLine();
            builder.AppendLine("[final physical textures]");
            var alphaCache = new Dictionary<Texture, bool>();
            foreach (var output in tuningResult.TextureDescriptors
                         .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
            {
                var aliases = tuningResult.RenderTextures
                    .Where(pair => ReferenceEquals(pair.Value, output.Key))
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                var sourceProperties = aliases
                    .SelectMany(alias =>
                        tuningResult.SourceProperties.TryGetValue(
                            alias, out var properties)
                            ? properties
                            : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                builder.Append("- ");
                builder.AppendLine(output.Key.Name);
                builder.Append("  properties: ");
                builder.AppendLine(
                    aliases.Length == 0 ? "(none)" : string.Join(", ", aliases));
                builder.Append("  source properties: ");
                builder.AppendLine(
                    sourceProperties.Length == 0
                        ? "(none)"
                        : string.Join(", ", sourceProperties));
                builder.Append("  resolution: ");
                builder.Append(output.Key.Width);
                builder.Append("x");
                builder.AppendLine(output.Key.Hight.ToString());

                if (TryGetFinalTextureDiagnosticDetail(
                        atlasContext,
                        tuningResult,
                        output.Key,
                        output.Value,
                        activeGroupIds,
                        alphaCache,
                        out var outputFormat,
                        out var outputMipCount,
                        out var outputTextureBytes))
                {
                    builder.Append("  format: ");
                    builder.AppendLine(outputFormat);
                    builder.Append("  mip count: ");
                    builder.AppendLine(outputMipCount.ToString());
                    builder.Append("  resident memory: ");
                    builder.Append(FormatTextureMemory(outputTextureBytes));
                    builder.Append(" (");
                    builder.Append(outputTextureBytes);
                    builder.AppendLine(" B)");
                }
                else
                {
                    builder.AppendLine("  format/mip/memory: unknown");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static bool TryGetSourceTextureDiagnosticDetail(
            IDomain domain,
            Texture texture,
            out string format,
            out int mipCount,
            out long bytes)
        {
            format = "unknown";
            mipCount = 1;
            bytes = 0;

            if (!TryGetOriginalTextureCostInfo(
                    domain,
                    texture,
                    out var block,
                    out mipCount))
            {
                return false;
            }

            switch (texture)
            {
                case Texture2D texture2D:
                {
                    try
                    {
                        var descriptor = domain.GetTextureDescriptor(texture2D);
                        format = descriptor.TextureFormat
                            .Get(texture2D).CompressFormat.ToString();
                    }
                    catch
                    {
                        return false;
                    }
                    break;
                }

                case RenderTexture renderTexture:
                {
                    try
                    {
                        var descriptor = domain.GetTextureDescriptor(renderTexture);
                        if (descriptor.TextureFormat
                                is ITexTransToolTextureFormatMemoryEstimate resolver
                            && resolver.TryGetMemoryEstimateFormat(
                                renderTexture,
                                hasAlpha: false,
                                out var estimatedFormat))
                        {
                            format = estimatedFormat.ToString();
                        }
                        else
                        {
                            format = renderTexture.graphicsFormat.ToString();
                        }
                    }
                    catch
                    {
                        format = renderTexture.graphicsFormat.ToString();
                    }
                    break;
                }

                default:
                    return false;
            }

            bytes = CalculateTextureMemory(
                texture.width,
                texture.height,
                block,
                mipCount);
            return true;
        }

        private static bool TryGetFinalTextureDiagnosticDetail(
            AtlasContext atlasContext,
            TexFineTuningResult tuningResult,
            ITTRenderTexture outputTexture,
            TexTransToolTextureDescriptor descriptor,
            IReadOnlyCollection<int> activeGroupIds,
            Dictionary<Texture, bool> alphaCache,
            out string format,
            out int mipCount,
            out long bytes)
        {
            format = "unknown";
            mipCount = 1;
            bytes = 0;

            if (descriptor.TextureFormat
                is not TextureCompressionData compression)
            {
                return false;
            }

            var aliases = tuningResult.RenderTextures
                .Where(pair => ReferenceEquals(pair.Value, outputTexture))
                .Select(pair => pair.Key)
                .ToArray();
            var sourceProperties = aliases
                .SelectMany(alias =>
                    tuningResult.SourceProperties.TryGetValue(
                        alias, out var properties)
                        ? properties
                        : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var sourceTextures = new HashSet<Texture>();
            var materialGroups = atlasContext.MaterialGroupingCtx.GroupMaterials;
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
                        sourceTexture,
                        alphaCache,
                        out var sourceHasAlpha))
                {
                    return false;
                }

                if (!sourceHasAlpha) continue;
                hasAlpha = true;
                break;
            }

            var outputFormat = compression.UseOverride
                ? compression.OverrideTextureFormat
                : TextureCompressionData.GetQuality2TextureFormat(
                    compression.FormatQualityValue, hasAlpha);
            mipCount = descriptor.UseMipMap
                ? CalculateMemoryMipCount(
                    outputTexture.Width, outputTexture.Hight)
                : 1;

            if (!TryCalculateTextureMemory(
                    outputTexture.Width,
                    outputTexture.Hight,
                    outputFormat,
                    mipCount,
                    out bytes))
            {
                return false;
            }

            format = outputFormat.ToString();
            return true;
        }

        private static long CalculateTextureMemory(
            int width,
            int height,
            (uint Width, uint Height, uint Size) block,
            int mipCount)
        {
            long bytes = 0;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            mipCount = Math.Max(1, mipCount);

            for (var mip = 0; mip < mipCount; mip += 1)
            {
                var blocksX =
                    ((long)width + block.Width - 1) / block.Width;
                var blocksY =
                    ((long)height + block.Height - 1) / block.Height;
                bytes += blocksX * blocksY * block.Size;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
            }

            return bytes;
        }

        internal static bool TryGetMemoryBlockLayout(
            TextureFormat format,
            out (uint Width, uint Height, uint Size) block)
        {
            block = default;
            try
            {
                if (!Enum.IsDefined(typeof(TextureFormat), format))
                    return false;

                var width = GraphicsFormatUtility.GetBlockWidth(format);
                var height = GraphicsFormatUtility.GetBlockHeight(format);
                var size = GraphicsFormatUtility.GetBlockSize(format);
                if (width == 0 || height == 0 || size == 0)
                    return false;

                block = (width, height, size);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryGetMemoryBlockLayout(
            GraphicsFormat format,
            out (uint Width, uint Height, uint Size) block)
        {
            block = default;
            try
            {
                if (!Enum.IsDefined(typeof(GraphicsFormat), format)
                    || format == GraphicsFormat.None)
                {
                    return false;
                }

                var width = GraphicsFormatUtility.GetBlockWidth(format);
                var height = GraphicsFormatUtility.GetBlockHeight(format);
                var size = GraphicsFormatUtility.GetBlockSize(format);
                if (width == 0 || height == 0 || size == 0)
                    return false;

                block = (width, height, size);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class AtlasTextureMemoryIncreaseDiagnosticCollector
    {
        internal sealed class Entry
        {
            internal readonly AtlasTexture Context;
            internal readonly string Message;

            internal Entry(AtlasTexture context, string message)
            {
                Context = context;
                Message = message;
            }
        }

        private static readonly List<Entry> Entries = new();

        internal static void Clear()
        {
            Entries.Clear();
        }

        internal static void Add(AtlasTexture context, string message)
        {
            Entries.Add(new Entry(context, message));
        }

        internal static Entry[] Drain()
        {
            var result = Entries.ToArray();
            Entries.Clear();
            return result;
        }
    }
}
