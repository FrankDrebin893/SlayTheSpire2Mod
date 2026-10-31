using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Map;

namespace MapPathHighlight;

/// <summary>
/// A single optimal route in the map.
/// </summary>
public sealed class OptimalRoute
{
    public required List<MapPoint> FullPath { get; init; }
    public required RouteScore Score { get; init; }
    public required MapPoint TargetPoint { get; init; }
    public required int TargetIndexInPath { get; init; }
    public required bool IsFromCurrentLocation { get; init; }

    /// <summary>
    /// Distinct node on this route where it diverges from other routes,
    /// used to position the branch counter badge.
    /// </summary>
    public MapPoint? DivergenceNode { get; set; }
}

/// <summary>
/// Set of all optimal routes that achieve the equal maximum high-value room count.
/// </summary>
public sealed class OptimalRouteSet
{
    public required List<OptimalRoute> Routes { get; init; }
    public required MapPoint TargetPoint { get; init; }
    public required bool IsFromCurrentLocation { get; init; }
    public required int MaxHighValueCount { get; init; }
}

/// <summary>
/// Calculates optimal paths in the map DAG that maximize high-value encounters
/// (Elites, Rest Sites / Campsites, Shops), branching when multiple paths achieve equal maximums.
/// </summary>
public static class PathOptimizer
{
    private const int MaxBranchRoutes = 3;

    public static OptimalRouteSet? Calculate(
        ActMap map,
        MapPoint targetPoint,
        IReadOnlyList<MapCoord>? visitedCoords)
    {
        if (map == null || targetPoint == null)
            return null;

        MapCoord? currentCoord = (visitedCoords != null && visitedCoords.Count > 0)
            ? visitedCoords[visitedCoords.Count - 1]
            : null;

        MapPoint? currentPoint = currentCoord.HasValue ? map.GetPoint(currentCoord.Value) : null;

        bool isReachableFromCurrent = false;
        HashSet<MapPoint> startNodes = [];

        if (currentPoint != null)
        {
            isReachableFromCurrent = IsReachable(currentPoint, targetPoint);
        }

        bool isFromCurrentLocation = currentPoint != null && isReachableFromCurrent;

        if (isFromCurrentLocation)
        {
            startNodes.Add(currentPoint!);
        }
        else
        {
            // Collect all starting candidates (row 0)
            if (map.startMapPoints != null && map.startMapPoints.Count > 0)
            {
                foreach (MapPoint p in map.startMapPoints)
                    startNodes.Add(p);
            }
            else
            {
                foreach (MapPoint p in map.GetPointsInRow(0))
                    startNodes.Add(p);
            }

            if (startNodes.Count == 0 && map.StartingMapPoint != null)
            {
                foreach (MapPoint child in map.StartingMapPoint.Children)
                    startNodes.Add(child);
            }
        }

        if (startNodes.Count == 0)
            return null;

        // Part A: All optimal incoming paths from startNodes to targetPoint with equal max high-value nodes
        Dictionary<MapPoint, (int MaxHV, List<List<MapPoint>> Paths)> memoA = [];
        var partA = GetBestIncomingPaths(targetPoint, startNodes, memoA);
        if (partA.Paths.Count == 0)
        {
            partA = (HighValue(targetPoint), [[targetPoint]]);
        }

        // Part B: All optimal outgoing paths from targetPoint to Boss with equal max high-value nodes
        MapPoint finalBoss = map.SecondBossMapPoint ?? map.BossMapPoint;
        Dictionary<MapPoint, (int MaxHV, List<List<MapPoint>> Paths)> memoB = [];
        var partB = GetBestOutgoingPaths(targetPoint, finalBoss, memoB);
        if (partB.Paths.Count == 0)
        {
            partB = (HighValue(targetPoint), [[targetPoint]]);
        }

        // Combine all incoming and outgoing branches
        List<OptimalRoute> allRoutes = [];
        bool isActStart = (visitedCoords == null || visitedCoords.Count == 0) && map.StartingMapPoint != null;

        foreach (var inPath in partA.Paths)
        {
            foreach (var outPath in partB.Paths)
            {
                List<MapPoint> fullPath = new(inPath.Count + outPath.Count - 1);
                fullPath.AddRange(inPath);
                for (int i = 1; i < outPath.Count; i++)
                {
                    fullPath.Add(outPath[i]);
                }

                int targetIndex = inPath.Count - 1;

                if (isActStart && map.StartingMapPoint != null)
                {
                    fullPath.Insert(0, map.StartingMapPoint);
                    targetIndex++;
                }

                RouteScore score = CalculateScore(fullPath);

                allRoutes.Add(new OptimalRoute
                {
                    FullPath = fullPath,
                    Score = score,
                    TargetPoint = targetPoint,
                    TargetIndexInPath = targetIndex,
                    IsFromCurrentLocation = isFromCurrentLocation
                });
            }
        }

        if (allRoutes.Count == 0)
            return null;

        // Deduplicate routes:
        // Prioritize distinct scores, then distinct physical branches, capped to MaxBranchRoutes (3)
        List<OptimalRoute> routes = [];
        HashSet<RouteScore> seenScores = [];
        HashSet<string> seenSignatures = [];

        foreach (OptimalRoute r in allRoutes)
        {
            if (seenScores.Add(r.Score))
            {
                routes.Add(r);
                seenSignatures.Add(GetPathSignature(r));
                if (routes.Count >= MaxBranchRoutes)
                    break;
            }
        }

        if (routes.Count < MaxBranchRoutes)
        {
            foreach (OptimalRoute r in allRoutes)
            {
                if (seenSignatures.Add(GetPathSignature(r)))
                {
                    routes.Add(r);
                    if (routes.Count >= MaxBranchRoutes)
                        break;
                }
            }
        }

        return new OptimalRouteSet
        {
            Routes = routes,
            TargetPoint = targetPoint,
            IsFromCurrentLocation = isFromCurrentLocation,
            MaxHighValueCount = routes[0].Score.TotalTargetRooms
        };
    }

    private static int HighValue(MapPoint p) =>
        (p.PointType == MapPointType.Elite || p.PointType == MapPointType.RestSite || p.PointType == MapPointType.Shop) ? 1 : 0;

    private static bool IsReachable(MapPoint from, MapPoint to)
    {
        if (from == to) return true;
        if (from.coord.row >= to.coord.row) return false;

        Queue<MapPoint> queue = new();
        HashSet<MapPoint> visited = [];
        queue.Enqueue(from);
        visited.Add(from);

        while (queue.Count > 0)
        {
            MapPoint curr = queue.Dequeue();
            if (curr == to) return true;

            foreach (MapPoint child in curr.Children)
            {
                if (child.coord.row <= to.coord.row && visited.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return false;
    }

    private static (int MaxHV, List<List<MapPoint>> Paths) GetBestIncomingPaths(
        MapPoint current,
        HashSet<MapPoint> startNodes,
        Dictionary<MapPoint, (int, List<List<MapPoint>>)> memo)
    {
        if (memo.TryGetValue(current, out var cached))
            return cached;

        int nodeHV = HighValue(current);

        if (startNodes.Contains(current))
        {
            var res = (nodeHV, new List<List<MapPoint>> { new() { current } });
            memo[current] = res;
            return res;
        }

        int bestParentHV = -1;
        List<List<MapPoint>> bestParentPaths = [];

        foreach (MapPoint parent in current.parents)
        {
            var parentRes = GetBestIncomingPaths(parent, startNodes, memo);
            if (parentRes.Paths.Count > 0)
            {
                if (parentRes.MaxHV > bestParentHV)
                {
                    bestParentHV = parentRes.MaxHV;
                    bestParentPaths.Clear();
                    foreach (var path in parentRes.Paths)
                    {
                        List<MapPoint> newPath = new(path.Count + 1);
                        newPath.AddRange(path);
                        newPath.Add(current);
                        bestParentPaths.Add(newPath);
                    }
                }
                else if (parentRes.MaxHV == bestParentHV)
                {
                    // Equal high value: branch!
                    foreach (var path in parentRes.Paths)
                    {
                        List<MapPoint> newPath = new(path.Count + 1);
                        newPath.AddRange(path);
                        newPath.Add(current);
                        bestParentPaths.Add(newPath);
                    }
                }
            }
        }

        if (bestParentHV >= 0)
        {
            var res = (bestParentHV + nodeHV, bestParentPaths);
            memo[current] = res;
            return res;
        }

        var emptyRes = (0, new List<List<MapPoint>>());
        memo[current] = emptyRes;
        return emptyRes;
    }

    private static (int MaxHV, List<List<MapPoint>> Paths) GetBestOutgoingPaths(
        MapPoint current,
        MapPoint finalBoss,
        Dictionary<MapPoint, (int, List<List<MapPoint>>)> memo)
    {
        if (memo.TryGetValue(current, out var cached))
            return cached;

        int nodeHV = HighValue(current);

        if (current == finalBoss || current.Children.Count == 0)
        {
            var res = (nodeHV, new List<List<MapPoint>> { new() { current } });
            memo[current] = res;
            return res;
        }

        int bestChildHV = -1;
        List<List<MapPoint>> bestChildPaths = [];

        foreach (MapPoint child in current.Children)
        {
            var childRes = GetBestOutgoingPaths(child, finalBoss, memo);
            if (childRes.Paths.Count > 0)
            {
                if (childRes.MaxHV > bestChildHV)
                {
                    bestChildHV = childRes.MaxHV;
                    bestChildPaths.Clear();
                    foreach (var path in childRes.Paths)
                    {
                        List<MapPoint> newPath = new(path.Count + 1) { current };
                        newPath.AddRange(path);
                        bestChildPaths.Add(newPath);
                    }
                }
                else if (childRes.MaxHV == bestChildHV)
                {
                    // Equal high value: branch!
                    foreach (var path in childRes.Paths)
                    {
                        List<MapPoint> newPath = new(path.Count + 1) { current };
                        newPath.AddRange(path);
                        bestChildPaths.Add(newPath);
                    }
                }
            }
        }

        if (bestChildHV >= 0)
        {
            var res = (nodeHV + bestChildHV, bestChildPaths);
            memo[current] = res;
            return res;
        }

        var fallbackRes = (nodeHV, new List<List<MapPoint>> { new() { current } });
        memo[current] = fallbackRes;
        return fallbackRes;
    }

    private static RouteScore CalculateScore(List<MapPoint> path)
    {
        int elites = 0, restSites = 0, shops = 0, unknowns = 0, monsters = 0, treasures = 0;
        foreach (MapPoint p in path)
        {
            switch (p.PointType)
            {
                case MapPointType.Elite: elites++; break;
                case MapPointType.RestSite: restSites++; break;
                case MapPointType.Shop: shops++; break;
                case MapPointType.Unknown: unknowns++; break;
                case MapPointType.Monster: monsters++; break;
                case MapPointType.Treasure: treasures++; break;
            }
        }
        return new RouteScore(elites, restSites, shops, unknowns, monsters, treasures, path.Count);
    }

    private static string GetPathSignature(OptimalRoute r)
    {
        return string.Join("->", r.FullPath.Select(p => $"{p.coord.col},{p.coord.row}"));
    }
}
