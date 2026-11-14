using System.Collections.Generic;

namespace ChestItemFinder
{
    /// <summary>One search in the history: the word, and the star filter that was selected when it was searched.</summary>
    public class SearchHistoryEntry
    {
        /// <summary>The "any quality" star filter (also the value for history saved by versions that did not remember the star).</summary>
        public const int AnyQuality = -1;

        /// <summary>
        /// The search words of this entry, without a "(category)" label: one word ("fruit") or several, joined as "fruit, fish".
        /// Only words that really found something in a chest are stored ("fish, ere" is stored as "fish"). When a newer search contains
        /// a longer spelling of a word ("fruit" for an old "frui"), the old word is removed from its entry (an entry with no words left is deleted).
        /// Entries saved by older versions may still hold words that found nothing; they are kept as they are until they are swallowed or cleared.
        /// </summary>
        public string Word { get; set; } = "";

        /// <summary>
        /// The star filter used for this search, as the game's quality value: -1 = any quality, 0 = no star,
        /// 1 = silver, 2 = gold, 4 = iridium. Clicking the word in the history puts the star button back to this.
        /// </summary>
        public int Quality { get; set; } = AnyQuality;

        /// <summary>
        /// Pinned with the small pin next to the word: a pinned entry always stays at the front of the history (pinned entries in the order they were
        /// pinned), never counts towards the limit of SearchMenu.MaxHistory and is never pushed out by newer searches. Searching it again only updates
        /// its star. Missing in saves of older versions = not pinned.
        /// </summary>
        public bool Pinned { get; set; }
    }

    /// <summary>The search history as stored in the save file (newest first).</summary>
    public class SearchHistoryData
    {
        /// <summary>
        /// The words only. This is all that save files from older versions contain (they come back with "any quality"),
        /// and it is still written next to <see cref="Entries"/> so an older version of the mod can still read the history.
        /// </summary>
        public List<string> Items { get; set; } = new();

        /// <summary>The same words, each with the star filter it was searched with. Used instead of <see cref="Items"/> when present.</summary>
        public List<SearchHistoryEntry> Entries { get; set; } = new();
    }
}