using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Timer = Godot.Timer;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// The full-screen seed search opened from the Custom Run screen. Built from stock Godot nodes
// only: node subclasses defined in a mod assembly are not registered with the engine (so it
// cannot be an NSubmenu), and this class is a plain object that lives as long as the signal
// handlers on its nodes do. It covers the Custom Run screen and is freed when closed.
public sealed class SeedSearchScreen
{
    private const int MaxResults = 100;
    private const int ListPreview = 5;

    private static readonly Color Background = new(0.07f, 0.08f, 0.11f, 0.985f);
    private static readonly Color PanelColor = new(0.13f, 0.15f, 0.2f);
    private static readonly Color InsetColor = new(0.09f, 0.1f, 0.14f);
    private static readonly Color Accent = new(0.95f, 0.78f, 0.35f);
    private static readonly Color Muted = new(0.68f, 0.71f, 0.78f);

    private static readonly (int Type, string Name, Color Color)[] MapTypes =
    [
        ((int)MapPointType.Elite, "elites", new Color(0.9f, 0.3f, 0.3f)),
        ((int)MapPointType.RestSite, "rests", new Color(0.95f, 0.6f, 0.25f)),
        ((int)MapPointType.Shop, "shops", new Color(0.95f, 0.85f, 0.3f)),
        ((int)MapPointType.Unknown, "unknowns", new Color(0.45f, 0.75f, 0.95f)),
        ((int)MapPointType.Monster, "monsters", new Color(0.62f, 0.65f, 0.7f)),
        ((int)MapPointType.Treasure, "treasures", new Color(0.5f, 0.85f, 0.5f))
    ];

    private static readonly FieldInfo SeedInputField = AccessTools.Field(typeof(NCustomRunScreen), "_seedInput");
    private static readonly string[] GameButtonFields = ["_backButton", "_confirmButton", "_randomizeButton"];

    // Filters survive closing the screen for as long as they refer to the same game data.
    private static readonly List<Cond> Conds = [];
    private static GameData? _condsData;

    private readonly NCustomRunScreen _screen;
    private readonly LineEdit _gameSeedInput;
    private readonly SeedEngine _engine;
    private readonly List<NClickableControl> _disabledGameButtons = [];

    // Its own canvas layer, so it sits above everything on the Custom Run screen whatever the
    // order and z-index of that screen's nodes.
    private readonly CanvasLayer _layer = new() { Layer = 20 };
    private readonly Control _root = new();
    private readonly VBoxContainer _condList = new();
    private readonly Button _searchButton = new();
    private readonly Label _status = new();
    private readonly ItemList _results = new();
    private readonly LineEdit _seedBox = new();
    private readonly RichTextLabel _detail = new();
    private readonly OptionButton _mapAct = new();
    private readonly Control _mapCanvas = new();
    private readonly Button _useButton = new();
    private readonly Timer _pollTimer = new();

    private ISeedSearch? _search;
    private SimResult? _shown;

    private GameData Data => _engine.Data;

    private SeedSearchScreen(NCustomRunScreen screen, SeedEngine engine)
    {
        _screen = screen;
        _engine = engine;
        _gameSeedInput = (LineEdit)SeedInputField.GetValue(screen)!;
    }

    public static bool CanOpen(NCustomRunScreen screen) =>
        screen.Lobby is { NetService: NetSingleplayerGameService } lobby && lobby.LocalPlayer.character is not (null or RandomCharacter);

    public static void Open(NCustomRunScreen screen)
    {
        StartRunLobby lobby = screen.Lobby;
        SeedEngine engine = SeedEngine.For(lobby.LocalPlayer.character, lobby.Ascension, lobby.Act1);
        if (!ReferenceEquals(_condsData, engine.Data))
        {
            Conds.Clear();
            _condsData = engine.Data;
        }

        new SeedSearchScreen(screen, engine).Build();
    }

    // ---- layout ----

    private void Build()
    {
        _root.Name = "SeedSearchScreen";
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.Theme = MakeTheme();
        _root.TreeExiting += StopSearch;

        ColorRect background = new() { Color = Background, MouseFilter = Control.MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(background);

        MarginContainer margin = new();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 40);
        foreach (string side in new[] { "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 30);
        _root.AddChild(margin);

        VBoxContainer page = new();
        page.AddThemeConstantOverride("separation", 16);
        margin.AddChild(page);
        page.AddChild(BuildHeader());

        HBoxContainer body = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        page.AddChild(body);
        body.AddChild(BuildFilterPanel());
        body.AddChild(BuildResultsPanel());

        _pollTimer.WaitTime = 0.1;
        _pollTimer.Timeout += PollSearch;
        _root.AddChild(_pollTimer);

        _layer.AddChild(_root);
        _screen.AddChild(_layer);
        DisableGameButtons();
        RefreshConds();
        ShowSeed(_gameSeedInput.Text);
    }

    private Theme MakeTheme()
    {
        Theme theme = new() { DefaultFontSize = 20 };
        // Borrow the game's font so the screen does not look like a debug overlay.
        if (_screen.GetNodeOrNull<Control>("%SeedLabel")?.GetThemeFont("font") is { } font)
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

        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.05f, 0.06f, 0.08f), new Color(0.25f, 0.28f, 0.35f)));
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0.05f, 0.06f, 0.08f), Accent));
        theme.SetStylebox("panel", "ItemList", Box(InsetColor, InsetColor));
        return theme;
    }

    private Control BuildHeader()
    {
        HBoxContainer header = new();
        header.AddThemeConstantOverride("separation", 24);

        Label title = new() { Text = "Seed search" };
        title.AddThemeFontSizeOverride("font_size", 38);
        title.AddThemeColorOverride("font_color", Accent);
        header.AddChild(title);

        string act1 = Data.SlotActs[0].Length == 1 ? Data.ActName(Data.SlotActs[0][0]) : "random";
        VBoxContainer context = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        context.AddChild(new Label { Text = $"{Data.Character.Title.GetFormattedText()}  ·  Ascension {Data.Ascension}  ·  Act 1: {act1}" });
        context.AddChild(MutedLabel($"{_engine.Status}. Character, ascension and act 1 come from the Custom Run screen."));
        header.AddChild(context);

        Button back = new() { Text = "Back", CustomMinimumSize = new Vector2(140, 48), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        back.Shortcut = new Shortcut { Events = new Godot.Collections.Array { new InputEventKey { Keycode = Key.Escape }, new InputEventAction { Action = "ui_cancel" } } };
        back.Pressed += Close;
        header.AddChild(back);
        return header;
    }

    private Control BuildFilterPanel()
    {
        PanelContainer panel = Panel(PanelColor);
        panel.CustomMinimumSize = new Vector2(720, 0);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        column.AddChild(SectionTitle("Filters"));
        column.AddChild(MutedLabel("A seed must match every filter in this list."));
        PanelContainer active = Panel(InsetColor);
        active.AddChild(_condList);
        column.AddChild(active);

        ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        VBoxContainer adders = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        adders.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(adders);

        if (Data.NeowRelics.Length > 0)
            AddNeowSection(adders);
        AddActSection(adders);
        AddEventSection(adders);
        AddEncounterSection(adders);
        AddRelicSection(adders);
        AddMapSection(adders);
        AddRewardSection(adders);
        return panel;
    }

    private Control BuildResultsPanel()
    {
        PanelContainer panel = Panel(PanelColor);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        HBoxContainer bar = new();
        bar.AddThemeConstantOverride("separation", 14);
        column.AddChild(bar);
        _searchButton.Text = "Search";
        _searchButton.CustomMinimumSize = new Vector2(160, 48);
        _searchButton.Pressed += ToggleSearch;
        bar.AddChild(_searchButton);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true;
        _status.Text = "Add filters on the left, then search.";
        bar.AddChild(_status);
        Button copy = new() { Text = "Copy seed" };
        copy.Pressed += () =>
        {
            if (_shown != null)
                DisplayServer.ClipboardSet(_shown.Seed);
        };
        bar.AddChild(copy);
        Button site = new() { Text = "Open on SearchTheSpire" };
        site.Pressed += OpenOnSearchTheSpire;
        bar.AddChild(site);


        HBoxContainer split = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        column.AddChild(split);

        VBoxContainer left = new() { CustomMinimumSize = new Vector2(200, 0) };
        split.AddChild(left);
        left.AddChild(MutedLabel("Matching seeds"));
        _results.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _results.ItemSelected += index => ShowSeed(_results.GetItemText((int)index), fromList: true);
        left.AddChild(_results);

        VBoxContainer right = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);

        HBoxContainer seedRow = new();
        seedRow.AddThemeConstantOverride("separation", 10);
        right.AddChild(seedRow);
        seedRow.AddChild(new Label { Text = "Preview seed" });
        _seedBox.PlaceholderText = "type or pick a seed";
        _seedBox.CustomMinimumSize = new Vector2(240, 0);
        _seedBox.TextChanged += text => ShowSeed(text, fromBox: true);
        seedRow.AddChild(_seedBox);
        _useButton.Text = "Use this seed";
        _useButton.Pressed += UseSeed;
        seedRow.AddChild(_useButton);
        _detail.BbcodeEnabled = true;
        _detail.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _detail.SizeFlagsStretchRatio = 1.25f;
        _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        right.AddChild(_detail);

        HBoxContainer mapBar = new();
        mapBar.AddThemeConstantOverride("separation", 10);
        right.AddChild(mapBar);
        mapBar.AddChild(new Label { Text = "Map of" });
        for (int slot = 0; slot < Data.SlotActs.Length; slot++)
            _mapAct.AddItem($"Act {slot + 1}", slot);
        _mapAct.ItemSelected += _ => _mapCanvas.QueueRedraw();
        mapBar.AddChild(_mapAct);
        RichTextLabel legend = new() { BbcodeEnabled = true, FitContent = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ScrollActive = false };
        legend.Text = string.Join("   ", MapTypes.Select(t => $"[color=#{t.Color.ToHtml(false)}]●[/color] {t.Name}"));
        mapBar.AddChild(legend);

        PanelContainer mapPanel = Panel(InsetColor);
        mapPanel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mapCanvas.Draw += DrawMap;
        mapPanel.AddChild(_mapCanvas);
        right.AddChild(mapPanel);
        return panel;
    }

    // ---- filter sections ----

    private void AddNeowSection(VBoxContainer parent)
    {
        Picker relic = new(Data.NeowRelics.Select(r => (r, Data.RelicName(r))));
        parent.AddChild(Section("Neow", "One of the three offers. Cursed offers are in the list too.",
            Row([relic.Root], () => relic.Selected is { } r ? new NeowOfferCond(r) : null)));
    }

    private void AddActSection(VBoxContainer parent)
    {
        List<Control> rows = [];

        // Which act: only worth showing when some slot is actually rolled.
        if (Data.SlotActs.Any(a => a.Length > 1))
        {
            OptionButton slot = SlotPicker(s => Data.SlotActs[s].Length > 1);
            Picker act = new([]);
            Bind(slot, s => act.SetItems(Data.SlotActs[s].Select(a => (a, Data.ActName(a)))));
            rows.Add(Row([new Label { Text = "Act" }, slot, new Label { Text = "is" }, act.Root],
                () => act.Selected is { } a ? new ActCond(slot.GetSelectedId(), a) : null));
        }

        OptionButton bossSlot = SlotPicker(_ => true);
        if (Data.DoubleBoss)
            bossSlot.AddItem("Second final boss", Data.SlotActs.Length);
        Picker boss = new([]);
        Bind(bossSlot, s => boss.SetItems(SlotItems(Math.Min(s, Data.SlotActs.Length - 1), a => a.Boss, Data.EncounterName)));
        rows.Add(Row([new Label { Text = "Boss of" }, bossSlot, new Label { Text = "is" }, boss.Root], () =>
        {
            if (boss.Selected is not { } e)
                return null;
            int s = bossSlot.GetSelectedId();
            return s < Data.SlotActs.Length ? new BossCond(s, e) : new SecondBossCond(e);
        }));

        OptionButton ancientSlot = SlotPicker(s => s > 0);
        Picker ancient = new([]);
        Bind(ancientSlot, s => ancient.SetItems(SlotItems(s, a => a.Ancients, Data.AncientName).Concat(Data.SharedAncients.Select(a => (a, Data.AncientName(a))))));
        if (ancientSlot.ItemCount > 0)
        {
            rows.Add(Row([new Label { Text = "Ancient of" }, ancientSlot, new Label { Text = "is" }, ancient.Root],
                () => ancient.Selected is { } a ? new AncientCond(ancientSlot.GetSelectedId(), a) : null));
        }

        parent.AddChild(Section("Acts, bosses and ancients", null, rows.ToArray()));
    }

    private void AddEventSection(VBoxContainer parent)
    {
        OptionButton slot = SlotPicker(_ => true);
        Picker ev = new([]);
        Bind(slot, s => ev.SetItems(SlotItems(s, a => a.Events, Data.EventName)));
        SpinBox within = Spin(1, 20, 3);
        parent.AddChild(Section("Events", "Events are queued per act; each unknown room that rolls an event takes the next one.",
            Row([slot, ev.Root, new Label { Text = "within the first" }, within],
                () => ev.Selected is { } e ? new EventCond(slot.GetSelectedId(), e, (int)within.Value) : null)));
    }

    private void AddEncounterSection(VBoxContainer parent)
    {
        OptionButton slot = SlotPicker(_ => true);
        OptionButton kind = new();
        kind.AddItem("fight", 0);
        kind.AddItem("elite", 1);
        Picker enc = new([]);
        void Refill() => enc.SetItems(SlotItems(slot.GetSelectedId(), a => kind.GetSelectedId() == 1 ? a.Elite : a.Weak.Concat(a.Regular), Data.EncounterName));
        slot.ItemSelected += _ => Refill();
        kind.ItemSelected += _ => Refill();
        Refill();
        SpinBox within = Spin(1, 15, 1);
        parent.AddChild(Section("Fights", "Fights and elites are queued per act in the order you will meet them.",
            Row([slot, kind, enc.Root, new Label { Text = "within the first" }, within],
                () => enc.Selected is { } e ? new EncounterCond(slot.GetSelectedId(), kind.GetSelectedId() == 1, e, (int)within.Value) : null)));
    }

    private void AddRelicSection(VBoxContainer parent)
    {
        OptionButton bag = new();
        bag.AddItem("your bag", 0);
        bag.AddItem("shared bag", 1);
        Picker relic = new([]);
        Bind(bag, b => relic.SetItems((b == 1 ? Data.SharedBag : Data.PlayerBag).Select(r => (r, $"{Data.RelicName(r)} ({Data.Relics[r].Rarity})"))));
        SpinBox within = Spin(1, 30, 3);
        parent.AddChild(Section("Relic order", "Relic rewards are dealt from a shuffled bag per rarity.",
            Row([bag, relic.Root, new Label { Text = "within the first" }, within],
                () => relic.Selected is { } r ? new RelicCond(bag.GetSelectedId() == 1, r, (int)within.Value) : null)));
    }

    private void AddMapSection(VBoxContainer parent)
    {
        OptionButton slot = SlotPicker(_ => true);
        OptionButton type = new();
        for (int i = 0; i < MapTypes.Length; i++)
            type.AddItem(MapTypes[i].Name, i);
        SpinBox min = Spin(0, 40, 0);
        SpinBox max = Spin(0, 40, 4);
        parent.AddChild(Section("Map", "Counts over the whole act map. Map filters are the slowest to search.",
            Row([slot, type, new Label { Text = "from" }, min, new Label { Text = "to" }, max], () =>
            {
                (int id, string name, _) = MapTypes[type.GetSelectedId()];
                int lo = (int)Math.Min(min.Value, max.Value), hi = (int)Math.Max(min.Value, max.Value);
                return new MapCountCond(slot.GetSelectedId(), id, name, lo, hi);
            })));
    }

    private void AddRewardSection(VBoxContainer parent)
    {
        Picker card = new(Data.RewardCards.Select(c => (c, $"{Data.CardName(c)} ({Data.Cards[c].Rarity})")));
        OptionButton potion = new();
        potion.AddItem("drops a potion", 1);
        potion.AddItem("drops no potion", 0);
        parent.AddChild(Section("First fight reward", "Holds if the first room is a normal fight and your Neow pick gave no cards, relics or potions.",
            Row([new Label { Text = "Card reward has" }, card.Root], () => card.Selected is { } c ? new RewardCardCond(c) : null),
            Row([new Label { Text = "First fight" }, potion], () => new RewardPotionCond(potion.GetSelectedId() == 1))));
    }

    private IEnumerable<(int, string)> SlotItems(int slot, Func<ActInfo, IEnumerable<int>> pick, Func<int, string> name) =>
        Data.SlotActs[slot].SelectMany(a => pick(Data.ActInfos[a])).Distinct().Select(id => (id, name(id)));

    private OptionButton SlotPicker(Func<int, bool> include)
    {
        OptionButton slot = new();
        for (int s = 0; s < Data.SlotActs.Length; s++)
        {
            if (include(s))
                slot.AddItem($"Act {s + 1}", s);
        }

        return slot;
    }

    private static void Bind(OptionButton option, Action<int> apply)
    {
        option.ItemSelected += _ => apply(option.GetSelectedId());
        if (option.ItemCount > 0)
            apply(option.GetSelectedId());
    }

    private Control Row(Control[] controls, Func<Cond?> make)
    {
        HFlowContainer row = new();
        row.AddThemeConstantOverride("h_separation", 8);
        foreach (Control c in controls)
            row.AddChild(c);
        Button add = new() { Text = "Add" };
        add.Pressed += () =>
        {
            if (make() is { } cond && !Conds.Contains(cond))
            {
                Conds.Add(cond);
                RefreshConds();
            }
        };
        row.AddChild(add);
        return row;
    }

    private static Control Section(string title, string? hint, params Control[] rows)
    {
        PanelContainer panel = Panel(InsetColor);
        VBoxContainer box = new();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        box.AddChild(SectionTitle(title));
        if (hint != null)
            box.AddChild(MutedLabel(hint));
        foreach (Control row in rows)
            box.AddChild(row);
        return panel;
    }

    private void RefreshConds()
    {
        foreach (Node child in _condList.GetChildren())
        {
            _condList.RemoveChild(child);
            child.QueueFree();
        }

        if (Conds.Count == 0)
            _condList.AddChild(MutedLabel("No filters yet. Add some below."));

        foreach (Cond cond in Conds.ToList())
        {
            HBoxContainer row = new();
            Label label = new() { Text = cond.Describe(Data), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            row.AddChild(label);
            Button remove = new() { Text = "Remove" };
            remove.Pressed += () =>
            {
                Conds.Remove(cond);
                RefreshConds();
            };
            row.AddChild(remove);
            _condList.AddChild(row);
        }
    }

    // ---- search ----

    private void ToggleSearch()
    {
        if (_search != null)
        {
            StopSearch();
            return;
        }

        if (Conds.Count == 0)
        {
            _status.Text = "Add at least one filter first.";
            return;
        }

        try
        {
            _search = _engine.StartSearch(Conds.ToList(), MaxResults);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed search could not start. {e}");
            _status.Text = "Search could not start. See godot.log.";
            return;
        }

        _results.Clear();
        _searchButton.Text = "Stop";
        _pollTimer.Start();
    }

    private void PollSearch()
    {
        if (_search == null)
            return;

        SearchProgress p = _search.Poll();
        for (int i = _results.ItemCount; i < p.Results.Count; i++)
        {
            _results.AddItem(p.Results[i]);
            if (i == 0)
            {
                _results.Select(0);
                ShowSeed(p.Results[0], fromList: true);
            }
        }

        string summary = $"{p.Results.Count} found  ·  {p.Tried:N0} seeds tried  ·  {Rate(p.SeedsPerSecond)}";
        if (p.Running)
        {
            _status.Text = $"Searching…  {summary}";
        }
        else
        {
            StopSearch();
            _status.Text = $"Done.  {summary}";
        }
    }

    private void StopSearch()
    {
        if (_search == null)
            return;

        _search.Dispose();
        _search = null;
        _pollTimer.Stop();
        _searchButton.Text = "Search";
        _status.Text = $"Stopped.  {_results.ItemCount} found";
    }

    private static string Rate(double perSecond) => perSecond switch
    {
        >= 1_000_000 => $"{perSecond / 1_000_000:F1}M/s",
        >= 1_000 => $"{perSecond / 1_000:F0}k/s",
        _ => $"{perSecond:F0}/s"
    };

    // ---- preview ----

    private void ShowSeed(string seed, bool fromBox = false, bool fromList = false)
    {
        seed = seed.Trim();
        if (!fromBox)
            _seedBox.Text = seed;
        if (!fromList)
            _results.DeselectAll();

        _shown = seed.Length == 0 ? null : _engine.Simulate(seed);
        _useButton.Disabled = _shown == null;
        _detail.Text = _shown == null
            ? seed.Length == 0 ? "[i]Pick a seed from the list, or type one above to preview it.[/i]" : "[i]Could not preview this seed. See godot.log.[/i]"
            : Describe(_shown);
        _mapCanvas.QueueRedraw();
    }

    private string Describe(SimResult r)
    {
        StringBuilder sb = new();
        sb.AppendLine($"[font_size=30][color=#{Accent.ToHtml(false)}]{r.Seed}[/color][/font_size]");

        if (r.Neow.Length == 3)
            sb.AppendLine($"[b]Neow offers[/b]  {Data.RelicName(r.Neow[0])}, {Data.RelicName(r.Neow[1])}, or cursed: {Data.RelicName(r.Neow[2])}");
        string potion = r.Potion < 0 ? "no potion" : Data.PotionName(r.Potion);
        sb.AppendLine($"[b]First fight reward[/b]  {string.Join(", ", r.Cards.Select(Data.CardName))}  ·  {potion}  ·  {r.Gold} gold");

        for (int i = 0; i < r.Acts.Length; i++)
        {
            string boss = Data.EncounterName(r.Bosses[i]);
            if (i == r.Acts.Length - 1 && r.SecondBoss >= 0)
                boss += $" then {Data.EncounterName(r.SecondBoss)}";
            sb.AppendLine();
            sb.AppendLine($"[b]Act {i + 1}  ·  {Data.ActName(r.Acts[i])}[/b]  ·  boss {boss}  ·  ancient {Data.AncientName(r.Ancients[i])}");
            sb.AppendLine($"  Events: {Preview(r.Events[i], Data.EventName)}");
            sb.AppendLine($"  Fights: {Preview(r.Normals[i], Data.EncounterName)}");
            sb.AppendLine($"  Elites: {Preview(r.Elites[i], Data.EncounterName, 3)}");
            if (i < r.Maps.Length && r.Maps[i] is { } map)
                sb.AppendLine($"  Map: {string.Join(", ", MapTypes.Take(4).Select(t => $"{map.Count(t.Type)} {t.Name}"))}");
        }

        sb.AppendLine();
        sb.AppendLine($"[b]Your relic bag[/b]  {Bag(r.PlayerRelics)}");
        sb.AppendLine($"[b]Shared relic bag[/b]  {Bag(r.SharedRelics)}");
        sb.AppendLine();
        sb.Append($"[color=#{Muted.ToHtml(false)}][i]Lists show what comes first. The fight reward assumes a normal first fight and a Neow pick that gives no cards, relics or potions. Custom modifiers are not taken into account.[/i][/color]");
        return sb.ToString();
    }

    private static string Preview(int[] ids, Func<int, string> name, int count = ListPreview) =>
        string.Join(", ", ids.Take(count).Select(name)) + (ids.Length > count ? ", …" : "");

    private string Bag(List<RelicDeque> deques) =>
        string.Join("  ·  ", deques
            .Where(d => (RelicRarity)d.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop)
            .Select(d => $"{(RelicRarity)d.Rarity}: {Preview(d.Relics, Data.RelicName, 4)}"));

    private void DrawMap()
    {
        int slot = _mapAct.ItemCount == 0 ? 0 : _mapAct.GetSelectedId();
        if (_shown == null || slot >= _shown.Maps.Length || _shown.Maps[slot] is not { } map || map.Points.Length == 0)
            return;

        // Rows run bottom to top like the in-game map, drawn sideways here to fit a wide panel:
        // the act starts at the left edge and the boss is at the right.
        int maxRow = map.Points.Max(p => p[1]);
        Vector2 size = _mapCanvas.Size;
        const float pad = 22;
        float dx = (size.X - 2 * pad) / Math.Max(1, maxRow - 1);
        float dy = (size.Y - 2 * pad) / 6;
        Vector2 At(int col, int row) => new(pad + (row - 1) * dx, pad + col * dy);

        foreach (int[] e in map.Edges)
            _mapCanvas.DrawLine(At(e[0], e[1]), At(e[2], e[3]), new Color(1, 1, 1, 0.28f), 2, true);
        float radius = Math.Clamp(Math.Min(dx, dy) * 0.28f, 4, 11);
        foreach (int[] p in map.Points)
        {
            Color color = MapTypes.FirstOrDefault(t => t.Type == p[2], (Type: 0, Name: "", Color: Muted)).Color;
            _mapCanvas.DrawCircle(At(p[0], p[1]), radius, color);
        }
    }

    // ---- leaving ----

    private void UseSeed()
    {
        if (_shown == null)
            return;

        // Setting Text from code does not emit TextChanged, so tell the lobby directly.
        _gameSeedInput.Text = _shown.Seed;
        _screen.Lobby.SetSeed(_shown.Seed);
        Close();
    }

    private void OpenOnSearchTheSpire()
    {
        string url = "https://searchthespire.app/";
        if (_shown != null)
            url += $"?seed={Uri.EscapeDataString(_shown.Seed)}&ichar={Data.Character.Id.Entry.ToLowerInvariant()}";
        OS.ShellOpen(url);
    }

    private void Close()
    {
        StopSearch();
        foreach (NClickableControl button in _disabledGameButtons)
            button.Enable();
        _disabledGameButtons.Clear();
        _layer.QueueFree();
    }

    // The game's own buttons keep their hotkeys while covered, so Esc would leave the Custom Run
    // screen and Enter would embark from underneath this one.
    private void DisableGameButtons()
    {
        foreach (string field in GameButtonFields)
        {
            if (AccessTools.Field(typeof(NCustomRunScreen), field)?.GetValue(_screen) is NClickableControl { IsEnabled: true } button)
            {
                button.Disable();
                _disabledGameButtons.Add(button);
            }
        }
    }

    // ---- small helpers ----

    private static StyleBoxFlat Box(Color fill, Color border)
    {
        StyleBoxFlat box = new() { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll(6);
        box.ContentMarginLeft = box.ContentMarginRight = 12;
        box.ContentMarginTop = box.ContentMarginBottom = 5;
        return box;
    }

    private static PanelContainer Panel(Color color)
    {
        StyleBoxFlat style = new() { BgColor = color };
        style.SetCornerRadiusAll(8);
        style.SetContentMarginAll(14);
        PanelContainer panel = new();
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static Label SectionTitle(string text)
    {
        Label label = new() { Text = text };
        label.AddThemeFontSizeOverride("font_size", 24);
        label.AddThemeColorOverride("font_color", Accent);
        return label;
    }

    private static Label MutedLabel(string text)
    {
        Label label = new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", Muted);
        return label;
    }

    private static SpinBox Spin(int min, int max, int value) => new() { MinValue = min, MaxValue = max, Value = value, Step = 1 };

    // A dropdown with a text box that narrows it, for lists too long to scroll through.
    private sealed class Picker
    {
        private readonly LineEdit _filter = new() { PlaceholderText = "filter", CustomMinimumSize = new Vector2(100, 0) };
        private readonly OptionButton _options = new() { CustomMinimumSize = new Vector2(240, 0), FitToLongestItem = false, ClipText = true };
        private List<(int Id, string Name)> _items = [];

        public HBoxContainer Root { get; } = new();

        public Picker(IEnumerable<(int, string)> items)
        {
            _options.GetPopup().MaxSize = new Vector2I(700, 620);
            _filter.TextChanged += _ => Fill();
            Root.AddChild(_filter);
            Root.AddChild(_options);
            SetItems(items);
        }

        public int? Selected => _options.ItemCount == 0 || _options.Selected < 0 ? null : _options.GetSelectedId();

        public void SetItems(IEnumerable<(int Id, string Name)> items)
        {
            _items = items.DistinctBy(i => i.Id).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            // Short lists do not need narrowing.
            _filter.Visible = _items.Count > 12;
            Fill();
        }

        private void Fill()
        {
            string filter = _filter.Visible ? _filter.Text.Trim() : "";
            _options.Clear();
            foreach ((int id, string name) in _items)
            {
                if (filter.Length == 0 || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                    _options.AddItem(name, id);
            }
        }
    }
}
