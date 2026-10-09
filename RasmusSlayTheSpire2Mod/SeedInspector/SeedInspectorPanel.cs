using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Timer = Godot.Timer;

namespace RasmusSlayTheSpire2Mod.SeedInspector;

// The panel on the Custom Run screen. Built from stock Godot nodes only: node subclasses defined
// in a mod assembly are not registered with the engine, so this class is a plain object that
// lives as long as the signal handlers on its nodes do.
public sealed class SeedInspectorPanel
{
    private const double SearchBudgetMs = 6;

    private static readonly FieldInfo SeedInputField = AccessTools.Field(typeof(NCustomRunScreen), "_seedInput");

    private readonly NCustomRunScreen _screen;
    private readonly LineEdit _seedInput;
    private readonly PanelContainer _root = new();
    private readonly VBoxContainer _body = new();
    private readonly RichTextLabel _previewLabel = new();
    private readonly Label _status = new();
    private readonly Button _searchButton = new();
    private readonly Timer _searchTimer = new();
    private readonly Dictionary<string, LineEdit> _filters = new();

    private string? _shownKey;
    private SeedSearch? _search;

    private SeedInspectorPanel(NCustomRunScreen screen)
    {
        _screen = screen;
        _seedInput = (LineEdit)SeedInputField.GetValue(screen)!;
    }

    public static void AttachTo(NCustomRunScreen screen)
    {
        new SeedInspectorPanel(screen).Build();
    }

    private void Build()
    {
        _root.Name = "SeedInspector";
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 16);
        _root.GrowHorizontal = Control.GrowDirection.Begin;
        _root.CustomMinimumSize = new Vector2(460, 0);

        VBoxContainer layout = new();
        _root.AddChild(layout);

        Button header = new() { Text = "Seed inspector", ToggleMode = true, ButtonPressed = true };
        header.Toggled += pressed => _body.Visible = pressed;
        layout.AddChild(header);
        layout.AddChild(_body);

        _previewLabel.BbcodeEnabled = true;
        _previewLabel.FitContent = true;
        _previewLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _body.AddChild(_previewLabel);

        _body.AddChild(new HSeparator());
        _body.AddChild(new Label { Text = "Find a seed (name contains)" });
        GridContainer grid = new() { Columns = 2 };
        _body.AddChild(grid);
        foreach (string name in new[] { "Neow offers", "Act 1 boss", "Act 2 ancient", "Act 3 ancient", "First act 1 event", "Card in first reward" })
        {
            LineEdit edit = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddChild(new Label { Text = name });
            grid.AddChild(edit);
            _filters[name] = edit;
        }

        HBoxContainer buttons = new();
        _body.AddChild(buttons);
        _searchButton.Text = "Search";
        _searchButton.Pressed += ToggleSearch;
        buttons.AddChild(_searchButton);
        Button openSite = new() { Text = "Open on SearchTheSpire" };
        openSite.Pressed += OpenOnSearchTheSpire;
        buttons.AddChild(openSite);

        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _body.AddChild(_status);

        // Polling the lobby is simpler and sturdier than hooking every way the seed, character,
        // ascension or modifiers can change.
        Timer refreshTimer = new() { WaitTime = 0.3, Autostart = true };
        refreshTimer.Timeout += Refresh;
        _root.AddChild(refreshTimer);

        _searchTimer.WaitTime = 0.02;
        _searchTimer.Timeout += SearchStep;
        _root.AddChild(_searchTimer);

        _screen.AddChild(_root);
    }

    private bool TryGetRunSetup(out StartRunLobby lobby, out CharacterModel character)
    {
        lobby = _screen.Lobby;
        character = lobby?.LocalPlayer.character!;
        return lobby is { NetService: NetSingleplayerGameService } && character != null;
    }

    private void Refresh()
    {
        bool usable = TryGetRunSetup(out StartRunLobby lobby, out CharacterModel character);
        _root.Visible = usable;
        if (!usable)
            return;

        string seed = _seedInput.Text.Trim();
        string key = $"{seed}|{character.Id.Entry}|{lobby.Ascension}|{lobby.Act1}|{lobby.Modifiers.Count}";
        if (key == _shownKey)
            return;

        _shownKey = key;
        _previewLabel.Text = Describe(seed, lobby, character);
    }

    private static string Describe(string seed, StartRunLobby lobby, CharacterModel character)
    {
        if (seed.Length == 0)
            return "Type a seed to preview it, or search for one below.";
        if (character is RandomCharacter)
            return "Pick a character to preview this seed.";

        SeedPreview? p = SeedSimulator.Simulate(seed, character, lobby.Ascension, lobby.Act1, includeCardReward: true);
        if (p == null)
            return "Could not preview this seed. See godot.log.";

        List<string> lines =
        [
            $"[b]Neow offers:[/b] {string.Join(", ", p.NeowOffers)}",
            $"[b]First card reward:[/b] {string.Join(", ", p.FirstCardReward ?? [])}",
            $"[b]Act 1 events:[/b] {string.Join(", ", p.Act1Events)}",
            $"[b]Act 1 map:[/b] {p.Act1Map}"
        ];
        for (int i = 0; i < p.Acts.Count; i++)
        {
            string line = $"[b]Act {i + 1}:[/b] {p.Acts[i]}, boss {p.Bosses[i]}";
            if (i == p.Acts.Count - 1 && p.SecondBoss != null)
                line += $" then {p.SecondBoss}";
            if (i > 0)
                line += $", ancient {p.Ancients[i]}";
            lines.Add(line);
        }

        lines.Add("[i]The card reward assumes a normal first combat and a Neow pick that gives no cards, relics or potions. Events are in queue order.[/i]");
        if (lobby.Modifiers.Count > 0)
            lines.Add("[i]Modifiers are ticked; this preview ignores them.[/i]");
        return string.Join("\n", lines);
    }

    private void ToggleSearch()
    {
        if (_search != null)
        {
            StopSearch($"Cancelled after {_search.Tried} seeds.");
            return;
        }

        if (!TryGetRunSetup(out StartRunLobby lobby, out CharacterModel character) || character is RandomCharacter)
        {
            _status.Text = "Pick a character first.";
            return;
        }

        SeedFilter filter = new()
        {
            NeowOffer = Filter("Neow offers"),
            Act1Boss = Filter("Act 1 boss"),
            Act2Ancient = Filter("Act 2 ancient"),
            Act3Ancient = Filter("Act 3 ancient"),
            Act1Event = Filter("First act 1 event"),
            RewardCard = Filter("Card in first reward")
        };
        if (filter.IsEmpty)
        {
            _status.Text = "Fill in at least one filter.";
            return;
        }

        _search = new SeedSearch(filter, character, lobby.Ascension, lobby.Act1);
        _searchButton.Text = "Cancel";
        _searchTimer.Start();
    }

    private string Filter(string name) => _filters[name].Text.Trim();

    private void SearchStep()
    {
        if (_search == null)
            return;

        SeedPreview? found = _search.Step(SearchBudgetMs);
        if (found != null)
        {
            // Setting Text from code does not emit TextChanged, so tell the lobby directly.
            _seedInput.Text = found.Seed;
            _screen.Lobby.SetSeed(found.Seed);
            StopSearch($"Found {found.Seed} after {_search.Tried} seeds.");
        }
        else if (_search.Failed)
        {
            StopSearch("Search failed. See godot.log.");
        }
        else
        {
            _status.Text = $"Tried {_search.Tried} seeds ({_search.SeedsPerSecond:F0}/s)...";
        }
    }

    private void StopSearch(string message)
    {
        _search = null;
        _searchTimer.Stop();
        _searchButton.Text = "Search";
        _status.Text = message;
    }

    private void OpenOnSearchTheSpire()
    {
        string url = "https://searchthespire.app/";
        string seed = _seedInput.Text.Trim();
        if (seed.Length > 0)
        {
            url += $"?seed={Uri.EscapeDataString(seed)}";
            if (TryGetRunSetup(out _, out CharacterModel character) && character is not RandomCharacter)
                url += $"&ichar={character.Id.Entry.ToLowerInvariant()}";
        }

        OS.ShellOpen(url);
    }
}
