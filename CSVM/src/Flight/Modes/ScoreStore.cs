using Godot;

namespace CSVM.Flight.Modes;

/// <summary>
/// Best-time persistence for stunt runs. One JSON object in
/// <c>user://stunt_scores.json</c>, the engine's writable user dir, never the repo (the hard
/// no-assets rule and, besides, scores are per-player), keyed <c>chapter/mission/plane</c>
/// (e.g. <c>C1/IA1/player_bhawk</c>) → <c>{ best: seconds, date: "YYYY-MM-DD" }</c>.
///
/// Read/written through Godot's <see cref="FileAccess"/> + <see cref="Json"/> rather than
/// System.Text.Json: only the Godot API resolves the <c>user://</c> scheme, and
/// <see cref="Json.Stringify"/> is locale-neutral where <c>ToString()</c> is not. A missing or
/// corrupt file is an empty store (a first run has no best), never an exception, a persistence
/// hiccup must not break the scoreboard.
/// </summary>
public sealed class ScoreStore
{
    private const string DefaultStorePath = "user://stunt_scores.json";

    private readonly string _storePath;
    private readonly Godot.Collections.Dictionary _data;

    private ScoreStore(string storePath, Godot.Collections.Dictionary data)
    {
        _storePath = storePath;
        _data = data;
    }

    /// <summary>Loads the store, or an empty one if the file is absent/unreadable/malformed.</summary>
    public static ScoreStore Load() => Load(DefaultStorePath);

    /// <summary>The stored best total, seconds, for this run key, or null if none is recorded yet.</summary>
    public float? GetBest(string key)
    {
        if (_data.TryGetValue(key, out var v) && v.VariantType == Variant.Type.Dictionary)
        {
            var entry = v.AsGodotDictionary();
            if (entry.TryGetValue("best", out var best))
                return best.AsSingle();
        }
        return null;
    }

    /// <summary>Records <paramref name="total"/> as the new best for <paramref name="key"/> if it
    /// beats the stored one (or there is none), persisting immediately. Returns true when it was a
    /// new best (the scoreboard flags NEW BEST), false when a stored time stands, never worsens a
    /// record.</summary>
    public bool RecordIfBest(string key, float total)
    {
        var prev = GetBest(key);
        if (prev.HasValue && total >= prev.Value)
            return false;
        _data[key] = new Godot.Collections.Dictionary
        {
            { "best", total },
            { "date", Time.GetDateStringFromSystem() },
        };
        Save();
        return true;
    }

    /// <summary>Loads a store at an alternate <paramref name="storePath"/>, for a suite that must
    /// not touch the player's own <c>user://stunt_scores.json</c>, point it at a throwaway path
    /// (e.g. under the suite's own scratch directory) instead.</summary>
    internal static ScoreStore Load(string storePath)
    {
        if (FileAccess.FileExists(storePath))
        {
            using var f = FileAccess.Open(storePath, FileAccess.ModeFlags.Read);
            if (f != null)
            {
                var parsed = Json.ParseString(f.GetAsText());
                if (parsed.VariantType == Variant.Type.Dictionary)
                    return new ScoreStore(storePath, parsed.AsGodotDictionary());
            }
            else
                GD.PushWarning($"stunt scores: could not read {storePath}: {FileAccess.GetOpenError()}");
        }
        return new ScoreStore(storePath, new Godot.Collections.Dictionary());
    }

    private void Save()
    {
        using var f = FileAccess.Open(_storePath, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PushWarning($"stunt scores: could not write {_storePath}: {FileAccess.GetOpenError()}");
            return;
        }
        f.StoreString(Json.Stringify(_data, "  "));
    }
}
