#if CONTAINS_AAO
using nadena.dev.ndmf;

[assembly: ExportsPlugin(
    typeof(net.rs64.TexTransTool.NDMF.AutoAtlasTexturePostAAODiagnosticPlugin))]

namespace net.rs64.TexTransTool.NDMF
{
    internal sealed class AutoAtlasTexturePostAAODiagnosticPlugin
        : Plugin<AutoAtlasTexturePostAAODiagnosticPlugin>
    {
        public override string QualifiedName =>
            "net.rs64.tex-trans-tool.auto-atlas-post-aao-diagnostic";
        public override string DisplayName =>
            "TexTransTool AutoAtlas diagnostic";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run(AutoAtlasTexturePostAAODiagnosticPass.Instance);
        }
    }
}
#endif
