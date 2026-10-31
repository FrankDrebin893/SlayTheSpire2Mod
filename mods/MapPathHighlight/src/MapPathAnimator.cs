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
        Color AccentColor,
        string Symbol);

    private static readonly BranchPalette[] Palettes =
    [
        new BranchPalette(
            Name: "Cyan",
            BaseDotColor: new Color(0.18f, 0.72f, 1.0f, 0.85f),
            SurgeDotColor: new Color(1.0f, 1.0f, 1.0f, 1.0f),
            GlowColor: new Color(0.18f, 0.75f, 1.0f, 0.55f),
            CoreColor: new Color(0.92f, 0.98f, 1.0f, 0.95f),
            AccentColor: new Color("33BFFF"),
            Symbol: "🔵"
        ),
        new BranchPalette(
            Name: "Amber",
            BaseDotColor: new Color(1.0f, 0.78f, 0.18f, 0.85f),
            SurgeDotColor: new Color(1.0f, 0.98f, 0.85f, 1.0f),
            GlowColor: new Color(1.0f, 0.75f, 0.15f, 0.55f),
            CoreColor: new Color(1.0f, 0.98f, 0.88f, 0.95f),
            AccentColor: new Color("FFD166"),
            Symbol: "🟡"
        ),
        new BranchPalette(
            Name: "Coral",
            BaseDotColor: new Color(1.0f, 0.35f, 0.55f, 0.85f),
            SurgeDotColor: new Color(1.0f, 0.90f, 0.95f, 1.0f),
            GlowColor: new Color(1.0f, 0.35f, 0.55f, 0.55f),
            CoreColor: new Color(1.0f, 0.92f, 0.95f, 0.95f),
            AccentColor: new Color("FF6B8B"),
            Symbol: "🔴"
        )
    ];

    // Stock Godot visual nodes
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
                if (rIdx < _glowLines.Count)
                {
                    _glowLines[rIdx].Points = pts;
                    _glowLines[rIdx].DefaultColor = pal.GlowColor;
                }
                if (rIdx < _coreLines.Count)
                {
                    _coreLines[rIdx].Points = pts;
                    _coreLines[rIdx].DefaultColor = pal.CoreColor;
                }
            }
        }

        // Color dots: shared trunk dots get bright cyan-white, branch dots get their distinct route hue
        foreach (TextureRect dot in _activeDots)
        {
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
            // Concept 4: Unified Multi-Branch Decision Card
            container = CreateUnifiedBranchCard(routeSet, distinctScoreRoutes);
        }
        else
        {
            // Single compact badge
            OptimalRoute primaryRoute = distinctScoreRoutes[0];
            int tiedCount = routeSet.Routes.Count;
            container = CreateSingleBadge(primaryRoute, tiedCount);
        }

        pointsContainer.AddChildSafely(container);
        _branchBadges.Add(container);

        container.ResetSize();
        Vector2 minSize = container.GetCombinedMinimumSize();
        float cardWidth = Math.Max(minSize.X, 150f);
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

    private PanelContainer CreateUnifiedBranchCard(OptimalRouteSet routeSet, List<OptimalRoute> distinctRoutes)
    {
        PanelContainer card = new()
        {
            Name = "UnifiedBranchCard",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.06f, 0.08f, 0.12f, 0.95f),
            BorderColor = new Color(0.20f, 0.75f, 1.0f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 7,
            ContentMarginBottom = 7
        };
        card.AddThemeStyleboxOverride("panel", style);

        VBoxContainer vbox = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vbox.AddThemeConstantOverride("separation", 5);
        card.AddChildSafely(vbox);

        // Header with fork status
        HBoxContainer header = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        header.AddThemeConstantOverride("separation", 6);
        vbox.AddChildSafely(header);

        Label icon = new()
        {
            Text = "⚡",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        icon.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.2f));
        icon.AddThemeFontSizeOverride("font_size", 13);
        header.AddChildSafely(icon);

        Label title = new()
        {
            Text = $"{routeSet.MaxHighValueCount} High-Value Rooms",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        title.AddThemeColorOverride("font_color", new Color("4DD0E1"));
        title.AddThemeFontSizeOverride("font_size", 13);
        header.AddChildSafely(title);

        if (!string.IsNullOrEmpty(routeSet.ForkDescription))
        {
            Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
            bullet.AddThemeColorOverride("font_color", new Color(0.5f, 0.6f, 0.7f));
            bullet.AddThemeFontSizeOverride("font_size", 12);
            header.AddChildSafely(bullet);

            Label fork = new()
            {
                Text = routeSet.ForkDescription,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            fork.AddThemeColorOverride("font_color", new Color("D0D5E0"));
            fork.AddThemeFontSizeOverride("font_size", 12);
            header.AddChildSafely(fork);
        }

        // Divider
        ColorRect divider = new()
        {
            CustomMinimumSize = new Vector2(0, 1),
            Color = new Color(0.2f, 0.35f, 0.5f, 0.45f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        vbox.AddChildSafely(divider);

        // Branch trade-off rows
        for (int i = 0; i < distinctRoutes.Count; i++)
        {
            OptimalRoute r = distinctRoutes[i];
            BranchPalette pal = Palettes[i % Palettes.Length];

            HBoxContainer row = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 5);
            vbox.AddChildSafely(row);

            Label symbol = new()
            {
                Text = pal.Symbol,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            symbol.AddThemeFontSizeOverride("font_size", 11);
            row.AddChildSafely(symbol);

            Label name = new()
            {
                Text = $"{pal.Name}:",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            name.AddThemeColorOverride("font_color", pal.AccentColor);
            name.AddThemeFontSizeOverride("font_size", 13);
            row.AddChildSafely(name);

            AddScoreLabelsToContainer(row, r.Score);
        }

        return card;
    }

    private PanelContainer CreateSingleBadge(OptimalRoute route, int tiedCount)
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
        hbox.AddThemeConstantOverride("separation", 5);
        badge.AddChildSafely(hbox);

        AddScoreLabelsToContainer(hbox, route.Score);

        if (tiedCount > 1)
        {
            Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
            bullet.AddThemeColorOverride("font_color", new Color(0.5f, 0.6f, 0.7f));
            bullet.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(bullet);

            Label tiedLabel = new()
            {
                Text = $"{tiedCount} Equal Paths",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            tiedLabel.AddThemeColorOverride("font_color", new Color("4DD0E1"));
            tiedLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(tiedLabel);
        }

        return badge;
    }

    private static void AddScoreLabelsToContainer(HBoxContainer hbox, RouteScore score)
    {
        bool hasItem = false;

        if (score.Elites > 0)
        {
            Label eliteLabel = new()
            {
                Text = $"{score.Elites} Elite" + (score.Elites > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            eliteLabel.AddThemeColorOverride("font_color", new Color("FF6B6B"));
            eliteLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(eliteLabel);
            hasItem = true;
        }

        if (score.RestSites > 0)
        {
            if (hasItem)
            {
                Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
                bullet.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
                bullet.AddThemeFontSizeOverride("font_size", 13);
                hbox.AddChildSafely(bullet);
            }

            Label campLabel = new()
            {
                Text = $"{score.RestSites} Camp" + (score.RestSites > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            campLabel.AddThemeColorOverride("font_color", new Color("51CF66"));
            campLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(campLabel);
            hasItem = true;
        }

        if (score.Shops > 0)
        {
            if (hasItem)
            {
                Label bullet = new() { Text = "•", MouseFilter = Control.MouseFilterEnum.Ignore };
                bullet.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
                bullet.AddThemeFontSizeOverride("font_size", 13);
                hbox.AddChildSafely(bullet);
            }

            Label shopLabel = new()
            {
                Text = $"{score.Shops} Shop" + (score.Shops > 1 ? "s" : ""),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            shopLabel.AddThemeColorOverride("font_color", new Color("FCC419"));
            shopLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(shopLabel);
            hasItem = true;
        }

        if (!hasItem)
        {
            Label fightLabel = new()
            {
                Text = $"{score.Monsters} Fights",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            fightLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f));
            fightLabel.AddThemeFontSizeOverride("font_size", 13);
            hbox.AddChildSafely(fightLabel);
        }
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

        if (routeSet.Routes.Count > 1)
        {
            if (routeSet.HasDistinctScores)
            {
                string forkText = !string.IsNullOrEmpty(routeSet.ForkDescription) ? $" • {routeSet.ForkDescription}" : "";
                _hudStats.Text = $"{routeSet.Routes.Count} Equal Branches ({routeSet.MaxHighValueCount} High-Value Rooms){forkText} • See Card for Trade-offs";
            }
            else
            {
                OptimalRoute single = routeSet.Routes[0];
                _hudStats.Text = $"{routeSet.Routes.Count} Equal Paths Found ({routeSet.MaxHighValueCount} High-Value Rooms) • {single.Score.Elites} Elites   •   {single.Score.RestSites} Camps   •   {single.Score.Shops} Shops";
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
