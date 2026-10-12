#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using net.rs64.TexTransCore.UVIsland;

namespace net.rs64.TexTransTool.NDMF
{
    /// <summary>
    /// Rectangle packer used only by the optional additional AutoAtlas merge stage.
    ///
    /// Existing AtlasTexture / standalone AutoAtlas keep using TexTransCore's NFDH
    /// relocator. Additional merging needs a packer that can make practical use of
    /// fragmented free rectangles, otherwise pairs with enough total free area can
    /// still be promoted to the next power-of-two atlas size.
    /// </summary>
    internal sealed class AutoAtlasMergeIslandRelocator : IIslandRelocator
    {
        private const float Epsilon = 1e-6f;

        private readonly struct Rect
        {
            internal readonly float X;
            internal readonly float Y;
            internal readonly float Width;
            internal readonly float Height;

            internal float Right => X + Width;
            internal float Top => Y + Height;

            internal Rect(float x, float y, float width, float height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        private readonly struct Placement
        {
            internal readonly Rect ReservedRect;
            internal readonly bool Rotated;
            internal readonly float ShortSideFit;
            internal readonly float LongSideFit;

            internal Placement(
                Rect reservedRect,
                bool rotated,
                float shortSideFit,
                float longSideFit)
            {
                ReservedRect = reservedRect;
                Rotated = rotated;
                ShortSideFit = shortSideFit;
                LongSideFit = longSideFit;
            }
        }

        private readonly struct WorkItem
        {
            internal readonly IslandTransform Island;
            internal readonly int OriginalIndex;
            internal readonly float Width;
            internal readonly float Height;

            internal WorkItem(
                IslandTransform island,
                int originalIndex,
                float width,
                float height)
            {
                Island = island;
                OriginalIndex = originalIndex;
                Width = width;
                Height = height;
            }
        }

        public RelocationResult Relocation(
            RelocationContext relocationContext,
            IslandTransform[] islandTransforms)
        {
            if (islandTransforms.Length == 0)
                return new RelocationResult(false, true);

            var targetHeight = relocationContext.TargetHeight;
            var padding = Math.Max(0f, relocationContext.Padding);
            if (!IsFinite(targetHeight) || targetHeight <= 0f)
                return new RelocationResult(false, true);

            var items = new List<WorkItem>(islandTransforms.Length);
            for (var index = 0; index < islandTransforms.Length; index += 1)
            {
                var island = islandTransforms[index];
                var width = island.Size.X;
                var height = island.Size.Y;
                if (!IsFinite(width) || !IsFinite(height)
                    || width <= 0f || height <= 0f)
                {
                    return new RelocationResult(false, true);
                }

                island.Rotation = 0f;
                items.Add(new WorkItem(island, index, width, height));
            }

            // Large/awkward rectangles first. Original index is a deterministic
            // final tie-breaker so repeated planner probes produce the same layout.
            items.Sort((left, right) =>
            {
                var leftMax = Math.Max(left.Width, left.Height);
                var rightMax = Math.Max(right.Width, right.Height);
                var cmp = rightMax.CompareTo(leftMax);
                if (cmp != 0) return cmp;

                var leftArea = left.Width * left.Height;
                var rightArea = right.Width * right.Height;
                cmp = rightArea.CompareTo(leftArea);
                if (cmp != 0) return cmp;

                cmp = right.Height.CompareTo(left.Height);
                if (cmp != 0) return cmp;
                cmp = right.Width.CompareTo(left.Width);
                if (cmp != 0) return cmp;
                return left.OriginalIndex.CompareTo(right.OriginalIndex);
            });

            var freeRects = new List<Rect>
            {
                new Rect(0f, 0f, 1f, targetHeight),
            };

            foreach (var item in items)
            {
                if (!TryFindPlacement(
                        freeRects,
                        item.Width,
                        item.Height,
                        padding,
                        out var placement))
                {
                    return new RelocationResult(false, true);
                }

                ApplyPlacement(item, placement, padding);
                SplitFreeRects(freeRects, placement.ReservedRect);
                PruneContainedFreeRects(freeRects);
            }

            return new RelocationResult(true, true);
        }

        private static bool TryFindPlacement(
            IReadOnlyList<Rect> freeRects,
            float width,
            float height,
            float padding,
            out Placement best)
        {
            best = default;
            var found = false;

            for (var index = 0; index < freeRects.Count; index += 1)
            {
                var free = freeRects[index];
                Evaluate(free, width, height, rotated: false);

                if (Math.Abs(width - height) > Epsilon)
                    Evaluate(free, height, width, rotated: true);
            }

            return found;

            void Evaluate(
                Rect free,
                float contentWidth,
                float contentHeight,
                bool rotated)
            {
                var reservedWidth = contentWidth + padding * 2f;
                var reservedHeight = contentHeight + padding * 2f;

                if (reservedWidth > free.Width + Epsilon
                    || reservedHeight > free.Height + Epsilon)
                {
                    return;
                }

                var remainingWidth = Math.Max(0f, free.Width - reservedWidth);
                var remainingHeight = Math.Max(0f, free.Height - reservedHeight);
                var shortFit = Math.Min(remainingWidth, remainingHeight);
                var longFit = Math.Max(remainingWidth, remainingHeight);
                var candidate = new Placement(
                    new Rect(
                        free.X,
                        free.Y,
                        reservedWidth,
                        reservedHeight),
                    rotated,
                    shortFit,
                    longFit);

                if (!found || IsBetter(candidate, best))
                {
                    best = candidate;
                    found = true;
                }
            }
        }

        private static bool IsBetter(Placement candidate, Placement current)
        {
            var compare = candidate.ShortSideFit.CompareTo(current.ShortSideFit);
            if (compare != 0) return compare < 0;

            compare = candidate.LongSideFit.CompareTo(current.LongSideFit);
            if (compare != 0) return compare < 0;

            compare = candidate.ReservedRect.Y.CompareTo(current.ReservedRect.Y);
            if (compare != 0) return compare < 0;

            compare = candidate.ReservedRect.X.CompareTo(current.ReservedRect.X);
            if (compare != 0) return compare < 0;

            // Keep the original orientation when both fits are identical.
            return current.Rotated && !candidate.Rotated;
        }

        private static void ApplyPlacement(
            WorkItem item,
            Placement placement,
            float padding)
        {
            var contentX = placement.ReservedRect.X + padding;
            var contentY = placement.ReservedRect.Y + padding;

            item.Island.Size = new Vector2(item.Width, item.Height);

            if (!placement.Rotated)
            {
                item.Island.Rotation = 0f;
                item.Island.Position = new Vector2(contentX, contentY);
                return;
            }

            // Match NFDHPlasFC's rotation convention. GenerateAtlasedMesh rotates
            // relative UV coordinates around Position, so a clockwise 90-degree
            // rectangle uses a pivot one original-width unit above the packed
            // bounding rectangle's lower-left corner.
            item.Island.Rotation = (float)(-Math.PI / 2d);
            item.Island.Position = new Vector2(
                contentX,
                contentY + item.Width);
        }

        private static void SplitFreeRects(
            List<Rect> freeRects,
            Rect used)
        {
            var output = new List<Rect>(freeRects.Count * 2);

            foreach (var free in freeRects)
            {
                if (!Intersects(free, used))
                {
                    output.Add(free);
                    continue;
                }

                if (used.X > free.X + Epsilon)
                {
                    output.Add(new Rect(
                        free.X,
                        free.Y,
                        used.X - free.X,
                        free.Height));
                }

                if (used.Right < free.Right - Epsilon)
                {
                    output.Add(new Rect(
                        used.Right,
                        free.Y,
                        free.Right - used.Right,
                        free.Height));
                }

                if (used.Y > free.Y + Epsilon)
                {
                    output.Add(new Rect(
                        free.X,
                        free.Y,
                        free.Width,
                        used.Y - free.Y));
                }

                if (used.Top < free.Top - Epsilon)
                {
                    output.Add(new Rect(
                        free.X,
                        used.Top,
                        free.Width,
                        free.Top - used.Top));
                }
            }

            freeRects.Clear();
            freeRects.AddRange(output.Where(IsUsable));
        }

        private static void PruneContainedFreeRects(List<Rect> freeRects)
        {
            for (var left = 0; left < freeRects.Count; left += 1)
            {
                var removeLeft = false;
                for (var right = 0; right < freeRects.Count; right += 1)
                {
                    if (left == right) continue;
                    if (!Contains(freeRects[right], freeRects[left])) continue;

                    removeLeft = true;
                    break;
                }

                if (!removeLeft) continue;
                freeRects.RemoveAt(left);
                left -= 1;
            }
        }

        private static bool Intersects(Rect left, Rect right)
        {
            return left.X < right.Right - Epsilon
                && left.Right > right.X + Epsilon
                && left.Y < right.Top - Epsilon
                && left.Top > right.Y + Epsilon;
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            return inner.X >= outer.X - Epsilon
                && inner.Y >= outer.Y - Epsilon
                && inner.Right <= outer.Right + Epsilon
                && inner.Top <= outer.Top + Epsilon;
        }

        private static bool IsUsable(Rect rect)
        {
            return rect.Width > Epsilon
                && rect.Height > Epsilon
                && IsFinite(rect.X)
                && IsFinite(rect.Y)
                && IsFinite(rect.Width)
                && IsFinite(rect.Height);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
