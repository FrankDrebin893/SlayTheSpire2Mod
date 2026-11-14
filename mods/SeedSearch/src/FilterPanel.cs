using Godot;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// The filters view of the seed search screen: a rail with one tab per kind of filter, in which the
// thing to filter on is picked from a grid of game icons. Clicking a tile in single-pick tabs adds
// the filter immediately in one click. The active filters live in Active (sidebar), with inline
// steppers for "within first N" / "min..max" and 1-click OR grouping.
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
    private readonly Button _orModeBtn = new()
    {
        Text = "+ Next as OR",
        ToggleMode = true,
        TooltipText = "When toggled on, the next filter you click will be combined with the last filter as an OR (Any Of) condition.",
        FocusMode = Control.FocusModeEnum.None
    };

    public HBoxContainer Root { get; } = new();

    public PanelContainer Active { get; } = Visuals.Panel(Visuals.PanelColor);

    public event Action? Changed;

    public FilterPanel(GameData data, List<Cond> conds, Action<Visual> hover, Action<string> notify)
    {
        _data = data;
        _conds = conds;
        _hover = hover;
        _notify = notify;

        Root.AddThemeConstantOverride("separation", 12);
        PanelContainer rail = Visuals.Panel(Visuals.PanelColor, 10);
        rail.CustomMinimumSize = new Vector2(195, 0);
        _tabBar.AddThemeConstantOverride("separation", 6);
        rail.AddChild(_tabBar);
        Root.AddChild(rail);
        _tabHost.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Root.AddChild(_tabHost);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 8);
        Active.AddChild(column);

        HBoxContainer title = new();
        title.AddThemeConstantOverride("separation", 6);
        Label heading = Visuals.SectionTitle("Active Filters", 22);
        heading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.AddChild(heading);
        _orModeBtn.AddThemeFontSizeOverride("font_size", 15);
        title.AddChild(_orModeBtn);
        Button save = new() { Text = "Save", TooltipText = "Keep these filters under the Saved tab", FocusMode = Control.FocusModeEnum.None };
        save.AddThemeFontSizeOverride("font_size", 15);
        save.Pressed += SaveSearch;
        title.AddChild(save);
        Button clear = new() { Text = "Clear", TooltipText = "Remove all active filters", FocusMode = Control.FocusModeEnum.None };
        clear.AddThemeFontSizeOverride("font_size", 15);
        clear.Pressed += () =>
        {
            if (_conds.Count == 0)
                return;
            _conds.Clear();
            RefreshChips();
            _notify("Cleared all filters.");
        };
        title.AddChild(clear);
        column.AddChild(title);

        ScrollContainer chipScroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _chips.AddThemeConstantOverride("separation", 6);
        chipScroll.AddChild(_chips);
        column.AddChild(chipScroll);

        if (_data.NeowRelics.Length > 0)
            AddTab(Visuals.Relic(_data, _data.NeowRelics[0]) with { Name = "Neow (Act 1)" }, NeowTab);
        AddTab(Visuals.Named("Ancients (Act 2/3)", Visuals.Load("res://images/ui/run_history/event.png")), AncientTab);
        AddTab(Visuals.Named("Chests & Shop", Visuals.Load("res://images/ui/run_history/shop.png")), ShopAndChestTab);
        AddTab(Visuals.Named("First Reward", Visuals.Load("res://images/ui/run_history/monster.png")), RewardTab);
        AddTab(Visuals.Named("Acts & Bosses", Visuals.Load("res://images/ui/run_history/elite.png")), ActTab);
        AddTab(Visuals.Named("Events", Visuals.Load("res://images/ui/run_history/event.png")), EventTab);
        AddTab(Visuals.Named("Fights & Elites", Visuals.Load("res://images/ui/run_history/monster.png")), FightTab);
        AddTab(Visuals.Named("Relic Bags", Visuals.Load("res://images/ui/run_history/treasure.png")), RelicTab);
        AddTab(Visuals.Named("Map & Paths", Visuals.Load("res://images/ui/run_history/rest_site.png")), MapTab);
        AddTab(Visuals.Named("Saved Searches"), SavedTab);
        RefreshChips();
    }

    // ---- active filters ----

    public void RefreshChips()
    {
        Visuals.Clear(_chips);
        if (_conds.Count == 0)
        {
            _chips.AddChild(Visuals.MutedLabel(
                "No filters yet. Click any item in the picker on the left to add it in 1 click.\n" +
                "• Use 'OR↑' or '+ Next as OR' to match ANY OF several options.\n" +
                "• Use '− / +' on a filter to adjust how early it must appear.", 15));
            Changed?.Invoke();
            return;
        }

        for (int i = 0; i < _conds.Count; i++)
        {
            int index = i;
            Cond cond = _conds[i];
            Visual visual = VisualOf(cond);

            HBoxContainer row = new();
            row.AddThemeConstantOverride("separation", 4);

            Button chip = Visuals.Pill(visual with { Name = "Click to remove this filter" }, _ => _hover(visual), cond.Describe(_data));
            chip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            chip.Alignment = HorizontalAlignment.Left;
            chip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            chip.Pressed += () =>
            {
                _conds.RemoveAt(index);
                RefreshChips();
            };
            row.AddChild(chip);

            if (cond.WithinStepper is { } ws)
            {
                Button minus = MiniButton("−", "Require earlier in the run", ws.Value <= 1);
                minus.Pressed += () => ReplaceAt(index, ws.With(Math.Max(1, ws.Value - 1)));
                Button plus = MiniButton("+", "Allow later in the run", ws.Value >= ws.Max);
                plus.Pressed += () => ReplaceAt(index, ws.With(Math.Min(ws.Max, ws.Value + 1)));
                row.AddChild(minus);
                row.AddChild(plus);
            }
            else if (cond.RangeStepper is { } rs)
            {
                Button minus = MiniButton("−", "Lower minimum count", rs.Min <= 0);
                minus.Pressed += () => ReplaceAt(index, rs.With(Math.Max(0, rs.Min - 1), rs.Max));
                Button plus = MiniButton("+", "Raise minimum count", rs.Min >= rs.Limit);
                plus.Pressed += () =>
                {
                    int nextMin = Math.Min(rs.Limit, rs.Min + 1);
                    ReplaceAt(index, rs.With(nextMin, Math.Max(nextMin, rs.Max)));
                };
                row.AddChild(minus);
                row.AddChild(plus);
            }

            if (cond is AnyOfCond any)
            {
                Button split = MiniButton("Split", "Unpack this OR group into separate filters");
                split.Pressed += () =>
                {
                    _conds.RemoveAt(index);
                    int insertAt = index;
                    foreach (Cond sub in any.Conds)
                    {
                        if (Feasibility.Conflict(_data, _conds, sub) == null)
                            _conds.Insert(insertAt++, sub);
                    }

                    RefreshChips();
                };
                row.AddChild(split);
            }

            if (index > 0)
            {
                Button mergeOr = MiniButton("OR↑", "Combine with the filter above as an OR (Any Of) group");
                mergeOr.Pressed += () => MergeAsOr(index - 1, index);
                row.AddChild(mergeOr);
            }

            Button remove = MiniButton("✕", "Remove this filter");
            remove.Pressed += () =>
            {
                _conds.RemoveAt(index);
                RefreshChips();
            };
            row.AddChild(remove);

            _chips.AddChild(row);
        }

        Changed?.Invoke();
    }

    private static Button MiniButton(string text, string tooltip, bool disabled = false)
    {
        Button b = new()
        {
            Text = text,
            TooltipText = tooltip,
            Disabled = disabled,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        b.AddThemeFontSizeOverride("font_size", 14);
        return b;
    }

    private void ReplaceAt(int index, Cond replacement)
    {
        List<Cond> others = _conds.Where((_, i) => i != index).ToList();
        if (Feasibility.Conflict(_data, others, replacement) is { } reason)
        {
            _notify($"Cannot adjust: {reason}");
            return;
        }

        _conds[index] = replacement;
        RefreshChips();
    }

    private void MergeAsOr(int firstIdx, int secondIdx)
    {
        Cond a = _conds[firstIdx];
        Cond b = _conds[secondIdx];
        Cond[] combined = (a is AnyOfCond anyA ? anyA.Conds : [a])
            .Concat(b is AnyOfCond anyB ? anyB.Conds : [b])
            .Distinct()
            .ToArray();
        _conds.RemoveAt(secondIdx);
        _conds[firstIdx] = combined.Length == 1 ? combined[0] : new AnyOfCond(combined);
        RefreshChips();
        _notify("Combined into an OR (Any Of) filter.");
    }

    private void Add(Cond? cond)
    {
        if (cond == null)
            return;

        if (_orModeBtn.ButtonPressed && _conds.Count > 0)
        {
            _orModeBtn.ButtonPressed = false;
            Cond prev = _conds[^1];
            Cond[] combined = (prev is AnyOfCond anyP ? anyP.Conds : [prev])
                .Concat(cond is AnyOfCond anyC ? anyC.Conds : [cond])
                .Distinct()
                .ToArray();
            _conds[^1] = combined.Length == 1 ? combined[0] : new AnyOfCond(combined);
            RefreshChips();
            _notify($"Added to OR group: {cond.Describe(_data)}");
            return;
        }

        if (_conds.Contains(cond))
        {
            _notify($"Already in active filters: {cond.Describe(_data)}");
            return;
        }

        if (Feasibility.Conflict(_data, _conds, cond) is { } reason)
        {
            _notify($"Not added ({reason}) — Tip: toggle '+ Next as OR' to add it as an alternative.");
            return;
        }

        _conds.Add(cond);
        RefreshChips();
        _notify($"Added: {cond.Describe(_data)}");
    }

    private void SaveSearch()
    {
        if (_conds.Count == 0)
        {
            _notify("Add at least one filter first.");
            return;
        }

        SavedData.RecordSearch(_data, _conds, keep: true);
        _notify("Search saved under the Saved Searches tab.");
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
        AncientRelicCond c => Visuals.Relic(_data, c.Relic),
        EventCond c => Visuals.Event(_data, c.Event),
        EncounterCond c => Visuals.Encounter(_data, c.Encounter, c.Elite ? RoomType.Elite : RoomType.Monster),
        RelicCond c => Visuals.Relic(_data, c.Relic),
        TreasureRelicCond c => Visuals.Relic(_data, c.Relic),
        ShopItemCond c => Given(c.What, c.Item),
        MapCountCond c => Visuals.MapType(Math.Max(0, Array.FindIndex(Visuals.MapTypes, t => t.Type == c.PointType))),
        MapPathCond c => Visuals.MapType(Math.Max(0, Array.FindIndex(Visuals.MapTypes, t => t.Type == c.PointType))),
        RewardCardCond c => Visuals.Card(_data, c.Card),
        AnyOfCond { Conds.Length: > 0 } c => VisualOf(c.Conds[0]),
        _ => Visuals.Named(cond.Describe(_data))
    };

    private Visual Given(GiveItem what, int item) => what switch
    {
        GiveItem.Card => Visuals.Card(_data, item),
        GiveItem.Relic => Visuals.Relic(_data, item),
        _ => Visuals.Potion(_data, item)
    };

    // ---- tabs ----

    private void AddTab(Visual visual, Func<Control> build)
    {
        Button tab = Visuals.Pill(visual, _ => { });
        tab.Alignment = HorizontalAlignment.Left;
        tab.CustomMinimumSize = new Vector2(0, 42);
        tab.AddThemeFontSizeOverride("font_size", 17);
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
        IconGrid nested = new(_hover, optional: true);

        Button quickAddOffer = new() { Text = "+ Add Neow offer (any outcome)", Disabled = true, FocusMode = Control.FocusModeEnum.None };
        Button quickAddGives = new() { Text = "+ Add Neow's Bones relic (any outcome)", Visible = false, FocusMode = Control.FocusModeEnum.None };

        (VBoxContainer Box, Label Note) Column(string title, Control? topAction, IconGrid grid)
        {
            VBoxContainer box = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 6);
            box.AddChild(new Label { Text = title });
            Label note = Visuals.MutedLabel("", 14);
            box.AddChild(note);
            if (topAction != null)
                box.AddChild(topAction);
            box.AddChild(grid.Root);
            return (box, note);
        }

        (VBoxContainer offerColumn, Label offerNote) = Column("1. Pick a Neow relic", null, relics);
        (VBoxContainer givesColumn, Label givesNote) = Column("2. Click what it must give (or click button below)", quickAddOffer, gives);
        (VBoxContainer nestedColumn, Label nestedNote) = Column("3. Click what that relic must give", quickAddGives, nested);
        HBoxContainer columns = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        foreach (Control column in new[] { offerColumn, givesColumn, nestedColumn })
            columns.AddChild(column);

        const int stride = 1_000_000;
        IEnumerable<(string, IEnumerable<(int, Visual)>)> Mixed((int[] Cards, int[] Relics, int[] Potions) options) =>
            new[]
            {
                ("Relics", options.Relics.Select(g => ((int)GiveItem.Relic * stride + g, Visuals.Relic(_data, g)))),
                ("Potions", options.Potions.Select(p => ((int)GiveItem.Potion * stride + p, Visuals.Potion(_data, p))))
            }.Concat(CardGroups(options.Cards, (int)GiveItem.Card * stride));

        quickAddOffer.Pressed += () =>
        {
            if (relics.Selected is { } r)
                Add(new NeowOfferCond(r));
        };

        quickAddGives.Pressed += () =>
        {
            if (relics.Selected is { } r && gives.Selected is { } given)
                Add(new NeowGivesCond(r, (GiveItem)(given / stride), given % stride));
        };

        void ShowNested()
        {
            (int[] Cards, int[] Relics, int[] Potions) options = ([], [], []);
            int? via = gives.Selected is { } given && given / stride == (int)GiveItem.Relic ? given % stride : null;
            if (via is { } v)
                options = _data.NeowGiveOptions(v);
            nested.SetGroups(Mixed(options));
            bool any = options.Cards.Length + options.Relics.Length + options.Potions.Length > 0;
            nestedColumn.Visible = any;
            quickAddGives.Visible = any && via != null;
            if (via is { } vId)
                quickAddGives.Text = $"+ Require {_data.RelicName(vId)} (any outcome)";
            nestedNote.Text = any ? $"Click an item {_data.RelicName(via!.Value)} gives to add it immediately:" : "";
        }

        void ShowGives()
        {
            (int[] Cards, int[] Relics, int[] Potions) options = ([], [], []);
            if (relics.Selected is { } r)
                options = _data.NeowGiveOptions(r);
            gives.SetGroups(Mixed(options));
            bool any = options.Cards.Length + options.Relics.Length + options.Potions.Length > 0;
            givesColumn.Visible = any;
            quickAddOffer.Disabled = relics.Selected == null;
            if (relics.Selected is { } selRelic)
            {
                quickAddOffer.Text = $"+ Just require Neow offers {_data.RelicName(selRelic)}";
                if (!any)
                {
                    // Relic rolls nothing: 1-click adds it immediately!
                    Add(new NeowOfferCond(selRelic));
                }
            }

            givesNote.Text = any ? $"Click any outcome below to add in 1 click, or click the button above for any {_data.RelicName(relics.Selected!.Value)}:" : "";
            offerNote.Text = "Relics without random rolls are added in 1 click. Red frames are cursed offers.";
            ShowNested();
        }

        relics.Changed += ShowGives;
        gives.Changed += () =>
        {
            ShowNested();
            if (relics.Selected is { } r && gives.Selected is { } item && !nestedColumn.Visible)
            {
                Add(new NeowGivesCond(r, (GiveItem)(item / stride), item % stride));
            }
        };
        nested.Changed += () =>
        {
            if (relics.Selected is { } r && gives.Selected is { } item && nested.Selected is { } inner)
            {
                Add(new NeowNestedCond(r, item % stride, (GiveItem)(inner / stride), inner % stride));
            }
        };
        ShowGives();

        return SimpleTab(
            "Click a Neow relic. Relics without random rolls add in 1 click; relics that roll cards/relics/potions let you pick their outcome.",
            [],
            [columns]);
    }

    private Control AncientTab()
    {
        Choice slot = new([(1, "Act 2"), (2, "Act 3")]);
        Choice mode = new([(0, "Ancient Relic Offering"), (1, "Which Ancient Appears")]);
        IconGrid grid = new(_hover, onClick: id =>
        {
            if (mode.Value == 1)
                Add(new AncientCond(slot.Value, id));
            else
                Add(new AncientRelicCond(slot.Value, id));
        });

        void Refill()
        {
            int s = slot.Value;
            if (mode.Value == 1)
            {
                int[] ancients = SlotItems(s, a => a.Ancients).Concat(_data.SharedAncients).Distinct().ToArray();
                grid.SetItems(ancients.Select(a => (a, Visuals.Ancient(_data, a))));
            }
            else
            {
                int[] ancients = SlotItems(s, a => a.Ancients).Concat(_data.SharedAncients).Distinct().ToArray();
                var groups = ancients
                    .Select(a => (
                        Heading: $"{_data.AncientName(a)} relics",
                        Items: _data.AncientRelicOptions(s, a)
                            .OrderBy(_data.RelicName, StringComparer.CurrentCultureIgnoreCase)
                            .Select(r => (r, Visuals.Relic(_data, r)))))
                    .Where(g => g.Items.Any());
                grid.SetGroups(groups);
            }
        }

        slot.Changed += Refill;
        mode.Changed += Refill;
        Refill();

        return SimpleTab(
            "Click any Ancient or Ancient relic below to require it in Act 2 or Act 3 (1 click). Note: Selecting a specific Ancient relic checks the Ancient rolled for that Act.",
            [slot.Root, mode.Root],
            [grid.Root]);
    }

    private Control ShopAndChestTab()
    {
        const int shopCards = 0, shopRelics = 1, shopPotions = 2, chests = 3;
        Choice mode = new([
            (shopCards, "First Shop: Cards"),
            (shopRelics, "First Shop: Relics"),
            (shopPotions, "First Shop: Potions"),
            (chests, "Treasure Chest Relics")
        ]);
        SpinBox within = Visuals.Spin(1, 6, 2);
        HBoxContainer chestOpts = new();
        chestOpts.AddThemeConstantOverride("separation", 6);
        chestOpts.AddChild(new Label { Text = "within first" });
        chestOpts.AddChild(within);
        chestOpts.AddChild(new Label { Text = "chests" });

        IconGrid grid = new(_hover, onClick: id =>
        {
            switch (mode.Value)
            {
                case shopCards:
                    Add(new ShopItemCond(GiveItem.Card, id));
                    break;
                case shopRelics:
                    Add(new ShopItemCond(GiveItem.Relic, id));
                    break;
                case shopPotions:
                    Add(new ShopItemCond(GiveItem.Potion, id));
                    break;
                case chests:
                    Add(new TreasureRelicCond(id, (int)within.Value));
                    break;
            }
        });

        void Refill()
        {
            chestOpts.Visible = mode.Value == chests;
            switch (mode.Value)
            {
                case shopCards:
                    grid.SetGroups(CardGroups(_data.ShopCardOptions));
                    break;
                case shopRelics:
                    grid.SetGroups(_data.ShopRelicOptions
                        .GroupBy(r => _data.Relics[r].Rarity)
                        .OrderBy(g => g.Key)
                        .Select(g => ($"{g.Key} relics", g.OrderBy(_data.RelicName, StringComparer.CurrentCultureIgnoreCase).Select(r => (r, Visuals.Relic(_data, r))))));
                    break;
                case shopPotions:
                    grid.SetGroups(_data.PotionOptions
                        .GroupBy(p => _data.Potions[p].Rarity)
                        .OrderBy(g => g.Key)
                        .Select(g => ($"{g.Key} potions", g.OrderBy(_data.PotionName, StringComparer.CurrentCultureIgnoreCase).Select(p => (p, Visuals.Potion(_data, p))))));
                    break;
                case chests:
                    grid.SetGroups(_data.TreasureRelicOptions
                        .GroupBy(r => _data.Relics[r].Rarity)
                        .OrderBy(g => g.Key)
                        .Select(g => ($"{g.Key} chest relics", g.OrderBy(_data.RelicName, StringComparer.CurrentCultureIgnoreCase).Select(r => (r, Visuals.Relic(_data, r))))));
                    break;
            }
        }

        mode.Changed += Refill;
        Refill();

        return SimpleTab(
            "Click any item to require it in the First Merchant Shop (5 character cards, 2 colorless cards, 3 relics, 3 potions) or in the first N Treasure Chests.",
            [mode.Root, chestOpts],
            [grid.Root]);
    }

    private Control ActTab()
    {
        const int boss = 0, act = 1, secondBoss = 2;
        Choice slot = SlotChoice();
        List<(int, string)> kinds = [(boss, "Act Boss"), (act, "Act Biome")];
        if (_data.DoubleBoss)
            kinds.Add((secondBoss, "Second Final Boss (A10)"));
        Choice kind = new(kinds);
        IconGrid grid = new(_hover, onClick: id =>
        {
            Cond c = kind.Value switch
            {
                act => new ActCond(slot.Value, id),
                secondBoss => new SecondBossCond(id),
                _ => new BossCond(slot.Value, id)
            };
            Add(c);
        });

        void Refill()
        {
            int s = kind.Value == secondBoss ? _data.SlotActs.Length - 1 : slot.Value;
            slot.Root.Visible = kind.Value != secondBoss;
            grid.SetItems(kind.Value switch
            {
                act => _data.SlotActs[s].Select(a => (a, Visuals.Named(_data.ActName(a)))),
                _ => SlotItems(s, a => a.Boss).Select(e => (e, Visuals.Encounter(_data, e, RoomType.Boss)))
            });
        }

        slot.Changed += Refill;
        kind.Changed += Refill;
        Refill();

        return SimpleTab(
            "Click any Boss or Act below to add it in 1 click.",
            [kind.Root, slot.Root],
            [grid.Root]);
    }

    private Control EventTab()
    {
        Choice slot = SlotChoice();
        SpinBox within = Visuals.Spin(1, 15, 3);
        IconGrid grid = new(_hover, onClick: e => Add(new EventCond(slot.Value, e, (int)within.Value)));
        void Refill() => grid.SetItems(SlotItems(slot.Value, a => a.Events).Select(e => (e, Visuals.Event(_data, e))));
        slot.Changed += Refill;
        Refill();

        HBoxContainer withinBox = new();
        withinBox.AddThemeConstantOverride("separation", 6);
        withinBox.AddChild(new Label { Text = "within first" });
        withinBox.AddChild(within);
        withinBox.AddChild(new Label { Text = "events" });

        return SimpleTab(
            "Click any Event below to require it among the first N queued events of the chosen Act.",
            [slot.Root, withinBox],
            [grid.Root]);
    }

    private Control FightTab()
    {
        Choice slot = SlotChoice();
        Choice kind = new([(0, "Normal Fight"), (1, "Elite Fight")]);
        SpinBox within = Visuals.Spin(1, 15, 1);
        IconGrid grid = new(_hover, onClick: e => Add(new EncounterCond(slot.Value, kind.Value == 1, e, (int)within.Value)));
        void Refill() => grid.SetItems(_data.FightOptions(slot.Value, kind.Value == 1, (int)within.Value)
            .Select(e => (e, Visuals.Encounter(_data, e, kind.Value == 1 ? RoomType.Elite : RoomType.Monster))));
        slot.Changed += Refill;
        kind.Changed += Refill;
        within.ValueChanged += _ => Refill();
        Refill();

        HBoxContainer withinBox = new();
        withinBox.AddThemeConstantOverride("separation", 6);
        withinBox.AddChild(new Label { Text = "within first" });
        withinBox.AddChild(within);
        withinBox.AddChild(new Label { Text = "fights" });

        return SimpleTab(
            "Click any Fight or Elite to require it among the first N encounters of the Act. (Raise 'within first' above 3 to include hard pool normal fights.)",
            [kind.Root, slot.Root, withinBox],
            [grid.Root]);
    }

    private Control RelicTab()
    {
        Choice bag = new([(0, "Your Relic Bag"), (1, "Shared Relic Bag")]);
        SpinBox within = Visuals.Spin(1, 15, 3);
        IconGrid grid = new(_hover, onClick: r => Add(new RelicCond(bag.Value == 1, r, (int)within.Value)));
        void Refill() => grid.SetGroups((bag.Value == 1 ? _data.SharedBagDealt : _data.PlayerBag)
            .GroupBy(r => _data.Relics[r].Rarity)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.ToString(), g.OrderBy(_data.RelicName, StringComparer.CurrentCultureIgnoreCase).Select(r => (r, Visuals.Relic(_data, r))))));
        bag.Changed += Refill;
        Refill();

        HBoxContainer withinBox = new();
        withinBox.AddThemeConstantOverride("separation", 6);
        withinBox.AddChild(new Label { Text = "within first" });
        withinBox.AddChild(within);
        withinBox.AddChild(new Label { Text = "of its rarity" });

        return SimpleTab(
            "Click any Relic below to require it near the front of its rarity queue in Your Bag or the Shared Bag.",
            [bag.Root, withinBox],
            [grid.Root]);
    }

    private Control MapTab()
    {
        Choice slot = SlotChoice();
        Choice mode = new([(0, "Best Single Path (Reachable)"), (1, "Whole Map Total")]);
        SpinBox min = Visuals.Spin(0, 25, 3);
        SpinBox max = Visuals.Spin(0, 25, 10);

        IconGrid grid = new(_hover, onClick: i =>
        {
            (int type, _, string name, _) = Visuals.MapTypes[i];
            int lo = (int)Math.Min(min.Value, max.Value), hi = (int)Math.Max(min.Value, max.Value);
            if (mode.Value == 0)
                Add(new MapPathCond(slot.Value, type, name, lo, hi));
            else
                Add(new MapCountCond(slot.Value, type, name, lo, hi));
        });

        void Refill()
        {
            // For single-path DP, the first 4 types (Elites, Rests, Shops, Unknowns) are supported.
            int count = mode.Value == 0 ? 4 : Visuals.MapTypes.Length;
            grid.SetItems(Enumerable.Range(0, count).Select(i => (i, Visuals.MapType(i))));
        }

        mode.Changed += Refill;
        Refill();

        HBoxContainer rangeBox = new();
        rangeBox.AddThemeConstantOverride("separation", 6);
        rangeBox.AddChild(new Label { Text = "from" });
        rangeBox.AddChild(min);
        rangeBox.AddChild(new Label { Text = "to" });
        rangeBox.AddChild(max);

        HFlowContainer presets = Visuals.Flow(6);
        presets.AddChild(Visuals.MutedLabel("Quick presets:", 15));
        (string Label, Func<Cond> Make)[] quickPresets =
        [
            ("Path ≥ 3 Elites", () => new MapPathCond(slot.Value, GameData.MapElite, "elites", 3, 10)),
            ("Path ≥ 4 Elites", () => new MapPathCond(slot.Value, GameData.MapElite, "elites", 4, 10)),
            ("Path ≥ 3 Rests", () => new MapPathCond(slot.Value, GameData.MapRest, "rests", 3, 10)),
            ("Path ≥ 4 Rests", () => new MapPathCond(slot.Value, GameData.MapRest, "rests", 4, 10)),
            ("Path ≥ 2 Shops", () => new MapPathCond(slot.Value, GameData.MapShop, "shops", 2, 10))
        ];
        foreach (var qp in quickPresets)
        {
            Button b = new() { Text = qp.Label, FocusMode = Control.FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 15);
            b.Pressed += () => Add(qp.Make());
            presets.AddChild(b);
        }

        return SimpleTab(
            "Filter by the maximum number of Elites, Rest Sites, Shops, or Unknowns reachable on a single legal path through the Act (or total across the whole map). Click a room type or preset to add.",
            [slot.Root, mode.Root, rangeBox],
            [presets, grid.Root]);
    }

    private Control RewardTab()
    {
        OptionButton neowTiming = new() { FocusMode = Control.FocusModeEnum.None };
        List<int?> timingRelics = [null];
        neowTiming.AddItem("Baseline (Neow pick didn't roll rewards)");
        foreach (int r in _data.NeowRelics)
        {
            if (GameData.NeowGiveOf(_data.Relics[r]) is { Kind: "arcane_scroll" or "hefty_tablet" or "lead_paperweight" or "lost_coffer" or "scroll_boxes" or "small_capsule" or "large_capsule" or "kaleidoscope" or "neows_bones" })
            {
                timingRelics.Add(r);
                neowTiming.AddItem($"After taking {_data.RelicName(r)}");
            }
        }

        IconGrid grid = new(_hover, onClick: c =>
        {
            int? after = timingRelics[Math.Clamp(neowTiming.Selected, 0, timingRelics.Count - 1)];
            Add(new RewardCardCond(c, after));
        });

        void Refill()
        {
            int? after = timingRelics[Math.Clamp(neowTiming.Selected, 0, timingRelics.Count - 1)];
            grid.SetGroups(CardGroups(after.HasValue ? _data.RewardCards : _data.FirstRewardCards));
        }

        neowTiming.ItemSelected += _ => Refill();
        Refill();

        Button potion = new() { Text = "+ Drops a potion", FocusMode = Control.FocusModeEnum.None };
        potion.Pressed += () => Add(new RewardPotionCond(true));
        Button noPotion = new() { Text = "+ Drops no potion", FocusMode = Control.FocusModeEnum.None };
        noPotion.Pressed += () => Add(new RewardPotionCond(false));

        return SimpleTab(
            "Click any Card below to require it in the Floor 1 combat reward. You can require it either on the baseline reward stream or specifically after taking a reward-rolling Neow relic!",
            [new Label { Text = "When:" }, neowTiming, potion, noPotion],
            [grid.Root]);
    }

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
                rows.AddChild(Visuals.MutedLabel("Nothing yet. Run a search, or press Save above."));
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

    private static Control SimpleTab(string hint, Control[] options, Control[] body)
    {
        VBoxContainer tab = new() { Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        tab.AddThemeConstantOverride("separation", 8);
        tab.AddChild(Visuals.MutedLabel(hint, 15));
        if (options.Length > 0)
        {
            HFlowContainer row = Visuals.Flow(12);
            foreach (Control option in options)
                row.AddChild(option);
            tab.AddChild(row);
        }

        foreach (Control part in body)
            tab.AddChild(part);
        return tab;
    }

    private Choice SlotChoice() => new(Enumerable.Range(0, _data.SlotActs.Length).Select(s => (s, $"Act {s + 1}")));

    private IEnumerable<int> SlotItems(int slot, Func<ActInfo, IEnumerable<int>> pick) =>
        _data.SlotActs[slot].SelectMany(a => pick(_data.ActInfos[a])).Distinct();

    private IEnumerable<(string Heading, IEnumerable<(int Id, Visual Visual)> Items)> CardGroups(IEnumerable<int> cards, int offset = 0) =>
        cards.GroupBy(_data.CardOwner)
            .SelectMany(owner => owner
                .GroupBy(c => _data.Cards[c].Rarity)
                .OrderBy(g => g.Key)
                .Select(g => (owner.Key == null ? $"{g.Key} cards" : $"{owner.Key}  ·  {g.Key}",
                    g.OrderBy(_data.CardName, StringComparer.CurrentCultureIgnoreCase).Select(c => (offset + c, Visuals.Card(_data, c))))));

    // A scrolling grid of tiles. When onClick is provided, clicking a tile triggers onClick(id)
    // immediately (1-click add). Otherwise, tiles act as a radio selection group.
    private sealed class IconGrid
    {
        private readonly Action<Visual> _hover;
        private readonly bool _optional;
        private readonly Action<int>? _onClick;
        private readonly LineEdit _search = new() { PlaceholderText = "Search by name...", ClearButtonEnabled = true };
        private readonly VBoxContainer _groups = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        private readonly List<(Label? Heading, List<(Button Tile, string Name)> Tiles)> _shown = [];
        private ButtonGroup _group = new();

        public VBoxContainer Root { get; } = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        public int? Selected { get; private set; }

        public event Action? Changed;

        public IconGrid(Action<Visual> hover, bool optional = false, Action<int>? onClick = null)
        {
            _hover = hover;
            _optional = optional;
            _onClick = onClick;
            _search.TextChanged += _ => Narrow();
            Root.AddChild(_search);
            ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _groups.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(_groups);
            Root.AddChild(scroll);
        }

        public void SetItems(IEnumerable<(int Id, Visual Visual)> items) => SetGroups([("", items)]);

        public void SetGroups(IEnumerable<(string Heading, IEnumerable<(int Id, Visual Visual)> Items)> groups)
        {
            Visuals.Clear(_groups);
            _shown.Clear();
            _group = new ButtonGroup { AllowUnpress = _optional };
            bool hadSelection = Selected != null;
            Selected = null;
            HashSet<int> seen = [];

            foreach ((string heading, IEnumerable<(int Id, Visual Visual)> items) in groups)
            {
                List<(Button Tile, string Name)> tiles = [];
                HFlowContainer flow = Visuals.Flow();
                foreach ((int id, Visual visual) in items.Where(item => seen.Add(item.Id)))
                {
                    Button tile = Visuals.Tile(visual, _hover);
                    if (_onClick != null)
                    {
                        tile.Pressed += () => _onClick(id);
                    }
                    else
                    {
                        tile.ToggleMode = true;
                        tile.ButtonGroup = _group;
                        tile.Toggled += on =>
                        {
                            Selected = on ? id : Selected == id ? null : Selected;
                            Changed?.Invoke();
                        };
                    }

                    flow.AddChild(tile);
                    tiles.Add((tile, visual.Name));
                }

                if (tiles.Count == 0)
                {
                    flow.QueueFree();
                    continue;
                }

                Label? label = heading.Length == 0 ? null : Visuals.SectionTitle($"{heading}  ·  {tiles.Count}", 18);
                if (label != null)
                    _groups.AddChild(label);
                _groups.AddChild(flow);
                _shown.Add((label, tiles));
            }

            _search.Visible = _shown.Sum(g => g.Tiles.Count) > 12;
            Narrow();
            if (hadSelection)
                Changed?.Invoke();
        }

        private void Narrow()
        {
            string filter = _search.Visible ? _search.Text.Trim() : "";
            foreach ((Label? heading, List<(Button Tile, string Name)> tiles) in _shown)
            {
                foreach ((Button tile, string name) in tiles)
                    tile.Visible = filter.Length == 0 || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
                if (heading != null)
                    heading.Visible = tiles.Any(t => t.Tile.Visible);
            }
        }
    }
}
