using System;
using System.Collections.Generic;
using System.Linq;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // One dimension within a group, and how many results have it.
    public class DimensionCount
    {
        public string Dimensions { get; set; }
        public int Count { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // A set of dimensions sharing an aspect ratio, under a recognisable name.
    public class AspectGrouping
    {
        public string Label { get; set; }          // "2:3 - Steam Vertical"
        public double Aspect { get; set; }         // width / height
        public List<DimensionCount> Dimensions { get; set; } = new List<DimensionCount>();

        public int TotalCount
        {
            get { return Dimensions.Sum(d => d.Count); }
        }
    }

    // Groups artwork results by aspect ratio so the filter UI can show
    // "2:3 - Steam Vertical" with its dimensions nested underneath, rather than
    // a flat list of numbers the user has to interpret.
    //
    // Groups are derived from the results, as the flat filter options already
    // were: a group cannot appear unless something in the current results has
    // that shape, so the list can never offer a filter that matches nothing.
    public static class AspectGroup
    {
        // Ratios SteamGridDB actually serves, with the names its own UI uses,
        // so a user who knows the site recognises them. Anything not listed
        // still groups - it just gets a plain ratio label.
        private static readonly Tuple<double, string>[] KnownFormats =
        {
            Tuple.Create(1.0, "LOCImageRotaterAspectSquare"),
            Tuple.Create(2.0 / 3.0, "LOCImageRotaterAspectSteamVertical"),
            Tuple.Create(920.0 / 430.0, "LOCImageRotaterAspectSteamHorizontal"),
            Tuple.Create(342.0 / 482.0, "LOCImageRotaterAspectGalaxy"),
            Tuple.Create(1920.0 / 620.0, "LOCImageRotaterAspectSteamHero"),
            Tuple.Create(1600.0 / 650.0, "LOCImageRotaterAspectGalaxyHero")
        };

        // Two ratios within this fraction of each other are the same format.
        // Enough to absorb rounding (342x482 and 660x930 are both "22:31" but
        // not exactly equal) without merging genuinely different shapes.
        private const double Tolerance = 0.02;

        public static IReadOnlyList<AspectGrouping> Build(IReadOnlyList<SteamGridDbArtwork> artwork)
        {
            if (artwork == null || artwork.Count == 0)
            {
                return new AspectGrouping[0];
            }

            var groups = new List<AspectGrouping>();

            foreach (SteamGridDbArtwork item in artwork)
            {
                if (item == null || item.Width <= 0 || item.Height <= 0)
                {
                    continue;
                }

                double aspect = (double)item.Width / item.Height;
                AspectGrouping group = FindGroup(groups, aspect);

                if (group == null)
                {
                    group = new AspectGrouping
                    {
                        Label = LabelFor(aspect, item.Width, item.Height),
                        Aspect = aspect
                    };
                    groups.Add(group);
                }

                DimensionCount dimension = group.Dimensions
                    .FirstOrDefault(d => string.Equals(d.Dimensions, item.Dimensions, StringComparison.OrdinalIgnoreCase));

                if (dimension == null)
                {
                    group.Dimensions.Add(new DimensionCount
                    {
                        Dimensions = item.Dimensions,
                        Width = item.Width,
                        Height = item.Height,
                        Count = 1
                    });
                }
                else
                {
                    dimension.Count++;
                }
            }

            // Largest dimension first within a group, and the group with the
            // most results first - what the user is most likely to want.
            foreach (AspectGrouping group in groups)
            {
                group.Dimensions = group.Dimensions
                    .OrderByDescending(d => (long)d.Width * d.Height)
                    .ToList();
            }

            return groups.OrderByDescending(g => g.TotalCount).ToList();
        }

        private static AspectGrouping FindGroup(List<AspectGrouping> groups, double aspect)
        {
            foreach (AspectGrouping group in groups)
            {
                if (Math.Abs(group.Aspect - aspect) / aspect <= Tolerance)
                {
                    return group;
                }
            }

            return null;
        }

        private static string LabelFor(double aspect, int width, int height)
        {
            foreach (Tuple<double, string> known in KnownFormats)
            {
                if (Math.Abs(known.Item1 - aspect) / aspect <= Tolerance)
                {
                    return Loc.Get(known.Item2);
                }
            }

            // Unrecognised shape: still group it, just name it by its reduced
            // ratio. Better than hiding it because it is not on a list.
            int divisor = Gcd(width, height);
            return divisor > 0
                ? $"{width / divisor}:{height / divisor}"
                : $"{width}x{height}";
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int t = b;
                b = a % b;
                a = t;
            }

            return a;
        }
    }
}
