using Godot;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// The filters view of the seed search screen: a rail with one tab per kind of filter, in which the
// thing to filter on is picked from a grid of game icons. The active filters are a separate list
// (Active) that the screen keeps in its sidebar.
public sealed class FilterPanel
{
    private readonly GameData _data;
    private readonly List<Cond> _conds;
    private readonly Action<Visual> _hover;
    private readonly Action<string> _notify;
    private readonly VBoxContainer _chips = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _tabBar = new();
    private readonly PanelContainer _tabHost = Visuals.Panel(Visuals.PanelColor);
    private readonly ButtonGroup _tabGroup = new();

    public HBoxContainer Root { get; } = new();

    public PanelContainer Active { get; } = Visuals.Panel(Visuals.PanelColor);

    // notify shows one line of feedback, for things that happen away from where the player looks.
    public FilterPanel(GameData data, List<Cond> conds, Action<Visual> hover, Action<string> notify)
    {
        _data = data;
        _conds = conds;
        _hover = hover;
        _notify = notify;

        Root.AddThemeConstantOverride("separation", 12);
        PanelContainer rail = Visuals.Panel(Visuals.PanelColor, 10);
        rail.CustomMinimumSize = new Vector2(190, 0);
        _tabBar.AddThemeConstantOverride("separation", 6);
        rail.AddChild(_tabBar);
        Root.AddChild(rail);
        _tabHost.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Root.AddChild(_tabHost);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 10);
        Active.AddChild(column);
        HBoxContainer title = new();
        Label heading = Visuals.SectionTitle("Filters");
        heading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.AddChild(heading);
        Button save = new() { Text = "Save search", TooltipText = "Keep these filters under the Saved tab", FocusMode = Control.FocusModeEnum.None };
        save.Pressed += SaveSearch;
        title.AddChild(save);
        column.AddChild(title);
        ScrollContainer chipScroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _chips.AddThemeConstantOverride("separation", 6);
        chipScroll.AddChild(_chips);
        column.AddChild(chipScroll);

        if (_data.NeowRelics.Length > 0)
            AddTab(Visuals.Relic(_data, _data.NeowRelics[0]) with { Name = "Neow" }, NeowTab);
        AddTab(Visuals.Named("Acts & bosses", Visuals.Load("res://images/ui/run_history/elite.png")), ActTab);
        AddTab(Visuals.Named("Events", Visuals.Load("res://images/ui/run_history/event.png")), EventTab);
        AddTab(Visuals.Named("Fights", Visuals.Load("res://images/ui/run_history/monster.png")), FightTab);
        AddTab(Visuals.Named("Relics", Visuals.Load("res://images/ui/run_history/treasure.png")), RelicTab);
        AddTab(Visuals.Named("Map", Visuals.Load("res://images/ui/run_history/rest_site.png")), MapTab);
        AddTab(Visuals.Named("First reward", Visuals.Load("res://images/ui/run_history/shop.png")), RewardTab);
        AddTab(Visuals.Named("Saved"), SavedTab);
        RefreshChips();
    }

    // ---- active filters ----

    private void RefreshChips()
    {
        Visuals.Clear(_chips);
        if (_conds.Count == 0)
        {
            _chips.AddChild(Visuals.MutedLabel("No filters yet. Pick something in the Filters view and add it. A seed must match every filter."));
        }

        foreach (Cond cond in _conds.ToList())
        {
            Visual visual = VisualOf(cond);
            Button chip = Visuals.Pill(visual with { Name = "Remove this filter" }, _ => _hover(visual), $"{cond.Describe(_data)}   ✕");
            chip.Alignment = HorizontalAlignment.Left;
            chip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            chip.Pressed += () =>
            {
                _conds.Remove(cond);
                RefreshChips();
            };
            _chips.AddChild(chip);
        }
    }

    private void Add(Cond? cond)
    {
        if (cond == null || _conds.Contains(cond))
            return;
        if (Feasibility.Conflict(_data, _conds, cond) is { } reason)
        {
            _notify($"Not added. {reason}");
            return;
        }

        _conds.Add(cond);
        RefreshChips();
    }

    private void SaveSearch()
    {
        if (_conds.Count == 0)
        {
            _notify("Add at least one filter first.");
            return;
        }

        SavedData.RecordSearch(_data, _conds, keep: true);
        _notify("Search saved. It is under the Saved tab.");
    }

    private void Load(SavedSearch search)
    {
        (List<Cond> conds, int dropped) = CondCodec.Read(search.CondsJson, _data);
        _conds.Clear();
        foreach (Cond cond in conds)
        {
            if (Feasibility.Conflict(_data, _conds, cond) == null)
                _conds.Add(cond);
            else
                dropped++;
        }

        RefreshChips();
        _notify(dropped == 0
            ? "Filters loaded."
            : $"Loaded. {dropped} filter{(dropped == 1 ? "" : "s")} left out: not possible in this setup.");
    }

    private Visual VisualOf(Cond cond) => cond switch
    {
        NeowOfferCond c => Visuals.Relic(_data, c.Relic, _data.NeowCurses.Contains(c.Relic)),
        NeowGivesCond c => Given(c.What, c.Item),
        NeowNestedCond c => Given(c.What, c.Item),
        BossCond c => Visuals.Encounter(_data, c.Encounter, RoomType.Boss),
        SecondBossCond c => Visuals.Encounter(_data, c.Encounter, RoomType.Boss),
        AncientCond c => Visuals.Ancient(_data, c.Ancient),
        EventCond c => Visuals.Event(_data, c.Event),
        EncounterCond c => Visuals.Encounter(_data, c.Encounter, c.Elite ? RoomType.Elite : RoomType.Monster),
        RelicCond c => Visuals.Relic(_data, c.Relic),
        MapCountCond c => Visuals.MapType(Math.Max(0, Array.FindIndex(Visuals.MapTypes, t => t.Type == c.PointType))),
        RewardCardCond c => Visuals.Card(_data, c.Card),
        _ => Visuals.Named(cond.Describe(_data))
    };

    private Visual Given(GiveItem what, int item) => what switch
    {
        GiveItem.Card => Visuals.Card(_data, item),
        GiveItem.Relic => Visuals.Relic(_data, item),
        _ => Visuals.Potion(_data, item)
    };

    // ---- tabs ----

    // Tabs are built the first time they are opened, so opening the screen stays quick.
    private void AddTab(Visual visual, Func<Control> build)
    {
        Button tab = Visuals.Pill(visual, _ => { });
        tab.Alignment = HorizontalAlignment.Left;
        tab.CustomMinimumSize = new Vector2(0, 44);
        tab.AddThemeFontSizeOverride("font_size", 18);
        tab.ToggleMode = true;
        tab.ButtonGroup = _tabGroup;
        Control? content = null;
        tab.Toggled += on =>
        {
            if (on && content == null)
            {
                content = build();
                _tabHost.AddChild(content);
            }

            if (content != null)
                content.Visible = on;
        };
        _tabBar.AddChild(tab);
        if (_tabBar.GetChildCount() == 1)
            tab.ButtonPressed = true;
    }

    private Control NeowTab()
    {
        IconGrid relics = new(_hover);
        relics.SetItems(_data.NeowRelics.Select(r => (r, Visuals.Relic(_data, r, _data.NeowCurses.Contains(r)))));
        IconGrid gives = new(_hover, optional: true);
        // Neow's Bones gives relics that roll things of their own.
        IconGrid nested = new(_hover, optional: true);

        // One step per column, left to right; the later ones are only there when they apply.
        (VBoxContainer Box, Label Note) Column(string title, IconGrid grid)
        {
            VBoxContainer box = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(new Label { Text = title });
            Label note = Visuals.MutedLabel("", 14);
            box.AddChild(note);
            box.AddChild(grid.Root);
            return (box, note);
        }

        (VBoxContainer offerColumn, Label offerNote) = Column("1. Neow offers", relics);
        (VBoxContainer givesColumn, Label givesNote) = Column("2. What it must give", gives);
        (VBoxContainer nestedColumn, Label nestedNote) = Column("3. What that must give in turn", nested);
        HBoxContainer columns = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        foreach (Control column in new[] { offerColumn, givesColumn, nestedColumn })
            columns.AddChild(column);

        // These grids mix cards, relics and potions, told apart by a stride on the id.
        const int stride = 1_000_000;
        IEnumerable<(int, Visual)> Mixed((int[] Cards, int[] Relics, int[] Potions) options) =>
            options.Relics.Select(g => ((int)GiveItem.Relic * stride + g, Visuals.Relic(_data, g)))
                .Concat(options.Potions.Select(p => ((int)GiveItem.Potion * stride + p, Visuals.Potion(_data, p))))
                .Concat(options.Cards.Select(c => ((int)GiveItem.Card * stride + c, Visuals.Card(_data, c))));

        void ShowNested()
        {
            (int[] Cards, int[] Relics, int[] Potions) options = ([], [], []);
            int? via = gives.Selected is { } given && given / stride == (int)GiveItem.Relic ? given % stride : null;
            if (via is { } v)
                options = _data.NeowGiveOptions(v);
            nested.SetItems(Mixed(options));
            bool any = options.Cards.Length + options.Relics.Length + options.Potions.Length > 0;
            nestedColumn.Visible = any;
            nestedNote.Text = any ? $"Optional: what {_data.RelicName(via!.Value)} gives. Leave unpicked for anything." : "";
        }

        void ShowGives()
        {
            (int[] Cards, int[] Relics, int[] Potions) options = ([], [], []);
            if (relics.Selected is { } r)
                options = _data.NeowGiveOptions(r);
            gives.SetItems(Mixed(options));
            bool any = options.Cards.Length + options.Relics.Length + options.Potions.Length > 0;
            givesColumn.Visible = any;
            givesNote.Text = any ? $"Optional: what {_data.RelicName(relics.Selected!.Value)} gives. Leave unpicked for anything." : "";
            offerNote.Text = relics.Selected is { } picked && !any
                ? $"{_data.RelicName(picked)} rolls nothing when you take it."
                : "Red frames are the cursed offers.";
            ShowNested();
        }

        relics.Changed += ShowGives;
        gives.Changed += ShowNested;
        ShowGives();

        (Control tab, Action refresh) = Tab("One of Neow's three offers, and if you like what it gives when you take it.",
            [],
            [columns],
            [],
            () => relics.Selected is not { } relic
                ? null
                : gives.Selected is not { } item
                    ? new NeowOfferCond(relic)
                    : nested.Selected is { } inner
                        ? new NeowNestedCond(relic, item % stride, (GiveItem)(inner / stride), inner % stride)
                        : new NeowGivesCond(relic, (GiveItem)(item / stride), item % stride));
        foreach (IconGrid grid in new[] { relics, gives, nested })
            grid.Changed += refresh;
        return tab;
    }

    private Control ActTab()
    {
        const int boss = 0, ancient = 1, act = 2, secondBoss = 3;
        Choice slot = SlotChoice();
        List<(int, string)> kinds = [(boss, "Boss"), (ancient, "Ancient"), (act, "Act")];
        if (_data.DoubleBoss)
            kinds.Add((secondBoss, "Second final boss"));
        Choice kind = new(kinds);
        IconGrid grid = new(_hover);

        void Refill()
        {
            int s = kind.Value == secondBoss ? _data.SlotActs.Length - 1 : slot.Value;
            slot.Root.Visible = kind.Value != secondBoss;
            grid.SetItems(kind.Value switch
            {
                ancient => SlotItems(s, a => a.Ancients).Concat(s > 0 ? _data.SharedAncients : []).Select(a => (a, Visuals.Ancient(_data, a))),
                act => _data.SlotActs[s].Select(a => (a, Visuals.Named(_data.ActName(a)))),
                _ => SlotItems(s, a => a.Boss).Select(e => (e, Visuals.Encounter(_data, e, RoomType.Boss)))
            });
        }

        slot.Changed += Refill;
        kind.Changed += Refill;
        Refill();

        (Control tab, Action refresh) = Tab("Which act a slot rolls, and its boss and ancient.", [kind.Root, slot.Root], [grid.Root], [], () =>
            grid.Selected is not { } id
                ? null
                : kind.Value switch
                {
                    ancient => new AncientCond(slot.Value, id),
                    act => new ActCond(slot.Value, id),
                    secondBoss => new SecondBossCond(id),
                    _ => new BossCond(slot.Value, id)
                });
        slot.Changed += refresh;
        kind.Changed += refresh;
        grid.Changed += refresh;
        return tab;
    }

    private Control EventTab()
    {
        Choice slot = SlotChoice();
        IconGrid grid = new(_hover);
        void Refill() => grid.SetItems(SlotItems(slot.Value, a => a.Events).Select(e => (e, Visuals.Event(_data, e))));
        slot.Changed += Refill;
        Refill();
        SpinBox within = Visuals.Spin(1, 20, 3);
        (Control tab, Action refresh) = Tab("Events are queued per act; each unknown room that rolls an event takes the next one.",
            [slot.Root], [grid.Root], [new Label { Text = "within the first" }, within],
            () => grid.Selected is { } e ? new EventCond(slot.Value, e, (int)within.Value) : null);
        slot.Changed += refresh;
        grid.Changed += refresh;
        within.ValueChanged += _ => refresh();
        return tab;
    }

    private Control FightTab()
    {
        Choice slot = SlotChoice();
        Choice kind = new([(0, "Fight"), (1, "Elite")]);
        IconGrid grid = new(_hover);
        SpinBox within = Visuals.Spin(1, 15, 1);
        void Refill() => grid.SetItems(_data.FightOptions(slot.Value, kind.Value == 1, (int)within.Value)
            .Select(e => (e, Visuals.Encounter(_data, e, kind.Value == 1 ? RoomType.Elite : RoomType.Monster))));
        slot.Changed += Refill;
        kind.Changed += Refill;
        within.ValueChanged += _ => Refill();
        Refill();
        (Control tab, Action refresh) = Tab("Fights and elites are queued per act in the order you will meet them. An act opens with its easy fights; raise the number below to pick from the ones after them.",
            [kind.Root, slot.Root], [grid.Root], [new Label { Text = "within the first" }, within],
            () => grid.Selected is { } e ? new EncounterCond(slot.Value, kind.Value == 1, e, (int)within.Value) : null);
        slot.Changed += refresh;
        kind.Changed += refresh;
        grid.Changed += refresh;
        within.ValueChanged += _ => refresh();
        return tab;
    }

    private Control RelicTab()
    {
        Choice bag = new([(0, "Your bag"), (1, "Shared bag")]);
        IconGrid grid = new(_hover);
        void Refill() => grid.SetItems((bag.Value == 1 ? _data.SharedBagDealt : _data.PlayerBag)
            .OrderBy(r => _data.Relics[r].Rarity)
            .Select(r => (r, Visuals.Relic(_data, r))));
        bag.Changed += Refill;
        Refill();
        SpinBox within = Visuals.Spin(1, 30, 3);
        (Control tab, Action refresh) = Tab("Relic rewards are dealt from a shuffled bag per rarity. Frames show the rarity.",
            [bag.Root], [grid.Root], [new Label { Text = "within the first" }, within, new Label { Text = "of its rarity" }],
            () => grid.Selected is { } r ? new RelicCond(bag.Value == 1, r, (int)within.Value) : null);
        bag.Changed += refresh;
        grid.Changed += refresh;
        within.ValueChanged += _ => refresh();
        return tab;
    }

    private Control MapTab()
    {
        Choice slot = SlotChoice();
        IconGrid grid = new(_hover);
        grid.SetItems(Enumerable.Range(0, Visuals.MapTypes.Length).Select(i => (i, Visuals.MapType(i))));
        SpinBox min = Visuals.Spin(0, 40, 0);
        SpinBox max = Visuals.Spin(0, 40, 4);
        (Control tab, Action refresh) = Tab("Counts over the whole act map. Map filters are the slowest to search.",
            [slot.Root], [grid.Root], [new Label { Text = "from" }, min, new Label { Text = "to" }, max], () =>
            {
                if (grid.Selected is not { } i)
                    return null;
                (int type, _, string name, _) = Visuals.MapTypes[i];
                int lo = (int)Math.Min(min.Value, max.Value), hi = (int)Math.Max(min.Value, max.Value);
                return new MapCountCond(slot.Value, type, name, lo, hi);
            });
        slot.Changed += refresh;
        grid.Changed += refresh;
        min.ValueChanged += _ => refresh();
        max.ValueChanged += _ => refresh();
        return tab;
    }

    private Control RewardTab()
    {
        IconGrid grid = new(_hover);
        grid.SetItems(_data.FirstRewardCards.OrderBy(c => _data.Cards[c].Rarity).Select(c => (c, Visuals.Card(_data, c))));
        Button potion = new() { Text = "Add: drops a potion" };
        potion.Pressed += () => Add(new RewardPotionCond(true));
        Button noPotion = new() { Text = "Add: drops no potion" };
        noPotion.Pressed += () => Add(new RewardPotionCond(false));
        (Control tab, Action refresh) = Tab("The reward of the first fight, with the cards it can roll this early. Holds if the first room is a normal fight and your Neow pick gave no cards, relics or potions.",
            [potion, noPotion], [grid.Root], [],
            () => grid.Selected is { } c ? new RewardCardCond(c) : null);
        grid.Changed += refresh;
        return tab;
    }

    // Every search that was run, most recent first, below the ones the player chose to keep.
    private Control SavedTab()
    {
        VBoxContainer tab = new() { Visible = false };
        tab.AddThemeConstantOverride("separation", 8);
        tab.AddChild(Visuals.MutedLabel($"The last {SavedData.MaxRecent} searches you ran are remembered here. Keep one to hold on to it for good."));
        ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        VBoxContainer rows = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rows);
        tab.AddChild(scroll);

        void Refill()
        {
            Visuals.Clear(rows);
            if (SavedData.Searches.Count == 0)
                rows.AddChild(Visuals.MutedLabel("Nothing yet. Run a search, or press Save search above."));
            foreach (SavedSearch search in SavedData.Searches.OrderByDescending(s => s.Kept))
                rows.AddChild(SavedRow(search));
        }

        SavedData.SearchesChanged += Refill;
        tab.TreeExiting += () => SavedData.SearchesChanged -= Refill;
        Refill();
        return tab;
    }

    private Control SavedRow(SavedSearch search)
    {
        PanelContainer panel = Visuals.Panel(Visuals.PanelColor, 8);
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        // Wrapping labels need a width to start from, or they grow tall instead of wide.
        VBoxContainer text = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(360, 0) };
        text.AddChild(Visuals.MutedLabel($"{search.Setup}  ·  {search.Used.ToLocalTime():g}", 14));
        Label filters = new() { Text = string.Join("\n", CondCodec.Texts(search.CondsJson)), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        filters.AddThemeFontSizeOverride("font_size", 16);
        text.AddChild(filters);
        row.AddChild(text);

        Button load = new() { Text = "Load", TooltipText = "Replace the filters above with these" };
        load.Pressed += () => Load(search);
        Button keep = new() { Text = "Keep", ToggleMode = true, ButtonPressed = search.Kept, TooltipText = "Kept searches are never forgotten" };
        keep.Toggled += on => SavedData.SetKept(search, on);
        Button remove = new() { Text = "✕", TooltipText = "Forget this search" };
        remove.Pressed += () => SavedData.RemoveSearch(search);
        foreach (Button button in new[] { load, keep, remove })
        {
            button.FocusMode = Control.FocusModeEnum.None;
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(button);
        }

        return panel;
    }

    // A hint, a row of options, the grids, and a bottom row that spells out the filter the picks
    // make and ends in the Add button. The returned action is to be called when a pick changes.
    private (Control Tab, Action Refresh) Tab(string hint, Control[] options, Control[] body, Control[] trailing, Func<Cond?> make)
    {
        VBoxContainer tab = new() { Visible = false };
        tab.AddThemeConstantOverride("separation", 8);
        tab.AddChild(Visuals.MutedLabel(hint));
        if (options.Length > 0)
        {
            HFlowContainer row = Visuals.Flow(16);
            foreach (Control option in options)
                row.AddChild(option);
            tab.AddChild(row);
        }

        foreach (Control part in body)
            tab.AddChild(part);

        HBoxContainer bottom = new();
        bottom.AddThemeConstantOverride("separation", 8);
        Label pending = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, ClipText = true };
        bottom.AddChild(pending);
        foreach (Control control in trailing)
            bottom.AddChild(control);
        Button add = new() { Text = "Add filter", CustomMinimumSize = new Vector2(150, 42) };
        add.Pressed += () => Add(make());
        bottom.AddChild(add);
        tab.AddChild(bottom);

        void Refresh()
        {
            Cond? cond = make();
            add.Disabled = cond == null;
            pending.Text = cond?.Describe(_data) ?? "";
        }

        Refresh();
        return (tab, Refresh);
    }

    private Choice SlotChoice() => new(Enumerable.Range(0, _data.SlotActs.Length).Select(s => (s, $"Act {s + 1}")));

    private IEnumerable<int> SlotItems(int slot, Func<ActInfo, IEnumerable<int>> pick) =>
        _data.SlotActs[slot].SelectMany(a => pick(_data.ActInfos[a])).Distinct();

    // A scrolling grid of tiles of which one can be picked, with a box that narrows long lists.
    private sealed class IconGrid
    {
        private readonly Action<Visual> _hover;
        private readonly bool _optional;
        private readonly LineEdit _search = new() { PlaceholderText = "Search by name", ClearButtonEnabled = true };
        private readonly HFlowContainer _flow = Visuals.Flow();
        private readonly List<(Button Tile, string Name)> _tiles = [];
        private ButtonGroup _group = new();

        public VBoxContainer Root { get; } = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        public int? Selected { get; private set; }

        public event Action? Changed;

        public IconGrid(Action<Visual> hover, bool optional = false)
        {
            _hover = hover;
            _optional = optional;
            _search.TextChanged += _ => Narrow();
            Root.AddChild(_search);
            ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scroll.AddChild(_flow);
            Root.AddChild(scroll);
        }

        public void SetItems(IEnumerable<(int Id, Visual Visual)> items)
        {
            Visuals.Clear(_flow);
            _tiles.Clear();
            _group = new ButtonGroup { AllowUnpress = _optional };
            bool hadSelection = Selected != null;
            Selected = null;

            foreach ((int id, Visual visual) in items.DistinctBy(i => i.Id))
            {
                Button tile = Visuals.Tile(visual, _hover);
                tile.ToggleMode = true;
                tile.ButtonGroup = _group;
                tile.Toggled += on =>
                {
                    Selected = on ? id : Selected == id ? null : Selected;
                    Changed?.Invoke();
                };
                _flow.AddChild(tile);
                _tiles.Add((tile, visual.Name));
            }

            // Short lists do not need narrowing.
            _search.Visible = _tiles.Count > 14;
            Narrow();
            if (hadSelection)
                Changed?.Invoke();
        }

        private void Narrow()
        {
            string filter = _search.Visible ? _search.Text.Trim() : "";
            foreach ((Button tile, string name) in _tiles)
                tile.Visible = filter.Length == 0 || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
