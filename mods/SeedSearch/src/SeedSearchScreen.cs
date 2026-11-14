using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Timer = Godot.Timer;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// The full-screen seed search opened from the Standard Run (Character Select) or Custom Run screen.
// Built from stock Godot nodes only: node subclasses defined in a mod assembly are not registered
// with the engine (so it cannot be an NSubmenu), and this class is a plain object that lives as
// long as the signal handlers on its nodes do.
public sealed class SeedSearchScreen
{
    private const int MaxResults = 100;
    private const int ResultsList = 0, BookmarksList = 1;
    private const int FiltersView = 0, ResultsView = 1;

    private static readonly FieldInfo SeedInputField = AccessTools.Field(typeof(NCustomRunScreen), "_seedInput");
    private static readonly string[] CustomButtonFields = ["_backButton", "_confirmButton", "_randomizeButton"];
    private static readonly string[] StandardButtonFields = ["_backButton", "_unreadyButton", "_embarkButton"];

    // Filters survive closing the screen for as long as they refer to the same game data.
    private static readonly List<Cond> Conds = [];
    private static GameData? _condsData;

    private readonly Control _hostScreen;
    private readonly StartRunLobby _lobby;
    private readonly string _initialSeed;
    private readonly Action<string> _applySeed;
    private readonly string[] _buttonFields;
    private readonly bool _isStandardRun;
    private readonly SeedEngine _engine;
    private readonly List<NClickableControl> _disabledGameButtons = [];

    private readonly CanvasLayer _layer = new() { Layer = 20 };
    private readonly Control _root = new();
    private readonly InfoPanel _info = new();
    private readonly PreviewPanel _preview;
    private readonly Button _searchButton = new();
    private readonly Label _status = Visuals.MutedLabel("Click any item to add a filter in 1 click, then press Search.");
    private readonly Choice _viewChoice = new([(FiltersView, "Filters"), (ResultsView, "Results & Preview")]);
    private readonly Choice _listChoice = new([(ResultsList, "Results"), (BookmarksList, "Bookmarks")]);
    private readonly ItemList _results = new();
    private readonly ScrollContainer _bookmarkScroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
    private readonly VBoxContainer _bookmarks = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly LineEdit _seedBox = new();
    private readonly Button _useButton = new();
    private readonly Button _bookmarkButton = new();
    private readonly HBoxContainer _noteRow = new();
    private readonly LineEdit _noteBox = new();
    private readonly Timer _pollTimer = new();

    private ISeedSearch? _search;
    private SimResult? _shown;
    private readonly List<(Button Row, string Seed)> _bookmarkRows = [];

    private GameData Data => _engine.Data;

    private SeedSearchScreen(
        Control hostScreen,
        StartRunLobby lobby,
        string initialSeed,
        Action<string> applySeed,
        string[] buttonFields,
        bool isStandardRun,
        SeedEngine engine)
    {
        _hostScreen = hostScreen;
        _lobby = lobby;
        _initialSeed = initialSeed;
        _applySeed = applySeed;
        _buttonFields = buttonFields;
        _isStandardRun = isStandardRun;
        _engine = engine;
        _preview = new PreviewPanel(engine.Data, Conds, _info.Show);
    }

    public static bool CanOpen(StartRunLobby? lobby) =>
        lobby is { NetService: NetSingleplayerGameService } && lobby.LocalPlayer.character is not (null or RandomCharacter);

    public static bool CanOpen(NCustomRunScreen screen) => CanOpen(screen.Lobby);

    public static bool CanOpen(NCharacterSelectScreen screen) => CanOpen(screen.Lobby);

    public static void Open(NCustomRunScreen screen)
    {
        StartRunLobby lobby = screen.Lobby;
        LineEdit seedInput = (LineEdit)SeedInputField.GetValue(screen)!;
        SeedEngine engine = PrepareEngine(lobby);
        new SeedSearchScreen(
            screen,
            lobby,
            seedInput.Text,
            seed =>
            {
                seedInput.Text = seed;
                lobby.SetSeed(seed);
            },
            CustomButtonFields,
            isStandardRun: false,
            engine).Build();
    }

    public static void Open(NCharacterSelectScreen screen, Action<string>? onSeedChanged = null)
    {
        StartRunLobby lobby = screen.Lobby;
        SeedEngine engine = PrepareEngine(lobby);
        new SeedSearchScreen(
            screen,
            lobby,
            lobby.Seed ?? "",
            seed =>
            {
                lobby.SetSeed(seed);
                onSeedChanged?.Invoke(seed);
            },
            StandardButtonFields,
            isStandardRun: true,
            engine).Build();
    }

    private static SeedEngine PrepareEngine(StartRunLobby lobby)
    {
        string act1 = string.IsNullOrEmpty(lobby.Act1) ? "random" : lobby.Act1;
        SeedEngine engine = SeedEngine.For(lobby.LocalPlayer.character, lobby.Ascension, act1);
        if (!ReferenceEquals(_condsData, engine.Data))
        {
            Conds.Clear();
            _condsData = engine.Data;
        }

        return engine;
    }

    // ---- layout ----

    private void Build()
    {
        _root.Name = "SeedSearchScreen";
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        Font? font = _hostScreen.GetNodeOrNull<Control>("%SeedLabel")?.GetThemeFont("font")
            ?? _hostScreen.GetNodeOrNull<Control>("InfoPanel/VBoxContainer/Name")?.GetThemeFont("font");
        _root.Theme = Visuals.MakeTheme(font);
        _root.TreeExiting += StopSearch;
        SavedData.BookmarksChanged += RefreshBookmarks;
        _root.TreeExiting += () => SavedData.BookmarksChanged -= RefreshBookmarks;

        ColorRect background = new() { Color = Visuals.Background, MouseFilter = Control.MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(background);

        MarginContainer margin = new();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 32);
        foreach (string side in new[] { "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 24);
        _root.AddChild(margin);

        VBoxContainer page = new();
        page.AddThemeConstantOverride("separation", 14);
        margin.AddChild(page);
        page.AddChild(BuildHeader());

        HBoxContainer body = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        page.AddChild(body);

        FilterPanel filters = new(Data, Conds, _info.Show, text => _status.Text = text);
        filters.Root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        filters.Changed += () =>
        {
            if (_shown != null)
                _preview.Show(_shown, "");
        };
        body.AddChild(filters.Root);
        Control results = BuildResultsPanel();
        body.AddChild(results);

        VBoxContainer sidebar = new() { CustomMinimumSize = new Vector2(390, 0) };
        sidebar.AddThemeConstantOverride("separation", 10);
        body.AddChild(sidebar);
        sidebar.AddChild(_info.Root);
        filters.Active.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        sidebar.AddChild(filters.Active);

        void ShowView()
        {
            filters.Root.Visible = _viewChoice.Value == FiltersView;
            results.Visible = _viewChoice.Value == ResultsView;
        }

        _viewChoice.Changed += ShowView;
        if (Conds.Count == 0 && _initialSeed.Trim().Length > 0)
            _viewChoice.Select(ResultsView);
        ShowView();

        _pollTimer.WaitTime = 0.1;
        _pollTimer.Timeout += PollSearch;
        _root.AddChild(_pollTimer);

        _layer.AddChild(_root);
        _hostScreen.AddChild(_layer);
        DisableGameButtons();
        RefreshBookmarks();
        ShowSeed(_initialSeed);
    }

    private Control BuildHeader()
    {
        HBoxContainer header = new();
        header.AddThemeConstantOverride("separation", 20);

        header.AddChild(Visuals.SectionTitle("Seed Search", 36));
        if (Visuals.CharacterIcon(Data.Character) is { } portrait)
        {
            header.AddChild(new TextureRect
            {
                Texture = portrait,
                CustomMinimumSize = new Vector2(48, 48),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            });
        }

        string modeLabel = _isStandardRun ? "Standard Run" : "Custom Run";
        string act1 = Data.SlotActs[0].Length == 1 ? Data.ActName(Data.SlotActs[0][0]) : "random";
        VBoxContainer context = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        context.AddChild(new Label
        {
            Text = $"{Data.Character.Title.GetFormattedText()}  ·  Ascension {Data.Ascension}  ·  Act 1: {act1}  ·  {modeLabel}  ·  {_engine.Status}",
            TooltipText = "Character, ascension and Act 1 come from your current lobby.",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ClipText = true
        });
        _status.AutowrapMode = TextServer.AutowrapMode.Off;
        _status.ClipText = true;
        context.AddChild(_status);
        header.AddChild(context);

        _viewChoice.Root.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        foreach (Node view in _viewChoice.Root.GetChildren())
            ((Button)view).CustomMinimumSize = new Vector2(145, 46);
        header.AddChild(_viewChoice.Root);
        _searchButton.Text = "Search";
        _searchButton.CustomMinimumSize = new Vector2(150, 46);
        _searchButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _searchButton.Pressed += ToggleSearch;
        header.AddChild(_searchButton);
        Button back = new() { Text = "Back (Esc)", CustomMinimumSize = new Vector2(135, 46), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
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

        HBoxContainer split = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        column.AddChild(split);

        VBoxContainer left = new() { CustomMinimumSize = new Vector2(220, 0) };
        split.AddChild(left);
        left.AddChild(_listChoice.Root);
        _listChoice.Changed += () =>
        {
            _results.Visible = _listChoice.Value == ResultsList;
            _bookmarkScroll.Visible = _listChoice.Value == BookmarksList;
        };
        _results.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _results.ItemSelected += index => ShowSeed(_results.GetItemText((int)index), fromList: true);
        left.AddChild(_results);
        _bookmarkScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _bookmarkScroll.Visible = false;
        _bookmarks.AddThemeConstantOverride("separation", 4);
        _bookmarkScroll.AddChild(_bookmarks);
        left.AddChild(_bookmarkScroll);

        VBoxContainer right = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);

        HBoxContainer seedRow = new();
        seedRow.AddThemeConstantOverride("separation", 8);
        right.AddChild(seedRow);
        seedRow.AddChild(new Label { Text = "Seed:" });
        _seedBox.PlaceholderText = "type or pick a seed";
        _seedBox.CustomMinimumSize = new Vector2(210, 0);
        _seedBox.TextChanged += text => ShowSeed(text, fromBox: true);
        seedRow.AddChild(_seedBox);
        _useButton.Text = _isStandardRun ? "Use in Standard Run" : "Use this seed";
        _useButton.Pressed += UseSeed;
        seedRow.AddChild(_useButton);
        _bookmarkButton.CustomMinimumSize = new Vector2(160, 0);
        _bookmarkButton.Pressed += () =>
        {
            if (_shown != null)
                SavedData.ToggleBookmark(_shown.Seed, Data);
        };
        seedRow.AddChild(_bookmarkButton);
        seedRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Button copy = new() { Text = "Copy seed" };
        copy.Pressed += () =>
        {
            if (_shown != null)
            {
                DisplayServer.ClipboardSet(_shown.Seed);
                _status.Text = $"Copied seed {_shown.Seed} to clipboard.";
            }
        };
        seedRow.AddChild(copy);
        Button copySummary = new() { Text = "Copy summary", TooltipText = "Copy a formatted summary of this seed (Neow, First Shop, Chests, Ancients, Bosses) to clipboard" };
        copySummary.Pressed += () =>
        {
            if (_shown != null)
            {
                DisplayServer.ClipboardSet(_preview.BuildSummaryText(_shown));
                _status.Text = "Copied full seed summary to clipboard.";
            }
        };
        seedRow.AddChild(copySummary);

        // Only there while the previewed seed is bookmarked.
        _noteRow.AddThemeConstantOverride("separation", 10);
        right.AddChild(_noteRow);
        _noteRow.AddChild(new Label { Text = "Note" });
        _noteBox.PlaceholderText = "why this seed is worth keeping";
        _noteBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _noteBox.TextChanged += text =>
        {
            if (ShownBookmark() is { } bookmark)
                SavedData.SetNote(bookmark, text.Trim());
        };
        _noteRow.AddChild(_noteBox);
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

        SavedData.RecordSearch(Data, Conds);
        _results.Clear();
        _listChoice.Select(ResultsList);
        _viewChoice.Select(ResultsView);
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

        _viewChoice.SetText(ResultsView, $"Results ({p.Results.Count})");
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
        ShowBookmarkState();
        _preview.Show(_shown, seed.Length == 0 ? "Pick a seed from the list, or type one above to preview it." : "Could not preview this seed. See godot.log.");
    }

    // ---- bookmarks ----

    private Bookmark? ShownBookmark() => _shown == null ? null : SavedData.FindBookmark(_shown.Seed, Data);

    private void RefreshBookmarks()
    {
        Visuals.Clear(_bookmarks);
        _bookmarkRows.Clear();
        if (SavedData.Bookmarks.Count == 0)
        {
            Label empty = Visuals.MutedLabel("No bookmarks yet. Preview a seed and press Bookmark.");
            empty.CustomMinimumSize = new Vector2(200, 0);
            _bookmarks.AddChild(empty);
        }

        StyleBoxFlat plain = Visuals.Box(Visuals.InsetColor, Visuals.Plain, 8);
        foreach (Bookmark b in SavedData.Bookmarks)
        {
            string detail = string.Join("  ·  ", new[] { b.IsFor(Data) ? "" : b.Setup, b.Note }.Where(part => part.Length > 0));
            Button row = new()
            {
                Text = detail.Length > 0 ? b.Seed + "\n" + detail : b.Seed,
                TooltipText = $"{b.Setup}, saved {b.Saved.ToLocalTime():d}" + (b.Note.Length > 0 ? "\n" + b.Note : ""),
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                ToggleMode = true,
                FocusMode = Control.FocusModeEnum.None
            };
            row.AddThemeFontSizeOverride("font_size", 16);
            row.AddThemeStyleboxOverride("normal", plain);
            row.Pressed += () => ShowSeed(b.Seed, fromList: true);
            _bookmarks.AddChild(row);
            _bookmarkRows.Add((row, b.Seed));
        }

        ShowBookmarkState();
    }

    private void ShowBookmarkState()
    {
        Bookmark? bookmark = ShownBookmark();
        _bookmarkButton.Disabled = _shown == null;
        _bookmarkButton.Text = bookmark == null ? "Bookmark" : "Unbookmark";
        _noteRow.Visible = bookmark != null;
        foreach ((Button row, string seed) in _bookmarkRows)
            row.SetPressedNoSignal(seed == _shown?.Seed);
        if (bookmark != null && _noteBox.Text.Trim() != bookmark.Note)
            _noteBox.Text = bookmark.Note;
    }

    // ---- leaving ----

    private void UseSeed()
    {
        if (_shown == null)
            return;

        _applySeed(_shown.Seed);
        Close();
    }

    private void Close()
    {
        StopSearch();
        foreach (NClickableControl button in _disabledGameButtons)
            button.Enable();
        _disabledGameButtons.Clear();
        _layer.QueueFree();
    }

    private void DisableGameButtons()
    {
        Type hostType = _hostScreen.GetType();
        foreach (string field in _buttonFields)
        {
            if (AccessTools.Field(hostType, field)?.GetValue(_hostScreen) is NClickableControl { IsEnabled: true } button)
            {
                button.Disable();
                _disabledGameButtons.Add(button);
            }
        }
    }
}
