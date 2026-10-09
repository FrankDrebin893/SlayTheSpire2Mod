using Godot;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// The left side of the seed search screen: the active filters as chips, and one tab per kind of
// filter in which the thing to filter on is picked from a grid of game icons.
public sealed class FilterPanel
{
    private readonly GameData _data;
    private readonly List<Cond> _conds;
    private readonly Action<Visual> _hover;
    private readonly HFlowContainer _chips = Visuals.Flow();
    private readonly HFlowContainer _tabBar = Visuals.Flow();
    private readonly PanelContainer _tabHost = Visuals.Panel(Visuals.InsetColor, 10);
    private readonly ButtonGroup _tabGroup = new();

    public PanelContainer Root { get; } = Visuals.Panel(Visuals.PanelColor);

    public FilterPanel(GameData data, List<Cond> conds, Action<Visual> hover)
    {
        _data = data;
        _conds = conds;
        _hover = hover;
        Root.CustomMinimumSize = new Vector2(760, 0);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 10);
        Root.AddChild(column);

        column.AddChild(Visuals.SectionTitle("Filters"));
        ScrollContainer chipScroll = new() { CustomMinimumSize = new Vector2(0, 92), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        chipScroll.AddChild(_chips);
        PanelContainer active = Visuals.Panel(Visuals.InsetColor, 8);
        active.AddChild(chipScroll);
        column.AddChild(active);

        column.AddChild(_tabBar);
        _tabHost.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(_tabHost);

        if (_data.NeowRelics.Length > 0)
            AddTab(Visuals.Relic(_data, _data.NeowRelics[0]) with { Name = "Neow" }, NeowTab);
        AddTab(Visuals.Named("Acts & bosses", Visuals.Load("res://images/ui/run_history/elite.png")), ActTab);
        AddTab(Visuals.Named("Events", Visuals.Load("res://images/ui/run_history/event.png")), EventTab);
        AddTab(Visuals.Named("Fights", Visuals.Load("res://images/ui/run_history/monster.png")), FightTab);
        AddTab(Visuals.Named("Relics", Visuals.Load("res://images/ui/run_history/treasure.png")), RelicTab);
        AddTab(Visuals.Named("Map", Visuals.Load("res://images/ui/run_history/rest_site.png")), MapTab);
        AddTab(Visuals.Named("First reward", Visuals.Load("res://images/ui/run_history/shop.png")), RewardTab);
        RefreshChips();
    }

    // ---- active filters ----

    private void RefreshChips()
    {
        Visuals.Clear(_chips);
        if (_conds.Count == 0)
        {
            Label empty = Visuals.MutedLabel("No filters yet. Pick something below and add it. A seed must match every filter.");
            empty.CustomMinimumSize = new Vector2(680, 0);
            _chips.AddChild(empty);
        }

        foreach (Cond cond in _conds.ToList())
        {
            Visual visual = VisualOf(cond);
            Button chip = Visuals.Pill(visual with { Name = "Remove this filter" }, _ => _hover(visual), $"{cond.Describe(_data)}   ✕");
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
        if (cond != null && !_conds.Contains(cond))
        {
            _conds.Add(cond);
            RefreshChips();
        }
    }

    private Visual VisualOf(Cond cond) => cond switch
    {
        NeowOfferCond c => Visuals.Relic(_data, c.Relic, _data.NeowCurses.Contains(c.Relic)),
        NeowGivesCond c => c.What switch
        {
            GiveItem.Card => Visuals.Card(_data, c.Item),
            GiveItem.Relic => Visuals.Relic(_data, c.Item),
            _ => Visuals.Potion(_data, c.Item)
        },
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

    // ---- tabs ----

    // Tabs are built the first time they are opened, so opening the screen stays quick.
    private void AddTab(Visual visual, Func<Control> build)
    {
        Button tab = Visuals.Pill(visual, _ => { });
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
        Label givesTitle = Visuals.MutedLabel("");

        // The second grid mixes cards, relics and potions, told apart by a stride on the id.
        const int stride = 1_000_000;
        void ShowGives()
        {
            int[] cards = [], givenRelics = [], potions = [];
            if (relics.Selected is { } r)
                (cards, givenRelics, potions) = _data.NeowGiveOptions(r);
            gives.SetItems(givenRelics.Select(g => ((int)GiveItem.Relic * stride + g, Visuals.Relic(_data, g)))
                .Concat(potions.Select(p => ((int)GiveItem.Potion * stride + p, Visuals.Potion(_data, p))))
                .Concat(cards.Select(c => ((int)GiveItem.Card * stride + c, Visuals.Card(_data, c)))));
            bool any = cards.Length + givenRelics.Length + potions.Length > 0;
            gives.Root.Visible = any;
            givesTitle.Text = relics.Selected is not { } picked
                ? ""
                : any
                    ? $"Optional: what {_data.RelicName(picked)} must give. Leave unpicked for anything."
                    : $"{_data.RelicName(picked)} rolls nothing when you take it.";
        }

        relics.Changed += ShowGives;
        ShowGives();

        return Tab("One of Neow's three offers. Red frames are the cursed offers.",
            [],
            [relics.Root, givesTitle, gives.Root],
            [],
            () => relics.Selected is not { } relic
                ? null
                : gives.Selected is { } item
                    ? new NeowGivesCond(relic, (GiveItem)(item / stride), item % stride)
                    : new NeowOfferCond(relic));
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

        return Tab("Which act a slot rolls, and its boss and ancient.", [kind.Root, slot.Root], [grid.Root], [], () =>
            grid.Selected is not { } id
                ? null
                : kind.Value switch
                {
                    ancient => new AncientCond(slot.Value, id),
                    act => new ActCond(slot.Value, id),
                    secondBoss => new SecondBossCond(id),
                    _ => new BossCond(slot.Value, id)
                });
    }

    private Control EventTab()
    {
        Choice slot = SlotChoice();
        IconGrid grid = new(_hover);
        void Refill() => grid.SetItems(SlotItems(slot.Value, a => a.Events).Select(e => (e, Visuals.Event(_data, e))));
        slot.Changed += Refill;
        Refill();
        SpinBox within = Visuals.Spin(1, 20, 3);
        return Tab("Events are queued per act; each unknown room that rolls an event takes the next one.",
            [slot.Root], [grid.Root], [new Label { Text = "within the first" }, within],
            () => grid.Selected is { } e ? new EventCond(slot.Value, e, (int)within.Value) : null);
    }

    private Control FightTab()
    {
        Choice slot = SlotChoice();
        Choice kind = new([(0, "Fight"), (1, "Elite")]);
        IconGrid grid = new(_hover);
        void Refill() => grid.SetItems(kind.Value == 1
            ? SlotItems(slot.Value, a => a.Elite).Select(e => (e, Visuals.Encounter(_data, e, RoomType.Elite)))
            : SlotItems(slot.Value, a => a.Weak.Concat(a.Regular)).Select(e => (e, Visuals.Encounter(_data, e, RoomType.Monster))));
        slot.Changed += Refill;
        kind.Changed += Refill;
        Refill();
        SpinBox within = Visuals.Spin(1, 15, 1);
        return Tab("Fights and elites are queued per act in the order you will meet them.",
            [kind.Root, slot.Root], [grid.Root], [new Label { Text = "within the first" }, within],
            () => grid.Selected is { } e ? new EncounterCond(slot.Value, kind.Value == 1, e, (int)within.Value) : null);
    }

    private Control RelicTab()
    {
        Choice bag = new([(0, "Your bag"), (1, "Shared bag")]);
        IconGrid grid = new(_hover);
        void Refill() => grid.SetItems((bag.Value == 1 ? _data.SharedBag : _data.PlayerBag)
            .OrderBy(r => _data.Relics[r].Rarity)
            .Select(r => (r, Visuals.Relic(_data, r))));
        bag.Changed += Refill;
        Refill();
        SpinBox within = Visuals.Spin(1, 30, 3);
        return Tab("Relic rewards are dealt from a shuffled bag per rarity. Frames show the rarity.",
            [bag.Root], [grid.Root], [new Label { Text = "within the first" }, within, new Label { Text = "of its rarity" }],
            () => grid.Selected is { } r ? new RelicCond(bag.Value == 1, r, (int)within.Value) : null);
    }

    private Control MapTab()
    {
        Choice slot = SlotChoice();
        IconGrid grid = new(_hover);
        grid.SetItems(Enumerable.Range(0, Visuals.MapTypes.Length).Select(i => (i, Visuals.MapType(i))));
        SpinBox min = Visuals.Spin(0, 40, 0);
        SpinBox max = Visuals.Spin(0, 40, 4);
        return Tab("Counts over the whole act map. Map filters are the slowest to search.",
            [slot.Root], [grid.Root], [new Label { Text = "from" }, min, new Label { Text = "to" }, max], () =>
            {
                if (grid.Selected is not { } i)
                    return null;
                (int type, _, string name, _) = Visuals.MapTypes[i];
                int lo = (int)Math.Min(min.Value, max.Value), hi = (int)Math.Max(min.Value, max.Value);
                return new MapCountCond(slot.Value, type, name, lo, hi);
            });
    }

    private Control RewardTab()
    {
        IconGrid grid = new(_hover);
        grid.SetItems(_data.RewardCards.OrderBy(c => _data.Cards[c].Rarity).Select(c => (c, Visuals.Card(_data, c))));
        Button potion = new() { Text = "Add: drops a potion" };
        potion.Pressed += () => Add(new RewardPotionCond(true));
        Button noPotion = new() { Text = "Add: drops no potion" };
        noPotion.Pressed += () => Add(new RewardPotionCond(false));
        return Tab("The reward of the first fight. Holds if the first room is a normal fight and your Neow pick gave no cards, relics or potions.",
            [potion, noPotion], [grid.Root], [new Label { Text = "Card reward has the picked card" }],
            () => grid.Selected is { } c ? new RewardCardCond(c) : null);
    }

    // A hint, a row of options, the grids, and a bottom row ending in the Add button.
    private Control Tab(string hint, Control[] options, Control[] body, Control[] trailing, Func<Cond?> make)
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

        HBoxContainer bottom = new() { Alignment = BoxContainer.AlignmentMode.End };
        bottom.AddThemeConstantOverride("separation", 8);
        foreach (Control control in trailing)
            bottom.AddChild(control);
        Button add = new() { Text = "Add filter", CustomMinimumSize = new Vector2(150, 42) };
        add.Pressed += () => Add(make());
        bottom.AddChild(add);
        tab.AddChild(bottom);
        return tab;
    }

    private Choice SlotChoice() => new(Enumerable.Range(0, _data.SlotActs.Length).Select(s => (s, $"Act {s + 1}")));

    private IEnumerable<int> SlotItems(int slot, Func<ActInfo, IEnumerable<int>> pick) =>
        _data.SlotActs[slot].SelectMany(a => pick(_data.ActInfos[a])).Distinct();

    // A row of buttons of which exactly one is down.
    private sealed class Choice
    {
        private readonly ButtonGroup _group = new();

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
                Root.AddChild(button);
            }
        }
    }

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
