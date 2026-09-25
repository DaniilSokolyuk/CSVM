namespace CSVM.Bindings;

/// <summary>Where seat 1's stick rows come from, as the binding layer sees it. Stick rows live in
/// per-model profile files, not in a keymap file. A loaded keymap is complete only once they are
/// merged in (<see cref="LaunchBindings.Profile"/>). The interface
/// keeps this namespace free of the stick library: the stick side implements it and registers
/// itself in <see cref="LaunchBindings.StickRows"/>.</summary>
public interface IStickRows
{
    /// <summary>Replaces every stick binding in <paramref name="keymap"/> with the rows of the
    /// profiles active now. Pad, keyboard and mouse rows are left exactly as they are.</summary>
    void MergeInto(BindingProfile keymap);
}
