using System.Reflection;
using Godot;
using HarmonyLib;
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
// The filters are built in FilterPanel and a seed is shown by PreviewPanel; Visuals holds the
// look and the game art both use.
public sealed class SeedSearchScreen
{
    private const int MaxResults = 100;

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
    private readonly InfoPanel _info = new();
    private readonly PreviewPanel _preview;
    private readonly Button _searchButton = new();
    private readonly Label _status = new();
    private readonly ItemList _results = new();
    private readonly LineEdit _seedBox = new();
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
        _preview = new PreviewPanel(engine.Data, _info.Show);
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
        _root.Theme = Visuals.MakeTheme(_screen.GetNodeOrNull<Control>("%SeedLabel")?.GetThemeFont("font"));
        _root.TreeExiting += StopSearch;

        ColorRect background = new() { Color = Visuals.Background, MouseFilter = Control.MouseFilterEnum.Ignore };
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

        VBoxContainer left = new();
        left.AddThemeConstantOverride("separation", 12);
        body.AddChild(left);
        FilterPanel filters = new(Data, Conds, _info.Show);
        filters.Root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        left.AddChild(filters.Root);
        left.AddChild(_info.Root);
        body.AddChild(BuildResultsPanel());

        _pollTimer.WaitTime = 0.1;
        _pollTimer.Timeout += PollSearch;
        _root.AddChild(_pollTimer);

        _layer.AddChild(_root);
        _screen.AddChild(_layer);
        DisableGameButtons();
        ShowSeed(_gameSeedInput.Text);
    }

    private Control BuildHeader()
    {
        HBoxContainer header = new();
        header.AddThemeConstantOverride("separation", 24);

        header.AddChild(Visuals.SectionTitle("Seed search", 38));
        if (Visuals.CharacterIcon(Data.Character) is { } portrait)
        {
            header.AddChild(new TextureRect
            {
                Texture = portrait,
                CustomMinimumSize = new Vector2(52, 52),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            });
        }

        string act1 = Data.SlotActs[0].Length == 1 ? Data.ActName(Data.SlotActs[0][0]) : "random";
        VBoxContainer context = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        context.AddChild(new Label { Text = $"{Data.Character.Title.GetFormattedText()}  ·  Ascension {Data.Ascension}  ·  Act 1: {act1}" });
        context.AddChild(Visuals.MutedLabel($"{_engine.Status}. Character, ascension and act 1 come from the Custom Run screen."));
        header.AddChild(context);

        Button back = new() { Text = "Back", CustomMinimumSize = new Vector2(140, 48), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        back.Shortcut = new Shortcut { Events = new Godot.Collections.Array { new InputEventKey { Keycode = Key.Escape }, new InputEventAction { Action = "ui_cancel" } } };
        back.Pressed += Close;
        header.AddChild(back);
        return header;
    }

    private Control BuildResultsPanel()
    {
        PanelContainer panel = Visuals.Panel(Visuals.PanelColor);
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
        left.AddChild(Visuals.MutedLabel("Matching seeds"));
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
        _preview.Root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        right.AddChild(_preview.Root);
        return panel;
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
        _preview.Show(_shown, seed.Length == 0 ? "Pick a seed from the list, or type one above to preview it." : "Could not preview this seed. See godot.log.");
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
}
