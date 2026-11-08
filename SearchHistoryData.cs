using System.Collections.Generic;

namespace ChestItemFinder
{
    /// <summary>The search history as stored in the save file (newest first).</summary>
    public class SearchHistoryData
    {
        public List<string> Items { get; set; } = new();
    }
}
