#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace net.rs64.TexTransTool.Editor
{
    // Editor-only cache. These objects contain names and dimensions, never references
    // to short-lived NDMF build-clone materials, meshes or textures.
    internal sealed class AutoAtlasTextureImageReport
    {
        public string Name = "";
        public string Properties = "";
        public int Width;
        public int Height;
        public long Pixels => (long)Width * Height;
    }

    internal sealed class AutoAtlasTexturePropertyReport
    {
        // Multiple shader properties can reference the same final RenderTexture
        // through TTT's existing FineTuning.ReferenceCopy.
        public string[] PropertyNames = Array.Empty<string>();
        public AutoAtlasTextureImageReport[] SourceTextures = Array.Empty<AutoAtlasTextureImageReport>();
        public AutoAtlasTextureImageReport GeneratedTexture = new();
    }

    internal sealed class AutoAtlasTextureResolutionChangeReport
    {
        public int BeforeWidth;
        public int BeforeHeight;
        public int AfterWidth;
        public int AfterHeight;
        public int TextureCount;
    }

    internal sealed class AutoAtlasTextureGroupReport
    {
        public AutoAtlasTextureResolutionChangeReport[] ResolutionChanges =
            Array.Empty<AutoAtlasTextureResolutionChangeReport>();
        public string[] MaterialNames = Array.Empty<string>();
        public string[] RendererNames = Array.Empty<string>();
        public AutoAtlasTexturePropertyReport[] PropertyChanges = Array.Empty<AutoAtlasTexturePropertyReport>();
        public float TopFreeFraction;
        public int TotalRelocateCount;
        public long RelocationTimeMilliseconds;
        public AutoAtlasTextureImageReport[] SourceTextures = Array.Empty<AutoAtlasTextureImageReport>();
        public AutoAtlasTextureImageReport[] GeneratedTextures = Array.Empty<AutoAtlasTextureImageReport>();
        public long SourcePixels => SourceTextures.Sum(image => image.Pixels);
        public long GeneratedPixels => GeneratedTextures.Sum(image => image.Pixels);
        public double SavedPercentage => SourcePixels == 0 ? 0 :
            (1.0 - (double)GeneratedPixels / SourcePixels) * 100.0;
    }

    internal sealed class AutoAtlasTextureBuildReport
    {
        public string AvatarName = "";
        public DateTime CreatedAt = DateTime.Now;
        public readonly List<AutoAtlasTextureGroupReport> Completed = new();

        public int MaterialCount => Completed.Sum(g => g.MaterialNames.Length);
        public int OriginalTextureCount => Completed.Sum(g => g.SourceTextures.Length);
        public int GeneratedTextureCount => Completed.Sum(g => g.GeneratedTextures.Length);
        public long OriginalPixels => Completed.Sum(g => g.SourcePixels);
        public long GeneratedPixels => Completed.Sum(g => g.GeneratedPixels);
        public double SavedPercentage => OriginalPixels == 0 ? 0 :
            (1.0 - (double)GeneratedPixels / OriginalPixels) * 100.0;
    }

    internal static class AutoAtlasTextureReportStore
    {
        internal static AutoAtlasTextureBuildReport? Latest { get; private set; }

        internal static void Set(AutoAtlasTextureBuildReport report)
        {
            Latest = report;
        }
    }
}
