using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace ChestItemFinder
{
    /// <summary>Settings saved to config.json in the mod folder.</summary>
    public class ModConfig
    {
        /// <summary>The key (or key combination) that opens the in-game search box.</summary>
        public KeybindList SearchKey { get; set; } = KeybindList.Parse("LeftShift + T");

        /// <summary>
        /// Peak opacity of the flashing chest highlight, 0 to 100 in steps of 20.
        /// 0 = highlight is not drawn at all, 100 = fully opaque at the brightest point of the flash.
        /// </summary>
        public int HighlightOpacityPercent { get; set; } = 60;

        /// <summary>
        /// Color of the flashing chest highlight. One of: Yellow, Red, Orange, Green, Blue, Purple, Black, White, Gray, Brown.
        /// </summary>
        public string HighlightColor { get; set; } = "Yellow";

        /// <summary>
        /// Real-world seconds before chest highlights disappear on their own (0 = never).
        /// One of: 0, 30, 60, 120, 300, 600, 900, 1200 (never, 30 s, 1, 2, 5, 10, 15, 20 min).
        /// Uses the real clock, so time-speed mods do not affect it.
        /// </summary>
        public int HighlightTimeoutSeconds { get; set; } = 600;
    }
}