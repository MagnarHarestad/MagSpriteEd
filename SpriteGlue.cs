using System;
using System.Collections.Generic;

namespace MagSpriteEd;

/// <summary>
/// Finds where to move a group of hardware sprites so it sits flush against
/// the nearest other sprite ("glue": Alt+click / Alt+drag in Construct, or
/// the toolbar Glue button). Pure maths on
/// positions only - no UI - so it can be reasoned about and tested alone.
///
/// Every sprite occupies a 24x21 box in sprite coordinates (multicolour and
/// hires alike). The selection moves as one rigid block (its members keep
/// their relative offsets), represented by its bounding box.
///
/// Candidates: for each non-selected sprite T, the block can touch T on one
/// of four sides. Flush against a side fixes the move on that axis exactly;
/// on the other axis the block is aligned so its leading or trailing edge
/// lines up with T's - which is what builds clean multi-sprite blocks (e.g.
/// the classic 2x2 of 48x42) rather than a staggered join. That gives
/// 4 sides x 2 alignments = 8 candidate moves per neighbour.
///
/// A candidate is rejected if any moved sprite would leave the valid
/// register range (X 0..511, Y 0..255) or overlap any non-selected sprite
/// (touching edges is fine - that's the point). Of the rest, the one with
/// the shortest move wins (Euclidean distance), so a glue always does the
/// smallest change that makes the selection glued. Ties prefer the move
/// with less total axis travel, then a horizontal join (sprites side by side
/// share the same raster lines, which is usually what a composed object wants).
/// </summary>
internal static class SpriteGlue
{
    public const int W = 24;
    public const int H = 21;

    public enum Side { Left, Right, Above, Below }

    /// <param name="Target">The sprite the selection ends up glued to.</param>
    /// <param name="Side">Where the selection sits relative to Target.</param>
    public readonly record struct Move(int Dx, int Dy, int Target, Side Side);

    /// <summary>Best glue move for <paramref name="selected"/>, or null if
    /// there's no other sprite to glue to or no candidate fits.</summary>
    public static Move? FindBestMove(IReadOnlyList<int> xs, IReadOnlyList<int> ys, IReadOnlyCollection<int> selected)
    {
        if (selected.Count == 0) return null;

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (int s in selected)
        {
            left = Math.Min(left, xs[s]);
            top = Math.Min(top, ys[s]);
            right = Math.Max(right, xs[s] + W);
            bottom = Math.Max(bottom, ys[s] + H);
        }

        var others = new List<int>();
        for (int i = 0; i < xs.Count; i++)
            if (!Contains(selected, i)) others.Add(i);
        if (others.Count == 0) return null;

        Move? best = null;
        long bestDist = long.MaxValue, bestTravel = long.MaxValue;
        bool bestHorizontal = false;

        void Consider(int dx, int dy, int target, Side side)
        {
            if (!Fits(xs, ys, selected, others, dx, dy)) return;
            long dist = (long)dx * dx + (long)dy * dy;
            long travel = Math.Abs(dx) + Math.Abs(dy);
            bool horizontal = side is Side.Left or Side.Right;
            bool better = dist < bestDist
                          || (dist == bestDist && travel < bestTravel)
                          || (dist == bestDist && travel == bestTravel && horizontal && !bestHorizontal);
            if (!better) return;
            best = new Move(dx, dy, target, side);
            bestDist = dist;
            bestTravel = travel;
            bestHorizontal = horizontal;
        }

        foreach (int t in others)
        {
            int tl = xs[t], tt = ys[t], tr = tl + W, tb = tt + H;

            // Side by side: X is fixed by the join, Y aligns top or bottom edges.
            foreach (int dy in new[] { tt - top, tb - bottom })
            {
                Consider(tr - left, dy, t, Side.Right);  // selection right of T
                Consider(tl - right, dy, t, Side.Left);  // selection left of T
            }
            // Stacked: Y is fixed by the join, X aligns left or right edges.
            foreach (int dx in new[] { tl - left, tr - right })
            {
                Consider(dx, tb - top, t, Side.Below);   // selection below T
                Consider(dx, tt - bottom, t, Side.Above); // selection above T
            }
        }
        return best;
    }

    private static bool Fits(IReadOnlyList<int> xs, IReadOnlyList<int> ys, IReadOnlyCollection<int> selected, List<int> others, int dx, int dy)
    {
        foreach (int s in selected)
        {
            int x = xs[s] + dx, y = ys[s] + dy;
            if (x < 0 || x > 511 || y < 0 || y > 255) return false;
            foreach (int o in others)
                if (Overlaps(x, y, xs[o], ys[o])) return false;
        }
        return true;
    }

    /// <summary>True if two 24x21 boxes share any area (edges touching doesn't count).</summary>
    private static bool Overlaps(int ax, int ay, int bx, int by) =>
        ax < bx + W && bx < ax + W && ay < by + H && by < ay + H;

    private static bool Contains(IReadOnlyCollection<int> set, int value)
    {
        foreach (int v in set) if (v == value) return true;
        return false;
    }
}
