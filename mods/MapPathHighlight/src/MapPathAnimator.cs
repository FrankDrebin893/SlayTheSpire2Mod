using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace MapPathHighlight;

/// <summary>
/// Manages the rendering, multi-branch highlighting, branch counters,
/// and electrical current animation flowing from bottom towards top.
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

    // Stock Godot nodes for visual effects
    private readonly List<Line2D> _glowLines = [];
    private readonly List<Line2D> _coreLines = [];
    private readonly List<PanelContainer> _branchBadges = [];
    private PanelContainer? _hudBanner;
    private Label? _hudIcon;
    private Label? _hudStats;

    // State tracking
    private NMapPoint? _currentHoveredPoint;
    private OptimalRouteSet? _activeRouteSet;
    private readonly List<TextureRect> _activeDots = [];
    private readonly Dictionary<TextureRect, Color> _originalModulate = [];
    private readonly Dictionary<TextureRect, Vector2> _originalScales = [];
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

    public void OnScreenProcess(double delta)
    {
        if (_activeDots.Count == 0)
            return;

        _animTime += delta;

        // Animate electrical current surging upward along the path dots across all branches
        const float waveSpeed = 460f;   // Map Y pixels per second
        const float waveSpacing = 280f; // Pixels between surges
        const float waveWidth = 120f;   // Width of surge pulse

        float waveHead = (float)(_animTime * waveSpeed);

        Color baseColor = new(0.18f, 0.72f, 1.0f, 0.85f); // Electric cyan
        Color surgeColor = new(1.0f, 1.0f, 1.0f, 1.0f);   // White-hot core

        for (int i = 0; i < _activeDots.Count; i++)
        {
            TextureRect dot = _activeDots[i];
            if (dot == null || !GodotObject.IsInstanceValid(dot))
                continue;

            // Height on map (starts low/positive Y at bottom, rises towards negative Y at top)
            float height = 750f - dot.Position.Y;
            float dist = ((waveHead - height) % waveSpacing + waveSpacing) % waveSpacing;
            float surge = 0f;
            if (dist < waveWidth)
            {
                float t = dist / waveWidth;
                surge = Mathf.Sin(t * Mathf.Pi);
            }

            dot.Modulate = baseColor.Lerp(surgeColor, surge);

            if (_originalScales.TryGetValue(dot, out Vector2 origScale))
            {
                dot.Scale = origScale * (1.15f + 0.65f * surge);
            }
        }

        // Pulse all outer glow lines
        float glowAlpha = 0.42f + 0.18f * Mathf.Sin((float)(_animTime * 5.0));
        Color glowCol = new(0.15f, 0.75f, 1.0f, glowAlpha);
        foreach (Line2D glow in _glowLines)
        {
            if (glow != null && GodotObject.IsInstanceValid(glow))
                glow.DefaultColor = glowCol;
        }
    }

    public void ClearHighlight()
    {
        _currentHoveredPoint = null;
        _activeRouteSet = null;

        // Restore original dot colors and scales
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

        foreach (Line2D glow in _glowLines)
        {
            if (glow != null && GodotObject.IsInstanceValid(glow))
                glow.ClearPoints();
        }

        foreach (Line2D core in _coreLines)
        {
            if (core != null && GodotObject.IsInstanceValid(core))
                core.ClearPoints();
        }

        foreach (PanelContainer badge in _branchBadges)
        {
            badge?.QueueFreeSafely();
        }
        _branchBadges.Clear();

        if (_hudBanner != null && GodotObject.IsInstanceValid(_hudBanner))
        {
            _hudBanner.Visible = false;
        }
    }

    private void ApplyRouteSetHighlight(NMapScreen screen, OptimalRouteSet routeSet)
    {
        EnsureHudBanner(screen);

        Control? pathsContainer = PathsContainerField?.GetValue(screen) as Control;
        Control? pointsContainer = PointsContainerField?.GetValue(screen) as Control;
        if (pathsContainer == null || !GodotObject.IsInstanceValid(pathsContainer))
            return;

        if (PathsField?.GetValue(screen) is not Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths)
            return;

        Dictionary<MapCoord, NMapPoint>? pointDict = MapPointDictField?.GetValue(screen) as Dictionary<MapCoord, NMapPoint>;

        EnsureLineCount(pathsContainer, routeSet.Routes.Count);

        HashSet<TextureRect> uniqueDots = [];

        for (int rIdx = 0; rIdx < routeSet.Routes.Count; rIdx++)
        {
            OptimalRoute route = routeSet.Routes[rIdx];
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

                        linePoints.Add(dot.Position + (dot.Size * 0.5f));
                    }
                }
            }

            if (linePoints.Count > 0)
            {
                Vector2[] pts = linePoints.ToArray();
                if (rIdx < _glowLines.Count)
                    _glowLines[rIdx].Points = pts;
                if (rIdx < _coreLines.Count)
                    _coreLines[rIdx].Points = pts;
            }
        }

        // Always display counter badges for all highlighted routes (single or multiple branches)
        // with intelligent collision avoidance to guarantee no overlapping badges or obscured nodes.
        if (pointsContainer != null && GodotObject.IsInstanceValid(pointsContainer) && pointDict != null)
        {
            PlaceBadges(pointsContainer, pointDict, routeSet);
        }

        UpdateHudBanner(screen, routeSet);
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

        // Check immediate horizontal neighbors to avoid placing over adjacent nodes
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
                if (dx > 0f && dx < 190f) hasRightNeighbor = true;
                if (dx < 0f && dx > -190f) hasLeftNeighbor = true;
            }
        }

        const float gapX = 46f;
        const float gapY = 46f;

        List<Rect2> placedBadgeRects = [];

        for (int i = 0; i < distinctScoreRoutes.Count; i++)
        {
            OptimalRoute route = distinctScoreRoutes[i];
            PanelContainer badge = CreateBranchBadge(route);
            pointsContainer.AddChildSafely(badge);
            _branchBadges.Add(badge);

            badge.ResetSize();
            Vector2 minSize = badge.GetCombinedMinimumSize();
            float badgeWidth = Math.Max(minSize.X, EstimateBadgeWidth(route));
            float badgeHeight = Math.Max(minSize.Y, 28f);
            Vector2 badgeSize = new(badgeWidth, badgeHeight);

            List<Vector2> slots = [];
            bool preferRight = center.X < -50f;

            if (distinctScoreRoutes.Count == 1)
            {
                // Single badge: pick preferred side, or vertical if horizontal neighbor is close
                if (preferRight && !hasRightNeighbor && center.X + gapX + badgeWidth <= 460f)
                {
                    slots.Add(new Vector2(center.X + gapX, center.Y - badgeHeight * 0.5f));
                }
                else if (!preferRight && !hasLeftNeighbor && center.X - gapX - badgeWidth >= -560f)
                {
                    slots.Add(new Vector2(center.X - gapX - badgeWidth, center.Y - badgeHeight * 0.5f));
                }

                slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y - gapY - badgeHeight));
                slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y + gapY));

                if (preferRight)
                {
                    if (!hasLeftNeighbor && center.X - gapX - badgeWidth >= -560f)
                        slots.Add(new Vector2(center.X - gapX - badgeWidth, center.Y - badgeHeight * 0.5f));
                }
                else
                {
                    if (!hasRightNeighbor && center.X + gapX + badgeWidth <= 460f)
                        slots.Add(new Vector2(center.X + gapX, center.Y - badgeHeight * 0.5f));
                }
            }
            else
            {
                // Multi-badge (distinct trade-offs): place in opposite cardinal slots around targetPoint
                if (i == 0)
                {
                    if (preferRight && !hasRightNeighbor && center.X + gapX + badgeWidth <= 460f)
                        slots.Add(new Vector2(center.X + gapX, center.Y - badgeHeight * 0.5f));
                    else if (!preferRight && !hasLeftNeighbor && center.X - gapX - badgeWidth >= -560f)
                        slots.Add(new Vector2(center.X - gapX - badgeWidth, center.Y - badgeHeight * 0.5f));
                    else
                        slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y - gapY - badgeHeight));
                }
                else if (i == 1)
                {
                    if (preferRight && !hasLeftNeighbor && center.X - gapX - badgeWidth >= -560f)
                        slots.Add(new Vector2(center.X - gapX - badgeWidth, center.Y - badgeHeight * 0.5f));
                    else if (!preferRight && !hasRightNeighbor && center.X + gapX + badgeWidth <= 460f)
                        slots.Add(new Vector2(center.X + gapX, center.Y - badgeHeight * 0.5f));
                    else
                        slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y - gapY - badgeHeight));
                }
                else
                {
                    slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y - gapY - badgeHeight));
                    slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y + gapY));
                }

                // Fallback slots
                slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y - gapY - badgeHeight));
                slots.Add(new Vector2(center.X - badgeWidth * 0.5f, center.Y + gapY));
                slots.Add(new Vector2(center.X + gapX, center.Y - badgeHeight * 0.5f));
                slots.Add(new Vector2(center.X - gapX - badgeWidth, center.Y - badgeHeight * 0.5f));
            }

            Vector2 chosenPos = slots[0];
            foreach (Vector2 slotPos in slots)
            {
                Rect2 candidateRect = new(slotPos, badgeSize);
                bool collides = false;
                foreach (Rect2 placed in placedBadgeRects)
                {
                    if (Overlaps(candidateRect, placed, 8f))
                    {
                        collides = true;
                        break;
                    }
                }
                if (!collides)
                {
                    chosenPos = slotPos;
                    break;
                }
            }

            badge.Position = chosenPos;
            placedBadgeRects.Add(new Rect2(chosenPos, badgeSize));
        }
    }

    private static bool Overlaps(Rect2 a, Rect2 b, float padding)
    {
        float aMinX = a.Position.X - padding;
        float aMaxX = a.Position.X + a.Size.X + padding;
        float aMinY = a.Position.Y - padding;
        float aMaxY = a.Position.Y + a.Size.Y + padding;

        float bMinX = b.Position.X;
        float bMaxX = b.Position.X + b.Size.X;
        float bMinY = b.Position.Y;
        float bMaxY = b.Position.Y + b.Size.Y;

        return aMinX < bMaxX && aMaxX > bMinX && aMinY < bMaxY && aMaxY > bMinY;
    }

    private static float EstimateBadgeWidth(OptimalRoute route)
    {
        float width = 24f;
        int items = 0;
        if (route.Score.Elites > 0)
        {
            string text = $"{route.Score.Elites} Elite" + (route.Score.Elites > 1 ? "s" : "");
            width += text.Length * 8.5f;
            items++;
        }
        if (route.Score.RestSites > 0)
        {
            string text = $"{route.Score.RestSites} Camp" + (route.Score.RestSites > 1 ? "s" : "");
            width += text.Length * 8.5f + (items > 0 ? 16f : 0f);
            items++;
        }
        if (route.Score.Shops > 0)
        {
            string text = $"{route.Score.Shops} Shop" + (route.Score.Shops > 1 ? "s" : "");
            width += text.Length * 8.5f + (items > 0 ? 16f : 0f);
            items++;
        }
        if (items == 0)
        {
            string text = $"{route.Score.Monsters} Fights";
            width += text.Length * 8.5f;
        }
        return Math.Max(width, 110f);
    }

    private void EnsureLineCount(Control pathsContainer, int requiredCount)
    {
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

    private PanelContainer CreateBranchBadge(OptimalRoute route)
    {
        PanelContainer badge = new()
        {
            Name = "BranchBadge",
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
        hbox.AddThemeConstantOverride("separation", 5);
        badge.AddChildSafely(hbox);

        bool hasItem = false;

        if (route.Score.Elites > 0)
        {
            Label eliteLabel = new()
            {
                Text = $"{route.Score.Elites} Elite" + (route.Score.Elites > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            eliteLabel.AddThemeColorOverride("font_color", new Color("FF6B6B"));
            eliteLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChildSafely(eliteLabel);
            hasItem = true;
        }

        if (route.Score.RestSites > 0)
        {
            if (hasItem)
            {
                Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
                bullet.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
                bullet.AddThemeFontSizeOverride("font_size", 14);
                hbox.AddChildSafely(bullet);
            }

            Label campLabel = new()
            {
                Text = $"{route.Score.RestSites} Camp" + (route.Score.RestSites > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            campLabel.AddThemeColorOverride("font_color", new Color("51CF66"));
            campLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChildSafely(campLabel);
            hasItem = true;
        }

        if (route.Score.Shops > 0)
        {
            if (hasItem)
            {
                Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
                bullet.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
                bullet.AddThemeFontSizeOverride("font_size", 14);
                hbox.AddChildSafely(bullet);
            }

            Label shopLabel = new()
            {
                Text = $"{route.Score.Shops} Shop" + (route.Score.Shops > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            shopLabel.AddThemeColorOverride("font_color", new Color("FCC419"));
            shopLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChildSafely(shopLabel);
            hasItem = true;
        }

        if (!hasItem)
        {
            Label fightLabel = new()
            {
                Text = $"{route.Score.Monsters} Fights",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            fightLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f));
            fightLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChildSafely(fightLabel);
        }

        return badge;
    }

    private void EnsureHudBanner(NMapScreen screen)
    {
        if (_hudBanner != null && GodotObject.IsInstanceValid(_hudBanner) && _hudBanner.GetParent() == screen)
            return;

        _hudBanner?.QueueFreeSafely();

        _hudBanner = new PanelContainer
        {
            Name = "MapPathHudBanner",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.92f),
            BorderColor = new Color(0.2f, 0.75f, 1.0f, 0.85f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        _hudBanner.AddThemeStyleboxOverride("panel", style);

        HBoxContainer hbox = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        hbox.AddThemeConstantOverride("separation", 10);
        _hudBanner.AddChildSafely(hbox);

        _hudIcon = new Label
        {
            Text = "⚡",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hudIcon.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.2f, 1.0f));
        _hudIcon.AddThemeFontSizeOverride("font_size", 22);
        hbox.AddChildSafely(_hudIcon);

        Label title = new()
        {
            Text = "OPTIMAL ROUTE",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeColorOverride("font_color", new Color(0.35f, 0.85f, 1.0f, 1.0f));
        title.AddThemeFontSizeOverride("font_size", 16);
        hbox.AddChildSafely(title);

        Label divider = new()
        {
            Text = "|",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        divider.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.6f, 0.7f));
        divider.AddThemeFontSizeOverride("font_size", 16);
        hbox.AddChildSafely(divider);

        _hudStats = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hudStats.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.98f, 1.0f));
        _hudStats.AddThemeFontSizeOverride("font_size", 16);
        hbox.AddChildSafely(_hudStats);

        _hudBanner.AnchorLeft = 0.5f;
        _hudBanner.AnchorRight = 0.5f;
        _hudBanner.OffsetLeft = -320f;
        _hudBanner.OffsetRight = 320f;
        _hudBanner.OffsetTop = 22f;
        _hudBanner.OffsetBottom = 66f;
        _hudBanner.GrowHorizontal = Control.GrowDirection.Both;

        screen.AddChildSafely(_hudBanner);
    }

    private void UpdateHudBanner(NMapScreen screen, OptimalRouteSet routeSet)
    {
        if (_hudBanner == null || !GodotObject.IsInstanceValid(_hudBanner) || _hudStats == null)
            return;

        string targetTypeName = routeSet.TargetPoint.PointType switch
        {
            MapPointType.RestSite => "Campsite",
            MapPointType.Shop => "Shop",
            MapPointType.Elite => "Elite",
            MapPointType.Treasure => "Chest",
            MapPointType.Boss => "Boss",
            _ => "Target"
        };

        bool allSameScore = routeSet.Routes.All(r => r.Score == routeSet.Routes[0].Score);
        if (routeSet.Routes.Count > 1)
        {
            if (allSameScore)
            {
                OptimalRoute single = routeSet.Routes[0];
                _hudStats.Text = $"{routeSet.Routes.Count} Equal Paths Found ({routeSet.MaxHighValueCount} High-Value Rooms) • {single.Score.Elites} Elites   •   {single.Score.RestSites} Camps   •   {single.Score.Shops} Shops";
            }
            else
            {
                _hudStats.Text = $"{routeSet.Routes.Count} Equal Branches ({routeSet.MaxHighValueCount} High-Value Rooms) • See Badges for Trade-offs";
            }
        }
        else
        {
            OptimalRoute single = routeSet.Routes[0];
            string prefix = single.IsFromCurrentLocation
                ? $"via {targetTypeName}: "
                : $"(Full Act via {targetTypeName}): ";
            _hudStats.Text = $"{prefix}{single.Score.Elites} Elites   •   {single.Score.RestSites} Campsites   •   {single.Score.Shops} Shops   •   {single.Score.Monsters} Fights";
        }

        _hudBanner.Visible = true;
    }
}
