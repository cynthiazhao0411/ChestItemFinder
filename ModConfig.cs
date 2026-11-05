using StardewModdingAPI;

namespace ChestItemFinder
{
    /// <summary>Settings saved to config.json in the mod folder.</summary>
    public class ModConfig
    {
        /// <summary>The key that opens the in-game search box.</summary>
        public SButton SearchKey { get; set; } = SButton.H;
    }
}
