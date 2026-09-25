namespace CSVM.Bindings;

/// <summary>Where a seat's keymap comes from when the seat is built: the player's own saved file, or
/// the shipped defaults. Every polling site asks this instead of calling
/// <see cref="BindingProfile.Defaults"/> for itself, so there is one place the read is gated and one
/// place to look when a rebind is not felt.
/// ⚠ A scripted run must never read it (`docs/verification.md`, DET-8). <c>--det</c> ignores
/// <c>config.json</c> so that a run is a function of its committed tree, and a keymap loaded from
/// the user's profile directory would make every golden and every probe depend on whoever ran it.
/// <see cref="Configure"/> is what closes that door, and the gate is shut until it is called, so a
/// host that forgets to configure gets the defaults rather than somebody's file.</summary>
public static class LaunchBindings
{
    /// <summary>The ship switch. Set it false and every seat launches on the shipped defaults while
    /// the rebinding screen still edits and saves, which is the state the feature shipped in before
    /// this read existed.</summary>
    public const bool ReadSavedKeymaps = true;

    private static bool _readsSaved;

    /// <summary>Whether a seat built now reads the player's saved file.</summary>
    public static bool ReadsSaved => _readsSaved;

    /// <summary>The stick profiles player 1's keymap is completed from, or null while sticks are off.
    /// Registered by the stick side when its roster starts, and null in every scripted run, since
    /// those never start one.</summary>
    public static IStickRows? StickRows { get; set; }

    /// <summary>Resolves the gate once at launch. <paramref name="deterministic"/> is true for a run
    /// whose result must depend only on the committed tree: <c>--det</c> and <c>--run-tests</c>.
    /// </summary>
    public static void Configure(bool deterministic) => _readsSaved = ReadSavedKeymaps && !deterministic;

    /// <summary>That player's whole profile, for a seat that reads several contexts. Players are
    /// numbered from 1, so a zero-based seat index passes <c>index + 1</c>. Player 1's stick rows
    /// come from the active stick profiles, never from the keymap file (<see cref="StickRows"/>).
    /// </summary>
    public static BindingProfile Profile(int player, DeviceId pad, bool readsKeyboard)
    {
        var profile = _readsSaved
            ? BindingStore.UserBindings().Load(player, pad, readsKeyboard)
            : BindingProfile.Defaults(pad, readsKeyboard);
        if (player == 1)
        {
            StickRows?.MergeInto(profile);
        }

        return profile;
    }

    /// <summary>One context's map out of that player's profile, for the three polling sites, each of
    /// which reads a single context and stamps its pad rows with its own placeholder identity.
    /// </summary>
    public static ActionMap Map(int player, InputContext context, DeviceId pad, bool readsKeyboard) =>
        Profile(player, pad, readsKeyboard).Map(context);
}
