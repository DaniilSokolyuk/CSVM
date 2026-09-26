using System;

namespace CSVM.UI.Hangar;

/// <summary>
/// The two word lists the PLANENAME screen composes a plane name from, and the roll across them.
/// Authored fiction rather than a decode, which is why it lives in code and not beside the hangar's
/// JSON tables: there is no original table to diff it against, and an unreadable file here would
/// leave the screen with no name to offer on a pad and the commit gate refusing to let the pilot
/// out. Every adjective is meant to read against every noun, so the pair needs no compatibility
/// table; a combination that reads badly is a word to delete, not a rule to add.
/// </summary>
public static class PlaneNameTables
{
    /// <summary>The first word. Mostly the pulp-menace register the setting runs on, with enough
    /// of the era's affectionate nose-art register that a pilot rolling six names sees range.</summary>
    public static readonly string[] Adjectives =
    {
        "Crimson", "Iron", "Black", "Savage", "Ragged", "Grim", "Bitter", "Broken",
        "Wicked", "Hollow", "Silent", "Feral", "Jagged", "Scarlet", "Ashen", "Vicious",
        "Brazen", "Sullen", "Thundering", "Howling", "Reckless", "Ruthless", "Bloody", "Wayward",
        "Rogue", "Crooked", "Lucky", "Painted", "Restless", "Sweet", "Gilded", "Merry",
        "Dizzy", "Velvet", "Gentle", "Idle", "Careless", "Golden", "Silver", "Lonesome",
    };

    /// <summary>The second word: birds and beasts, things with an edge, a person the plane is
    /// named after, and weather. No name the setting already owns, which is a licence question
    /// rather than a taste one.</summary>
    public static readonly string[] Nouns =
    {
        "Vulture", "Kestrel", "Falcon", "Magpie", "Raven", "Buzzard", "Hornet", "Viper",
        "Jackal", "Coyote", "Panther", "Mantis", "Shrike", "Wolf", "Talon", "Lance",
        "Dagger", "Hammer", "Anvil", "Bullet", "Saber", "Spur", "Bolt", "Cleaver",
        "Duchess", "Baroness", "Widow", "Jenny", "Mary", "Bandit", "Gambler", "Preacher",
        "Vagrant", "Thunder", "Ember", "Cinder", "Comet", "Tempest", "Lantern", "Compass",
    };

    /// <summary>How many names the two lists spell between them.</summary>
    public static int Count => Adjectives.Length * Nouns.Length;

    /// <summary>The name a pair of indices spells. Both wrap, so a stepper can hand this its own
    /// running index without clamping first.</summary>
    public static string Compose(int adjective, int noun) =>
        Adjectives[Wrap(adjective, Adjectives.Length)] + " " + Nouns[Wrap(noun, Nouns.Length)];

    /// <summary>An index folded into a list's range, the launchscreen's own wrap.</summary>
    public static int Wrap(int index, int count) => ((index % count) + count) % count;

    /// <summary>A pair drawn from <paramref name="rng"/>, skipping every name
    /// <paramref name="taken"/> claims. Passing null takes the draw as it lands.</summary>
    public static (int Adjective, int Noun) Roll(Random rng, Func<string, bool>? taken = null)
    {
        int adjective = rng.Next(Adjectives.Length);
        int noun = rng.Next(Nouns.Length);
        if (taken == null)
        {
            return (adjective, noun);
        }

        // The whole cross product walked from where the draw landed, rather than redrawing until
        // something is free: a hangar holding most of the names would leave a redraw loop
        // guessing, and this one is bounded and finds the last free name as readily as the first.
        int start = (adjective * Nouns.Length) + noun;
        for (int step = 0; step < Count; step++)
        {
            int at = (start + step) % Count;
            if (!taken(Compose(at / Nouns.Length, at % Nouns.Length)))
            {
                return (at / Nouns.Length, at % Nouns.Length);
            }
        }

        // Every name is on disk already. The draw stands, and the screen's own overwrite warning
        // is what tells the pilot what saving it would cost.
        return (adjective, noun);
    }
}
