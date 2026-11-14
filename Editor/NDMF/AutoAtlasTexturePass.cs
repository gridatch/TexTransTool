#nullable enable
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.Editor;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Runs after regular, explicitly configured AtlasTexture components.
    /// AutoAtlasTexture is deliberately not in the ordinary component phase list.
    /// </summary>
    internal sealed class AutoAtlasTexturePass : TTTPass<AutoAtlasTexturePass>
    {
        protected override void Execute(BuildContext context)
        {
            var configurations = AutoAtlasTextureProcessor.GetActiveConfigurations(
                context.AvatarRootObject);

            if (configurations.Length == 0) return;
            if (configurations.Length != 1)
            {
                TTTLog.Warning("AutoAtlasTexture:warn:MultipleConfigurations");
                return;
            }

            var session = TTTContext(context);
            var manualAtlases = session.PhaseAtList[TexTransPhase.Optimizing]
                .OfType<AtlasTexture>()
                .Where(atlas => TexTransBehaviorSearch.CheckIsActiveBehavior(
                    atlas, context.AvatarRootObject))
                .ToArray();
            var configuration = configurations[0];
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                AutoAtlasTextureExecutionSettings.FromComponent(configuration),
                manualAtlases,
                configuration,
                OriginalMaterialName);
        }

        private static string OriginalMaterialName(Material material)
        {
            // Reuse NDMF's object identity; build-stage suffixes are not
            // stripped or guessed from the material name.
            var identity = ObjectRegistry.GetReference(material);
            if (identity?.Object is Material source && source != null)
                return source.name;
            // ObjectReference snapshots its display name at creation; this
            // remains available even if the original Unity object was destroyed.
            return identity?.ToString() ?? material.name;
        }
    }
}
