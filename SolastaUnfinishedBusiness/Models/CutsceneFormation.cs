using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// Where heroes without an authored cutscene seat stand: a row one step behind the seated heroes, staggered so they show
// between them. Plain arithmetic with no game or Unity types, so Tests/CutsceneChecks can run it outside the game. It uses
// only its inputs, which come from the scene, so every computer gets the same answer.
internal static class CutsceneFormation
{
    // How far the back row stands behind the rearmost seat (user direction, 2026-11-07: "You can reduce the distance from the
    // front row"; it was 1.2 m). At 0.6 m a rear hero's body clears a front hero's back, about 0.3 m deep, and stays well
    // above the 0.3 m the user set as the closest. Measured from the rearmost seat, so the back row is straight even where
    // the game's markers are staggered.
    internal const float BackRowDistance = 0.6f;

    // How far past the end of the seated row a hero stands, and the step between heroes beyond it. There is no smallest
    // distance between back-row heroes (user direction, 2026-11-07, "remove the minimum spacing between rear heroes"): the
    // 0.8 m floor it replaced pushed the second rear hero past the 0.5 m gaps to directly behind a tall flank hero, hidden in
    // party shots (evidence/cutscene-staging-79619edc).
    internal const float SideMargin = 0.4f;
    internal const float PastEndStep = 0.8f;

    // how near a camera's ray has to pass a marker, and how far along it, for the shot to count as framing that marker
    private const float FramedPointTolerance = 0.3f;
    private const float FramedPointReach = 6f;

    internal readonly struct Point(float x, float y, float z)
    {
        internal readonly float X = x;
        internal readonly float Y = y;
        internal readonly float Z = z;
    }


    internal sealed class Result(Point forward, Point right, Point[] spots)
    {
        // the way the party faces, flat on the ground and of length 1
        internal Point Forward { get; } = forward;

        // along the row, of length 1, used to look for another cell when a spot is taken
        internal Point Right { get; } = right;

        internal Point[] Spots { get; } = spots;
    }

    // Spots for spotCount heroes given the seats they stand behind and those seats' forward directions. Everything is measured
    // from the seats given, so the row turns with the party at each whole-party teleport.
    //
    // Each rear hero stands exactly halfway between two neighbouring front heroes, measured along the row, and BackRowDistance
    // behind it, whatever any camera sees (user direction, 2026-11-07: "Ignore the camera completely. Just have them be
    // objectively between the gaps"). Which gaps follows the user's sketch of the same day
    // (evidence/cutscene-staging-e8e1a944/user-sketch-outer-gaps.jpg): with 2 rear heroes and 4 seats, one behind each outer
    // gap (seats 1-2 and 3-4 counted along the row), the middle gap left empty. One rear hero stands behind the middle gap; 3
    // take all three gaps; more go past the ends of the row. With 2 seats, so a single gap, the second goes past the end on the
    // side away from Right.
    //
    // e8e1a944 and 475912d2 instead put each rear hero on the line from the answer prompt's camera through the gap. Where that
    // camera stands close to the row (1.66 m in Nikannen's second stage) the line spreads outward, and the rear heroes ended
    // up past the end heroes, not behind the gaps (evidence/cutscene-staging-475912d2).
    internal static Result Compute(IReadOnlyList<Point> seats, IReadOnlyList<Point> seatForwards, int spotCount)
    {
        if (seats.Count == 0)
        {
            throw new ArgumentException("A formation needs at least one seat.", nameof(seats));
        }

        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var laterals = seats.Select(seat => (seat.X * right.X) + (seat.Z * right.Z)).OrderBy(x => x).ToList();
        var rearmost = seats.Min(seat => (seat.X * forward.X) + (seat.Z * forward.Z));
        var height = seats.Average(seat => seat.Y);
        var spotDepth = rearmost - BackRowDistance;
        // left to right as seen from the front (larger lateral first), so the shorter back-row hero, who AssignByHeight lists
        // first, stands on the left, as in the user's "   5 6 / 3124"
        var spots = PreferredLaterals(laterals, spotCount)
            .OrderByDescending(lateral => lateral)
            .Select(lateral => new Point(
                (right.X * lateral) + (forward.X * spotDepth),
                height,
                (right.Z * lateral) + (forward.Z * spotDepth)))
            .ToArray();

        return new Result(forward, right, spots);
    }

    // Who stands where, by height (user direction, 2026-11-07: "all the shortest characters are in the front row, and the
    // taller characters are behind"): the shortest heroes take the seats and the tallest the back row, where they show over
    // the front row's heads. Within the front row the shorter half stands at the centre and the taller half on the flanks, as
    // in the user's first sketch, each pair shorter on the left (SeatsFromCentre). The back row goes shorter first, and
    // Compute lists its spots left first, so the shorter rear hero stands on the left. Before,
    // the tallest stood on the flanks and middle heights behind, and a tall flank hero could hide a rear one
    // (evidence/cutscene-staging-85858711). heights has one entry per hero, in the party's order, which also settles ties.
    // seatsFromCentre lists the seats from the row's centre outward (SeatsFromCentre). Returns the hero index for each seat,
    // and for each back-row spot from the first. It changes no list in the game: actor order decides roles and shots, so
    // those stay as they were.
    internal static (int[] Seats, int[] Spots) AssignByHeight(IReadOnlyList<int> heights, IReadOnlyList<int> seatsFromCentre)
    {
        var sorted = Enumerable.Range(0, heights.Count).OrderBy(i => heights[i]).ToList();
        var seatCount = Math.Min(seatsFromCentre.Count, sorted.Count);
        var seats = new int[seatsFromCentre.Count];

        for (var i = 0; i < seatCount; i++)
        {
            seats[seatsFromCentre[i]] = sorted[i];
        }

        return (seats, sorted.Skip(seatCount).ToArray());
    }

    // The seats' indexes from the row's centre outward, in pairs, and within each pair the one on the left as seen from the
    // front first. Seen from the front means facing the party, as an answer prompt's camera does, so the left is the
    // party's own right, the larger lateral (in Nikannen's second stage Thgil, at x 10.3, stood at the left of the prompt
    // shot). AssignByHeight gives these places out from the shortest, so the front row reads 3 1 2 4 from left to right
    // (user direction, 2026-11-07: each pair shorter to taller from left to right, "   5 6 / 3124"). Pairs are taken by
    // rank rather than by equal distance, because staggered markers are never exactly equally far from the centre.
    internal static int[] SeatsFromCentre(IReadOnlyList<Point> seats, IReadOnlyList<Point> seatForwards)
    {
        var forward = AverageFlatDirection(seatForwards);
        var right = new Point(forward.Z, 0f, -forward.X);
        var laterals = seats.Select(seat => (seat.X * right.X) + (seat.Z * right.Z)).ToList();
        var centre = (laterals.Min() + laterals.Max()) * 0.5f;

        return Enumerable.Range(0, seats.Count)
            .OrderBy(i => Math.Abs(laterals[i] - centre))
            .Select((seat, rank) => (seat, pair: rank / 2))
            .OrderBy(x => x.pair)
            .ThenByDescending(x => laterals[x.seat])
            .Select(x => x.seat)
            .ToArray();
    }

    // Which of the points a camera looks at, or -1. Scene cameras' numbers don't say which marker they're for (the Nikannen
    // scene's CharSingle0 frames Char_5), but their authored rays go through it to within centimetres.
    internal static int FindFramedPoint(Point position, Point forward, IReadOnlyList<Point> points)
    {
        var length = (float)Math.Sqrt((forward.X * forward.X) + (forward.Z * forward.Z));
        var best = -1;
        var bestMiss = FramedPointTolerance;

        if (length < 0.001f)
        {
            return best;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var dx = points[i].X - position.X;
            var dz = points[i].Z - position.Z;
            var along = ((dx * forward.X) + (dz * forward.Z)) / length;
            var miss = Math.Abs((dx * forward.Z) - (dz * forward.X)) / length;

            if (along > 0f && along <= FramedPointReach && miss < bestMiss)
            {
                best = i;
                bestMiss = miss;
            }
        }

        return best;
    }

    // positions along the row: gap midpoints, then places past the ends. laterals is sorted.
    private static List<float> PreferredLaterals(List<float> laterals, int spotCount)
    {
        var centre = (laterals[0] + laterals[laterals.Count - 1]) * 0.5f;
        var gaps = laterals.Zip(laterals.Skip(1), (a, b) => (a + b) * 0.5f).ToList();
        var result = new List<float>();

        if (spotCount == 0)
        {
            return result;
        }

        // a real gap between two heroes, the one nearest the row's centre; a lone seat has none
        var middleIndex = gaps.Count == 0
            ? -1
            : gaps.IndexOf(gaps.OrderBy(gap => Math.Abs(gap - centre)).First());
        var middle = middleIndex < 0 ? centre : gaps[middleIndex];
        var pastEnd = Math.Max(((laterals[laterals.Count - 1] - laterals[0]) * 0.5f) + SideMargin, PastEndStep);

        // the user's sketch: two rear heroes behind the two outer gaps, the middle one left empty
        if (spotCount == 2 && gaps.Count >= 2)
        {
            result.Add(gaps[0]);
            result.Add(gaps[gaps.Count - 1]);

            return result;
        }

        result.Add(middle);

        if (spotCount == 2)
        {
            result.Add(middle - pastEnd);

            return result;
        }

        // walks outward from the middle on each side, through that side's gaps and then past the end of the row
        var low = middle;
        var high = middle;
        var lowGap = middleIndex - 1;
        var highGap = middleIndex + 1;

        for (var i = 1; i < spotCount; i++)
        {
            if (i % 2 == 1)
            {
                low = lowGap >= 0 ? gaps[lowGap] : Math.Min(laterals[0] - SideMargin, low - PastEndStep);
                lowGap--;
                result.Add(low);
            }
            else
            {
                high = highGap >= 0 && highGap < gaps.Count
                    ? gaps[highGap]
                    : Math.Max(laterals[laterals.Count - 1] + SideMargin, high + PastEndStep);
                highGap++;
                result.Add(high);
            }
        }

        return result;
    }


    // the seats' facing can differ (the outer ones turn inward), so the row's facing is their average, kept flat
    private static Point AverageFlatDirection(IReadOnlyList<Point> directions)
    {
        var x = 0f;
        var z = 0f;

        foreach (var direction in directions)
        {
            x += direction.X;
            z += direction.Z;
        }

        var length = (float)Math.Sqrt((x * x) + (z * z));

        // markers facing exactly opposite ways have no average: any direction is as good, and this one is stable
        return length < 0.001f ? new Point(0f, 0f, 1f) : new Point(x / length, 0f, z / length);
    }
}
