using System;
using MegaCrit.Sts2.Core.Map;

namespace MapPathHighlight;

/// <summary>
/// Represents the scoring and breakdown of high-value rooms along a map route.
/// Exclusively evaluates Elites, Rest Sites (Campfires), and Shops as high-value nodes.
/// </summary>
public readonly struct RouteScore : IComparable<RouteScore>, IEquatable<RouteScore>
{
    public readonly int TotalTargetRooms;
    public readonly int Elites;
    public readonly int RestSites;
    public readonly int Shops;
    public readonly int Unknowns;
    public readonly int Monsters;
    public readonly int Treasures;
    public readonly int Length;

    public RouteScore(
        int elites,
        int restSites,
        int shops,
        int unknowns,
        int monsters,
        int treasures,
        int length)
    {
        Elites = elites;
        RestSites = restSites;
        Shops = shops;
        TotalTargetRooms = elites + restSites + shops;
        Unknowns = unknowns;
        Monsters = monsters;
        Treasures = treasures;
        Length = length;
    }

    public static RouteScore Zero => new(0, 0, 0, 0, 0, 0, 0);

    public static RouteScore FromNode(MapPointType type) => type switch
    {
        MapPointType.Elite => new RouteScore(1, 0, 0, 0, 0, 0, 1),
        MapPointType.RestSite => new RouteScore(0, 1, 0, 0, 0, 0, 1),
        MapPointType.Shop => new RouteScore(0, 0, 1, 0, 0, 0, 1),
        MapPointType.Unknown => new RouteScore(0, 0, 0, 1, 0, 0, 1),
        MapPointType.Monster => new RouteScore(0, 0, 0, 0, 1, 0, 1),
        MapPointType.Treasure => new RouteScore(0, 0, 0, 0, 0, 1, 1),
        _ => new RouteScore(0, 0, 0, 0, 0, 0, 1)
    };

    public static RouteScore operator +(RouteScore a, RouteScore b) => new(
        a.Elites + b.Elites,
        a.RestSites + b.RestSites,
        a.Shops + b.Shops,
        a.Unknowns + b.Unknowns,
        a.Monsters + b.Monsters,
        a.Treasures + b.Treasures,
        a.Length + b.Length
    );

    public static RouteScore operator -(RouteScore a, RouteScore b) => new(
        a.Elites - b.Elites,
        a.RestSites - b.RestSites,
        a.Shops - b.Shops,
        a.Unknowns - b.Unknowns,
        a.Monsters - b.Monsters,
        a.Treasures - b.Treasures,
        a.Length - b.Length
    );

    public int CompareTo(RouteScore other)
    {
        // 1. Primary objective: maximize total count of high-value rooms (Elites + Campsites + Shops)
        int cmp = TotalTargetRooms.CompareTo(other.TotalTargetRooms);
        if (cmp != 0) return cmp;

        // 2. Tie-break 1: prioritize Elites
        cmp = Elites.CompareTo(other.Elites);
        if (cmp != 0) return cmp;

        // 3. Tie-break 2: prioritize Rest Sites (Campfires)
        cmp = RestSites.CompareTo(other.RestSites);
        if (cmp != 0) return cmp;

        // 4. Tie-break 3: prioritize Shops
        cmp = Shops.CompareTo(other.Shops);
        if (cmp != 0) return cmp;

        // 5. Tie-break 4: shorter path length (more direct route)
        return other.Length.CompareTo(Length);
    }

    /// <summary>
    /// Two route scores are considered equal when their high-value node composition is identical.
    /// Non-high-value nodes (hallway fights, unknowns, treasures) do not differentiate high-value scores.
    /// </summary>
    public bool Equals(RouteScore other) =>
        Elites == other.Elites &&
        RestSites == other.RestSites &&
        Shops == other.Shops;

    public override bool Equals(object? obj) => obj is RouteScore other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Elites, RestSites, Shops);

    public static bool operator ==(RouteScore left, RouteScore right) => left.Equals(right);
    public static bool operator !=(RouteScore left, RouteScore right) => !left.Equals(right);
    public static bool operator <(RouteScore left, RouteScore right) => left.CompareTo(right) < 0;
    public static bool operator <=(RouteScore left, RouteScore right) => left.CompareTo(right) <= 0;
    public static bool operator >(RouteScore left, RouteScore right) => left.CompareTo(right) > 0;
    public static bool operator >=(RouteScore left, RouteScore right) => left.CompareTo(right) >= 0;
}
