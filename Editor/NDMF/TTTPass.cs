using System;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.Build;
using net.rs64.TexTransTool.Editor.OtherMenuItem;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.Utils;
using static net.rs64.TexTransTool.Build.AvatarBuildUtils;

namespace net.rs64.TexTransTool.NDMF
{
    abstract class TTTPass<T> : Pass<T> where T : Pass<T>, new()
    {
        protected TexTransBuildSession TTTContext(BuildContext context)
        {
            return context.GetState(b =>
            {
                using var pf = new PFScope("FindAtPhase");

                var p2b = TexTransBehaviorSearch.FindAtPhase(context.AvatarRootObject);

                pf.Split("TexTransBuildSession.ctr");
                switch (TTTProjectConfig.instance.TexTransCoreEngineBackend)
                {

#if CONTAINS_TTCE_WGPU
                    case TTTProjectConfig.TexTransCoreEngineBackendEnum.Wgpu:
                        {
                            var wgpuCtx = TTCEWgpuDeviceWithTTT4UnityHolder.Device().GetTTCEWgpuContext();
                            return new TexTransBuildSession(context.AvatarRootObject, new NDMFDomain(b, null, wgpuCtx), p2b);
                        }
#endif

                    default:
                    case TTTProjectConfig.TexTransCoreEngineBackendEnum.Unity:
                        {
                            return new TexTransBuildSession(context.AvatarRootObject, new NDMFDomain(b), p2b);
                        }
                }
            });
        }
    }
    internal class PreviewCancelerPass : Pass<PreviewCancelerPass>
    {
        protected override void Execute(BuildContext context)
        {
            if (!PreviewUtility.IsPreviewContains) { return; }
            PreviewUtility.ExitPreviews();
            TTTLog.Error("Common:error:BuildWasRunDuringPreviewing");
            throw new TTTNotExecutable();
        }
    }
    internal class CheckOldSaveDataComponents : Pass<CheckOldSaveDataComponents>
    {
        protected override void Execute(BuildContext context)
        {
            var containsOldSaveDataComponents = context.AvatarRootObject.GetComponentsInChildren<TexTransMonoBase>().Where(TexTransMonoBase.IsOldSaveData).ToArray();
            if (containsOldSaveDataComponents.Length is not 0) TTTLog.Warning("Common:warning:ContainsOldSaveDataComponents", containsOldSaveDataComponents);
        }
    }
    internal class MaterialModificationPass : TTTPass<MaterialModificationPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.MaterialModification);
        }
    }
    internal class BeforeUVModificationPass : TTTPass<BeforeUVModificationPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.BeforeUVModification);
        }
    }
    internal class UVModificationPass : TTTPass<UVModificationPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.UVModification);
        }
    }
    internal class AfterUVModificationPass : TTTPass<AfterUVModificationPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.AfterUVModification);
        }
    }
    internal class PostProcessingPass : TTTPass<PostProcessingPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.PostProcessing);
        }
    }
    internal class UnDefinedPass : TTTPass<UnDefinedPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).ApplyFor(TexTransPhase.UnDefined);
        }
    }
    internal class ReFindRenderersPass : TTTPass<ReFindRenderersPass>
    {
        protected override void Execute(BuildContext context)
        {
            if (TTTContext(context).Domain is NDMFDomain domain) { domain.ReFindRenderers(); }
        }
    }
    internal sealed class AtlasTextureMemoryIncreaseDiagnosticError
        : SimpleError
    {
        private readonly string _message;

        internal AtlasTextureMemoryIncreaseDiagnosticError(string message)
        {
            _message = message;
        }

        public override Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey =>
            "AtlasTexture:info:MemoryIncreaseDiagnosticTitle";
        public override string[] TitleSubst => Array.Empty<string>();

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            root.style.minWidth = 0;

            var diagnosticScroll = new ScrollView(
                ScrollViewMode.VerticalAndHorizontal);
            diagnosticScroll.style.flexGrow = 1;
            diagnosticScroll.style.minWidth = 0;
            diagnosticScroll.style.minHeight = 120;
            diagnosticScroll.style.height = 520;

            var linesContainer = new VisualElement();
            linesContainer.style.flexShrink = 0;
            linesContainer.style.minWidth = 0;

            foreach (var line in _message.Split(
                         new[] { '\n' }, StringSplitOptions.None))
            {
                var diagnosticLine = new Label(line)
                {
                    enableRichText = false,
                };
                diagnosticLine.style.flexShrink = 0;
                diagnosticLine.style.whiteSpace = WhiteSpace.NoWrap;
                if (line.Length == 0)
                    diagnosticLine.style.minHeight =
                        EditorGUIUtility.singleLineHeight;
                linesContainer.Add(diagnosticLine);
            }

            diagnosticScroll.Add(linesContainer);
            root.Add(diagnosticScroll);

            var copyButton = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = _message)
            {
                text = "AtlasTexture:button:CopyMemoryIncreaseDiagnostic"
                    .GetLocalize(),
            };
            copyButton.style.alignSelf = Align.FlexEnd;
            root.Add(copyButton);

            return root;
        }
    }

    internal class OptimizingPass : TTTPass<OptimizingPass>
    {
        protected override void Execute(BuildContext context)
        {
            AtlasTextureMemoryIncreaseDiagnosticCollector.Clear();
            TTTContext(context).ApplyFor(TexTransPhase.Optimizing);

            var projectName = Path.GetFileName(
                Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectName))
                projectName = Application.productName;

            foreach (var diagnostic
                     in AtlasTextureMemoryIncreaseDiagnosticCollector.Drain())
            {
                var completeMessage =
                    "Project: " + projectName + "\n" + diagnostic.Message;
                TTTLog.ReportingObject(diagnostic.Context, () =>
                    ErrorReport.ReportError(
                        new AtlasTextureMemoryIncreaseDiagnosticError(
                            completeMessage)));
            }
        }
    }
    internal class TTTSessionEndPass : TTTPass<TTTSessionEndPass>
    {
        protected override void Execute(BuildContext context)
        {
            TTTContext(context).TTTSessionEnd();
        }
    }
    internal class TTTComponentPurgePass : TTTPass<TTTComponentPurgePass>
    {
        protected override void Execute(BuildContext context)
        {
            DestroyITexTransToolTags(context.AvatarRootObject);
        }
    }
}
