using StardewModdingAPI;

namespace ChestItemFinder
{
    /// <summary>Settings saved to config.json in the mod folder.</summary>
    public class ModConfig
    {
        /// <summary>The key that opens the in-game search box.</summary>
        public SButton SearchKey { get; set; } = SButton.H;

        /// <summary>
        /// Real-world minutes before chest highlights disappear on their own (0 = never).
        /// Uses the real clock, so time-speed mods do not affect it.
        /// </summary>
        public int HighlightTimeoutMinutes { get; set; } = 10;
    }
}