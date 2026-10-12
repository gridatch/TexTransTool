using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using net.rs64.TexTransTool.TextureAtlas;

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
