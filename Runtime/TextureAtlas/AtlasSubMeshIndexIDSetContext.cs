#nullable enable
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

namespace net.rs64.TexTransTool.TextureAtlas
{
    internal class AtlasSubMeshIndexIDSetContext
    {
        public HashSet<AtlasSubMeshIndexID> AtlasSubMeshIndexIDHash;
        public List<AtlasSubMeshIndexID?[]> AtlasSubSets;
        public AtlasSubMeshIndexIDSetContext(
            IRendererTargeting targeting
            , Renderer[] targetRenderers
            , HashSet<Material> targetMaterials
            , AtlasMeshSourceContext atlasMeshSourceContext
            , MaterialGroupingContext materialGroupingContext
            )
        {
            AtlasSubMeshIndexIDHash = new();
            AtlasSubSets = new();// レンダラー - マテリアルスロット(SubMesh) - nullable はターゲットではないマテリアルの分
            for (var rendererIndex = 0; targetRenderers.Length > rendererIndex; rendererIndex += 1)
            {
                var renderer = targetRenderers[rendererIndex];

                var mats = targeting.GetMaterials(renderer);
                var mesh = targeting.GetMesh(renderer)!;
                var meshID = atlasMeshSourceContext.Normalized2MeshID[atlasMeshSourceContext.Origin2NormalizedMesh[mesh!]];

                var atlasSubSet = BuildAtlasSubSet(
                    mesh,
                    mats,
                    meshID,
                    targetMaterials,
                    materialGroupingContext);

                foreach (var atSubData in atlasSubSet
                             .Where(item => item.HasValue)
                             .Select(item => item!.Value))
                {
                    AtlasSubMeshIndexIDHash.Add(atSubData);
                }

                // A renderer can reference a target material only on an empty
                // effective submesh. Such a renderer has no UVs to rewrite and
                // therefore must not create an output-mesh subset. Leaving an
                // all-null subset here makes GenerateAtlasedMesh.First(...) fail.
                if (atlasSubSet.Any(item => item.HasValue))
                    AtlasSubSets.Add(atlasSubSet);
            }

            IdenticalSubSetRemove(AtlasSubSets);
        }



        internal static AtlasSubMeshIndexID?[] BuildAtlasSubSet(
            Mesh mesh,
            Material?[] materials,
            int meshID,
            HashSet<Material>? targetMaterials,
            MaterialGroupingContext materialGroupingContext)
        {
            var atlasSubSet =
                new AtlasSubMeshIndexID?[materials.Length];

            // MeshRenderer semantics repeat the final submesh when there are
            // more material slots than submeshes, so keep the existing clamp.
            // With no submeshes there is no geometry to atlas.
            if (mesh.subMeshCount == 0)
                return atlasSubSet;

            for (var subMeshIndex = 0;
                 subMeshIndex < materials.Length;
                 subMeshIndex += 1)
            {
                var material = materials[subMeshIndex];
                if (material == null)
                    continue;
                if (targetMaterials != null
                    && !targetMaterials.Contains(material))
                    continue;

                var materialGroupID =
                    materialGroupingContext.GetMaterialGroupID(material);
                if (materialGroupID == -1)
                    continue;

                var effectiveSubMeshIndex = Math.Clamp(
                    subMeshIndex, 0, mesh.subMeshCount - 1);
                if (mesh.GetSubMesh(effectiveSubMeshIndex).indexCount == 0)
                    continue;

                atlasSubSet[subMeshIndex] =
                    new AtlasSubMeshIndexID(
                        meshID,
                        subMeshIndex,
                        materialGroupID);
            }

            return atlasSubSet;
        }



        /*
        AtlasSubSetは、AtlasSubDataの一つの塊みたいな扱いで、 出力されるメッシュに相当する。

        SubMeshが多い分には問題がないから、
        同一メッシュを参照し、マテリアルがそれぞれ

        ABC
        ABCDE

        のようなものの場合同一のものにすべきなため、同一サブセット扱いを行う。

        */
        static void IdenticalSubSetRemove(List<AtlasSubMeshIndexID?[]> atlasSubSets)
        {
            while (true)
            {
                var identicalSubset = FindIdentical(atlasSubSets);
                if (identicalSubset is null) { return; }
                atlasSubSets.RemoveAt(identicalSubset.Value);
            }
        }
        static int? FindIdentical(List<AtlasSubMeshIndexID?[]> atlasSubSets)
        {
            var assCount = atlasSubSets.Count;
            for (var i = 0; assCount > i; i += 1)
            {
                for (var i2 = i + 1; assCount > i2; i2 += 1)
                {
                    var subSet = atlasSubSets[i];
                    var subSet2 = atlasSubSets[i2];

                    if (subSet.Length == subSet2.Length && subSet.SequenceEqual(subSet2)) { return i; }
                    if (SubPartEqual(subSet, subSet2))
                    {
                        if (subSet.Length > subSet2.Length) { return i2; }
                        else { return i; }
                    }
                }
            }
            return null;
        }
        internal static bool SubPartEqual(AtlasSubMeshIndexID?[] fSubSet, AtlasSubMeshIndexID?[] tSubSet)
        {
            var minCount = Math.Min(fSubSet.Length, tSubSet.Length);
            for (var i = 0; minCount > i; i += 1)
            {
                if (fSubSet[i].HasValue != tSubSet[i].HasValue) { return false; }
                if (fSubSet[i] != tSubSet[i]) { return false; }
            }
            return true;
        }

    }

}
