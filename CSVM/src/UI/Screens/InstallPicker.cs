using System;
using System.IO;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The install folder picker: Godot's own <see cref="FileDialog"/> in directory mode, never the
/// native one. It stays in the game window, so a controller or a touchscreen drives it like the
/// rest of the screen. Godot's navigation covers the list (d-pad, A opens a folder, B closes).
/// Focus cannot leave the list without Tab, so two pad buttons are added. The left shoulder goes
/// up a folder and Y takes the folder being shown. The chosen folder goes to <see cref="Chosen"/>.
/// </summary>
public sealed partial class InstallPicker : FileDialog
{
    /// <summary>The pad button that goes up one folder.</summary>
    public const JoyButton UpButton = JoyButton.LeftShoulder;

    /// <summary>The pad button that takes the folder being shown.</summary>
    public const JoyButton TakeButton = JoyButton.Y;

    // The share of the window the dialog covers, large enough to read on a handheld.
    private const float WindowShare = 0.86f;

    /// <summary>Gets or sets where a chosen folder goes.</summary>
    public Action<string>? Chosen { get; set; }

    /// <summary>A picker over the whole file system in folder mode. The dialog's file-managing
    /// extras are off, since this chooses a folder to read and never changes one.</summary>
    public static InstallPicker Create(Theme theme)
    {
        var picker = new InstallPicker
        {
            FileMode = FileModeEnum.OpenDir,
            Access = AccessEnum.Filesystem,
            UseNativeDialog = false,
            DisplayMode = DisplayModeEnum.List,
            Title = "Choose your Crimson Skies install folder",
            ModeOverridesTitle = false,
            FolderCreationEnabled = false,
            DeletingEnabled = false,
            FileFilterToggleEnabled = false,
            FileSortOptionsEnabled = false,
            LayoutToggleEnabled = false,
            FavoritesEnabled = false,
            RecentListEnabled = false,

            // Wine and Steam keep their prefixes under dot folders, which Linux hides by default.
            ShowHiddenFiles = !OperatingSystem.IsWindows(),
            Theme = theme,
        };
        picker.DirSelected += dir => picker.Chosen?.Invoke(Path.GetFullPath(dir));
        return picker;
    }

    /// <summary>Shows the picker over the window, starting in <paramref name="from"/> when that is a
    /// folder. Focus goes to the folder list so the d-pad moves in it at once.</summary>
    public void Open(string? from)
    {
        if (!string.IsNullOrWhiteSpace(from) && Directory.Exists(from))
        {
            CurrentDir = from;
        }

        var window = GetTree().Root.Size;
        PopupCentered(new Vector2I((int)(window.X * WindowShare), (int)(window.Y * WindowShare)));
        Callable.From(FocusList).CallDeferred();
    }

    /// <summary>Handles the two added pad buttons; answers whether <paramref name="input"/> was one.
    /// Public so a suite can hand it a synthetic press.</summary>
    public bool HandlePad(InputEvent input)
    {
        if (input is not InputEventJoypadButton { Pressed: true } press)
        {
            return false;
        }

        if (press.ButtonIndex == UpButton)
        {
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(CurrentDir));
            if (!string.IsNullOrEmpty(parent))
            {
                CurrentDir = parent;
            }

            return true;
        }

        if (press.ButtonIndex == TakeButton)
        {
            string dir = Path.GetFullPath(CurrentDir);
            Hide();
            Chosen?.Invoke(dir);
            return true;
        }

        return false;
    }

    public override void _Input(InputEvent @event)
    {
        if (Visible && HandlePad(@event))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    // The dialog decides its own first focus; the list is where a pad has to start.
    private void FocusList()
    {
        foreach (var node in FindChildren("*", nameof(ItemList), true, false))
        {
            if (node is ItemList list && list.IsVisibleInTree())
            {
                list.GrabFocus();
                if (list.ItemCount > 0 && list.GetSelectedItems().Length == 0)
                {
                    list.Select(0);
                }

                return;
            }
        }
    }
}
