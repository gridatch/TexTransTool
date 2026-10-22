#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using net.rs64.TexTransCore;
using net.rs64.TexTransCore.UVIsland;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using net.rs64.TexTransTool.Editor;
using net.rs64.TexTransTool.TextureAtlas.IslandSizePriorityTuner;
using net.rs64.TexTransTool.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;

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
            var animationServices =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                    ? context.Extension<AnimatorServicesContext>()
                    : null;
            AutoAtlasTextureProcessor.Execute(
                session.Domain,
                configuration,
                manualAtlases,
                animationServices?.AnimationIndex,
                animationServices?.ObjectPathRemapper,
                context.AvatarRootTransform);
        }
    }


    // TEMPORARY, read-only investigation of ORIGINAL emission textures.
    // The atlas output (including its generated white background) is never
    // sampled here. "candidate" means only neutral at mip 0 under the static
    // UV/filter model; it is NOT approval to remove a texture or shader feature.
    internal sealed class AutoAtlasEmissionProbe
    {
        private const long DefaultResidentPixelBudget = 8L * 1024 * 1024;
        private const long MaxTrianglePixelChecks = 12L * 1024 * 1024;
        private readonly long _residentPixelBudget;

        internal AutoAtlasEmissionProbe(long residentPixelBudget = DefaultResidentPixelBudget)
        {
            if (residentPixelBudget <= 0)
                throw new ArgumentOutOfRangeException(nameof(residentPixelBudget));
            _residentPixelBudget = residentPixelBudget;
        }
        // The half-width of a texel's contributing UV region measured
        // relative to its pixel center. Linear filtering spans +/-1px;
        // point filtering spans +/-0.5px.

        private readonly List<string> _lines = new();
        // Temporary, source-observed zero mask candidates and final-output
        // ownership inspection. This does not mutate generated materials.
        private readonly List<(Material material, string property, Texture source)> _zeroMaskCandidates = new();
        private readonly List<string> _zeroMaskOwnershipLines = new();
        private readonly List<string> _shadowReplanLines = new();
        private readonly List<string> _featureDisableLines = new();
        private readonly List<string> _featureRejections = new();
        private readonly List<(Material material, string property, Texture source,
            AutoAtlasFeatureDisableRules.Rule rule)> _featureCandidates = new();
        internal (Material material, string property, Texture source,
            AutoAtlasFeatureDisableRules.Rule rule)[] GetFeatureCandidates() =>
            _featureCandidates.Distinct().ToArray();
        internal void AddFeatureDisableLine(string line) =>
            _featureDisableLines.Add(line);

        internal (Material material, string property, Texture source)[] GetZeroMaskCandidates() =>
            _zeroMaskCandidates.Distinct().ToArray();

        internal void AddShadowReplanLine(string line) => _shadowReplanLines.Add(line);
        private readonly Dictionary<Texture, SourceImage> _images = new();
        private long _readbackPixels;
        private long _residentPixels;
        private long _peakResidentPixels;
        private long _imageCacheClock;
        private int _imageCacheEvictions;
        private int _imageCacheReloads;
        private long _readbackMilliseconds;
        private long _probeMilliseconds;
        private long _triangleChecks;
        private int _observed;
        private int _candidates;
        private int _unknown;
        private readonly List<string> _sourceSurvey = new();
        private int _surveyProperties;
        private int _surveyUniformWhole;
        private int _surveyUniformUsed;
        private int _surveyUnknownCoverage;
        private int _surveyCoverageCacheHits;
        // TEMPORARY candidate-first GPU survey. The original Emission
        // inspection remains separate; only this generic source survey changes.
        private const int MaxGpuStatusSamples = 8;
        private readonly List<string> _gpuStatusLines = new();
        private int _gpuScreens;
        private int _gpuRejectedVariable;
        private int _gpuUniformCandidates;
        private int _gpuStrictConfirmed;
        private int _gpuStrictRejected;
        private int _gpuFallbacks;
        private int _gpuFlippedRows;
        private double _gpuScreenMs;
        private double _gpuStrictMs;

        private sealed class SourceImage
        {
            internal string Status = "";
            internal Color[]? Pixels;
            internal string? WholeStats;
            internal bool WholeIsConstant;
            internal bool WasReadSuccessfully;
            internal long LastAccess;
            internal int Width;
            internal int Height;
        }

        private sealed class Inspection
        {
            internal string Status = "";
            internal bool? RgbZero;
            internal bool? AlphaZero;
        }

        internal void Observe(
            IDomain domain,
            Renderer[] renderers,
            Dictionary<Material, Material> materialMap)
        {
            // Candidates accumulate across original groups. The complete
            // scan happens before any domain material/mesh replacements.
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // The actual pre-atlas material slots and UVs, not output meshes.
            // Keep multiple users of a material; any one may emit.
            var slots = new Dictionary<Material, HashSet<(Mesh, int)>>();
            var unresolved = new HashSet<Material>();
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var materials = domain.GetMaterials(renderer);
                var originalMesh = domain.GetMesh(renderer);
                for (int materialIndex = 0; materialIndex < materials.Length; ++materialIndex)
                {
                    var material = materials[materialIndex];
                    if (material == null || !materialMap.ContainsKey(material)) continue;
                    if (originalMesh == null || originalMesh.subMeshCount == 0)
                    {
                        unresolved.Add(material);
                        continue;
                    }
                    int submesh = Math.Min(materialIndex, originalMesh.subMeshCount - 1);
                    if (originalMesh.GetSubMesh(submesh).indexCount == 0) continue;
                    if (!slots.TryGetValue(material, out var usedSlots))
                        slots.Add(material, usedSlots = new HashSet<(Mesh, int)>());
                    usedSlots.Add((originalMesh, submesh));
                }
            }

            foreach (var entry in materialMap.OrderBy(x => x.Key.name,
                         StringComparer.Ordinal))
            {
                var original = entry.Key;
                if (original == null || original.shader == null) continue;
                var shader = original.shader.name;
                if (shader.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) < 0
                    || shader.IndexOf("Lite", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                slots.TryGetValue(original, out var used);
                if (unresolved.Contains(original)) used = null;
                InspectLayer(original, used, "_UseEmission", "_EmissionBlendMode",
                    "_EmissionMap", "_EmissionBlendMask", "1st");
                InspectLayer(original, used, "_UseEmission2nd", "_Emission2ndBlendMode",
                    "_Emission2ndMap", "_Emission2ndBlendMask", "2nd");
            }

            // Emission-specific interpretation remains separate. In parallel,
            // collect candidate examples from every source texture property.
            // These are observations, never deletion approvals.
            try
            {
                SurveySourceTextures(materialMap.Keys, slots, unresolved);
            }
            catch (Exception ex)
            {
                // A temporary diagnosis must never prevent normal atlasing.
                _sourceSurvey.Add("Source survey aborted: "
                    + ex.GetType().Name);
            }

            watch.Stop();
            _probeMilliseconds += watch.ElapsedMilliseconds;
        }

        // Broad collection for all texture properties, not just emission.
        // 'whole' is exact over the obtained source mip0, while 'uv0' is a
        // provisional observation of the subset that the static UV0 transform
        // can reach. Neither result authorizes texture removal.
        private void SurveySourceTextures(
            IEnumerable<Material> materials,
            Dictionary<Material, HashSet<(Mesh, int)>> slots,
            HashSet<Material> unresolved)
        {
            // Cache only within this survey invocation. Identical source
            // textures and UV transforms on the same material must not
            // rerasterize the same mesh triangles for _MainTex / _BaseMap /
            // _BaseColorMap or other shared material texture references.
            // Different materials never share coverage implicitly.
            var coverageCache = new Dictionary<
                (Material material, Texture texture, Vector2 scale, Vector2 offset),
                (string status, bool constant, Color constantColor)>();

            foreach (var material in materials
                         .Where(m => m != null && m.shader != null)
                         .OrderBy(m => m.name, StringComparer.Ordinal))
            {
                IReadOnlyDictionary<string, TextureUVUsage>? usage = null;
                string usageError = "";
                try
                {
                    usage = TTShaderTextureUsageInformationUtil
                        .GetContainsUVTransformUsage(material);
                }
                catch (Exception ex)
                {
                    usageError = "usage-provider:" + ex.GetType().Name;
                }

                slots.TryGetValue(material, out var used);
                if (unresolved.Contains(material)) used = null;
                foreach (var property in material.GetTexturePropertyNames()
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    try
                    {
                        var texture = material.GetTexture(property);
                        if (texture == null) continue;
                        ++_surveyProperties;

                        var image = ReadOriginal(texture);
                        if (image.Pixels == null)
                        {
                            ++_surveyUnknownCoverage;
                            _sourceSurvey.Add("material=" + material.name
                                + ", property=" + property
                                + ", source=" + texture.name
                                + ", whole=unknown:" + image.Status);
                            continue;
                        }

                        if (image.WholeStats == null)
                        {
                            var full = ScanSourcePixels(image.Pixels, null);
                            image.WholeStats = FormatSurveyStats(full);
                            image.WholeIsConstant = full.isConstant;
                        }
                        if (image.WholeIsConstant) ++_surveyUniformWhole;
                        string uvStatus;
                        if (used == null || used.Count == 0)
                            uvStatus = "unknown:source-mesh";
                        else if (usage == null)
                            uvStatus = "unknown:" + usageError;
                        else if (!usage.TryGetValue(property, out var uvUsage))
                            uvStatus = "unknown:UV-not-registered";
                        else if (uvUsage.UVChannel != UsageUVChannel.UV0
                                 || uvUsage.UVMatrix is not { } matrix)
                            uvStatus = "unknown:nonstatic-or-non-UV0";
                        else if (matrix.M01 != 0f || matrix.M10 != 0f)
                            uvStatus = "unknown:rotated-or-sheared-UV";
                        else
                        {
                            var scale = new Vector2(matrix.M00, matrix.M11);
                            var offset = new Vector2(matrix.M02, matrix.M12);
                            var key = (material, texture, scale, offset);
                            bool constant;
                            Color constantColor;
                            if (coverageCache.TryGetValue(key, out var cached))
                            {
                                uvStatus = cached.status;
                                constant = cached.constant;
                                constantColor = cached.constantColor;
                                ++_surveyCoverageCacheHits;
                            }
                            else
                            {
                                (uvStatus, constant, constantColor) = EvaluateSourceUvGpuFirst(
                                    used, scale, offset, image.Width, image.Height,
                                    texture.wrapModeU, texture.wrapModeV,
                                    texture.filterMode, image.Pixels,
                                    image.WholeIsConstant,
                                    material.name + " / " + property);
                                coverageCache.Add(key, (uvStatus, constant, constantColor));
                            }
                            if (constant) ++_surveyUniformUsed;
                            // Only strict CPU coverage may produce a feature
                            // hypothesis; GPU interior samples do not qualify.
                            if (constant
                                && uvStatus.StartsWith("UV0-texels=", StringComparison.Ordinal))
                            {
                                if (AutoAtlasFeatureDisableRules.TryClassify(
                                        material, property, constantColor, out var rule))
                                    _featureCandidates.Add((material, property, texture, rule!));
                                else
                                {
                                    var reason = AutoAtlasFeatureDisableRules
                                        .ExplainRejectedZeroMask(
                                            material, property, constantColor);
                                    if (reason != null)
                                        _featureRejections.Add(
                                            "material=" + material.name
                                            + ", property=" + property
                                            + ", rejected=" + reason);
                                }
                            }
                        }
                        if (uvStatus.StartsWith("unknown:", StringComparison.Ordinal))
                            ++_surveyUnknownCoverage;

                        _sourceSurvey.Add("material=" + material.name
                            + ", property=" + property
                            + ", source=" + texture.name
                            + ", size=" + image.Width + "x" + image.Height
                            + ", read=" + image.Status
                            + ", whole=" + image.WholeStats
                            + ", used=" + uvStatus);
                    }
                    catch (Exception ex)
                    {
                        ++_surveyUnknownCoverage;
                        _sourceSurvey.Add("material=" + material.name
                            + ", property=" + property
                            + ", whole=unknown:survey-error:"
                            + ex.GetType().Name);
                    }
                }
            }
        }

        /// <summary>
        /// GPU-screen nonuniform interiors first; strict CPU filtering
        /// runs only for uniform candidates or GPU fallback. This is an
        /// observational mip0 survey, not shader removability approval.
        /// </summary>
        internal (string status, bool constant, Color constantColor) EvaluateSourceUvGpuFirst(
            HashSet<(Mesh, int)> slots, Vector2 scale, Vector2 offset,
            int width, int height, TextureWrapMode wrapU, TextureWrapMode wrapV,
            FilterMode filterMode, Color[] pixels, bool wholeSourceConstant,
            string label)
        {
            string route = "whole-constant";
            if (!wholeSourceConstant)
            {
                ++_gpuScreens;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool supportedSampling =
                    (filterMode == FilterMode.Point
                     || filterMode == FilterMode.Bilinear
                     || filterMode == FilterMode.Trilinear)
                    && (wrapU == TextureWrapMode.Clamp
                        || wrapU == TextureWrapMode.Repeat)
                    && (wrapV == TextureWrapMode.Clamp
                        || wrapV == TextureWrapMode.Repeat);
                bool ok;
                bool[] interiorMask;
                int interiorCount;
                string gpuReason;
                bool verticalFlipCorrected;
                if (!supportedSampling)
                {
                    ok = false;
                    interiorMask = Array.Empty<bool>();
                    interiorCount = 0;
                    gpuReason = "source-sampling-unhandled";
                    verticalFlipCorrected = false;
                }
                else
                {
                    ok = DiagnosticSourceUvGpuMask.TryRender(
                        slots, scale, offset, width, height, _residentPixelBudget,
                        out interiorMask, out interiorCount, out gpuReason,
                        out verticalFlipCorrected);
                }
                watch.Stop();
                _gpuScreenMs += watch.Elapsed.TotalMilliseconds;
                if (ok && verticalFlipCorrected) ++_gpuFlippedRows;

                if (ok)
                {
                    var screen = ScanSourcePixels(pixels, interiorMask);
                    if (!screen.isConstant)
                    {
                        // Every interior texel lies in the sampled UV support.
                        // Different interior colors are sufficient to reject
                        // a uniform candidate without computing the boundary.
                        ++_gpuRejectedVariable;
                        AddGpuStatus(label + ": GPU-interior=" + interiorCount
                            + ":variable; CPU=skipped; GPUms="
                            + FormatMs(watch.Elapsed.TotalMilliseconds));
                        return ("GPU-UV0-interior-texels=" + interiorCount
                            + ":" + FormatSurveyStats(screen), false, default);
                    }
                    ++_gpuUniformCandidates;
                    route = "candidate-check";
                    AddGpuStatus(label + ": GPU-interior=" + interiorCount
                        + ":uniform; CPU=strict-boundary");
                }
                else
                {
                    ++_gpuFallbacks;
                    route = "GPU-fallback:" + gpuReason;
                    AddGpuStatus(label + ": GPU=unavailable:" + gpuReason
                        + "; CPU=strict-fallback");
                }
            }

            var cpuWatch = System.Diagnostics.Stopwatch.StartNew();
            var success = TrySourceCoverage(slots, scale, offset, width,
                height, wrapU, wrapV, filterMode,
                out var strictMask, out var strictCount, out var reason);
            cpuWatch.Stop();
            _gpuStrictMs += cpuWatch.Elapsed.TotalMilliseconds;
            if (!success)
            {
                AddGpuStatus(label + ": " + route + "; CPU=unknown:" + reason);
                return ("unknown:" + reason, false, default);
            }
            var strict = ScanSourcePixels(pixels, strictMask);
            if (route == "candidate-check")
            {
                if (strict.isConstant) ++_gpuStrictConfirmed;
                else ++_gpuStrictRejected;
                AddGpuStatus(label + ": " + route
                    + (strict.isConstant ? "; confirmed" : "; rejected")
                    + "; CPUms=" + FormatMs(cpuWatch.Elapsed.TotalMilliseconds));
            }
            return ("UV0-texels=" + strictCount
                + ":" + FormatSurveyStats(strict), strict.isConstant,
                strict.constantColor);
        }

        private void AddGpuStatus(string line)
        {
            if (_gpuStatusLines.Count < MaxGpuStatusSamples)
                _gpuStatusLines.Add(line);
        }

        private static string FormatMs(double milliseconds) =>
            milliseconds.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture);

        // Retained for the mask comparison regression tests.
        internal static int CountCoverageDifferences(bool[] source, bool[] alternative)
        {
            if (source.Length != alternative.Length)
                throw new ArgumentException("Coverage length mismatch.");
            int different = 0;
            for (int i = 0; i < source.Length; ++i)
                if (source[i] != alternative[i]) ++different;
            return different;
        }

        private static string FormatSurveyStats(
            (bool rgbZero, bool alphaZero, int nonzeroRgbTexels,
             float maxAbsRgb, bool isConstant, Color constantColor,
             int sampledTexels) stats)
        {
            return (stats.isConstant
                       ? "constant:" + FormatSurveyColor(stats.constantColor)
                       : "variable")
                   + ",RGB" + (stats.rgbZero ? "=0" : "!=0")
                   + ",nonzeroRGBTexels=" + stats.nonzeroRgbTexels
                   + ",maxAbsRGB=" + stats.maxAbsRgb.ToString("R",
                       System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FormatSurveyColor(Color c)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "(" + c.r.ToString("R", ci) + ","
                + c.g.ToString("R", ci) + ","
                + c.b.ToString("R", ci) + ","
                + c.a.ToString("R", ci) + ")";
        }

        private void InspectLayer(Material material, HashSet<(Mesh, int)>? slots,
            string enabledProperty, string modeProperty,
            string mapProperty, string maskProperty, string layer)
        {
            if (!material.HasProperty(enabledProperty)
                || material.GetFloat(enabledProperty) == 0f) return;

            var mapTexture = material.HasProperty(mapProperty)
                ? material.GetTexture(mapProperty) : null;
            var maskTexture = material.HasProperty(maskProperty)
                ? material.GetTexture(maskProperty) : null;
            if (mapTexture == null && maskTexture == null) return;

            ++_observed;
            int mode = material.HasProperty(modeProperty)
                ? Mathf.RoundToInt(material.GetFloat(modeProperty)) : -1;

            // Check mask first: a zero mask can prove no emission even when
            // the emission map uses a different or animated UV channel.
            var mask = InspectTexture(material, slots, maskProperty, maskTexture,
                isMask: true);
            bool maskProvesZero = mask.AlphaZero == true
                || ((mode == 1 || mode == 2) && mask.RgbZero == true);
            var map = maskProvesZero
                ? new Inspection { Status = "unnecessary:mask-is-zero" }
                : InspectTexture(material, slots, mapProperty, mapTexture,
                    isMask: false);

            bool possible = maskProvesZero
                || map.AlphaZero == true
                || ((mode == 1 || mode == 2) && map.RgbZero == true);
            bool unknown = !possible && (mask.RgbZero == null
                && maskTexture != null
                || map.RgbZero == null && mapTexture != null);
            if (possible) ++_candidates;
            else if (unknown) ++_unknown;
            if (maskProvesZero && maskTexture != null)
                _zeroMaskCandidates.Add((material, maskProperty, maskTexture));

            _lines.Add(
                "material=" + material.name + ", emission=" + layer
                + ", blendMode=" + mode
                + ", source-map=" + map.Status
                + ", source-mask=" + mask.Status
                + ", candidate=" + (possible
                    ? "source-mip0-zero" : unknown ? "unknown" : "nonzero")
            );
        }

        private Inspection InspectTexture(
            Material material, HashSet<(Mesh, int)>? slots,
            string property, Texture? texture, bool isMask)
        {
            if (texture == null) return new Inspection { Status = "unset" };
            if (slots == null || slots.Count == 0)
                return new Inspection { Status = "source-mesh-unknown" };

            if (!TryStaticUvTransform(material, property, isMask,
                    out var scale, out var offset, out var reason))
                return new Inspection { Status = reason };

            var source = ReadOriginal(texture);
            if (source.Pixels == null)
                return new Inspection { Status = source.Status };

            var sampler = isMask && material.HasProperty("_MainTex")
                ? material.GetTexture("_MainTex") ?? texture : texture;
            if (!TrySourceCoverage(slots, scale, offset, source.Width,
                    source.Height, sampler.wrapModeU, sampler.wrapModeV,
                    sampler.filterMode, out var covered, out var count,
                    out reason))
                return new Inspection { Status = reason };

            var scan = ScanCoveredSourcePixels(source.Pixels, covered);
            return new Inspection
            {
                RgbZero = scan.rgbZero,
                AlphaZero = scan.alphaZero,
                Status = source.Width + "x" + source.Height
                    + ":read=" + source.Status
                    + ",UV-filter-texels=" + count
                    + ",RGB" + (scan.rgbZero ? "=0" : "!=0")
                    + ",A" + (scan.alphaZero ? "=0" : "!=0")
                    + ",nonzeroRGBTexels=" + scan.nonzeroRgbTexels
                    + ",maxAbsRGB=" + scan.maxAbsRgb.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        internal void RecordOwnershipDiagnosticError(Exception exception)
        {
            _zeroMaskOwnershipLines.Add("ownership-inspection-unknown:"
                + exception.GetType().Name);
        }

        /// <summary>
        /// Diagnostic only: attributes a zero source-emission mask to its
        /// actual final generated render texture and other material/property
        /// users. Per-output memory uses the existing final memory estimator.
        /// It does NOT assume that a mip0-zero mask can safely be disabled.
        /// </summary>
        internal void InspectZeroMaskOutputOwnership<TTexture>(
            IDomain domain,
            AtlasContext atlasContext,
            TexFineTuningResult tuningResult,
            Dictionary<Material, Material> materialMap,
            IReadOnlyDictionary<string, TTexture> resultingTextures,
            IReadOnlyCollection<Texture> retainedOriginalSources,
            AtlasTexture.AtlasExecutionMetrics executedLayout)
            where TTexture : Texture
        {
            if (_zeroMaskCandidates.Count == 0) return;
            var knownZeroMasks = _zeroMaskCandidates
                .Select(candidate => (candidate.material, candidate.property))
                .ToHashSet();
            var activeGroups = atlasContext.AtlasSubMeshIndexSetCtx
                .AtlasSubMeshIndexIDHash
                .Select(submesh => submesh.MaterialGroupID)
                .Distinct().ToArray();

            foreach (var (material, property, source) in _zeroMaskCandidates
                         .Distinct()
                         .Where(candidate => materialMap.ContainsKey(candidate.material)))
            {
                string id = "material=" + material.name
                    + ", property=" + property
                    + ", source=" + source.name;
                // Source residency is a separate axis from atlas output
                // residency, even when no new atlas output was assigned.
                string originalBytes = "unknown";
                if (AtlasTexture.TryCalculateSourceTextureMemory(
                        domain, new[] { source }, out var originalMemory))
                    originalBytes = originalMemory.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                bool sourceRetained = retainedOriginalSources.Contains(source);
                var counterfactual = AnalyzeZeroMaskExclusion(
                    atlasContext, activeGroups, knownZeroMasks,
                    material, property, source, executedLayout);

                if (!materialMap.TryGetValue(material, out var outputMaterial)
                    || !tuningResult.RenderTextures.TryGetValue(
                        property, out var outputRt)
                    || !resultingTextures.TryGetValue(
                        property, out var outputUnityTexture)
                    || outputMaterial.GetTexture(property) != outputUnityTexture)
                {
                    _zeroMaskOwnershipLines.Add(id
                        + ", result=not-bound-to-new-atlas-output"
                        + ", originalSourceBytes=" + originalBytes
                        + ", originalSourceRetainedByOtherRefs=" + sourceRetained
                        + ", " + counterfactual.report
                        + ", note=no-output-memory-savings-inferred");
                    continue;
                }

                // Several shader properties may alias one render texture
                // through ReferenceCopy or MergeTexture.
                var outputProperties = tuningResult.RenderTextures
                    .Where(item => ReferenceEquals(item.Value, outputRt))
                    .Select(item => item.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                var outputMaterialReferences = materialMap
                    .SelectMany(pair => pair.Value
                        .GetTexturePropertyNames()
                        .Where(name =>
                            pair.Value.GetTexture(name) == outputUnityTexture)
                        .Select(name => (material: pair.Key, property: name)))
                    .Distinct()
                    .ToArray();

                var otherConsumers = outputMaterialReferences
                    .Where(reference => !knownZeroMasks.Contains(reference))
                    .ToArray();

                string bytes = "unknown";
                if (tuningResult.TextureDescriptors.TryGetValue(
                        outputRt, out var descriptor))
                {
                    var propertySet = outputProperties.ToHashSet(
                        StringComparer.Ordinal);
                    var isolated = new TexFineTuningResult(
                        tuningResult.RenderTextures
                            .Where(item => propertySet.Contains(item.Key))
                            .ToDictionary(item => item.Key, item => item.Value),
                        new Dictionary<ITTRenderTexture,
                            TexTransToolTextureDescriptor>
                            { [outputRt] = descriptor },
                        tuningResult.SourceProperties
                            .Where(item => propertySet.Contains(item.Key))
                            .ToDictionary(item => item.Key, item => item.Value));
                    if (AtlasTexture.TryCalculateFinalTextureMemory(
                            atlasContext, isolated, out var outputBytes))
                        bytes = outputBytes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                }

                // Only an unshared, untuned-size output may be projected
                // through the existing final-memory estimator. Shared
                // outputs must never be counted as removable resources.
                string conditionalOutputMemoryDelta = "unresolved";
                if (counterfactual.inputRemoved
                    && counterfactual.layoutUnchanged
                    && outputProperties.Length == 1
                    && outputProperties[0] == property)
                {
                    if (counterfactual.newPropertySize == null)
                    {
                        if (otherConsumers.Length == 0 && bytes != "unknown")
                            conditionalOutputMemoryDelta = bytes
                                + ":whole-output-conditional";
                        else
                            conditionalOutputMemoryDelta =
                                "unresolved:other-output-references";
                    }
                    else if (executedLayout.PropertySizes.TryGetValue(
                                 property, out var oldPlannedSize)
                             && outputRt.Width == oldPlannedSize.x
                             && outputRt.Hight == oldPlannedSize.y)
                    {
                        var alternative = new Dictionary<
                            ITTRenderTexture, Vector2Int>
                        {
                            [outputRt] = counterfactual.newPropertySize.Value
                        };
                        if (AtlasTexture.TryCalculateFinalTextureMemory(
                                atlasContext, tuningResult, out var currentBytes)
                            && AtlasTexture.TryCalculateFinalTextureMemory(
                                atlasContext, tuningResult, out var projectedBytes,
                                alternative))
                        {
                            // Signed bytes: a hypothetical increase must
                            // remain visible rather than being clamped to zero.
                            conditionalOutputMemoryDelta =
                                (currentBytes - projectedBytes)
                                + ":same-format-and-sharing-conditional";
                        }
                    }
                }

                var remainingLabels = otherConsumers
                    .Select(reference => reference.material.name
                        + "/" + reference.property)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
                _zeroMaskOwnershipLines.Add(id
                    + ", output=" + outputRt.Width + "x" + outputRt.Hight
                    + ", outputBytes=" + bytes
                    + ", outputProperties=[" + string.Join(", ", outputProperties) + "]"
                    + ", actualOutputReferences=" + outputMaterialReferences.Length
                    + ", otherThanKnownZeroMasks=" + otherConsumers.Length
                    + ", otherConsumers=[" + string.Join(", ", remainingLabels)
                    + (otherConsumers.Length > remainingLabels.Length
                        ? ", ..." : "") + "]"
                    + ", originalSourceBytes=" + originalBytes
                    + ", originalSourceRetainedByOtherRefs="
                    + sourceRetained
                    + ", outputExclusivelyZeroMasks="
                    + (outputMaterialReferences.Length != 0
                        && otherConsumers.Length == 0)
                    + ", " + counterfactual.report
                    + ", conditionalOutputMemoryDeltaBytes="
                    + conditionalOutputMemoryDelta
                    + ", note=projected-delta-excludes-relayout-and-changed-retained-sources");
            }
        }

        /// <summary>
        /// Fixed-material-group counterfactual. Computes only what can be
        /// inferred without changing the atlas context, mesh or material.
        /// A primary-size change requires a full layout/merge replan and
        /// cannot be represented by a claimed new free-space percentage.
        /// </summary>
        internal static (string report, bool inputRemoved,
            bool layoutUnchanged, Vector2Int? newPropertySize)
            AnalyzeZeroMaskExclusion(
            AtlasContext context,
            IReadOnlyCollection<int> activeGroupIds,
            HashSet<(Material material, string property)> knownZeroMasks,
            Material candidateMaterial, string property, Texture source,
            AtlasTexture.AtlasExecutionMetrics layout)
        {
            var materialGroups = context.MaterialGroupingCtx.GroupMaterials;
            var materialProperties =
                context.MaterialGroupingCtx.ContainsTextureDictionaries;
            var groupId = context.MaterialGroupingCtx
                .GetMaterialGroupID(candidateMaterial);
            if (!activeGroupIds.Contains(groupId) || groupId < 0)
                return ("whatIf=unknown:inactive-material-group",
                    false, false, null);

            var group = materialGroups[groupId];
            if (!group.GroupedTexture.TryGetValue(property, out var groupSource)
                || groupSource != source)
                return ("whatIf=unknown:source-not-group-input",
                    false, false, null);

            // Removing a property from one material is NOT equivalent to
            // removing its group source when another material still uses it.
            if (!AllGroupSourceConsumersHaveProof(group, materialProperties,
                    knownZeroMasks, property, groupSource))
                return ("whatIf=source-remains:other-group-material-needs-it"
                    + ", layoutImpact=unknown:partial-island-or-regrouping"
                    + ", regroupAndMerge=not-simulated",
                    false, false, null);

            // Source removal may alter connectivity among materials. Do not
            // claim this fixed-group projection captures regrouping/merging.
            var oldPrimary = context.MaterialGroupingCtx
                .GetPrimaryTexture(groupId);
            Texture? newPrimary = PrimaryTextureWithoutProperty(
                group.GroupedTexture, property);

            bool primaryDimensionChanged =
                oldPrimary?.width != newPrimary?.width
                || oldPrimary?.height != newPrimary?.height;

            // Determine whether this change can affect any virtual island
            // actually scaled using the candidate group's primary texture.
            bool scaledIslandAffected = false;
            foreach (var virtualIsland in context.SourceVirtualIslands)
            {
                var firstOrigin = context
                    .ReverseSourceVirtualIsland2OriginIslands[virtualIsland]
                    .First();
                if (context.AtlasIslandCtx.ReverseOriginDict[firstOrigin]
                        .MaterialGroupID != groupId)
                    continue;
                if (primaryDimensionChanged)
                    scaledIslandAffected = true;
            }

            var layoutSize = layout.LayoutSize;
            string before = layout.PropertySizes.TryGetValue(
                property, out var originalSize)
                ? originalSize.x + "x" + originalSize.y
                : "none";
            var primarySizes = new List<Vector2Int?>();
            var sourceSizes = new List<Vector2Int>();
            foreach (var id in activeGroupIds)
            {
                var currentGroup = materialGroups[id];
                if (id == groupId) continue; // hypothetical removed source
                if (!currentGroup.GroupedTexture.TryGetValue(
                        property, out var remainingSource)
                    || remainingSource == null) continue;

                var primary = context.MaterialGroupingCtx.GetPrimaryTexture(id);
                primarySizes.Add(primary == null ? null :
                    new Vector2Int(primary.width, primary.height));
                sourceSizes.Add(new Vector2Int(
                    remainingSource.width, remainingSource.height));
            }

            if (scaledIslandAffected)
                return ("whatIf=fixed-group-input-exclusion"
                    + ", layoutImpact=repack-required:primary-size-changed"
                    + ", baselineLayout=" + layoutSize.x + "x" + layoutSize.y
                    + ", baselineTopFree=" + FormatSurveyFraction(
                        layout.TopFreeFraction)
                    + ", hypotheticalTopFree=unknown"
                    + ", propertyPlanBefore=" + before
                    + ", propertyPlanAfter=unknown"
                    + ", regroupAndMerge=not-simulated",
                    true, false, null);

            // The current packing geometry is unchanged only UNDER the
            // fixed-group assumption. The property-specific resolution
            // requirement can still change, even for a shared RT.
            Vector2Int? afterSize = sourceSizes.Count == 0
                ? null
                : TryCalculateHypotheticalPropertySize(
                    layoutSize, primarySizes, sourceSizes);
            string after = sourceSizes.Count == 0
                ? "absent"
                : afterSize is { } size
                    ? size.x + "x" + size.y
                    : "unknown";
            return ("whatIf=fixed-group-input-exclusion"
                + ", layoutImpact=unchanged-under-fixed-group-assumption"
                + ", baselineLayout=" + layoutSize.x + "x" + layoutSize.y
                + ", baselineTopFree=" + FormatSurveyFraction(
                    layout.TopFreeFraction)
                + ", hypotheticalTopFree=fixed-group-same"
                + ", propertyPlanBefore=" + before
                + ", propertyPlanAfter=" + after
                + ", regroupAndMerge=not-simulated",
                true, afterSize != null || sourceSizes.Count == 0,
                afterSize);
        }

        internal static bool AllGroupSourceConsumersHaveProof(
            IEnumerable<Material> materials,
            IReadOnlyDictionary<Material,
                IReadOnlyDictionary<string, Texture>> materialProperties,
            HashSet<(Material material, string property)> knownZeroMasks,
            string property, Texture groupSource)
        {
            bool found = false;
            foreach (var material in materials)
            {
                if (!materialProperties.TryGetValue(material, out var inputs)
                    || !inputs.TryGetValue(property, out var tex)
                    || tex != groupSource) continue;
                found = true;
                if (!knownZeroMasks.Contains((material, property)))
                    return false;
            }
            return found;
        }

        internal static Texture? PrimaryTextureWithoutProperty(
            IReadOnlyDictionary<string, Texture> groupedTextures,
            string excludedProperty)
        {
            Texture? next = null;
            long largest = -1;
            foreach (var input in groupedTextures)
            {
                if (string.Equals(input.Key, excludedProperty,
                        StringComparison.Ordinal)
                    || input.Value == null) continue;
                var pixels = (long)input.Value.width * input.Value.height;
                if (pixels <= largest) continue;
                next = input.Value;
                largest = pixels;
            }
            return next;
        }

        private static Vector2Int? TryCalculateHypotheticalPropertySize(
            Vector2Int layoutSize,
            IReadOnlyList<Vector2Int?> primarySizes,
            IReadOnlyList<Vector2Int> sourceSizes)
        {
            try
            {
                var next = AtlasTextureResolutionPlan.CalculatePropertySize(
                    layoutSize, true, primarySizes, sourceSizes);
                return next;
            }
            catch (Exception ex)
            {
                // Unknown-size projection is not a removal benefit.
                _ = ex;
                return null;
            }
        }

        private static string FormatSurveyFraction(float value) =>
            value.ToString("F6",
                System.Globalization.CultureInfo.InvariantCulture);

        // Production and regression tests use the same float-precision
        // scan. Counts/amplitudes are diagnostic only: they are NOT used
        // as an arbitrary pixel-count or RGB-value threshold.
        internal static (bool rgbZero, bool alphaZero,
            int nonzeroRgbTexels, float maxAbsRgb, bool isConstant,
            Color constantColor, int sampledTexels)
            ScanCoveredSourcePixels(Color[] pixels, bool[] covered)
        {
            if (pixels.Length != covered.Length)
                throw new ArgumentException("Source pixel/coverage size mismatch.");
            return ScanSourcePixels(pixels, covered);
        }

        // A null coverage means ALL original source mip0 pixels.
        internal static (bool rgbZero, bool alphaZero,
            int nonzeroRgbTexels, float maxAbsRgb, bool isConstant,
            Color constantColor, int sampledTexels)
            ScanSourcePixels(Color[] pixels, bool[]? covered)
        {
            if (covered != null && pixels.Length != covered.Length)
                throw new ArgumentException("Source pixel/coverage size mismatch.");
            bool rgbZero = true, alphaZero = true, isConstant = true;
            int nonzeroRgbTexels = 0, sampledTexels = 0;
            float maxAbsRgb = 0f;
            Color first = default;
            for (int i = 0; i < pixels.Length; ++i)
            {
                if (covered != null && !covered[i]) continue;
                var pixel = pixels[i];
                if (sampledTexels == 0) first = pixel;
                else if (isConstant
                         && (pixel.r != first.r || pixel.g != first.g
                             || pixel.b != first.b || pixel.a != first.a))
                    isConstant = false;
                ++sampledTexels;
                float absolute = Mathf.Max(Mathf.Abs(pixel.r),
                    Mathf.Max(Mathf.Abs(pixel.g), Mathf.Abs(pixel.b)));
                if (pixel.r != 0f || pixel.g != 0f || pixel.b != 0f)
                {
                    rgbZero = false;
                    ++nonzeroRgbTexels;
                    maxAbsRgb = Mathf.Max(maxAbsRgb, absolute);
                }
                if (pixel.a != 0f) alphaZero = false;
            }
            return (rgbZero, alphaZero, nonzeroRgbTexels, maxAbsRgb,
                isConstant && sampledTexels > 0, first, sampledTexels);
        }

        private SourceImage ReadOriginal(Texture texture)
        {
            SourceImage image;
            bool isReload = false;
            if (_images.TryGetValue(texture, out var cached))
            {
                image = cached;
                if (image.Pixels != null)
                {
                    image.LastAccess = ++_imageCacheClock;
                    return image;
                }
                if (image.Status != "source-cache-evicted") return image;
                isReload = true;
            }
            else
            {
                image = new SourceImage
                {
                    Width = texture.width,
                    Height = texture.height
                };
                _images.Add(texture, image);
            }

            if (texture.dimension != TextureDimension.Tex2D)
            {
                image.Status = "source-non-2D-texture";
                return image;
            }
            long pixelCount = (long)texture.width * texture.height;
            if (pixelCount <= 0 || pixelCount > _residentPixelBudget)
            {
                // A single image larger than the bounded in-memory working
                // set is not analysed. This is distinct from the earlier
                // cumulative cap which starved later, smaller images.
                image.Status = "source-exceeds-per-image-memory-budget";
                return image;
            }

            // Keep at most the configured number of Color samples strongly
            // referenced. Later materials can still be analyzed after earlier
            // images are evicted. All image-wide statistics remain cached.
            while (_residentPixels + pixelCount > _residentPixelBudget)
            {
                var victim = _images.Values
                    .Where(candidate => candidate.Pixels != null)
                    .OrderBy(candidate => candidate.LastAccess)
                    .FirstOrDefault();
                if (victim == null)
                {
                    image.Status = "source-cache-eviction-failed";
                    return image;
                }
                _residentPixels -= (long)victim.Width * victim.Height;
                victim.Pixels = null;
                victim.Status = "source-cache-evicted";
                ++_imageCacheEvictions;
            }
            if (isReload) ++_imageCacheReloads;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            RenderTexture? staging = null;
            Texture2D? copy = null;
            var previousActive = RenderTexture.active;
            string directFailure = "not-readable";
            string stagingFailure = "not-attempted";
            try
            {
                // Reading imported Texture2Ds directly with AsyncGPUReadback
                // may fail for compressed or limited GPU formats. Prefer the
                // real source texels on CPU when Unity makes them readable.
                if (texture is Texture2D source2D && source2D.isReadable)
                {
                    try
                    {
                        var sourcePixels = source2D.GetPixels(0);
                        if (sourcePixels.Length == pixelCount)
                        {
                            image.Pixels = sourcePixels;
                            image.Status = "source-CPU-float";
                            return image;
                        }
                        directFailure = "CPU-size-mismatch";
                    }
                    catch (Exception ex)
                    {
                        directFailure = "CPU-" + ex.GetType().Name;
                    }
                }

                // 8-bit RGBA staging can quantize genuinely nonzero emission
                // texels to black (for example sRGB 1/255 converted to linear).
                // That would yield a false "unused" diagnosis. Fail closed
                // rather than fall back to RGBA32 when RGBAFloat is unavailable.
                if (!SystemInfo.SupportsRenderTextureFormat(
                        RenderTextureFormat.ARGBFloat))
                {
                    image.Status = "source-float-staging-unsupported";
                    return image;
                }
                // Stage a copy of the original GPU image, never the generated
                // atlas. 1:1 Blit can involve color-space conversion, so this
                // still is a mip0 screening observation, not lossless proof.
                staging = RenderTexture.GetTemporary(texture.width,
                    texture.height, 0, RenderTextureFormat.ARGBFloat,
                    RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, staging);

                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    try
                    {
                        var request = AsyncGPUReadback.Request(
                            staging, 0, TextureFormat.RGBAFloat);
                        request.WaitForCompletion();
                        if (!request.hasError)
                        {
                            var data = request.GetData<Color>();
                            if (data.Length == pixelCount)
                            {
                                image.Pixels = data.ToArray();
                                image.Status = "source-GPU-staged-RGBAFloat";
                                return image;
                            }
                            stagingFailure = "GPU-staging-size-mismatch";
                        }
                        else
                        {
                            stagingFailure = "GPU-staging-readback-error";
                        }
                    }
                    catch (Exception ex)
                    {
                        stagingFailure = "GPU-staging-" + ex.GetType().Name;
                    }
                }
                else
                {
                    stagingFailure = "GPU-readback-unsupported";
                }

                // Final fallback when AsyncGPUReadback is unsupported or
                // fails even for the staging RT. ReadPixels transfers only
                // the temporary staging target, never changes a source asset.
                copy = new Texture2D(texture.width, texture.height,
                    TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = staging;
                copy.ReadPixels(new Rect(0, 0, texture.width,
                    texture.height), 0, 0, false);
                var fallbackPixels = copy.GetPixels(0);
                if (fallbackPixels.Length == pixelCount)
                {
                    image.Pixels = fallbackPixels;
                    image.Status = "source-GPU-staged-ReadPixelsFloat"
                        + "(after=" + stagingFailure + ")";
                    return image;
                }
                image.Status = "source-ReadPixels-size-mismatch";
            }
            catch (Exception ex)
            {
                image.Status = "source-unreadable:" + ex.GetType().Name
                    + "(CPU=" + directFailure
                    + ",staging=" + stagingFailure + ")";
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (copy != null)
                    UnityEngine.Object.DestroyImmediate(copy);
                if (staging != null)
                    RenderTexture.ReleaseTemporary(staging);
                clock.Stop();
                if (image.Pixels != null)
                {
                    image.WasReadSuccessfully = true;
                    image.LastAccess = ++_imageCacheClock;
                    _residentPixels += pixelCount;
                    _peakResidentPixels = Math.Max(_peakResidentPixels,
                        _residentPixels);
                    _readbackPixels += pixelCount;
                }
                _readbackMilliseconds += clock.ElapsedMilliseconds;
            }
            return image;
        }

        // Test seam: shares the exact source-reading path used at build time.
        internal (Color[]? pixels, string status) ReadOriginalForTest(Texture texture)
        {
            var source = ReadOriginal(texture);
            return (source.Pixels, source.Status);
        }

        private static bool TryStaticUvTransform(Material material,
            string property, bool isMask,
            out Vector2 scale, out Vector2 offset, out string reason)
        {
            scale = Vector2.one;
            offset = Vector2.zero;
            reason = "";
            if (isMask)
            {
                if (!ZeroVector(material, "_EmissionBlendMask_ScrollRotate")
                    || !ZeroVector(material, "_Emission2ndBlendMask_ScrollRotate")
                    || !ZeroVector(material, "_MainTex_ScrollRotate")
                    || (material.HasProperty("_ShiftBackfaceUV")
                        && material.GetFloat("_ShiftBackfaceUV") != 0f))
                {
                    reason = "nonstatic-or-backface-mask-UV";
                    return false;
                }

                if (material.HasProperty("_MainTex"))
                {
                    var mainScale = material.GetTextureScale("_MainTex");
                    var mainOffset = material.GetTextureOffset("_MainTex");
                    offset = Vector2.Scale(offset, mainScale) + mainOffset;
                    scale = Vector2.Scale(scale, mainScale);
                }
            }
            else
            {
                var mode = property == "_EmissionMap"
                    ? "_EmissionMap_UVMode" : "_Emission2ndMap_UVMode";
                var parallax = property == "_EmissionMap"
                    ? "_EmissionParallaxDepth" : "_Emission2ndParallaxDepth";
                if ((material.HasProperty(mode)
                        && (material.HasInt(mode)
                            ? material.GetInt(mode) != 0
                            : material.GetFloat(mode) != 0f))
                    || (material.HasProperty(parallax)
                        && material.GetFloat(parallax) != 0f)
                    || !ZeroVector(material, property + "_ScrollRotate"))
                {
                    reason = "non-UV0-or-dynamic-emission-map";
                    return false;
                }
            }
            var texScale = material.GetTextureScale(property);
            var texOffset = material.GetTextureOffset(property);
            offset = Vector2.Scale(offset, texScale) + texOffset;
            scale = Vector2.Scale(scale, texScale);
            if (!ValidFloat(scale.x) || !ValidFloat(scale.y)
                || !ValidFloat(offset.x) || !ValidFloat(offset.y))
            {
                reason = "invalid-source-UV-transform";
                return false;
            }
            return true;
        }

        private static bool ZeroVector(Material material, string property) =>
            !material.HasProperty(property)
            || material.GetVector(property) == Vector4.zero;

        private static bool ValidFloat(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        internal bool TrySourceCoverage(HashSet<(Mesh, int)> slots,
            Vector2 scale, Vector2 offset, int width, int height,
            TextureWrapMode wrapU, TextureWrapMode wrapV, FilterMode filterMode,
            out bool[] coverage, out int coveredCount, out string reason)
        {
            coverage = Array.Empty<bool>();
            coveredCount = 0;
            reason = "";
            if (width <= 0 || height <= 0
                || (long)width * height > _residentPixelBudget)
            {
                reason = "invalid-source-size";
                return false;
            }
            if (filterMode != FilterMode.Point
                && filterMode != FilterMode.Bilinear
                && filterMode != FilterMode.Trilinear)
            {
                reason = "source-filter-unsupported";
                return false;
            }
            float radius = filterMode == FilterMode.Point ? 0.5f : 1f;
            coverage = new bool[width * height];
            var uvs = new List<Vector2>();
            bool encountered = false;
            long checksThisCoverage = 0;

            foreach (var slot in slots)
            {
                try
                {
                    uvs.Clear();
                    slot.Item1.GetUVs(0, uvs);
                    var indices = slot.Item1.GetTriangles(slot.Item2);
                    if (slot.Item1.GetTopology(slot.Item2) != MeshTopology.Triangles
                        || uvs.Count == 0 || indices.Length % 3 != 0)
                    {
                        reason = "missing-source-UV0-or-triangles";
                        return false;
                    }
                    for (int t = 0; t < indices.Length; t += 3)
                    {
                        var i0 = indices[t];
                        var i1 = indices[t + 1];
                        var i2 = indices[t + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0
                            || i0 >= uvs.Count || i1 >= uvs.Count
                            || i2 >= uvs.Count)
                        {
                            reason = "invalid-source-index";
                            return false;
                        }
                        encountered = true;
                        var a = Vector2.Scale(uvs[i0], scale) + offset;
                        var b = Vector2.Scale(uvs[i1], scale) + offset;
                        var c = Vector2.Scale(uvs[i2], scale) + offset;
                        if (!InsideAtlasUV(a) || !InsideAtlasUV(b)
                            || !InsideAtlasUV(c))
                        {
                            reason = "source-UV-outside-0-to-1";
                            return false;
                        }

                        a = new Vector2(a.x * width - 0.5f, a.y * height - 0.5f);
                        b = new Vector2(b.x * width - 0.5f, b.y * height - 0.5f);
                        c = new Vector2(c.x * width - 0.5f, c.y * height - 0.5f);

                        int x0 = Mathf.FloorToInt(
                            Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - radius);
                        int x1 = Mathf.CeilToInt(
                            Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + radius);
                        int y0 = Mathf.FloorToInt(
                            Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - radius);
                        int y1 = Mathf.CeilToInt(
                            Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + radius);
                        long checks = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                        // Apply the work cap per distinct UV-coverage query,
                        // not cumulatively across the entire diagnostic.
                        // Otherwise early textures exhaust the cap and all
                        // later materials become unknown regardless of cost.
                        if (checks > MaxTrianglePixelChecks - checksThisCoverage)
                        {
                            reason = "source-per-coverage-raster-budget";
                            return false;
                        }
                        checksThisCoverage += checks;
                        _triangleChecks += checks;

                        for (int y = y0; y <= y1; ++y)
                            for (int x = x0; x <= x1; ++x)
                            {
                                if (!TriangleOverlapsTexelFilter(
                                        new Vector2(x, y), a, b, c, radius)) continue;
                                if (!TryWrapTexel(x, width, wrapU,
                                        out var wrappedX)
                                    || !TryWrapTexel(y, height, wrapV,
                                        out var wrappedY))
                                {
                                    reason = "source-wrap-mode-unknown";
                                    return false;
                                }
                                int index = wrappedY * width + wrappedX;
                                if (coverage[index]) continue;
                                coverage[index] = true;
                                ++coveredCount;
                            }
                    }
                }
                catch (Exception ex)
                {
                    reason = "source-mesh-read-error:" + ex.GetType().Name;
                    return false;
                }
            }
            if (!encountered || coveredCount == 0)
            {
                reason = "no-source-UV-coverage";
                return false;
            }
            return true;
        }

        private static bool TryWrapTexel(
            int texel, int size, TextureWrapMode mode, out int wrapped)
        {
            wrapped = Mathf.Clamp(texel, 0, size - 1);
            if (texel >= 0 && texel < size) return true;
            if (mode == TextureWrapMode.Clamp)
                return true;
            // Mirrored addressing outside [0,1] must not silently
            // be treated as Clamp; let the diagnostic return unknown.
            if (mode != TextureWrapMode.Repeat) return false;
            wrapped = ((texel % size) + size) % size;
            return true;
        }

        private static bool InsideAtlasUV(Vector2 pos) =>
            ValidFloat(pos.x) && ValidFloat(pos.y)
            && pos.x >= 0f && pos.x <= 1f && pos.y >= 0f && pos.y <= 1f;

        internal static bool TriangleOverlapsTexelFilter(
            Vector2 pixel, Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            var maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            var minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            var maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            // A triangle which only touches the boundary of the filter
            // square has zero bilinear weight at that texel: do not treat
            // such a tangency as nonzero emission.
            if (maxX <= pixel.x - radius
                || minX >= pixel.x + radius
                || maxY <= pixel.y - radius
                || minY >= pixel.y + radius) return false;
            return OverlapAxis(pixel, a, b, c, radius)
                && OverlapAxis(pixel, b, c, a, radius)
                && OverlapAxis(pixel, c, a, b, radius);
        }

        private static bool OverlapAxis(
            Vector2 pixel, Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var edge = b - a;
            var normal = new Vector2(-edge.y, edge.x);
            float aa = Vector2.Dot(a, normal);
            float bb = Vector2.Dot(b, normal);
            float cc = Vector2.Dot(c, normal);
            float center = Vector2.Dot(pixel, normal);
            float projectedRadius = (Mathf.Abs(normal.x) + Mathf.Abs(normal.y))
                * radius;
            return Mathf.Max(aa, Mathf.Max(bb, cc)) > center - projectedRadius
                && Mathf.Min(aa, Mathf.Min(bb, cc)) < center + projectedRadius;
        }

        internal string Report()
        {
            var project = new System.IO.DirectoryInfo(Application.dataPath)
                .Parent?.Name ?? "(unknown)";
            return "[Original-source texture probe: TEMPORARY; observation only]"
                + "\nUnity project: " + project
                + "\nLayers observed: " + _observed
                + "\nSource mip0 zero-emission candidates: " + _candidates
                + "\nUnknown layers: " + _unknown
                + "\nOriginal-source image pixels read (including reloads): " + _readbackPixels
                + "\nUnique 2D source images loaded: "
                + _images.Values.Count(image => image.WasReadSuccessfully)
                + "\nSource image memory cache: peak "
                + _peakResidentPixels + " pixels / budget " + _residentPixelBudget
                + ", evictions " + _imageCacheEvictions
                + ", reloads " + _imageCacheReloads
                + "\nSource image load elapsed: " + _readbackMilliseconds + "ms"
                + "\nProbe observation elapsed: " + _probeMilliseconds + "ms"
                + "\nTriangle pixel checks: " + _triangleChecks
                + " (per-coverage cap: " + MaxTrianglePixelChecks + ")"
                + "\nUnique source textures observed (including unsupported): "
                + _images.Count
                + "\nTexture-property survey: " + _surveyProperties
                + ", whole-source constants: " + _surveyUniformWhole
                + ", UV0-covered constants: " + _surveyUniformUsed
                + ", unknown coverage: " + _surveyUnknownCoverage
                + ", repeated UV calculations avoided: " + _surveyCoverageCacheHits
                + "\nLimits: original mesh UV0 and source image mip0 only;"
                + " geometric point/bilinear texel support (strict float zero test)." 
                + " RGBAFloat source staging is required when CPU pixels are unavailable;"
                + " Nonzero RGB count/max are observational, not thresholds."
                + " GPU-staged source images may have sampling/color conversion;"
                + " the source-read method is logged for each property."
                + " Unknown UV transforms/animations, mipmaps,"
                + " compression and shader effects are NOT verified."
                + " Clamp/repeat texture-edge texels are included at mip0."
                + " Atlas pixels/background are NEVER sampled."
                + " Candidates are NOT approved-to-remove textures."
                + "\n[Emission use: mip0 diagnostic]"
                + "\n"
                + (_lines.Count == 0
                    ? "No non-Lite lilToon emission source textures found."
                    : string.Join("\n", _lines))
                + "\n\n[Zero emission source-mask output ownership: TEMPORARY]"
                + "\nThis section describes resource ownership only."
                + " It does not authorize shader changes or texture removal."
                + "\n" + (_zeroMaskOwnershipLines.Count == 0
                    ? "No zero-mask ownership candidates observed."
                    : string.Join("\n", _zeroMaskOwnershipLines))
                + "\n\n[Zero-mask shadow atlas replan: TEMPORARY]" 
                + "\nGenerated meshes, textures and materials are not applied. An assumed removal is not shader-equivalence proof."
                + "\n" + (_shadowReplanLines.Count == 0
                    ? "No shadow-replan candidate evaluated."
                    : string.Join("\n", _shadowReplanLines))
                + "\n\n[Shader-neutral feature-disable candidates: TEMPORARY]"
                + "\nOnly strict mip0 sampling is tested. No animation, mip, shader-variant or render-equivalence proof."
                + "\n" + (_featureCandidates.Count == 0
                    ? "No feature candidates detected."
                    : string.Join("\n", _featureCandidates.Distinct().Select(item =>
                        "material=" + item.material.name
                        + ", property=" + item.property
                        + ", feature=" + item.rule.Name
                        + ", switch=" + item.rule.SwitchProperty
                        + ", condition=" + item.rule.Condition)))
                + "\n[Rejected zero-mask candidates]"
                + "\n" + (_featureRejections.Count == 0
                    ? "None."
                    : string.Join("\n", _featureRejections.Distinct()))
                + "\n\n[Feature-disable merge-planner comparison: TEMPORARY]"
                + "\n" + (_featureDisableLines.Count == 0
                    ? "No feature-disable comparison run."
                    : string.Join("\n", _featureDisableLines))
                + "\n\n[GPU-first UV source survey: diagnostic only]"
                + "\nGPU screens: " + _gpuScreens
                + ", GPU variable / CPU skipped: " + _gpuRejectedVariable
                + ", GPU uniform / CPU checked: " + _gpuUniformCandidates
                + ", boundary confirmed: " + _gpuStrictConfirmed
                + ", boundary rejected: " + _gpuStrictRejected
                + ", GPU fallback: " + _gpuFallbacks
                + ", Y-flip corrected: " + _gpuFlippedRows
                + "\nGPU-screen elapsed: " + FormatMs(_gpuScreenMs)
                + "ms, CPU strict coverage elapsed (candidates, fallback,"
                + " whole-source constants): " + FormatMs(_gpuStrictMs) + "ms"
                + "\nGPU variable source observations are interior-only;"
                + " they do not report a full CPU-filter texel count."
                + " The CPU validator still determines all uniform candidates."
                + " Original Emission-layer diagnostics remain unchanged."
                + "\n" + (_gpuStatusLines.Count == 0
                    ? "No GPU source survey entries."
                    : string.Join("\n", _gpuStatusLines))
                + "\n\n[Source texture property survey: mip0; informational]"
                + "\nWhole-source and UV-covered constants are observations,"
                + " NOT removability claims. Unsupported UV usage is unknown."
                + "\n"
                + (_sourceSurvey.Count == 0
                    ? "No source texture properties observed."
                    : string.Join("\n", _sourceSurvey));
        }
    }

    internal sealed class AutoAtlasEmissionProbeError : SimpleError
    {
        private readonly string _message;
        internal AutoAtlasEmissionProbeError(string message)
        {
            _message = message;
        }

        public override Localizer Localizer => TTTLog.NDMFLocalizer;
        public override ErrorSeverity Severity => ErrorSeverity.Information;
        public override string TitleKey =>
            "AutoAtlasTexture:info:EmissionProbeTitle";
        public override string[] TitleSubst => Array.Empty<string>();

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            // The NDMF console already owns vertical scrolling. A nested
            // fixed-height ScrollView traps its wheel events and prevents
            // users from navigating the surrounding diagnostics.
            var root = new VisualElement();
            root.style.minWidth = 0;
            foreach (var line in _message.Split(
                         new[] { '\n' }, StringSplitOptions.None))
            {
                var label = new Label(line) { enableRichText = false };
                label.style.whiteSpace = WhiteSpace.Normal;
                if (line.Length == 0)
                    label.style.minHeight = EditorGUIUtility.singleLineHeight;
                root.Add(label);
            }
            root.Add(new Button(() =>
                EditorGUIUtility.systemCopyBuffer = _message)
            {
                text = "AutoAtlasTexture:button:EmissionProbeCopy".GetLocalize(),
            });
            return root;
        }
    }

    /// <summary>
    /// Detects visibility animation on renderers and their parent GameObjects.
    /// This only determines whether a property can take a value different from
    /// its current one; it does not infer runtime usage frequency or menu reachability.
    /// </summary>
    internal static class AutoAtlasTextureVisibilityAnimationDetector
    {
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            ObjectPathRemapper pathRemapper,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers)
        {
            return Detect(
                animationIndex,
                avatarRoot,
                renderers,
                transform =>
                {
                    // An NDMF ReplaceObject can map multiple historical animation
                    // paths to the same current object. Inspect every alias.
                    // Register new objects first, just as the original lookup did.
                    pathRemapper.GetVirtualPathForObject(transform);
                    return pathRemapper.GetAllPathsForObject(transform);
                });
        }

        // Allows tests to supply deterministic object paths without requiring
        // a live NDMF BuildContext.
        internal static HashSet<Renderer> Detect(
            AnimationIndex animationIndex,
            Transform avatarRoot,
            IEnumerable<Renderer> renderers,
            Func<Transform, IEnumerable<string>> pathsForObject)
        {
            var animatedRenderers = new HashSet<Renderer>();
            var gameObjectVisibility = new Dictionary<Transform, bool>();

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var transform = renderer.transform;
                var cursor = transform;
                var insideAvatar = false;
                while (cursor != null)
                {
                    if (cursor == avatarRoot)
                    {
                        insideAvatar = true;
                        break;
                    }
                    cursor = cursor.parent;
                }
                if (!insideAvatar) continue;

                if (HasAnimatedVisibility(
                        animationIndex, pathsForObject(transform),
                        renderer.GetType(), "m_Enabled", renderer.enabled))
                {
                    animatedRenderers.Add(renderer);
                    continue;
                }

                for (cursor = transform; cursor != null; cursor = cursor.parent)
                {
                    if (!gameObjectVisibility.TryGetValue(cursor, out var animated))
                    {
                        animated = HasAnimatedVisibility(
                            animationIndex, pathsForObject(cursor),
                            typeof(GameObject), "m_IsActive",
                            cursor.gameObject.activeSelf);
                        gameObjectVisibility.Add(cursor, animated);
                    }

                    if (animated)
                    {
                        animatedRenderers.Add(renderer);
                        break;
                    }

                    if (cursor == avatarRoot) break;
                }
            }

            return animatedRenderers;
        }

        private static bool HasAnimatedVisibility(
            AnimationIndex index, IEnumerable<string> paths,
            Type componentType, string propertyName, bool currentValue)
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                foreach (var clip in index.GetClipsForObjectPath(path))
                {
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (binding.path != path
                            || binding.propertyName != propertyName
                            || binding.type == null
                            || !binding.type.IsAssignableFrom(componentType)
                            || (propertyName == "m_IsActive"
                                ? binding.type != typeof(GameObject)
                                : !typeof(Renderer).IsAssignableFrom(binding.type)))
                            continue;

                        if (CurveChangesVisibility(
                                clip.GetFloatCurve(binding), currentValue))
                            return true;
                    }
                }
            }

            return false;
        }

        internal static bool CurveChangesVisibility(
            AnimationCurve? curve, bool currentValue)
        {
            if (curve == null) return false;
            // Boolean animation properties are encoded as float 0 or 1.
            // Curves that keep the current state must not exclude an item.
            return curve.keys.Any(key =>
                !float.IsNaN(key.value)
                && !float.IsInfinity(key.value)
                && (key.value >= 0.5f) != currentValue);
        }
    }

    internal static class AutoAtlasTextureProcessor
    {
        internal sealed class ExecutionGroup
        {
            internal readonly HashSet<Material> Materials;
            internal readonly Renderer[] Renderers;
            internal readonly HashSet<Texture> SourceTextures;
            internal readonly HashSet<Texture> RetainedSourceTextures;
            internal readonly AutoAtlasTextureMergePlanner.MergeSelection? MergeSelection;

            internal ExecutionGroup(
                HashSet<Material> materials,
                Renderer[] renderers,
                HashSet<Texture> sourceTextures,
                HashSet<Texture> retainedSourceTextures,
                AutoAtlasTextureMergePlanner.MergeSelection? mergeSelection)
            {
                Materials = materials;
                Renderers = renderers;
                SourceTextures = sourceTextures;
                RetainedSourceTextures = retainedSourceTextures;
                MergeSelection = mergeSelection;
            }
        }

        internal static Queue<ExecutionGroup> CreateExecutionGroups(
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups,
            IReadOnlyList<AutoAtlasTextureMergePlanner.MergeSelection> selections)
        {
            var selectedIds = selections.SelectMany(selection => selection.GroupIds)
                .ToHashSet();
            var queue = new Queue<ExecutionGroup>();

            foreach (var selection in selections)
            {
                var selected = selection.GroupIds
                    .Select(id => preparedGroups[id])
                    .ToArray();
                queue.Enqueue(new ExecutionGroup(
                    selected.SelectMany(group => group.Materials).ToHashSet(),
                    selected.SelectMany(group => group.Renderers).Distinct().ToArray(),
                    selected.SelectMany(group => group.SourceTextures).ToHashSet(),
                    selected.SelectMany(group => group.RetainedSourceTextures).ToHashSet(),
                    selection));
            }

            foreach (var prepared in preparedGroups.Where(
                         group => !selectedIds.Contains(group.Id)))
            {
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    prepared.RetainedSourceTextures,
                    null));
            }

            return queue;
        }

        internal static void EnqueueStandaloneFallback(
            Queue<ExecutionGroup> queue,
            AutoAtlasTextureMergePlanner.MergeSelection failedMerge,
            IReadOnlyList<AutoAtlasTextureMergePlanner.PreparedGroup> preparedGroups)
        {
            foreach (var id in failedMerge.GroupIds)
            {
                var prepared = preparedGroups[id];
                queue.Enqueue(new ExecutionGroup(
                    prepared.Materials,
                    prepared.Renderers,
                    prepared.SourceTextures,
                    prepared.RetainedSourceTextures,
                    null));
            }
        }

        private static string FormatLocalized(
            string key,
            params object[] args)
        {
            return string.Format(key.GetLocalize(), args);
        }

        internal static AutoAtlasTexture[] GetActiveConfigurations(GameObject root)
        {
            return root.GetComponentsInChildren<AutoAtlasTexture>(true)
                .Where(component => component != null &&
                    TexTransBehaviorSearch.CheckIsActive(component.gameObject, root))
                .ToArray();
        }

        internal static List<Renderer> GetAutoAtlasAllowedRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            // AutoAtlasTexture adds automatic material selection and lossless
            // sizing on top of AtlasTexture. Renderer eligibility must therefore
            // follow AtlasTexture's normal default target resolution instead of
            // inventing a broader "include disabled" rule.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            return AtlasTexture.GetAtlasAllowedRenderers(
                    domain,
                    domain.EnumerateRenderer(),
                    includeDisabledRenderer: false)
                .Where(renderer => !excludedRenderers.Contains(renderer))
                .ToList();
        }

        internal static Renderer[] ResolveAutoAtlasTargetRenderers(
            IRendererTargeting domain,
            AutoAtlasTexture configuration,
            HashSet<Material> targetMaterials)
        {
            var allowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var targetRenderers = AtlasTexture.FilterTargetRenderers(
                domain, allowedRenderers, targetMaterials);
            return AtlasTexture.FilterExistUVChannel(
                domain, targetRenderers, UVChannel.UV0);
        }

        internal static Renderer[] GetAAONegotiationRenderers(
            IRendererTargeting domain, AutoAtlasTexture configuration)
        {
            return GetAutoAtlasAllowedRenderers(domain, configuration)
                .ToArray();
        }

        internal static void Execute(
            IDomain domain,
            AutoAtlasTexture configuration,
            IReadOnlyCollection<AtlasTexture> manualAtlases,
            AnimationIndex? animationIndex = null,
            ObjectPathRemapper? objectPathRemapper = null,
            Transform? avatarRoot = null)
        {
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();

            // Store only the NDMF console summary for this build.
            var completed = new List<string>();

            // Temporary emission-content survey; never alters atlas selection.
            var emissionProbe = new AutoAtlasEmissionProbe();
            // Expensive full-pipeline diagnostic: at most one hypothetical use per build.
            var shadowReplansRemaining = 1;


            var twoDimensionalFallbackReasons = new List<string>();

            string OriginalMaterialName(Material material)
            {
                // Reuse NDMF's object identity; build-stage suffixes are not
                // stripped or guessed from the material name.
                var identity = ObjectRegistry.GetReference(material);
                if (identity?.Object is Material source && source != null) return source.name;
                // ObjectReference snapshots its display name at creation; this
                // remains available even if the original Unity object was destroyed.
                return identity?.ToString() ?? material.name;
            }

            var allRenderers = domain.EnumerateRenderer()
                .Where(renderer => renderer != null)
                .ToArray();

            // Auto-select materials from the same renderer population that a
            // normal AtlasTexture with its default IncludeDisabledRenderer=false
            // would use. Additional referenced materials are still inspected
            // separately by the source-texture exclusivity guard.
            var excludedRenderers = domain
                .GetDomainsRenderers(configuration.ExcludedRenderers)
                .ToHashSet();
            var atlasAllowedRenderers =
                GetAutoAtlasAllowedRenderers(domain, configuration);
            var candidateMaterials = atlasAllowedRenderers
                .SelectMany(domain.GetMaterials)
                .UOfType<Material>()
                .ToHashSet();
            var allReferencedMaterials = domain.GetAllMaterials();
            if (candidateMaterials.Count == 0)
            {
                processingTimer.Stop();
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            // Material grouping remains shader/property-aware, but it is now
            // scoped to the same renderer-selected materials as AtlasTexture.
            var textureUsages = new MaterialGroupingContext(
                candidateMaterials,
                UVChannel.UV0,
                primaryTexturePropertyOrMaximum: null);
            var propertyTextures = textureUsages.ContainsTextureDictionaries;

            var excludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.ExcludedMaterials);
            var mergingExcludedRenderers = domain
                .GetDomainsRenderers(configuration.TextureMergingExcludedRenderers)
                .ToHashSet();
            var mergingExcludedMaterials = domain.GetDomainsMaterialsHashSet(
                configuration.TextureMergingExcludedMaterials);
            var animatedVisibilityRenderers =
                configuration.EnableAdditionalAtlasMerging
                && configuration.ExcludeVisibilityAnimatedItemsFromTextureMerging
                && animationIndex != null
                && objectPathRemapper != null
                && avatarRoot != null
                    ? AutoAtlasTextureVisibilityAnimationDetector.Detect(
                        animationIndex, objectPathRemapper, avatarRoot,
                        allRenderers)
                    : new HashSet<Renderer>();

            // Resolve manual AtlasTexture targets through TTT's existing lookup
            // rather than rescanning hierarchy and comparing raw user selections.
            var manuallySelectedMaterials = manualAtlases
                .SelectMany(atlas => atlas.GetTargetMaterials(domain,
                    AtlasTexture.GetAtlasAllowedRenderers(domain,
                        domain.EnumerateRenderer(), atlas.AtlasSetting.IncludeDisabledRenderer)))
                .ToHashSet();

            var settings = new AtlasSetting
            {
                AutoAtlasTextureSize = true,
                UsePrimaryMaximumTexture = true,
                IncludeDisabledRenderer = false,
                AtlasTargetUVChannel = UVChannel.UV0,
                IslandPadding = Mathf.Clamp(configuration.IslandPadding, 0f, 0.05f),
            };
            var groups = MakeTextureConnectedGroups(
                candidateMaterials, propertyTextures);
            var preparedGroups =
                new List<AutoAtlasTextureMergePlanner.PreparedGroup>();

            foreach (var group in groups)
            {
                if (group.Any(material => excludedMaterials.Contains(material)
                    || manuallySelectedMaterials.Contains(material)))
                {
                    continue;
                }

                // Preserve the explicit AutoAtlas renderer exclusion contract.
                // A shared material would otherwise be replaced globally even
                // though the user explicitly excluded one of its consumers.
                var explicitlyExcludedConsumers = excludedRenderers
                    .Where(renderer => domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(group.Contains))
                    .ToArray();
                if (explicitlyExcludedConsumers.Length != 0)
                {
                    continue;
                }

                // Resolve renderers through the same AtlasTexture-equivalent
                // path that execution revalidation uses.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                if (atlasRenderers.Length == 0)
                {
                    continue;
                }

                // Material replacement is domain-wide. A currently
                // active consumer that cannot receive the corresponding UV
                // rewrite would otherwise get an atlased material on its
                // original mesh. Disabled/inactive consumers remain outside
                // this check to preserve AtlasTexture's default targeting.
                if (HasActiveUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers))
                {
                    continue;
                }

                var texturesToReplace = group
                    .SelectMany(material => propertyTextures[material].Values)
                    .Where(texture => texture != null)
                    .ToHashSet();
                if (texturesToReplace.Count == 0)
                {
                    continue;
                }

                var retainedSourceTextures =
                    FindRetainedSourceTextures(
                        allReferencedMaterials,
                        group,
                        propertyTextures,
                        texturesToReplace);

                preparedGroups.Add(new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = preparedGroups.Count,
                    Materials = group,
                    Renderers = atlasRenderers,
                    SourceTextures = texturesToReplace,
                    RetainedSourceTextures = retainedSourceTextures,
                    OriginalPixels = texturesToReplace.Sum(texture =>
                        (long)texture.width * texture.height),
                    ExcludedFromTextureMerging = IsExcludedFromTextureMerging(
                        group, atlasRenderers, mergingExcludedMaterials,
                        mergingExcludedRenderers)
                        || atlasRenderers.Any(animatedVisibilityRenderers.Contains),
                });
            }

            var mergePlan = AutoAtlasTextureMergePlanner.Plan(
                domain,
                configuration,
                settings,
                propertyTextures,
                preparedGroups);

            // Source shader-feature hypotheses and their full merge-planner
            // counterfactual MUST be evaluated before any application changes
            // the domain's materials or meshes. Previously this ran inside
            // the execution loop, making processed renderers vanish from the
            // counterfactual and producing misleading 29->17 group counts.
            foreach (var prepared in preparedGroups)
            {
                try
                {
                    emissionProbe.Observe(domain, prepared.Renderers,
                        prepared.Materials.ToDictionary(material => material,
                            material => material));
                }
                catch (Exception ex)
                {
                    emissionProbe.AddFeatureDisableLine(
                        "source-feature-survey=unknown:" + ex.GetType().Name);
                }
            }

            var hypotheses = emissionProbe.GetFeatureCandidates()
                .Where(item => preparedGroups.Any(group =>
                    group.Materials.Contains(item.material)))
                .GroupBy(item => (item.material, item.rule.SwitchProperty))
                .Select(group => group.First())
                .OrderBy(item => item.material.name, StringComparer.Ordinal)
                .ThenBy(item => item.rule.Name, StringComparer.Ordinal)
                .Select(item => (item.material, item.rule))
                .ToArray();
            emissionProbe.AddFeatureDisableLine(
                "live-output-trial-modes=" + AutoAtlasFeatureTrialSession.EnabledModeSummary
                + ", applies-only-to-standalone-groups"
                + ", visual-equivalence=NOT-PROVEN, disabled-by-default");
            if (hypotheses.Length != 0)
            {
                try
                {
                    emissionProbe.AddFeatureDisableLine(
                        AutoAtlasShadowReplan.CompareMergePlans(
                            domain, configuration, settings,
                            preparedGroups, propertyTextures, mergePlan,
                            hypotheses));
                }
                catch (Exception ex)
                {
                    emissionProbe.AddFeatureDisableLine(
                        "featureSet=combined, planner=unknown:"
                        + ex.GetType().Name + ":"
                        + ex.Message.Replace('\n', ' '));
                }
            }

            var executionGroups = CreateExecutionGroups(
                preparedGroups, mergePlan.Selections);

            var materialBlock = "AutoAtlasTexture:info:MaterialBlock".GetLocalize();
            while (executionGroups.Count != 0)
            {
                var execution = executionGroups.Dequeue();
                var group = execution.Materials;

                // Revalidate against the exact same renderer-targeting rule
                // used during planning. Do not broaden execution to every
                // domain-wide material consumer; AutoAtlas is required to
                // preserve AtlasTexture's normal target semantics.
                var atlasRenderers = ResolveAutoAtlasTargetRenderers(
                    domain, configuration, group);
                var plannedRenderers = execution.Renderers
                    .Where(renderer => renderer != null)
                    .Distinct()
                    .ToHashSet();

                var hasActiveUntransformedConsumer =
                    HasActiveUntransformedMaterialConsumer(
                        domain,
                        group,
                        atlasRenderers);
                if (atlasRenderers.Length == 0
                    || !plannedRenderers.SetEquals(atlasRenderers)
                    || hasActiveUntransformedConsumer)
                {
                    if (execution.MergeSelection is { } invalidMerge)
                    {
                        var reason = hasActiveUntransformedConsumer
                            ? FormatLocalized(
                                "AutoAtlasTexture:fallback:ActiveUntransformedConsumer")
                            : FormatLocalized(
                                "AutoAtlasTexture:fallback:RendererSetChanged");
                        twoDimensionalFallbackReasons.Add(
                            $"groups={string.Join(",", invalidMerge.GroupIds.Select(id => "g" + id))}: "
                            + reason);
                        EnqueueStandaloneFallback(
                            executionGroups, invalidMerge, preparedGroups);
                    }
                    continue;
                }

                var originalPixels = execution.SourceTextures.Sum(texture =>
                    (long)texture.width * texture.height);

                // Explicit debug-only A/B trial. Merged atomic groups are
                // intentionally not altered: their planned envelopes were
                // calculated for the unmodified source inputs.
                var trialCandidates = hypotheses
                    .Where(item => group.Contains(item.material)
                        && AutoAtlasFeatureTrialSession.EnabledFor(item.rule))
                    .ToArray();
                if (execution.MergeSelection != null && trialCandidates.Length != 0)
                {
                    foreach (var item in trialCandidates)
                        emissionProbe.AddFeatureDisableLine(
                            "trial=skipped-merged-execution, material="
                            + item.material.name + ", feature=" + item.rule.Name);
                    trialCandidates = Array.Empty<(Material material,
                        AutoAtlasFeatureDisableRules.Rule rule)>();
                }
                IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>>?
                    projectedTextureUsesForTrial = trialCandidates.Length == 0
                        ? null
                        : AutoAtlasShadowReplan.ProjectFeatureSetUses(
                            propertyTextures, trialCandidates);

                var engine = domain.GetTexTransCoreEngineForUnity();

                IReadOnlyDictionary<Material, int>? mergeAtomicGroupByMaterial = null;
                if (execution.MergeSelection is { } selectedMerge)
                {
                    mergeAtomicGroupByMaterial = selectedMerge.GroupIds
                        .Select(id => preparedGroups[id])
                        .SelectMany(group => group.Materials.Select(
                            material => (material, group.Id)))
                        .ToDictionary(pair => pair.material, pair => pair.Id);
                }

                AutoAtlas2DMergeIslandRelocator? twoDimensionalRelocator = null;
                string? twoDimensionalSizeRejection = null;
                var executionMetrics = default(AtlasTexture.AtlasExecutionMetrics);
                var atlasResult = AtlasTexture.DoAtlasTexture(
                    domain,
                    engine,
                    group,
                    atlasRenderers,
                    new List<IIslandSizePriorityTuner?>(),
                    settings,
                    new AtlasTexture.AtlasExecutionOptions
                    {
                        RequireLossless = true,
                        ReportProgressInfo = false,
                        IslandRelocatorFactory = mergeAtomicGroupByMaterial != null
                            ? atlasContext =>
                            {
                                if (execution.MergeSelection?.PackedRegions
                                    is { } packedRegions)
                                {
                                    twoDimensionalRelocator =
                                        new AutoAtlas2DMergeIslandRelocator(
                                            atlasContext,
                                            mergeAtomicGroupByMaterial,
                                            packedRegions,
                                            execution.MergeSelection.PlannedLayoutSize.x);
                                    return twoDimensionalRelocator;
                                }

                                throw new InvalidOperationException(
                                    "Production merge candidates must have 2D packed regions.");
                            }
                            : null,
                        ForcedAutoAtlasWidth =
                            execution.MergeSelection?.PackedRegions != null
                                ? execution.MergeSelection.PlannedLayoutSize.x
                                : null,
                        AcceptAtlasSize = (atlasContext, size, metrics) =>
                        {
                            executionMetrics = metrics;
                            if (execution.MergeSelection is { } mergeSelection)
                            {
                                // The planner reserves an independently valid
                                // 2D rectangle for each atomic group. Accept only
                                // if execution stays within both the predicted
                                // layout and every predicted property-resolution
                                // envelope.
                                if (metrics.OutputTexturePixelCount <= 0
                                    || (mergeSelection.PackedRegions != null
                                        && metrics.LayoutSize.x
                                            != mergeSelection.PlannedLayoutSize.x)
                                    || metrics.LayoutSize.x
                                        > mergeSelection.PlannedLayoutSize.x
                                    || metrics.LayoutSize.y
                                        > mergeSelection.PlannedLayoutSize.y)
                                {
                                    if (mergeSelection.PackedRegions != null)
                                        twoDimensionalSizeRejection =
                                            FormatLocalized(
                                                "AutoAtlasTexture:fallback:LayoutMismatch",
                                                metrics.LayoutSize.x,
                                                metrics.LayoutSize.y,
                                                mergeSelection.PlannedLayoutSize.x,
                                                mergeSelection.PlannedLayoutSize.y,
                                                metrics.OutputTexturePixelCount);
                                    return false;
                                }

                                foreach (var property in metrics.PropertySizes)
                                {
                                    if (!mergeSelection.PlannedPropertySizes
                                            .TryGetValue(
                                                property.Key,
                                                out var plannedSize)
                                        || property.Value.x > plannedSize.x
                                        || property.Value.y > plannedSize.y)
                                    {
                                        if (mergeSelection.PackedRegions != null)
                                            twoDimensionalSizeRejection =
                                                FormatLocalized(
                                                    "AutoAtlasTexture:fallback:PropertyResolutionMismatch",
                                                    property.Key,
                                                    property.Value.x,
                                                    property.Value.y,
                                                    plannedSize.x,
                                                    plannedSize.y);
                                        return false;
                                    }
                                }

                                return true;
                            }

                            // For standalone AutoAtlas, the pre-FineTuning
                            // property count is not authoritative. Default
                            // ReferenceCopy (and other FineTuning) can remove or
                            // merge outputs. Defer the benefit check until after
                            // DoTextureFinTuning(), before any mesh/material
                            // mutation.
                            return metrics.OutputTexturePixelCount > 0;
                        },
                    }, projectedTextureUsesForTrial);
                if (!atlasResult.IsSuccess)
                {
                    if (execution.MergeSelection is { } failedMerge)
                    {
                        if (failedMerge.PackedRegions != null)
                        {
                            var reason = twoDimensionalRelocator?.FailureDetail;
                            if (string.IsNullOrEmpty(reason))
                                reason = twoDimensionalSizeRejection
                                    ?? FormatLocalized(
                                        "AutoAtlasTexture:fallback:AtlasExecutionFailed");
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", failedMerge.GroupIds.Select(id => "g" + id))}, layout={failedMerge.PlannedLayoutSize.x}×{failedMerge.PlannedLayoutSize.y}: {reason}");
                        }
                        // A merge plan is only an optimization. If its
                        // placement cannot be executed losslessly, retry the
                        // original atomic groups without merging them.
                        EnqueueStandaloneFallback(
                            executionGroups, failedMerge, preparedGroups);
                    }
                    continue;
                }

                using var atlasContext = atlasResult.AtlasContext!;
                var compiledTextures = atlasResult.CompiledAtlasTextures!;

                // ReplaceMesh silently skips a renderer when no corresponding
                // generated atlas mesh can be resolved. That is only harmless
                // when the target material has no drawable geometry on that
                // renderer. Otherwise the later domain-wide material replacement
                // would pair an atlased material with unchanged UVs.
                var unresolvedRequiredRenderer =
                    atlasRenderers.FirstOrDefault(renderer =>
                        RendererRequiresAtlasMesh(
                            domain,
                            renderer,
                            group)
                        && AtlasTexture.ResolveAtlasedMeshForRenderer(
                            domain,
                            renderer,
                            atlasContext,
                            atlasResult.AtlasedMeshes!) is null);
                if (unresolvedRequiredRenderer != null)
                {
                    foreach (var rt in compiledTextures.Values.Distinct())
                        rt.Dispose();
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } unresolvedMerge)
                    {
                        if (unresolvedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", unresolvedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={unresolvedMerge.PlannedLayoutSize.x}×{unresolvedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:RequiredMeshResolutionFailed",
                                    unresolvedRequiredRenderer.name));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            unresolvedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var tuningResult = AtlasTexture.DoTextureFinTuning(
                    engine, atlasContext, settings, compiledTextures, null);

                var finalOutputPixels = tuningResult.TextureDescriptors.Keys
                    .Distinct()
                    .Sum(texture => (long)texture.Width * texture.Hight);
                var retainedSourcePixels = execution.RetainedSourceTextures
                    .Sum(texture => (long)texture.width * texture.height);
                var finalTotalPixels =
                    finalOutputPixels + retainedSourcePixels;
                var pixelBaseline = execution.MergeSelection?.BaselinePixels
                    ?? originalPixels;

                if (finalOutputPixels <= 0
                    || finalTotalPixels >= pixelBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } pixelCheckedMerge)
                    {
                        if (pixelCheckedMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", pixelCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={pixelCheckedMerge.PlannedLayoutSize.x}×{pixelCheckedMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:FinalPixelsNotReduced",
                                    finalTotalPixels,
                                    pixelCheckedMerge.BaselinePixels));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            pixelCheckedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var hasExecutionTextureMemory =
                    AtlasTexture.TryCalculateFinalTextureMemory(
                        atlasContext,
                        tuningResult,
                        out var executionTextureMemoryBytes);
                var hasRetainedSourceMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.RetainedSourceTextures,
                        out var retainedSourceMemoryBytes);
                var hasSourceTextureMemory =
                    AtlasTexture.TryCalculateSourceTextureMemory(
                        domain,
                        execution.SourceTextures,
                        out var sourceTextureMemoryBytes);
                var hasMemoryBaseline =
                    execution.MergeSelection != null
                    || hasSourceTextureMemory;
                var memoryBaseline =
                    execution.MergeSelection?.BaselineBytes
                    ?? sourceTextureMemoryBytes;
                var finalTotalTextureMemoryBytes =
                    executionTextureMemoryBytes + retainedSourceMemoryBytes;

                if (!hasExecutionTextureMemory
                    || !hasRetainedSourceMemory
                    || !hasMemoryBaseline
                    || finalTotalTextureMemoryBytes >= memoryBaseline)
                {
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } memoryCheckedMerge)
                    {
                        if (memoryCheckedMerge.PackedRegions != null)
                        {
                            var reason =
                                !hasExecutionTextureMemory
                                || !hasRetainedSourceMemory
                                    ? FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryUnknown")
                                    : FormatLocalized(
                                        "AutoAtlasTexture:fallback:FinalMemoryNotReduced",
                                        finalTotalTextureMemoryBytes,
                                        memoryCheckedMerge.BaselineBytes);
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", memoryCheckedMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={memoryCheckedMerge.PlannedLayoutSize.x}×{memoryCheckedMerge.PlannedLayoutSize.y}: {reason}");
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            memoryCheckedMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var resultingTextures = tuningResult.RenderTextures.ToDictionary(
                    item => item.Key,
                    item => engine.GetReferenceRenderTexture(item.Value));
                var retainedRenderTextures = tuningResult.TextureDescriptors.Keys.ToHashSet();

                var atlasMaterialMaps =
                    AtlasTexture.GenerateAtlasedMaterialMaps(
                        domain,
                        group,
                        settings,
                        (new List<AtlasTexture.MaterialMergeGroup>(), null, null),
                        resultingTextures,
                        atlasResult.PreserveBump2ndMaterials,
                        atlasResult.PreservedOriginalUVChannel);

                // All changes are confined to freshly generated AtlasedMaterial
                // clones. Nothing is applied to a source Unity material.
                foreach (var item in trialCandidates)
                {
                    if (!atlasMaterialMaps.materialMap.TryGetValue(
                            item.material, out var outputMaterial))
                        throw new InvalidOperationException(
                            "Trial material was not generated: " + item.material.name);
                    AutoAtlasFeatureDisableRules.DisableOnGeneratedMaterial(
                        outputMaterial, item.rule);
                }

                var generatedAtlasMaterials =
                    atlasMaterialMaps.materialMap.Values
                        .Concat(
                            atlasMaterialMaps
                                .domainsMaterial2ReplaceMaterial.Values)
                        .Distinct()
                        .ToArray();

                // References already classified as retained are part of the
                // cost model. Only an unexpected source reference invalidates
                // the prediction and forces a conservative fallback.
                var generatedRetainedSourceTextures =
                    CollectRetainedSourceTextures(
                        generatedAtlasMaterials,
                        execution.SourceTextures);
                if (generatedRetainedSourceTextures.Any(texture =>
                        !execution.RetainedSourceTextures.Contains(texture)))
                {
                    DestroyUnappliedAtlasedMaterials(
                        generatedAtlasMaterials);
                    foreach (var rt in compiledTextures.Values
                                 .Concat(tuningResult.TextureDescriptors.Keys)
                                 .Distinct())
                    {
                        rt.Dispose();
                    }
                    DestroyUnappliedAtlasedMeshes(
                        atlasResult.AtlasedMeshes!);

                    if (execution.MergeSelection is { } retainedSourceMerge)
                    {
                        if (retainedSourceMerge.PackedRegions != null)
                        {
                            twoDimensionalFallbackReasons.Add(
                                $"groups={string.Join(",", retainedSourceMerge.GroupIds.Select(id => "g" + id))}, "
                                + $"layout={retainedSourceMerge.PlannedLayoutSize.x}×{retainedSourceMerge.PlannedLayoutSize.y}: "
                                + FormatLocalized(
                                    "AutoAtlasTexture:fallback:SourceTextureRetained"));
                        }

                        EnqueueStandaloneFallback(
                            executionGroups,
                            retainedSourceMerge,
                            preparedGroups);
                    }

                    continue;
                }

                var resolutionRoutes = AtlasResolutionRouteReport
                    .Collect(atlasContext, tuningResult);
                var block = string.Format(materialBlock,
                    string.Join(", ", group.Select(OriginalMaterialName)
                        .Distinct().OrderBy(name => name, StringComparer.Ordinal)),
                    executionMetrics.TopFreeFraction,
                    TTTLog.FormatLocalizedLines(
                        AtlasResolutionRouteReport.CreateLocalizedLogLines(
                            resolutionRoutes)));

                if (hasSourceTextureMemory)
                {
                    block += "\n" + string.Format(
                        "AtlasTexture:info:TargetTextureMemory".GetLocalize(),
                        AtlasTexture.FormatTextureMemory(sourceTextureMemoryBytes),
                        AtlasTexture.FormatTextureMemory(
                            finalTotalTextureMemoryBytes));
                }

                // Source readback and merge comparison already ran on the
                // original domain before any execution changed its state.
                // Here only final-output ownership is inspected.
                try
                {
                    emissionProbe.InspectZeroMaskOutputOwnership(
                        domain, atlasContext, tuningResult,
                        atlasMaterialMaps.materialMap, resultingTextures,
                        execution.RetainedSourceTextures, executionMetrics);
                }
                catch (Exception ex)
                {
                    // Diagnostics are observational and must never block
                    // an otherwise valid avatar atlas build.
                    emissionProbe.RecordOwnershipDiagnosticError(ex);
                }

                if (shadowReplansRemaining > 0 && execution.MergeSelection == null)
                {
                    var candidate = emissionProbe.GetZeroMaskCandidates()
                        .Where(item => group.Contains(item.material))
                        .OrderBy(item => item.material.name, StringComparer.Ordinal)
                        .ThenBy(item => item.property, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (candidate.material != null)
                    {
                        shadowReplansRemaining--;
                        try
                        {
                            emissionProbe.AddShadowReplanLine(
                                AutoAtlasShadowReplan.Compare(
                                    domain, configuration, settings, group,
                                    propertyTextures, execution.SourceTextures,
                                    finalTotalPixels, finalTotalTextureMemoryBytes,
                                    executionMetrics,
                                    candidate.material, candidate.property));
                        }
                        catch (Exception ex)
                        {
                            emissionProbe.AddShadowReplanLine(
                                "shadow-replan=unknown:" + ex.GetType().Name);
                        }
                    }
                }

                AtlasTexture.ReplaceMesh(
                    domain, atlasRenderers, atlasContext, atlasResult.AtlasedMeshes!);
                AtlasTexture.ApplyAtlasedMaterialMaps(
                    domain,
                    atlasMaterialMaps.materialMap,
                    atlasMaterialMaps.domainsMaterial2ReplaceMaterial);

                foreach (var item in trialCandidates)
                    emissionProbe.AddFeatureDisableLine(
                        "trial=APPLIED-to-generated-material, material="
                        + item.material.name + ", feature=" + item.rule.Name
                        + ", switch=" + item.rule.SwitchProperty
                        + ", texturesCleared=["
                        + string.Join(",", item.rule.TextureProperties) + "]"
                        + ", actualGeneratedTextureMemoryBytes="
                        + executionTextureMemoryBytes
                        + ", actualRetainedSourceMemoryBytes="
                        + retainedSourceMemoryBytes
                        + ", actualTotalTextureMemoryBytes="
                        + finalTotalTextureMemoryBytes
                        + ", originalMemoryBaselineBytes="
                        + memoryBaseline
                        + ", shaderVisualEquivalence=UNVERIFIED");

                foreach (var rt in compiledTextures.Values)
                    if (!retainedRenderTextures.Contains(rt)) rt.Dispose();

                foreach (var descriptor in tuningResult.TextureDescriptors)
                    domain.RegisterPostProcessingAndLazyGPUReadBack(
                        descriptor.Key, descriptor.Value);

                completed.Add(block);
            }

            processingTimer.Stop();

            TTTLog.ReportingObject(configuration, () =>
                ErrorReport.ReportError(new AutoAtlasEmissionProbeError(
                    emissionProbe.Report())));

            if (twoDimensionalFallbackReasons.Count != 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info(
                        "AutoAtlasTexture:info:TwoDimensionalFallback",
                        string.Join("\n", twoDimensionalFallbackReasons)));
            }

            if (completed.Count == 0)
            {
                TTTLog.ReportingObject(configuration, () =>
                    TTTLog.Info("AutoAtlasTexture:info:TargetNotFound",
                        processingTimer.ElapsedMilliseconds));
                return;
            }

            TTTLog.ReportingObject(configuration, () =>
                TTTLog.Info("AutoAtlasTexture:info:RelocateResult",
                    string.Join("\n\n", completed),
                    processingTimer.ElapsedMilliseconds));
        }

        internal static bool RendererRequiresAtlasMesh(
            IRendererTargeting targeting,
            Renderer renderer,
            HashSet<Material> targetMaterials)
        {
            var mesh = targeting.GetMesh(renderer);
            if (mesh == null || mesh.subMeshCount == 0)
                return false;

            var materials = targeting.GetMaterials(renderer);
            for (var slot = 0; slot < materials.Length; slot += 1)
            {
                var material = materials[slot];
                if (material == null
                    || !targetMaterials.Contains(material))
                {
                    continue;
                }

                var effectiveSubMesh = Math.Clamp(
                    slot, 0, mesh.subMeshCount - 1);
                if (mesh.GetSubMesh(effectiveSubMesh).indexCount != 0)
                    return true;
            }

            return false;
        }

        internal static HashSet<Texture>
            CollectRetainedSourceTextures(
                IEnumerable<Material> materials,
                HashSet<Texture> sourceTextures)
        {
            return materials
                .Where(material => material != null)
                .SelectMany(material =>
                    material.EnumerateReferencedTextures())
                .Where(sourceTextures.Contains)
                .ToHashSet();
        }

        internal static bool RetainsAnySourceTexture(
            IEnumerable<Material> materials,
            HashSet<Texture> sourceTextures)
        {
            return CollectRetainedSourceTextures(
                materials, sourceTextures).Count != 0;
        }

        internal static void DestroyUnappliedAtlasedMaterials(
            IEnumerable<Material> materials)
        {
            foreach (var material in materials
                         .Where(material => material != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        internal static bool HasActiveUntransformedMaterialConsumer(
            IRendererTargeting domain,
            IReadOnlyCollection<Material> materials,
            IReadOnlyCollection<Renderer> transformedRenderers)
        {
            var transformed = transformedRenderers.ToHashSet();

            foreach (var renderer in domain.EnumerateRenderer())
            {
                if (renderer == null || renderer.tag == "EditorOnly")
                    continue;

                if (!domain.IsEnable(renderer)
                    || !domain.IsActive(renderer.gameObject))
                {
                    continue;
                }

                if (!domain.GetMaterials(renderer)
                        .UOfType<Material>()
                        .Any(materials.Contains))
                {
                    continue;
                }

                if (!transformed.Contains(renderer))
                    return true;
            }

            return false;
        }

        internal static bool HasStrictMergePixelBenefit(
            long finalOutputPixels,
            long standaloneBaselinePixels)
        {
            return finalOutputPixels > 0
                && finalOutputPixels < standaloneBaselinePixels;
        }

        internal static void DestroyUnappliedAtlasedMeshes(
            IEnumerable<Mesh> meshes)
        {
            foreach (var mesh in meshes
                         .Where(mesh => mesh != null)
                         .Distinct())
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        internal static bool IsExcludedFromTextureMerging(
            IReadOnlyCollection<Material> materials,
            IEnumerable<Renderer> renderers,
            HashSet<Material> excludedMaterials,
            HashSet<Renderer> excludedRenderers)
        {
            return materials.Any(excludedMaterials.Contains)
                || renderers.Any(excludedRenderers.Contains);
        }

        internal static HashSet<Texture> FindRetainedSourceTextures(
            HashSet<Material> allReferencedMaterials,
            HashSet<Material> group,
            IReadOnlyDictionary<
                Material,
                IReadOnlyDictionary<string, Texture>> propertyTextures,
            HashSet<Texture> replacedTextures)
        {
            var retained = new HashSet<Texture>();
            foreach (var material in allReferencedMaterials)
            {
                var isTarget = group.Contains(material);
                propertyTextures.TryGetValue(
                    material, out var atlasProperties);

                // A texture remains resident when any reference is outside the
                // exact property rewrite performed for this atlas group.
                foreach (var property in material.GetTextureReferences())
                {
                    if (!replacedTextures.Contains(property.Value)) continue;
                    if (!isTarget
                        || atlasProperties == null
                        || !atlasProperties.TryGetValue(
                            property.Key, out var atlasTexture)
                        || atlasTexture != property.Value)
                    {
                        retained.Add(property.Value);
                    }
                }
            }

            return retained;
        }

        internal static List<HashSet<Material>> MakeTextureConnectedGroups(
            HashSet<Material> materials,
            IReadOnlyDictionary<Material, IReadOnlyDictionary<string, Texture>> properties)
        {
            var materialsByTexture = new Dictionary<Texture, HashSet<Material>>();
            foreach (var material in materials)
            {
                foreach (var texture in properties[material].Values)
                {
                    if (texture == null) continue;
                    if (!materialsByTexture.TryGetValue(texture, out var consumers))
                        materialsByTexture[texture] = consumers = new HashSet<Material>();
                    consumers.Add(material);
                }
            }

            var remaining = new HashSet<Material>(materials.Where(material =>
                properties[material].Count != 0));
            var results = new List<HashSet<Material>>();
            while (remaining.Count != 0)
            {
                var seed = remaining.First();
                remaining.Remove(seed);
                var group = new HashSet<Material> { seed };
                var pending = new Queue<Material>();
                pending.Enqueue(seed);

                while (pending.Count != 0)
                {
                    var current = pending.Dequeue();
                    foreach (var texture in properties[current].Values)
                    {
                        if (texture == null) continue;
                        foreach (var adjacent in materialsByTexture[texture])
                        {
                            if (!remaining.Remove(adjacent)) continue;
                            group.Add(adjacent);
                            pending.Enqueue(adjacent);
                        }
                    }
                }
                results.Add(group);
            }
            return results;
        }
    }
}
