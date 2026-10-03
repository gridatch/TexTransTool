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

        public TextureUVUsage(UsageUVChannel uvChannel, Matrix2x3? uvMatrix)
        {
            UVChannel = uvChannel;
            UVMatrix = uvMatrix;
        }
    }

    internal interface ITTTextureUVTransformUsageWriter
    {
        void WriteTextureUVUsage(string propertyName, UsageUVChannel uvChannel, Matrix2x3? uvMatrix);
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
    }
    class MaterialUVUsageProvider : ITTTextureUVUsageWriter, ITTTextureUVTransformUsageWriter
    {
        Material _material;
        Dictionary<string, TextureUVUsage> _uvUsage = new();
        public IReadOnlyDictionary<string, TextureUVUsage> UVUsageWithTransform => _uvUsage;
        public MaterialUVUsageProvider(Material material)
        {
            _material = material;
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
            return _material.HasVector(propertyName) ? _material.GetVector(propertyName) : Vector4.zero;
        }

        public bool IsShaderKeywordEnabled(string keywordName)
        {
            return _material.IsKeywordEnabled(keywordName);
        }

        public void WriteTextureUVUsage(string propertyName, UsageUVChannel uVChannel)
        {
            WriteTextureUVUsage(propertyName, uVChannel, Matrix2x3.Identity);
        }

        public void WriteTextureUVUsage(string propertyName, UsageUVChannel uVChannel, Matrix2x3? uvMatrix)
        {
            if (_material.HasTexture(propertyName) is false) { return; }
            _uvUsage[propertyName] = new TextureUVUsage(uVChannel, uvMatrix);
        }
    }
}
