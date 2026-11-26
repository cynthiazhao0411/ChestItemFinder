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
        /// The main color of the flashing chest highlight. One of: Yellow, Orange, Red, Pink, Purple, Blue, Sky Blue, Green, White, Black.
        /// A search with several words uses it for the first word (see SecondaryColor1 / SecondaryColor2).
        /// </summary>
        public string HighlightColor { get; set; } = "Yellow";

        /// <summary>
        /// Real-world seconds before chest highlights disappear on their own (0 = never).
        /// One of: 0, 30, 60, 120, 180, 300 (never, 30 s, 1, 2, 3, 5 min).
        /// Uses the real clock, so time-speed mods do not affect it.
        /// </summary>
        public int HighlightTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// The flash colors of the second and the third word of a search with several words ("fruit, fish, gem"); the first word uses
        /// HighlightColor (the main color). Same color names as HighlightColor.
        /// </summary>
        public string SecondaryColor1 { get; set; } = "Pink";
        public string SecondaryColor2 { get; set; } = "Sky Blue";

        /// <summary>
        /// The player flashes like a chest while the backpack holds something of the search. Off = no flash on the player (hovering the player still
        /// lists the backpack's matching items while the highlights last).
        /// </summary>
        public bool FlashPlayer { get; set; } = true;

        /// <summary>
        /// The red arrow with the word "Backpack" above the player's head while the backpack holds something of the search. Off = no arrow and no text.
        /// </summary>
        public bool ShowPlayerArrow { get; set; } = true;
    }
}