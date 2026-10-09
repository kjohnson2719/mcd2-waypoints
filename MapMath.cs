using System.Collections.Generic;
using UE.CoreUObject;
using UE.Engine;

namespace Waypoints;

/// <summary>Vector helpers for routes on the ground. World units are centimetres; map and screen units are UI pixels.</summary>
public static class MapMath
{
    public static FVector Vec(double x, double y, double z) => new FVector { X = x, Y = y, Z = z };

    public static FVector2D Vec2(double x, double y) => new FVector2D { X = x, Y = y };

    public static double Abs(double v) => v < 0 ? -v : v;

    public static double Min(double a, double b) => a < b ? a : b;

    public static double Length2(double x, double y) => UKismetMathLibrary.sqrt(x * x + y * y);

    /// <summary>Distance on the ground, ignoring height.</summary>
    public static double Dist2D(FVector a, FVector b) => Length2(a.X - b.X, a.Y - b.Y);

    public static double Dist3D(FVector a, FVector b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return UKismetMathLibrary.sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static double DistUi(FVector2D a, FVector2D b) => Length2(a.X - b.X, a.Y - b.Y);

    public static FVector Lerp(FVector a, FVector b, double t) =>
        Vec(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>Where on segment a-b (0 to 1) the point closest to p is, on the ground.</summary>
    public static double ClosestT(FVector a, FVector b, FVector p)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1) return 0;
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        if (t < 0) return 0;
        if (t > 1) return 1;
        return t;
    }

    /// <summary>
    /// Points every <paramref name="spacing"/> along a route, starting <paramref name="offset"/> past
    /// <paramref name="start"/> (a point on segment <paramref name="startSeg"/>), up to <paramref name="maxDist"/> from it.
    /// </summary>
    public static List<FVector> PointsAlong(List<FVector> path, int startSeg, FVector start, double offset, double spacing, int max, double maxDist)
    {
        var result = new List<FVector>();
        int n = path.Count;
        if (n < 2 || spacing <= 0 || startSeg < 0 || startSeg > n - 2) return result;
        int seg = startSeg;
        FVector a = start;
        FVector b = path[seg + 1];
        double segLen = Dist3D(a, b);
        double acc = 0;
        double d = offset;
        while (result.Count < max && d <= maxDist)
        {
            while (acc + segLen < d)
            {
                acc = acc + segLen;
                seg = seg + 1;
                if (seg >= n - 1) return result;
                a = path[seg];
                b = path[seg + 1];
                segLen = Dist3D(a, b);
            }
            double t = segLen > 0.001 ? (d - acc) / segLen : 0;
            result.Add(Lerp(a, b, t));
            d = d + spacing;
        }
        return result;
    }

    /// <summary>A point clamped to a circle: unchanged inside it, on its edge outside it.</summary>
    public static FVector2D ClampToCircle(FVector2D p, FVector2D centre, double radius)
    {
        double dx = p.X - centre.X;
        double dy = p.Y - centre.Y;
        double len = Length2(dx, dy);
        if (len <= radius || len < 0.001) return p;
        return Vec2(centre.X + dx / len * radius, centre.Y + dy / len * radius);
    }
}
