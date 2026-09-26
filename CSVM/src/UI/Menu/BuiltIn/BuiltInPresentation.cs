using System;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.UI.Menu.BuiltIn;

/// <summary>
/// The Built-in presentation: the existing launchscreen, <see cref="LaunchMenu"/>, registered
/// under <see cref="PresentationId.BuiltIn"/>. <see cref="Activate"/> builds the menu under the
/// given parent on the first call and shows it on every call, mapping the semantic destination
/// onto the launchscreen's own screens; <see cref="Hide"/> takes it off screen with its cursors
/// intact; <see cref="Deactivate"/> frees it. The menu's frame runs from <see cref="Tick"/>, so
/// its own process callback is switched off. The <c>--menu=</c> aid opens the first show alone;
/// every later show lands on the destination itself, so a return from flight is the top level.
/// </summary>
public sealed class BuiltInPresentation : IMenuPresentation
{
    private readonly Node _parent;
    private readonly string _zrdrPath;
    private readonly string _dataRoot;
    private readonly MenuInput _player1;
    // Consumed by the first Activate: an aid names a screen a shot wants, never where a return lands.
    private string _aid;
    private LaunchMenu? _menu;

    /// <summary>A presentation that builds its menu under <paramref name="parent"/>, opening its
    /// first show on <paramref name="aid"/> (a <c>--menu=</c> value or "") and binding seat 0's
    /// devices through <paramref name="player1"/>, the poller behind the host's first seat.</summary>
    public BuiltInPresentation(Node parent, string zrdrPath, string dataRoot, string aid, MenuInput player1)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _zrdrPath = zrdrPath;
        _dataRoot = dataRoot;
        _aid = aid ?? string.Empty;
        _player1 = player1 ?? throw new ArgumentNullException(nameof(player1));
    }

    public PresentationId Id => PresentationId.BuiltIn;

    /// <summary>The launchscreen while built. The owner's door for what is Built-in's alone: its
    /// one-shot debug aids and the failed-build note.</summary>
    public LaunchMenu? Menu => _menu;

    public void Activate(IMenuHost host, MenuReturnDestination destination)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_menu == null)
        {
            _menu = LaunchMenu.Build(_zrdrPath, _dataRoot, host, _player1);
            _menu.SetProcess(false);
            _parent.AddChild(_menu);
        }

        string aid = _aid;
        _aid = string.Empty;
        _menu.ShowMenu(aid);
        switch (destination)
        {
            case InstantActionReturn:
            // Built-in has no wrap-up page: its own board took the screen inside the flight, so the
            // nearest screen this graph has is the one that board's Exit already lands on.
            case InstantActionWrapupReturn:
                _menu.OpenInstantAction();
                break;
            case CabinReturn cabin:
                _menu.OpenCampaignCabin(cabin.Profile);
                break;
            case DebriefReturn debrief:
                _menu.OpenCampaignScrapbook(debrief.Profile, debrief.MissionSeq, debrief.MissionWon);
                break;
        }
    }

    public void Tick(float dt) => _menu?._Process(dt);

    public void Hide() => _menu?.HideMenu();

    public void Deactivate()
    {
        if (_menu == null)
        {
            return;
        }

        _parent.RemoveChild(_menu);
        _menu.QueueFree();
        _menu = null;
    }
}
