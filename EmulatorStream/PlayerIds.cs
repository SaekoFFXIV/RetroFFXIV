using System.Linq;

namespace EmulatorStream;

// Short public player IDs (Lightless/PlayerSync style): 8 random
// letters/digits formatted with a dash in the middle, e.g. "K7QX-4MRT".
// The RELAY generates them on registration and ties each one to the
// player's XIVAuth identity; the plugin only normalizes and displays them.
public static class PlayerIds
{
    // Dashes/spaces/case never matter; IDs compare on their core chars only.
    public static string Normalize(string value)
    {
        var core = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Format(core);
    }

    public static string Digits(string normalizedId) => normalizedId.Replace("-", "");

    private static string Format(string core)
    {
        if (core.Length <= 3)
            return core;
        var split = core.Length / 2;
        return $"{core[..split]}-{core[split..]}";
    }
}
