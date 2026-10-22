using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;
using UnityEngine;

namespace net.rs64.TexTransTool.TestCode
{
    internal class AtlasResolutionRouteReportTest
    {
        [Test]
        public void IdenticalRoutesAreAggregatedIntoOccurrences()
        {
            var route = Route(
                Inputs((2048, 2048, 1)),
                Outputs((512, 512, 1))
            );

            var result = AtlasResolutionRouteReport.AggregateRoutes(
                new[] { route, route, route });

            Assert.That(result.Length, Is.EqualTo(1));
            Assert.That(result[0].Occurrences, Is.EqualTo(3));
            Assert.That(
                AtlasResolutionRouteReport.FormatComposition(result[0].Inputs),
                Is.EqualTo("2048×2048 ×1"));
            Assert.That(
                AtlasResolutionRouteReport.FormatComposition(result[0].Outputs),
                Is.EqualTo("512×512 ×1"));
        }

        [Test]
        public void RoutesAreSortedByOutputThenInputComposition()
        {
            var instances = new List<AtlasResolutionRouteInstance>();

            Add(instances, 3, Route(
                Inputs((2048, 2048, 1)),
                Outputs((512, 512, 1))
            ));
            Add(instances, 5, Route(
                Inputs(
                    (2048, 2048, 1),
                    (1024, 1024, 1),
                    (512, 512, 1)),
                Outputs((2048, 2048, 1))
            ));
            Add(instances, 3, Route(
                Inputs(
                    (2048, 2048, 1),
                    (512, 512, 1)),
                Outputs((1024, 1024, 1))
            ));
            Add(instances, 2, Route(
                Inputs((2048, 2048, 1)),
                Outputs((1024, 1024, 1))
            ));

            var result = AtlasResolutionRouteReport.AggregateRoutes(instances);

            CollectionAssert.AreEqual(
                new[]
                {
                    "2048×2048 ×1 + 1024×1024 ×1 + 512×512 ×1 -> 2048×2048 ×1 : 5",
                    "2048×2048 ×1 + 512×512 ×1 -> 1024×1024 ×1 : 3",
                    "2048×2048 ×1 -> 1024×1024 ×1 : 2",
                    "2048×2048 ×1 -> 512×512 ×1 : 3",
                },
                result.Select(Describe).ToArray()
            );
        }

        [Test]
        public void InputCompositionUsesResolutionThenCountLexicographicOrder()
        {
            var result = AtlasResolutionRouteReport.AggregateRoutes(new[]
            {
                Route(
                    Inputs(
                        (2048, 2048, 1),
                        (1024, 1024, 1),
                        (512, 512, 2)),
                    Outputs((1024, 1024, 1))
                ),
                Route(
                    Inputs(
                        (2048, 2048, 2),
                        (1024, 1024, 1),
                        (512, 512, 1)),
                    Outputs((1024, 1024, 1))
                ),
                Route(
                    Inputs(
                        (2048, 2048, 1),
                        (1024, 1024, 2),
                        (512, 512, 1)),
                    Outputs((1024, 1024, 1))
                ),
            });

            CollectionAssert.AreEqual(
                new[]
                {
                    "2048×2048 ×2 + 1024×1024 ×1 + 512×512 ×1",
                    "2048×2048 ×1 + 1024×1024 ×2 + 512×512 ×1",
                    "2048×2048 ×1 + 1024×1024 ×1 + 512×512 ×2",
                },
                result.Select(route =>
                    AtlasResolutionRouteReport.FormatComposition(route.Inputs))
                    .ToArray()
            );
        }

        [Test]
        public void MissingSourceSlotsAreAggregatedAsZeroSizeAtTheEnd()
        {
            var result = AtlasResolutionRouteReport.AggregateRoutes(new[]
            {
                Route(
                    Inputs(
                        (0, 0, 1),
                        (512, 512, 1),
                        (0, 0, 2)),
                    Outputs((1024, 1024, 1))
                ),
            });

            Assert.That(
                AtlasResolutionRouteReport.FormatComposition(result[0].Inputs),
                Is.EqualTo("512×512 ×1 + 0×0 ×3")
            );
        }

        [Test]
        public void CompositionIsNormalizedLargestResolutionFirst()
        {
            var result = AtlasResolutionRouteReport.AggregateRoutes(new[]
            {
                Route(
                    Inputs(
                        (512, 512, 1),
                        (2048, 1024, 1),
                        (1024, 2048, 1),
                        (2048, 2048, 1),
                        (512, 512, 1)),
                    Outputs((1024, 1024, 1))
                ),
            });

            Assert.That(
                AtlasResolutionRouteReport.FormatComposition(result[0].Inputs),
                Is.EqualTo(
                    "2048×2048 ×1 + 2048×1024 ×1 + 1024×2048 ×1 + 512×512 ×2")
            );
        }

        [Test]
        public void OmittedFeatureTextureIsShownAsZeroOutputInNormalRoutes()
        {
            var shader = Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            var main = new Texture2D(1024, 1024);
            var mask = new Texture2D(512, 512);
            try
            {
                var original = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_MainTex"] = main,
                        ["_Emission2ndBlendMask"] = mask,
                    }
                };
                var effective = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_MainTex"] = main,
                    }
                };
                var omitted = AtlasResolutionRouteReport.CollectOmittedPropertyRoutes(
                    new[] { material }, original, effective,
                    new[] { (material, "_Emission2ndBlendMask") },
                    new[] { "_MainTex" });
                var routes = AtlasResolutionRouteReport.AggregateRoutes(omitted);

                Assert.That(routes.Length, Is.EqualTo(1));
                Assert.That(Describe(routes[0]),
                    Is.EqualTo("512×512 ×1 -> 0×0 ×1 : 1"));
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(main);
                Object.DestroyImmediate(mask);
            }
        }

        [Test]
        public void OmittedFeatureRoutesCountPropertiesNotMaterialInstances()
        {
            var shader = Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var first = new Material(shader);
            var second = new Material(shader);
            var originalMask = new Texture2D(512, 512);
            try
            {
                var original = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [first] = new Dictionary<string, Texture>
                    {
                        ["_Emission2ndBlendMask"] = originalMask,
                    },
                    [second] = new Dictionary<string, Texture>
                    {
                        ["_Emission2ndBlendMask"] = originalMask,
                    },
                };
                var effective = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [first] = new Dictionary<string, Texture>(),
                    [second] = new Dictionary<string, Texture>(),
                };
                var inputs = AtlasResolutionRouteReport.CollectOmittedPropertyRoutes(
                    new[] { first, second }, original, effective,
                    new[]
                    {
                        (first, "_Emission2ndBlendMask"),
                        (second, "_Emission2ndBlendMask"),
                    },
                    System.Array.Empty<string>());
                var routes = AtlasResolutionRouteReport.AggregateRoutes(inputs);
                Assert.That(routes.Length, Is.EqualTo(1));
                Assert.That(Describe(routes[0]),
                    Is.EqualTo("512×512 ×1 -> 0×0 ×1 : 1"));

                // A surviving property consumer must still generate an atlas
                // output; a zero-sized output would be misleading.
                effective[second] = new Dictionary<string, Texture>
                {
                    ["_Emission2ndBlendMask"] = originalMask,
                };
                Assert.That(
                    AtlasResolutionRouteReport.CollectOmittedPropertyRoutes(
                        new[] { first, second }, original, effective,
                        new[] { (first, "_Emission2ndBlendMask") },
                        new[] { "_Emission2ndBlendMask" }),
                    Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(originalMask);
            }
        }

        [Test]
        public void OmittedFeatureRouteIsNotShownIfOutputIsStillGenerated()
        {
            var shader = Shader.Find("Standard")
                ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var mask = new Texture2D(512, 512);
            try
            {
                var original = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>
                    {
                        ["_Emission2ndBlendMask"] = mask,
                    }
                };
                var effective = new Dictionary<Material,
                    IReadOnlyDictionary<string, Texture>>
                {
                    [material] = new Dictionary<string, Texture>(),
                };
                Assert.That(
                    AtlasResolutionRouteReport.CollectOmittedPropertyRoutes(
                        new[] { material }, original, effective,
                        new[] { (material, "_Emission2ndBlendMask") },
                        new[] { "_Emission2ndBlendMask" }),
                    Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(mask);
            }
        }

        private static AtlasResolutionRouteInstance Route(
            AtlasTextureSizeCount[] inputs,
            AtlasTextureSizeCount[] outputs)
        {
            return new AtlasResolutionRouteInstance(inputs, outputs);
        }

        private static AtlasTextureSizeCount[] Inputs(
            params (int Width, int Height, int Count)[] values)
        {
            return values.Select(value =>
                new AtlasTextureSizeCount(
                    value.Width, value.Height, value.Count)).ToArray();
        }

        private static AtlasTextureSizeCount[] Outputs(
            params (int Width, int Height, int Count)[] values)
        {
            return Inputs(values);
        }

        private static void Add(
            ICollection<AtlasResolutionRouteInstance> routes,
            int count,
            AtlasResolutionRouteInstance route)
        {
            for (var index = 0; index < count; index += 1)
                routes.Add(route);
        }

        private static string Describe(AtlasResolutionRoute route)
        {
            return
                $"{AtlasResolutionRouteReport.FormatComposition(route.Inputs)} -> " +
                $"{AtlasResolutionRouteReport.FormatComposition(route.Outputs)} : " +
                route.Occurrences;
        }
    }
}
