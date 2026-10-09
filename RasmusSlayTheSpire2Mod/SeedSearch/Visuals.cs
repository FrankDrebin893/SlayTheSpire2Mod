using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// How one game thing is shown: its name, the game's own icon for it, a frame colour, and the text
// and larger picture for the info panel, both only fetched when it is hovered.
public sealed record Visual(string Name, Texture2D? Icon, Color Frame, Func<string>? Describe = null, Func<Texture2D?>? Big = null);

// The look of the seed search screen: its theme, the game art for each kind of id in a GameData,
// and the tiles built from them. Stock Godot nodes only.
public static class Visuals
{
    public static readonly Color Background = new(0.07f, 0.08f, 0.11f, 0.985f);
    public static readonly Color PanelColor = new(0.13f, 0.15f, 0.2f);
    public static readonly Color InsetColor = new(0.09f, 0.1f, 0.14f);
    public static readonly Color Accent = new(0.95f, 0.78f, 0.35f);
    public static readonly Color Muted = new(0.68f, 0.71f, 0.78f);
    public static readonly Color Plain = new(0.25f, 0.28f, 0.35f);
    public static readonly Color Cursed = new(0.85f, 0.3f, 0.3f);

    private static readonly Color TileFill = new(0.11f, 0.13f, 0.18f);
    private static readonly Color TileHover = new(0.17f, 0.2f, 0.27f);
    private static readonly Color CommonColor = new(0.55f, 0.58f, 0.64f);
    private static readonly Color UncommonColor = new(0.35f, 0.65f, 0.9f);
    private static readonly Color CurseColor = new(0.62f, 0.38f, 0.8f);

    public static readonly (int Type, RoomType Room, string Name, Color Color)[] MapTypes =
    [
        ((int)MapPointType.Elite, RoomType.Elite, "elites", new Color(0.9f, 0.3f, 0.3f)),
        ((int)MapPointType.RestSite, RoomType.RestSite, "rests", new Color(0.95f, 0.6f, 0.25f)),
        ((int)MapPointType.Shop, RoomType.Shop, "shops", new Color(0.95f, 0.85f, 0.3f)),
        ((int)MapPointType.Unknown, RoomType.Event, "unknowns", new Color(0.45f, 0.75f, 0.95f)),
        ((int)MapPointType.Monster, RoomType.Monster, "monsters", new Color(0.62f, 0.65f, 0.7f)),
        ((int)MapPointType.Treasure, RoomType.Treasure, "treasures", new Color(0.5f, 0.85f, 0.5f))
    ];

    private static readonly Dictionary<Color, (StyleBoxFlat Normal, StyleBoxFlat Hover, StyleBoxFlat Pressed)> TileStyles = new();

    // ---- theme and plain building blocks ----

    public static Theme MakeTheme(Font? font)
    {
        Theme theme = new() { DefaultFontSize = 20 };
        // Borrow the game's font so the screen does not look like a debug overlay.
        if (font != null)
            theme.DefaultFont = font;

        // Teal buttons like the game's own Randomize button, and visible input boxes.
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(new Color(0.15f, 0.36f, 0.4f), new Color(0.09f, 0.22f, 0.25f)));
            theme.SetStylebox("hover", type, Box(new Color(0.2f, 0.47f, 0.52f), Accent));
            theme.SetStylebox("pressed", type, Box(new Color(0.11f, 0.27f, 0.3f), Accent));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetStylebox("disabled", type, Box(new Color(0.17f, 0.19f, 0.23f), new Color(0.12f, 0.13f, 0.16f)));
        }

        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.05f, 0.06f, 0.08f), Plain));
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0.05f, 0.06f, 0.08f), Accent));
        theme.SetStylebox("panel", "ItemList", Box(InsetColor, InsetColor));
        return theme;
    }

    public static StyleBoxFlat Box(Color fill, Color border, int margin = 12)
    {
        StyleBoxFlat box = new() { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll(6);
        box.ContentMarginLeft = box.ContentMarginRight = margin;
        box.ContentMarginTop = box.ContentMarginBottom = Math.Min(margin, 5);
        return box;
    }

    public static PanelContainer Panel(Color color, int margin = 14)
    {
        StyleBoxFlat style = new() { BgColor = color };
        style.SetCornerRadiusAll(8);
        style.SetContentMarginAll(margin);
        PanelContainer panel = new();
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    public static Label SectionTitle(string text, int size = 24)
    {
        Label label = new() { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Accent);
        return label;
    }

    public static Label MutedLabel(string text, int size = 16)
    {
        Label label = new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Muted);
        return label;
    }

    public static SpinBox Spin(int min, int max, int value) => new() { MinValue = min, MaxValue = max, Value = value, Step = 1 };

    public static HFlowContainer Flow(int separation = 6)
    {
        HFlowContainer flow = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", separation);
        flow.AddThemeConstantOverride("v_separation", separation);
        return flow;
    }

    public static void Clear(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ---- tiles ----

    // A square of art with the name underneath. Clickable; hovering reports the visual.
    public static Button Tile(Visual v, Action<Visual> hover, int width = 104, int height = 96)
    {
        Button tile = IconButton(v, hover);
        // The name is already under the art.
        tile.TooltipText = "";
        tile.Text = v.Name;
        tile.CustomMinimumSize = new Vector2(width, height);
        tile.VerticalIconAlignment = VerticalAlignment.Top;
        tile.IconAlignment = HorizontalAlignment.Center;
        tile.AddThemeFontSizeOverride("font_size", 13);
        return tile;
    }

    // Art only, for dense strips. Without art it falls back to the name.
    public static Button Icon(Visual v, Action<Visual> hover, int size = 44)
    {
        Button icon = IconButton(v, hover);
        icon.CustomMinimumSize = new Vector2(v.Icon == null ? size * 2 : size, size);
        icon.IconAlignment = HorizontalAlignment.Center;
        if (v.Icon == null)
        {
            icon.Text = v.Name;
            icon.AddThemeFontSizeOverride("font_size", 13);
        }

        return icon;
    }

    // A small icon followed by the name, for lists where the names are what matters.
    public static Button Pill(Visual v, Action<Visual> hover, string? text = null)
    {
        Button pill = IconButton(v, hover);
        pill.ExpandIcon = false;
        pill.ClipText = false;
        pill.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        pill.Text = text ?? v.Name;
        pill.AddThemeConstantOverride("icon_max_width", 22);
        pill.AddThemeFontSizeOverride("font_size", 16);
        return pill;
    }

    private static Button IconButton(Visual v, Action<Visual> hover)
    {
        Button button = new()
        {
            Icon = v.Icon,
            ExpandIcon = true,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = v.Name,
            FocusMode = Control.FocusModeEnum.None
        };
        (StyleBoxFlat normal, StyleBoxFlat hovered, StyleBoxFlat pressed) = StylesFor(v.Frame);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hovered);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        button.MouseEntered += () => hover(v);
        return button;
    }

    private static (StyleBoxFlat, StyleBoxFlat, StyleBoxFlat) StylesFor(Color frame)
    {
        if (!TileStyles.TryGetValue(frame, out var styles))
        {
            StyleBoxFlat pressed = Box(new Color(0.24f, 0.21f, 0.12f), Accent, 4);
            pressed.SetBorderWidthAll(3);
            styles = (Box(TileFill, frame, 4), Box(TileHover, frame.Lightened(0.3f), 4), pressed);
            TileStyles[frame] = styles;
        }

        return styles;
    }

    // ---- game art ----

    public static Texture2D? Load(string? path)
    {
        try
        {
            return path != null && ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Game text without its BBCode, which a stock label cannot render.
    public static string Text(Func<string> read)
    {
        try
        {
            string text = Regex.Replace(read(), @"\[img[^\]]*\].*?\[/img\]", "●");
            return Regex.Replace(text, @"\[/?[A-Za-z_#][^\[\]]*\]", "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static Visual Relic(GameData d, int id, bool cursed = false)
    {
        if (id < 0)
            return new Visual("?", null, Plain);
        RelicModel relic = d.Relics[id];
        Color frame = cursed
            ? Cursed
            : relic.Rarity switch
            {
                RelicRarity.Uncommon => UncommonColor,
                RelicRarity.Rare or RelicRarity.Ancient => Accent,
                RelicRarity.Shop => new Color(0.5f, 0.8f, 0.55f),
                _ => CommonColor
            };
        string entry = relic.Id.Entry.ToLowerInvariant();
        return new Visual(d.RelicName(id), Load(relic.PackedIconPath), frame,
            () => Text(() => relic.DynamicDescription.GetFormattedText()),
            () => Load(ImageHelper.GetImagePath($"relics/{entry}.png")) ?? Load(ImageHelper.GetImagePath($"relics/beta/{entry}.png")));
    }

    public static Visual Card(GameData d, int id)
    {
        CardModel card = d.Cards[id];
        Color frame = card.Rarity switch
        {
            CardRarity.Uncommon => UncommonColor,
            CardRarity.Rare => Accent,
            CardRarity.Curse => CurseColor,
            _ => CommonColor
        };
        Texture2D? portrait = Load(Safe(() => card.PortraitPath)) ?? Load(Safe(() => card.BetaPortraitPath)) ?? Load(CardModel.MissingPortraitPath);
        return new Visual(d.CardName(id), portrait, frame,
            () => $"{card.Rarity} {card.Type}\n{Text(() => card.GetDescriptionForPile(PileType.None))}");
    }

    public static Visual Potion(GameData d, int id)
    {
        if (id < 0)
            return new Visual("No potion", null, Plain);
        PotionModel potion = d.Potions[id];
        return new Visual(d.PotionName(id), Load(potion.ImagePath), UncommonColor,
            () => Text(() => potion.DynamicDescription.GetFormattedText()),
            () => Load(potion.LargeImagePath));
    }

    public static Visual Ancient(GameData d, int id)
    {
        if (id < 0)
            return new Visual("No ancient", null, Plain);
        return new Visual(d.AncientName(id), RoomIcon(MapPointType.Ancient, RoomType.Event, d.Ancients[id].Id), Accent, () => "Ancient");
    }

    // Only bosses have art of their own; fights and elites share the icon of their room type.
    public static Visual Encounter(GameData d, int id, RoomType room)
    {
        if (id < 0)
            return new Visual("None", null, Plain);
        EncounterModel encounter = d.Encounters[id];
        MapPointType point = room switch
        {
            RoomType.Boss => MapPointType.Boss,
            RoomType.Elite => MapPointType.Elite,
            _ => MapPointType.Monster
        };
        Texture2D? icon = RoomIcon(point, room, encounter.Id) ?? RoomIcon(point, room == RoomType.Boss ? RoomType.Elite : room, null);
        Color frame = room switch
        {
            RoomType.Boss => Accent,
            RoomType.Elite => Cursed,
            _ => CommonColor
        };
        return new Visual(d.EncounterName(id), icon, frame,
            () => $"{room}\n{Text(() => string.Join(", ", encounter.AllPossibleMonsters.Select(m => m.Title.GetFormattedText()).Distinct()))}");
    }

    public static Visual Event(GameData d, int id)
    {
        EventModel ev = d.Events[id];
        return new Visual(d.EventName(id), RoomIcon(MapPointType.Unknown, RoomType.Event, null), Plain,
            () => Text(() => ev.InitialDescription.GetFormattedText()),
            () => Load(ImageHelper.GetImagePath($"events/{ev.Id.Entry.ToLowerInvariant()}.png")));
    }

    public static Visual MapType(int index, string? name = null)
    {
        (int type, RoomType room, string typeName, Color color) = MapTypes[index];
        return new Visual(name ?? typeName, RoomIcon((MapPointType)type, room, null), color);
    }

    public static Texture2D? MapIcon(int pointType)
    {
        int index = Array.FindIndex(MapTypes, t => t.Type == pointType);
        return index < 0 ? null : RoomIcon((MapPointType)pointType, MapTypes[index].Room, null);
    }

    public static Visual Named(string name, Texture2D? icon = null) => new(name, icon, Plain);

    public static Texture2D? CharacterIcon(CharacterModel character) =>
        Load(ImageHelper.GetImagePath($"ui/top_panel/character_icon_{character.Id.Entry.ToLowerInvariant()}.png"));

    private static Texture2D? RoomIcon(MapPointType point, RoomType room, ModelId? id) => Load(ImageHelper.GetRoomIconPath(point, room, id));
}

// A row of buttons of which exactly one is down.
public sealed class Choice
{
    private readonly ButtonGroup _group = new();
    private readonly Dictionary<int, Button> _buttons = new();

    public HBoxContainer Root { get; } = new();

    public int Value { get; private set; }

    public event Action? Changed;

    public Choice(IEnumerable<(int Id, string Text)> options)
    {
        Root.AddThemeConstantOverride("separation", 4);
        foreach ((int id, string text) in options)
        {
            Button button = new() { Text = text, ToggleMode = true, ButtonGroup = _group, FocusMode = Control.FocusModeEnum.None };
            if (Root.GetChildCount() == 0)
            {
                Value = id;
                button.ButtonPressed = true;
            }

            button.Toggled += on =>
            {
                if (on && Value != id)
                {
                    Value = id;
                    Changed?.Invoke();
                }
            };
            _buttons[id] = button;
            Root.AddChild(button);
        }
    }

    public void Select(int id) => _buttons[id].ButtonPressed = true;
}

// The strip that explains whatever tile the mouse is over: larger art, name and the game's text.
public sealed class InfoPanel
{
    private readonly TextureRect _art = new()
    {
        CustomMinimumSize = new Vector2(120, 120),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
    };
    private readonly Label _title = Visuals.SectionTitle("", 22);
    private readonly Label _text = Visuals.MutedLabel("Hover over an icon to see what it is.");
    private Visual? _shown;

    public PanelContainer Root { get; } = Visuals.Panel(Visuals.InsetColor, 10);

    public InfoPanel()
    {
        Root.CustomMinimumSize = new Vector2(0, 144);
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 12);
        Root.AddChild(row);
        row.AddChild(_art);

        VBoxContainer column = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(column);
        _title.ClipText = true;
        column.AddChild(_title);
        ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        _text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_text);
    }

    public void Show(Visual v)
    {
        if (ReferenceEquals(v, _shown))
            return;

        _shown = v;
        _title.Text = v.Name;
        _text.Text = v.Describe?.Invoke() ?? "";
        _art.Texture = v.Big?.Invoke() ?? v.Icon;
    }
}
