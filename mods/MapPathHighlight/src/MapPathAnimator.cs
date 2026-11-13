using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MapPathHighlight;

/// <summary>
/// Manages optimal path highlighting, synchronized electrical current animation,
/// color-coded branch streams, and unified branch decision cards.
/// </summary>
public sealed class MapPathAnimator
{
    private static readonly Lazy<MapPathAnimator> _instance = new(() => new MapPathAnimator());
    public static MapPathAnimator Instance => _instance.Value;

    // Reflection access to NMapScreen private fields
    private static readonly FieldInfo PathsField = AccessTools.Field(typeof(NMapScreen), "_paths");
    private static readonly FieldInfo MapField = AccessTools.Field(typeof(NMapScreen), "_map");
    private static readonly FieldInfo RunStateField = AccessTools.Field(typeof(NMapScreen), "_runState");
    private static readonly FieldInfo PathsContainerField = AccessTools.Field(typeof(NMapScreen), "_pathsContainer");
    private static readonly FieldInfo PointsContainerField = AccessTools.Field(typeof(NMapScreen), "_points");
    private static readonly FieldInfo MapPointDictField = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary");

    public readonly record struct BranchPalette(
        string Name,
        Color BaseDotColor,
        Color SurgeDotColor,
        Color GlowColor,
        Color CoreColor,
        Color AccentColor);

    private static readonly BranchPalette[] Palettes =
    [
        new BranchPalette(
            Name: "Cyan",
            BaseDotColor: new Color(0.18f, 0.72f, 1.0f, 0.85f),
            SurgeDotColor: new Color(1.0f, 1.0f, 1.0f, 1.0f),
            GlowColor: new Color(0.18f, 0.75f, 1.0f, 0.55f),
            CoreColor: new Color(0.92f, 0.98f, 1.0f, 0.95f),
            AccentColor: new Color("33BFFF")
        ),
        new BranchPalette(
            Name: "Amber",
            BaseDotColor: new Color(1.0f, 0.78f, 0.18f, 0.85f),
            SurgeDotColor: new Color(1.0f, 0.98f, 0.85f, 1.0f),
            GlowColor: new Color(1.0f, 0.75f, 0.15f, 0.55f),
            CoreColor: new Color(1.0f, 0.98f, 0.88f, 0.95f),
            AccentColor: new Color("FFD166")
        ),
        new BranchPalette(
            Name: "Coral",
            BaseDotColor: new Color(1.0f, 0.35f, 0.55f, 0.85f),
            SurgeDotColor: new Color(1.0f, 0.90f, 0.95f, 1.0f),
            GlowColor: new Color(1.0f, 0.35f, 0.55f, 0.55f),
            CoreColor: new Color(1.0f, 0.92f, 0.95f, 0.95f),
            AccentColor: new Color("FF6B8B")
        )
    ];

    // Stock Godot visual nodes
    private readonly List<Line2D> _glowLines = [];
    private readonly List<Line2D> _coreLines = [];
    private readonly List<PanelContainer> _branchBadges = [];
    private readonly Dictionary<MapPointType, Texture2D?> _roomIcons = [];

    // State tracking
    private NMapPoint? _currentHoveredPoint;
    private OptimalRouteSet? _activeRouteSet;
    private readonly List<TextureRect> _activeDots = [];
    private readonly Dictionary<TextureRect, Color> _originalModulate = [];
    private readonly Dictionary<TextureRect, Vector2> _originalScales = [];
    private readonly Dictionary<TextureRect, Color> _dotBaseColors = [];
    private readonly Dictionary<TextureRect, Color> _dotSurgeColors = [];
    private double _animTime;

    private MapPathAnimator() { }

    public void OnPointFocused(NMapPoint mapPointNode)
    {
        if (mapPointNode == null || !GodotObject.IsInstanceValid(mapPointNode))
            return;

        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
            return;

        ActMap? map = MapField?.GetValue(screen) as ActMap;
        if (map == null)
            return;

        RunState? runState = RunStateField?.GetValue(screen) as RunState;
        IReadOnlyList<MapCoord>? visitedCoords = runState?.VisitedMapCoords;

        OptimalRouteSet? routeSet = PathOptimizer.Calculate(map, mapPointNode.Point, visitedCoords);
        if (routeSet == null || routeSet.Routes.Count == 0)
            return;

        ClearHighlight();

        _currentHoveredPoint = mapPointNode;
        _activeRouteSet = routeSet;

        ApplyRouteSetHighlight(screen, routeSet);
    }

    public void OnPointUnfocused(NMapPoint mapPointNode)
    {
        if (_currentHoveredPoint == mapPointNode)
        {
            ClearHighlight();
        }
    }

    public void OnScreenClosed()
    {
        ClearHighlight();
    }

    public void OnMapReset()
    {
        ClearHighlight();
        _glowLines.Clear();
        _coreLines.Clear();
    }

    public void OnScreenProcess(double delta)
    {
        if (_activeDots.Count == 0)
            return;

        _animTime += delta;

        const float waveSpeed = 460f;   // Map Y pixels per second
        const float waveSpacing = 280f; // Pixels between surges
        const float waveWidth = 120f;   // Width of surge pulse

        float waveHead = (float)(_animTime * waveSpeed);

        for (int i = 0; i < _activeDots.Count; i++)
        {
            TextureRect dot = _activeDots[i];
            if (dot == null || !GodotObject.IsInstanceValid(dot))
                continue;

            float height = 750f - dot.Position.Y;
            float dist = ((waveHead - height) % waveSpacing + waveSpacing) % waveSpacing;
            float surge = 0f;
            if (dist < waveWidth)
            {
                float t = dist / waveWidth;
                surge = Mathf.Sin(t * Mathf.Pi);
            }

            Color baseColor = _dotBaseColors.GetValueOrDefault(dot, Palettes[0].BaseDotColor);
            Color surgeColor = _dotSurgeColors.GetValueOrDefault(dot, Palettes[0].SurgeDotColor);
            dot.Modulate = baseColor.Lerp(surgeColor, surge);

            if (_originalScales.TryGetValue(dot, out Vector2 origScale))
            {
                dot.Scale = origScale * (1.15f + 0.65f * surge);
            }
        }

        // Pulse all outer glow lines with their assigned palette colors
        float glowAlpha = 0.42f + 0.18f * Mathf.Sin((float)(_animTime * 5.0));
        for (int i = 0; i < _glowLines.Count; i++)
        {
            Line2D glow = _glowLines[i];
            if (glow != null && GodotObject.IsInstanceValid(glow))
            {
                BranchPalette pal = Palettes[i % Palettes.Length];
                Color glowCol = pal.GlowColor;
                glowCol.A = glowAlpha;
                glow.DefaultColor = glowCol;
            }
        }
    }

    public void ClearHighlight()
    {
        _currentHoveredPoint = null;
        _activeRouteSet = null;

        foreach (var kvp in _originalModulate)
        {
            if (kvp.Key != null && GodotObject.IsInstanceValid(kvp.Key))
            {
                kvp.Key.Modulate = kvp.Value;
            }
        }

        foreach (var kvp in _originalScales)
        {
            if (kvp.Key != null && GodotObject.IsInstanceValid(kvp.Key))
            {
                kvp.Key.Scale = kvp.Value;
            }
        }

        _originalModulate.Clear();
        _originalScales.Clear();
        _activeDots.Clear();
        _dotBaseColors.Clear();
        _dotSurgeColors.Clear();

        for (int i = _glowLines.Count - 1; i >= 0; i--)
        {
            Line2D glow = _glowLines[i];
            if (glow != null && GodotObject.IsInstanceValid(glow))
                glow.ClearPoints();
            else
                _glowLines.RemoveAt(i);
        }

        for (int i = _coreLines.Count - 1; i >= 0; i--)
        {
            Line2D core = _coreLines[i];
            if (core != null && GodotObject.IsInstanceValid(core))
                core.ClearPoints();
            else
                _coreLines.RemoveAt(i);
        }

        foreach (PanelContainer badge in _branchBadges)
        {
            badge?.QueueFreeSafely();
        }
        _branchBadges.Clear();
    }

    private void ApplyRouteSetHighlight(NMapScreen screen, OptimalRouteSet routeSet)
    {
        try
        {
            Control? pathsContainer = PathsContainerField?.GetValue(screen) as Control;
            Control? pointsContainer = PointsContainerField?.GetValue(screen) as Control;
            if (pathsContainer == null || !GodotObject.IsInstanceValid(pathsContainer))
                return;

            if (PathsField?.GetValue(screen) is not Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths)
                return;

            Dictionary<MapCoord, NMapPoint>? pointDict = MapPointDictField?.GetValue(screen) as Dictionary<MapCoord, NMapPoint>;

            EnsureLineCount(pathsContainer, routeSet.Routes.Count);

            HashSet<TextureRect> uniqueDots = [];
            Dictionary<TextureRect, int> dotRouteCount = [];
            Dictionary<TextureRect, int> dotFirstRoute = [];

            for (int rIdx = 0; rIdx < routeSet.Routes.Count; rIdx++)
            {
                OptimalRoute route = routeSet.Routes[rIdx];
                BranchPalette pal = Palettes[rIdx % Palettes.Length];
                List<Vector2> linePoints = [];

                for (int i = 0; i < route.FullPath.Count - 1; i++)
                {
                    MapPoint pA = route.FullPath[i];
                    MapPoint pB = route.FullPath[i + 1];

                    if (paths.TryGetValue((pA.coord, pB.coord), out var segmentDots) && segmentDots != null)
                    {
                        foreach (TextureRect dot in segmentDots)
                        {
                            if (dot == null || !GodotObject.IsInstanceValid(dot))
                                continue;

                            if (uniqueDots.Add(dot))
                            {
                                if (!_originalModulate.ContainsKey(dot))
                                {
                                    _originalModulate[dot] = dot.Modulate;
                                    _originalScales[dot] = dot.Scale;
                                }
                                _activeDots.Add(dot);
                            }

                            dotRouteCount[dot] = dotRouteCount.GetValueOrDefault(dot, 0) + 1;
                            if (!dotFirstRoute.ContainsKey(dot))
                                dotFirstRoute[dot] = rIdx;

                            linePoints.Add(dot.Position + (dot.Size * 0.5f));
                        }
                    }
                }

                if (linePoints.Count > 0)
                {
                    Vector2[] pts = linePoints.ToArray();
                    if (rIdx < _glowLines.Count && GodotObject.IsInstanceValid(_glowLines[rIdx]))
                    {
                        _glowLines[rIdx].Points = pts;
                        _glowLines[rIdx].DefaultColor = pal.GlowColor;
                    }
                    if (rIdx < _coreLines.Count && GodotObject.IsInstanceValid(_coreLines[rIdx]))
                    {
                        _coreLines[rIdx].Points = pts;
                        _coreLines[rIdx].DefaultColor = pal.CoreColor;
                    }
                }
            }

            // Color dots: shared trunk dots get bright cyan-white, branch dots get their distinct route hue
            foreach (TextureRect dot in _activeDots)
            {
                if (dot == null || !GodotObject.IsInstanceValid(dot))
                    continue;

                int count = dotRouteCount.GetValueOrDefault(dot, 1);
                if (count > 1)
                {
                    _dotBaseColors[dot] = new Color(0.20f, 0.85f, 1.0f, 0.90f);
                    _dotSurgeColors[dot] = new Color(1.0f, 1.0f, 1.0f, 1.0f);
                }
                else
                {
                    int rIdx = dotFirstRoute.GetValueOrDefault(dot, 0);
                    BranchPalette pal = Palettes[rIdx % Palettes.Length];
                    _dotBaseColors[dot] = pal.BaseDotColor;
                    _dotSurgeColors[dot] = pal.SurgeDotColor;
                }
            }

            // Present counter badges in active screen vision directly adjacent to the hovered node
            if (pointsContainer != null && GodotObject.IsInstanceValid(pointsContainer) && pointDict != null)
            {
                PlaceBadges(pointsContainer, pointDict, routeSet);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Error applying route set highlight: {ex}");
        }
    }

    private void PlaceBadges(Control pointsContainer, Dictionary<MapCoord, NMapPoint> pointDict, OptimalRouteSet routeSet)
    {
        if (!pointDict.TryGetValue(routeSet.TargetPoint.coord, out NMapPoint? targetNode) ||
            targetNode == null || !GodotObject.IsInstanceValid(targetNode))
        {
            return;
        }

        Vector2 center = targetNode.Position;

        // Group routes by distinct score trade-offs so we never show duplicate counts
        List<OptimalRoute> distinctScoreRoutes = [];
        HashSet<RouteScore> seenScores = [];
        foreach (OptimalRoute r in routeSet.Routes)
        {
            if (seenScores.Add(r.Score))
            {
                distinctScoreRoutes.Add(r);
                if (distinctScoreRoutes.Count >= 3)
                    break;
            }
        }

        if (distinctScoreRoutes.Count == 0)
            return;

        // Check horizontal clearance against adjacent column nodes
        bool hasRightNeighbor = false;
        bool hasLeftNeighbor = false;
        foreach (var kvp in pointDict)
        {
            NMapPoint mapPointNode = kvp.Value;
            if (mapPointNode == null || !GodotObject.IsInstanceValid(mapPointNode) || mapPointNode == targetNode)
                continue;

            float dy = Math.Abs(mapPointNode.Position.Y - center.Y);
            if (dy < 42f)
            {
                float dx = mapPointNode.Position.X - center.X;
                if (dx > 0f && dx < 200f) hasRightNeighbor = true;
                if (dx < 0f && dx > -200f) hasLeftNeighbor = true;
            }
        }

        PanelContainer container;
        if (routeSet.HasDistinctScores && distinctScoreRoutes.Count > 1)
        {
            container = CreateUnifiedBranchCard(distinctScoreRoutes);
        }
        else
        {
            OptimalRoute primaryRoute = distinctScoreRoutes[0];
            container = CreateSingleBadge(primaryRoute);
        }

        container.ZIndex = 25; // Always render above map nodes & paths
        pointsContainer.AddChildSafely(container);
        _branchBadges.Add(container);

        container.ResetSize();
        Vector2 minSize = container.GetCombinedMinimumSize();
        float cardWidth = Math.Max(minSize.X, 48f);
        float cardHeight = Math.Max(minSize.Y, 28f);

        const float gapX = 46f;
        const float gapY = 46f;

        List<Vector2> candidateSlots = [];
        bool preferRight = center.X < -50f;

        if (preferRight && !hasRightNeighbor && center.X + gapX + cardWidth <= 460f)
            candidateSlots.Add(new Vector2(center.X + gapX, center.Y - cardHeight * 0.5f));
        else if (!preferRight && !hasLeftNeighbor && center.X - gapX - cardWidth >= -560f)
            candidateSlots.Add(new Vector2(center.X - gapX - cardWidth, center.Y - cardHeight * 0.5f));

        // Vertical slots (always wide open between rows)
        candidateSlots.Add(new Vector2(center.X - cardWidth * 0.5f, center.Y - gapY - cardHeight));
        candidateSlots.Add(new Vector2(center.X - cardWidth * 0.5f, center.Y + gapY));

        // Opposite horizontal slot
        if (preferRight)
        {
            if (!hasLeftNeighbor && center.X - gapX - cardWidth >= -560f)
                candidateSlots.Add(new Vector2(center.X - gapX - cardWidth, center.Y - cardHeight * 0.5f));
        }
        else
        {
            if (!hasRightNeighbor && center.X + gapX + cardWidth <= 460f)
                candidateSlots.Add(new Vector2(center.X + gapX, center.Y - cardHeight * 0.5f));
        }

        Vector2 bestPos = candidateSlots.FirstOrDefault();
        if (bestPos == Vector2.Zero && candidateSlots.Count == 0)
        {
            bestPos = preferRight
                ? new Vector2(center.X + gapX, center.Y - cardHeight * 0.5f)
                : new Vector2(center.X - gapX - cardWidth, center.Y - cardHeight * 0.5f);
        }

        container.Position = bestPos;
    }

    private PanelContainer CreateUnifiedBranchCard(List<OptimalRoute> distinctRoutes)
    {
        PanelContainer card = new()
        {
            Name = "UnifiedBranchCard",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.95f),
            BorderColor = new Color(0.2f, 0.75f, 1.0f, 0.9f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 9,
            ContentMarginRight = 9,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        card.AddThemeStyleboxOverride("panel", style);

        VBoxContainer vbox = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vbox.AddThemeConstantOverride("separation", 5);
        card.AddChildSafely(vbox);

        for (int i = 0; i < distinctRoutes.Count; i++)
        {
            OptimalRoute r = distinctRoutes[i];
            BranchPalette pal = Palettes[i % Palettes.Length];

            HBoxContainer row = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 8);
            vbox.AddChildSafely(row);

            Panel swatch = new()
            {
                CustomMinimumSize = new Vector2(10, 10),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            StyleBoxFlat swatchStyle = new()
            {
                BgColor = pal.AccentColor,
                CornerRadiusTopLeft = 5,
                CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5,
                CornerRadiusBottomRight = 5
            };
            swatch.AddThemeStyleboxOverride("panel", swatchStyle);
            row.AddChildSafely(swatch);

            AddScoreIconsToContainer(row, r.Score);
        }

        return card;
    }

    private PanelContainer CreateSingleBadge(OptimalRoute route)
    {
        PanelContainer badge = new()
        {
            Name = "SinglePathBadge",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.94f),
            BorderColor = new Color(0.2f, 0.75f, 1.0f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        badge.AddThemeStyleboxOverride("panel", style);

        HBoxContainer hbox = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        hbox.AddThemeConstantOverride("separation", 8);
        badge.AddChildSafely(hbox);

        AddScoreIconsToContainer(hbox, route.Score);

        return badge;
    }

    private void AddScoreIconsToContainer(HBoxContainer hbox, RouteScore score)
    {
        bool hasItem = false;

        if (score.Elites > 0)
        {
            AddRoomCountPair(hbox, MapPointType.Elite, score.Elites, new Color("FF6B6B"), "E");
            hasItem = true;
        }

        if (score.RestSites > 0)
        {
            AddRoomCountPair(hbox, MapPointType.RestSite, score.RestSites, new Color("51CF66"), "R");
            hasItem = true;
        }

        if (score.Shops > 0)
        {
            AddRoomCountPair(hbox, MapPointType.Shop, score.Shops, new Color("FCC419"), "$");
            hasItem = true;
        }

        if (!hasItem)
        {
            Label emptyLabel = new()
            {
                Text = "—",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            emptyLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.7f, 0.78f));
            emptyLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChildSafely(emptyLabel);
        }
    }

    private void AddRoomCountPair(HBoxContainer parent, MapPointType pointType, int count, Color color, string fallbackSymbol)
    {
        HBoxContainer pair = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pair.AddThemeConstantOverride("separation", 3);

        Texture2D? icon = GetRoomIcon(pointType);
        if (icon != null)
        {
            TextureRect iconRect = new()
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(20, 20),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            pair.AddChildSafely(iconRect);
        }
        else
        {
            Label fallback = new()
            {
                Text = fallbackSymbol,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            fallback.AddThemeColorOverride("font_color", color);
            fallback.AddThemeFontSizeOverride("font_size", 14);
            pair.AddChildSafely(fallback);
        }

        Label countLabel = new()
        {
            Text = count.ToString(),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        countLabel.AddThemeColorOverride("font_color", color);
        countLabel.AddThemeFontSizeOverride("font_size", 14);
        pair.AddChildSafely(countLabel);

        parent.AddChildSafely(pair);
    }

    private Texture2D? GetRoomIcon(MapPointType pointType)
    {
        if (_roomIcons.TryGetValue(pointType, out Texture2D? cached))
            return cached;

        (string runHistoryPath, RoomType roomType) = pointType switch
        {
            MapPointType.Elite => ("res://images/ui/run_history/elite.png", RoomType.Elite),
            MapPointType.RestSite => ("res://images/ui/run_history/rest_site.png", RoomType.RestSite),
            MapPointType.Shop => ("res://images/ui/run_history/shop.png", RoomType.Shop),
            _ => ("", RoomType.Unassigned)
        };

        Texture2D? tex = LoadTexture(runHistoryPath)
            ?? (roomType != RoomType.Unassigned ? LoadTexture(ImageHelper.GetRoomIconPath(pointType, roomType, null)) : null);

        _roomIcons[pointType] = tex;
        return tex;
    }

    private static Texture2D? LoadTexture(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && ResourceLoader.Exists(path)
                ? ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureLineCount(Control pathsContainer, int requiredCount)
    {
        // Prune any freed or detached lines from previous acts or scene resets
        _glowLines.RemoveAll(l => l == null || !GodotObject.IsInstanceValid(l) || l.GetParent() != pathsContainer);
        _coreLines.RemoveAll(l => l == null || !GodotObject.IsInstanceValid(l) || l.GetParent() != pathsContainer);

        while (_glowLines.Count < requiredCount)
        {
            Line2D glow = new()
            {
                Name = $"MapPathGlowLine_{_glowLines.Count}",
                Width = 7.5f,
                DefaultColor = new Color(0.15f, 0.72f, 1.0f, 0.5f),
                JointMode = Line2D.LineJointMode.Round,
                BeginCapMode = Line2D.LineCapMode.Round,
                EndCapMode = Line2D.LineCapMode.Round,
                Antialiased = true
            };
            pathsContainer.AddChildSafely(glow);
            _glowLines.Add(glow);

            Line2D core = new()
            {
                Name = $"MapPathCoreLine_{_coreLines.Count}",
                Width = 2.4f,
                DefaultColor = new Color(0.9f, 0.98f, 1.0f, 0.95f),
                JointMode = Line2D.LineJointMode.Round,
                BeginCapMode = Line2D.LineCapMode.Round,
                EndCapMode = Line2D.LineCapMode.Round,
                Antialiased = true
            };
            pathsContainer.AddChildSafely(core);
            _coreLines.Add(core);
        }
    }
}
