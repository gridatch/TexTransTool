using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace net.rs64.TexTransTool.NDMF.TestCode
{
    internal class AutoAtlasTextureMergePlannerSafetyTest
    {
        [Test]
        public void CandidateAcceptsStrictlyBetterNonRegressingMerge()
        {
            var baseline = Plan(
                new[]
                {
                    Entry(64, 64, 1, false, new[] { 0 }),
                    Entry(64, 64, 1, false, new[] { 1 }),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });
            var merged = Plan(
                new[]
                {
                    Entry(64, 64, 1, false, new[] { 0, 1 }),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.True);
        }

        [Test]
        public void CandidateRejectsWhenPixelCountDoesNotDecrease()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }, blockSize: 4),
                Entry(64, 64, 1, false, new[] { 1 }, blockSize: 4),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }, blockSize: 2),
                Entry(64, 64, 1, false, new[] { 1 }, blockSize: 2),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateRejectsWhenResidentMemoryDoesNotDecrease()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }, blockSize: 2),
                Entry(64, 64, 1, false, new[] { 1 }, blockSize: 2),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0, 1 }, blockSize: 4),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateRejectsWhenMaximumTextureDimensionIncreases()
        {
            var baseline = Plan(new[]
            {
                Entry(128, 128, 1, false, new[] { 0 }),
                Entry(128, 128, 1, false, new[] { 1 }),
            });
            var merged = Plan(new[]
            {
                Entry(256, 32, 1, false, new[] { 0, 1 }),
            });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateRejectsSamplingCostRegression()
        {
            var baseline = Plan(
                new[]
                {
                    Entry(64, 64, 1, false, new[] { 0 }),
                    Entry(64, 64, 1, false, new[] { 1 }),
                },
                new Dictionary<int, double>
                {
                    [0] = 4d,
                    [1] = 4d,
                });
            var merged = Plan(
                new[]
                {
                    Entry(64, 64, 1, false, new[] { 0, 1 }),
                },
                new Dictionary<int, double>
                {
                    [0] = 8d,
                    [1] = 4d,
                });

            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateRecordsSingletonStreamingRegressionWithoutRejecting()
        {
            // The merged full-resident texture is smaller than the two
            // standalone textures combined, but group 0 alone would have to
            // stream a 128x128 shared atlas instead of its own 32x32 texture.
            // This is the regression that an all-groups-active check can hide.
            var baseline = Plan(new[]
            {
                Entry(
                    32, 32, 6, true, new[] { 0 },
                    demandLog2: new Dictionary<int, double> { [0] = 0d }),
                Entry(
                    128, 128, 8, true, new[] { 1 },
                    demandLog2: new Dictionary<int, double> { [1] = 0d }),
            });
            var merged = Plan(new[]
            {
                Entry(
                    128, 128, 8, true, new[] { 0, 1 },
                    demandLog2: new Dictionary<int, double>
                    {
                        [0] = 0d,
                        [1] = 0d,
                    }),
            });

            Assert.That(
                merged.PixelCount,
                Is.LessThan(baseline.PixelCount),
                "Precondition: merged pixel count must look beneficial.");
            Assert.That(
                merged.FullResidentBytes,
                Is.LessThan(baseline.FullResidentBytes),
                "Precondition: merged resident memory must look beneficial.");
            var candidate = Candidate(baseline, merged);
            Assert.That(candidate.IsAdoptable, Is.True);
            Assert.That(candidate.StreamingWouldRegress, Is.True);
            Assert.That(candidate.RejectionReason,
                Is.EqualTo(AutoAtlasTextureMergePlanner.CandidateRejectionReason.None));
        }

        [Test]
        public void StreamingDiagnosticIdentifiesActiveGroupAndMips()
        {
            var baseline = Plan(new[]
            {
                Entry(32, 32, 6, true, new[] { 0 },
                    demandLog2: new Dictionary<int, double> { [0] = 0d }),
                Entry(128, 128, 8, true, new[] { 1 },
                    demandLog2: new Dictionary<int, double> { [1] = 0d }),
            });
            var merged = Plan(new[]
            {
                Entry(128, 128, 8, true, new[] { 0, 1 },
                    demandLog2: new Dictionary<int, double>
                    {
                        [0] = 0d,
                        [1] = 0d,
                    }),
            });

            var candidate = Candidate(baseline, merged);
            Assert.That(candidate.IsAdoptable, Is.True);
            Assert.That(candidate.StreamingWouldRegress, Is.True);

            var diagnostic =
                AutoAtlasTextureMergePlanner.DiagnoseStreamingRegression(candidate);
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic.ActiveGroupId, Is.EqualTo(0));
            Assert.That(diagnostic.MergedBytes,
                Is.GreaterThan(diagnostic.BaselineBytes));
            Assert.That(diagnostic.BaselineEntries, Does.Contain("mip="));
            Assert.That(diagnostic.MergedEntries, Does.Contain("streaming(model)=True"));
            Assert.That(diagnostic.MergedEntries, Does.Contain("bytes="));
        }

        [Test]
        public void StreamingDiagnosticIsAbsentWithoutRegression()
        {
            var baseline = Plan(new[]
            {
                Entry(128, 128, 8, true, new[] { 0 }),
                Entry(128, 128, 8, true, new[] { 1 }),
            });
            var merged = Plan(new[]
            {
                Entry(128, 128, 8, true, new[] { 0, 1 }),
            });
            var diagnostic =
                AutoAtlasTextureMergePlanner.DiagnoseStreamingRegression(
                    Candidate(baseline, merged));
            Assert.That(diagnostic, Is.Null);
        }

        [Test]
        public void CandidateRejectsComplementaryPropertyDensityInflation()
        {
            // Two groups have opposite high/low requirements across two
            // properties. Putting both properties on one wider shared layout
            // raises both generated property textures to the larger envelope.
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }),
                Entry(32, 32, 1, false, new[] { 0 }),
                Entry(32, 32, 1, false, new[] { 1 }),
                Entry(64, 64, 1, false, new[] { 1 }),
            });
            var merged = Plan(new[]
            {
                Entry(128, 64, 1, false, new[] { 0, 1 }),
                Entry(128, 64, 1, false, new[] { 0, 1 }),
            });

            Assert.That(merged.PixelCount, Is.GreaterThan(baseline.PixelCount));
            Assert.That(Candidate(baseline, merged).IsAdoptable, Is.False);
        }

        [Test]
        public void CandidateCanBecomeBeneficialAfterAddingThirdGroup()
        {
            // The pair is not useful by itself: the shared output only ties the
            // two standalone textures. A third group can make the same shared
            // output strictly cheaper than keeping all three independently.
            var pairBaseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }),
                Entry(64, 64, 1, false, new[] { 1 }),
            });
            var pairMerged = Plan(new[]
            {
                Entry(128, 64, 1, false, new[] { 0, 1 }),
            });

            Assert.That(
                Candidate(pairBaseline, pairMerged, new[] { 0, 1 }).IsAdoptable,
                Is.False);

            var tripleBaseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 0 }),
                Entry(64, 64, 1, false, new[] { 1 }),
                Entry(128, 64, 1, false, new[] { 2 }),
            });
            var tripleMerged = Plan(new[]
            {
                Entry(128, 64, 1, false, new[] { 0, 1, 2 }),
            });

            Assert.That(
                tripleMerged.PixelCount,
                Is.LessThan(tripleBaseline.PixelCount));
            Assert.That(
                Candidate(
                    tripleBaseline,
                    tripleMerged,
                    new[] { 0, 1, 2 }).IsAdoptable,
                Is.True);
        }

        [Test]
        public void StreamingVisibilityDiagnosticRecordsSharedRendererAndSourceMesh()
        {
            var root = new GameObject("AvatarRoot");
            var sharedMesh = new Mesh { name = "SameSourceMesh" };
            try
            {
                var child = new GameObject("Clothes");
                child.transform.SetParent(root.transform);
                var sharedRenderer = child.AddComponent<MeshRenderer>();
                sharedRenderer.enabled = false;

                var separate = new GameObject("Accessories");
                separate.transform.SetParent(root.transform);
                var separateRenderer = separate.AddComponent<MeshRenderer>();

                var g3 = new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = 3,
                    Renderers = new Renderer[] { sharedRenderer },
                };
                var g6 = new AutoAtlasTextureMergePlanner.PreparedGroup
                {
                    Id = 6,
                    Renderers = new Renderer[]
                    {
                        sharedRenderer, separateRenderer,
                    },
                };
                var candidate = new AutoAtlasTextureMergePlanner.Candidate(
                    new[] { 3, 6 },
                    Plan(new AutoAtlasTextureMergePlanner.TextureCostEntry[0]),
                    Plan(new AutoAtlasTextureMergePlanner.TextureCostEntry[0]),
                    new Vector2Int(1024, 1024),
                    new Dictionary<string, Vector2Int>());
                var example =
                    new AutoAtlasTextureMergePlanner.StreamingRegressionExample(
                        candidate, 3, -16d, 100, 200, "", "");

                var report =
                    AutoAtlasTextureProcessor.DescribeStreamingVisibilityRelations(
                        new[] { g3, g6 },
                        example,
                        renderer => new Material[0],
                        renderer => sharedMesh,
                        material => material.name,
                        root.transform);

                Assert.That(report, Does.Contain("g3"));
                Assert.That(report, Does.Contain("g6"));
                Assert.That(report, Does.Contain("Clothes"));
                Assert.That(report, Does.Contain("Accessories"));
                Assert.That(report, Does.Contain("SameSourceMesh"));
                Assert.That(report, Does.Contain("Renderer.enabled=False"));
                Assert.That(report, Does.Contain("GO.activeSelf=True"));
            }
            finally
            {
                Object.DestroyImmediate(sharedMesh);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TextureMergingExclusionAppliesToRendererWithoutRemovingGroup()
        {
            var root = new GameObject("TextureMergingExclusionTest");
            try
            {
                var a = root.AddComponent<MeshRenderer>();
                var bObject = new GameObject("Other");
                bObject.transform.SetParent(root.transform);
                var b = bObject.AddComponent<MeshRenderer>();
                var exclusions = new HashSet<Renderer> { a };

                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material>(), new Renderer[] { a, b },
                    new HashSet<Material>(), exclusions), Is.True);
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material>(), new Renderer[] { b },
                    new HashSet<Material>(), exclusions), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TextureMergingExclusionAppliesToAnyMaterialInAtomicGroup()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material> { material },
                    System.Array.Empty<Renderer>(),
                    new HashSet<Material> { material },
                    new HashSet<Renderer>()), Is.True);
                Assert.That(AutoAtlasTextureProcessor.IsExcludedFromTextureMerging(
                    new HashSet<Material> { material },
                    System.Array.Empty<Renderer>(),
                    new HashSet<Material>(),
                    new HashSet<Renderer>()), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void TwoDimensionalPackingRejectsOverlapAndOutOfBounds()
        {
            var valid = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(512, 0, 512, 1024),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                valid, 1024), Is.True);

            var overlap = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(256, 256, 512, 512),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                overlap, 1024), Is.False);

            var outside = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 512, 512),
                [6] = new RectInt(512, 0, 1024, 512),
            };
            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                outside, 1024), Is.False);

            Assert.That(AutoAtlas2DMergeIslandRelocator.ValidatePlannedRegions(
                new Dictionary<int, RectInt> { [3] = valid[3] }, 1024),
                Is.False);
        }

        [Test]
        public void TwoDimensionalSelectionCarriesPlannedRegions()
        {
            var baseline = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 3 }),
                Entry(64, 64, 1, false, new[] { 6 }),
            });
            var merged = Plan(new[]
            {
                Entry(64, 64, 1, false, new[] { 3, 6 }),
            });
            var candidate = Candidate(baseline, merged, new[] { 3, 6 });
            var regions = new Dictionary<int, RectInt>
            {
                [3] = new RectInt(0, 0, 256, 128),
                [6] = new RectInt(256, 0, 256, 128),
            };
            var selection = new AutoAtlasTextureMergePlanner.MergeSelection(
                candidate, regions);

            Assert.That(selection.PackedRegions, Is.Not.Null);
            Assert.That(selection.GroupIds, Is.EqualTo(new[] { 3, 6 }));
            Assert.That(selection.PackedRegions[6].x, Is.EqualTo(256));
            Assert.That(selection.PredictedSavingBytes, Is.GreaterThan(0));
        }

        [Test]
        public void GroupLayoutCacheKeyMatchesDistinctEquivalentArrays()
        {
            var key = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 256));
            var same = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 256));
            var differentGroup = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 2, 8 },
                new Vector2Int(512, 256));
            var differentWidth = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(1024, 256));
            var differentHeight = new AutoAtlasTextureMergePlanner.GroupLayoutKey(
                new[] { 0, 1, 8 },
                new Vector2Int(512, 512));

            Assert.That(key.Equals(same), Is.True);
            Assert.That(key.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(key.Equals(differentGroup), Is.False);
            Assert.That(key.Equals(differentWidth), Is.False);
            Assert.That(key.Equals(differentHeight), Is.False);

            var cache = new Dictionary<
                AutoAtlasTextureMergePlanner.GroupLayoutKey,
                int> { [key] = 42 };
            Assert.That(cache.TryGetValue(same, out var value), Is.True);
            Assert.That(value, Is.EqualTo(42));
        }

        private static AutoAtlasTextureMergePlanner.Candidate Candidate(
            AutoAtlasTextureMergePlanner.TextureCostPlan baseline,
            AutoAtlasTextureMergePlanner.TextureCostPlan merged,
            int[] groupIds = null)
        {
            return new AutoAtlasTextureMergePlanner.Candidate(
                groupIds ?? new[] { 0, 1 },
                baseline,
                merged,
                new Vector2Int(64, 64),
                new Dictionary<string, Vector2Int>());
        }

        private static AutoAtlasTextureMergePlanner.TextureCostPlan Plan(
            IEnumerable<AutoAtlasTextureMergePlanner.TextureCostEntry> entries,
            IReadOnlyDictionary<int, double> sampling = null)
        {
            return new AutoAtlasTextureMergePlanner.TextureCostPlan(
                entries,
                sampling);
        }

        private static AutoAtlasTextureMergePlanner.TextureCostEntry Entry(
            int width,
            int height,
            int mipCount,
            bool streaming,
            IEnumerable<int> contributors,
            int blockSize = 4,
            Dictionary<int, double> demandLog2 = null)
        {
            demandLog2 ??= new Dictionary<int, double>();
            return new AutoAtlasTextureMergePlanner.TextureCostEntry(
                width,
                height,
                (1u, 1u, (uint)blockSize),
                mipCount,
                streaming,
                contributors,
                demandLog2);
        }
    }
}
