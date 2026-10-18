#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using net.rs64.TexTransTool.TextureAtlas.AAOCode;
using net.rs64.TexTransTool.Utils;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas
{
    public interface ITTShaderTextureUsageInformation
    {
        void GetMaterialTextureUVUsage(ITTTextureUVUsageWriter writer);
    }
    public interface ITTTextureUVUsageWriter
    {
        // なぜこのあたりの Property の取得にインターフェースを用いさせるかと言うと ... 今後 TTT 内で完全に Unity のマテリアルを捨てる可能性がなくはないから。
        int GetInteger(string propertyName);
        int GetInt(string propertyName);
        float GetFloat(string propertyName);
        Vector4 GetVector(string propertyName);
        bool IsShaderKeywordEnabled(string keywordName);

        void WriteTextureUVUsage(string propertyName, UsageUVChannel uVChannel);
    }
    internal readonly struct TextureUVUsage
    {
        public readonly UsageUVChannel UVChannel;
        public readonly Matrix2x3? UVMatrix;
        public readonly bool UVMatrixExplicitlyReported;

        public TextureUVUsage(
            UsageUVChannel uvChannel,
            Matrix2x3? uvMatrix,
            bool uvMatrixExplicitlyReported = true)
        {
            UVChannel = uvChannel;
            UVMatrix = uvMatrix;
            UVMatrixExplicitlyReported = uvMatrixExplicitlyReported;
        }
    }

    internal interface ITTTextureUVTransformUsageWriter
    {
        void WriteTextureUVUsage(string propertyName, UsageUVChannel uvChannel, Matrix2x3? uvMatrix);
    }

    internal interface ITTTextureOtherUVUsageWriter
    {
        void WriteOtherUVUsage(UsingUVChannels uvChannels);
    }

    public enum UsageUVChannel
    {
        Unknown = 0,

        UV0 = 1,
        UV1 = 2,
        UV2 = 3,
        UV3 = 4,
        UV4 = 5,
        UV5 = 6,
        UV6 = 7,
        UV7 = 8,

        /// <summary>
        /// Sampling that is known not to use mesh UV channels, such as
        /// screen-space or vertex-color-derived coordinates.
        /// </summary>
        NonMesh = 9,
    }
    class MaterialUVUsageProvider :
        ITTTextureUVUsageWriter,
        ITTTextureUVTransformUsageWriter,
        ITTTextureOtherUVUsageWriter
    {
        Material _material;
        readonly HashSet<string> _texturePropertyNames;
        Dictionary<string, TextureUVUsage> _uvUsage = new();
        private UsingUVChannels _otherUVUsage;
        public IReadOnlyDictionary<string, TextureUVUsage> UVUsageWithTransform => _uvUsage;
        internal UsingUVChannels OtherUVUsage => _otherUVUsage;
        public MaterialUVUsageProvider(Material material)
        {
            _material = material;
            _texturePropertyNames = material.GetTexturePropertyNames().ToHashSet();
        }
        public float GetFloat(string propertyName)
        {
            return _material.HasFloat(propertyName) ? _material.GetFloat(propertyName) : 0f;
        }

        public int GetInt(string propertyName)
        {
            return _material.HasInt(propertyName) ? _material.GetInt(propertyName) : 0;
        }

        public int GetInteger(string propertyName)
        {
            return _material.HasInteger(propertyName) ? _material.GetInteger(propertyName) : 0;
        }

        public Vector4 GetVector(string propertyName)
        {
            const string textureSTSuffix = "_ST";
            if (propertyName.EndsWith(textureSTSuffix, StringComparison.Ordinal))
            {
                var texturePropertyName = propertyName.Substring(0, propertyName.Length - textureSTSuffix.Length);
                if (_texturePropertyNames.Contains(texturePropertyName))
                {
                    var scale = _material.GetTextureScale(texturePropertyName);
                    var offset = _material.GetTextureOffset(texturePropertyName);
                    return new Vector4(scale.x, scale.y, offset.x, offset.y);
                }
            }

            return _material.HasVector(propertyName) ? _material.GetVector(propertyName) : Vector4.zero;
        }

        public bool IsShaderKeywordEnabled(string keywordName)
        {
            return _material.IsKeywordEnabled(keywordName);
        }

        public void WriteTextureUVUsage(string propertyName, UsageUVChannel uVChannel)
        {
            if (_material.HasTexture(propertyName) is false) { return; }
            // Preserve the legacy channel-only API's historical identity
            // behavior for manual AtlasTexture while remembering that the
            // transform was not actually reported. AutoAtlas can then fail
            // closed without changing manual AtlasTexture semantics.
            _uvUsage[propertyName] = new TextureUVUsage(
                uVChannel,
                Matrix2x3.Identity,
                uvMatrixExplicitlyReported: false);
        }

        public void WriteTextureUVUsage(string propertyName, UsageUVChannel uVChannel, Matrix2x3? uvMatrix)
        {
            if (_material.HasTexture(propertyName) is false) { return; }
            _uvUsage[propertyName] = new TextureUVUsage(
                uVChannel,
                uvMatrix,
                uvMatrixExplicitlyReported: true);
        }

        public void WriteOtherUVUsage(UsingUVChannels uvChannels)
        {
            _otherUVUsage |= uvChannels;
        }
    }
}
