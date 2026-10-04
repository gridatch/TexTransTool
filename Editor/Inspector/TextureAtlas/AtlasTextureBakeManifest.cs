#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    internal sealed class AtlasTextureBakeManifest : ScriptableObject
    {
        internal const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public string BakeName = "";
        public List<Entry> Entries = new();

        [Serializable]
        public sealed class Entry
        {
            public string Role = "";
            public string AssetPath = "";
        }
    }
}
