using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.ItemTypeDefinitions;

namespace ChestItemFinder
{
    /// <summary>One line of the auto-complete drop-down.</summary>
    public readonly struct Suggestion
    {
        /// <summary>The item name as the game shows it.</summary>
        public readonly string Text;

        /// <summary>How many characters at the start of <see cref="Text"/> the player has already typed.</summary>
        public readonly int MatchedLength;

        /// <summary>True if the search treats <see cref="Text"/> as a whole category ("fruit", "fish" ...). The drop-down then adds "(category)" after it; <see cref="Text"/> itself stays unchanged.</summary>
        public readonly bool IsCategory;

        /// <summary>
        /// For an id suggestion ("268", "-79"): the real name of the id ("Starfruit", "fruit"). The drop-down shows it after the number in brackets
        /// ("268 (Starfruit)"); <see cref="Text"/> stays the plain number, and that is what a click or Tab puts into the search box. Null for every other suggestion.
        /// </summary>
        public readonly string? IdLabel;

        public Suggestion(string text, int matchedLength, bool isCategory = false, string? idLabel = null)
        {
            Text = text;
            MatchedLength = matchedLength;
            IsCategory = isCategory;
            IdLabel = idLabel;
        }
    }

    /// <summary>
    /// The word list behind the search box's auto-complete, and the prefix search over it.
    /// </summary>
    /// <remarks>
    /// Word list = every item the game has that a player can put into a chest (built once from the game's own item data,
    /// rebuilt only if the language or the item data changes) + the names of the items that are in the player's chests right now.
    /// The second part matters because the game builds names like "Starfruit Wine" on the fly: they are not in the item data,
    /// but they are exactly what a chest search needs.
    ///
    /// Matching works like the first step of Google's auto-complete: the typed text must be the START of the name.
    /// Spaces, hyphens, underscores and case are ignored, exactly like the real search (see Norm in ModEntry), so
    /// "star fruit" finds "Starfruit" and "minif" finds "Mini-Fridge".
    ///
    /// Order (Google ranks by popularity and puts your own history first; the closest thing here is "what is in your chests"):
    ///   1. the name that equals the typed text
    ///   2. items that are in your chests right now
    ///   3. shorter names before longer ones
    ///   4. alphabetical
    /// </remarks>
    internal sealed class SuggestionIndex
    {
        private readonly struct Entry
        {
            public readonly string Text;   // as shown
            public readonly string Key;    // lower-case, without spaces / hyphens / underscores

            public Entry(string text, string key)
            {
                Text = text;
                Key = key;
            }
        }

        private readonly struct Hit
        {
            public readonly Entry Entry;
            public readonly bool Owned;
            public readonly bool Exact;

            public Hit(Entry entry, bool owned, bool exact)
            {
                Entry = entry;
                Owned = owned;
                Exact = exact;
            }
        }

        /// <summary>
        /// Item kinds (the game's own type prefixes) whose items can lie in a chest and are worth suggesting:
        /// objects, big craftables, furniture, hats, weapons, tools, boots, trinkets.
        /// Clothing, wallpaper, flooring and mannequins are left out on purpose (hundreds of look-alike names).
        /// </summary>
        private static readonly HashSet<string> IncludedItemTypes = new() { "(O)", "(BC)", "(F)", "(H)", "(W)", "(T)", "(B)", "(TR)" };

        /// <summary>
        /// Category words offered in the drop-down (names from the Stardew Valley Wiki, Modding:Items, section "Categories", in the spelling
        /// a player types). Only words that the search really treats as a category (ModEntry.IsCategoryWord) are used, so the
        /// "(category)" label can never promise a category search that would not happen.
        /// </summary>
        private static readonly string[] CategoryWords =
        {
            "animal product", "artisan goods", "bait", "big craftable", "book", "boots", "building resources", "clothing", "cooking", "crafting",
            "decor", "egg", "equipment", "fertilizer", "fish", "fishing tackle", "flower", "forage", "fruit", "furniture", "gem", "hat", "ingredient", "junk",
            "meat", "metal resources", "milk", "mineral", "monster loot", "ring", "seed", "skill book", "syrup", "tackle", "tool",
            "trash", "trinket", "vegetable", "weapon"
        };

        /// <summary>One id the search understands: an object id ("268") or a category id ("-79"), with its real name.</summary>
        private readonly struct IdEntry
        {
            public readonly string Text;    // the number as typed: "268", "-79"
            public readonly int Number;
            public readonly string Label;   // "Starfruit", "fruit, category"

            public IdEntry(string text, int number, string label)
            {
                Text = text;
                Number = number;
                Label = label;
            }
        }

        private Entry[] vocabulary = Array.Empty<Entry>();
        private IdEntry[] idVocabulary = Array.Empty<IdEntry>();
        private Entry[] ownedEntries = Array.Empty<Entry>();
        private HashSet<string> ownedKeys = new();

        // what the vocabulary was built from, so it can be rebuilt when the player changes language or a mod reloads the item data
        private object? builtFromObjectData;
        private LocalizedContentManager.LanguageCode builtLanguage;

        /// <summary>Lower-case a name and drop spaces, underscores and hyphens: "Star Fruit" -> "starfruit".</summary>
        public static string MakeKey(string text)
        {
            return new string(text.Where(ch => !IsSeparator(ch)).ToArray()).ToLowerInvariant();
        }

        private static bool IsSeparator(char ch)
        {
            return char.IsWhiteSpace(ch) || ch == '_' || ch == '-';
        }

        /// <summary>Builds the vocabulary from the game's item data (only when it is missing or out of date).</summary>
        public void EnsureBuilt()
        {
            if (vocabulary.Length > 0
                && ReferenceEquals(builtFromObjectData, Game1.objectData)
                && builtLanguage == LocalizedContentManager.CurrentLanguageCode)
                return;

            var byKey = new Dictionary<string, Entry>();

            foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
            {
                if (!IncludedItemTypes.Contains(type.Identifier))
                    continue;

                bool isObject = type.Identifier == "(O)";

                foreach (string id in type.GetAllIds())
                {
                    if (isObject && !CanBeHeld(id))
                        continue;

                    string? name = type.GetData(id)?.DisplayName?.Trim();
                    if (!IsUsableName(name))
                        continue;

                    string key = MakeKey(name);
                    if (key.Length > 0 && !byKey.ContainsKey(key))
                        byKey[key] = new Entry(name, key);
                }
            }

            // category words: the search treats them as whole categories, so they are offered as well (and marked in the drop-down)
            foreach (string word in CategoryWords)
            {
                if (!ModEntry.IsCategoryWord(word))
                    continue;

                string categoryKey = MakeKey(word);
                if (categoryKey.Length > 0 && !byKey.ContainsKey(categoryKey))
                    byKey[categoryKey] = new Entry(word, categoryKey);
            }

            vocabulary = byKey.Values.ToArray();
            idVocabulary = BuildIdVocabulary();
            builtFromObjectData = Game1.objectData;
            builtLanguage = LocalizedContentManager.CurrentLanguageCode;
        }

        /// <summary>
        /// Every id a player can search for, with its name: the numeric object ids of items that can be held (names from the game's own item data) and the
        /// category ids of SearchMenu.CategoryNamesById. The name of each id is asked from SearchMenu.TryGetIdName, so the drop-down, the title of the
        /// result panel and the history always show exactly the same name (and an id without a usable name is not offered).
        /// </summary>
        private static IdEntry[] BuildIdVocabulary()
        {
            var entries = new List<IdEntry>();

            foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
            {
                if (type.Identifier != "(O)")
                    continue;

                foreach (string id in type.GetAllIds())
                {
                    // plain numbers only ("268"); text ids of other mods cannot be searched by number
                    if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                        || number.ToString(CultureInfo.InvariantCulture) != id
                        || !CanBeHeld(id))
                        continue;

                    string? label = SearchMenu.TryGetIdName(id);
                    if (label != null)
                        entries.Add(new IdEntry(id, number, label));
                }
            }

            foreach (int categoryId in SearchMenu.CategoryNamesById.Keys)
            {
                if (categoryId == -999)
                    continue;   // field litter can never be in a chest

                string text = categoryId.ToString(CultureInfo.InvariantCulture);
                string? label = SearchMenu.TryGetIdName(text);
                if (label != null)
                    entries.Add(new IdEntry(text, categoryId, label + ", category"));   // "-79 (fruit, category)": a category id says so
            }

            return entries.ToArray();
        }

        /// <summary>True for a number typed as an id: an optional minus and at least one digit, nothing else ("26", "-7"). A lone "-" is not one.</summary>
        private static bool IsIdQuery(string text)
        {
            int start = text.Length > 0 && text[0] == '-' ? 1 : 0;
            if (text.Length == start)
                return false;

            for (int i = start; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The ids that start with the typed number, each with its name: the number that equals the typed text first, then shorter numbers, then closer to zero.
        /// "-7" -> "-7 (cooking, category)", "26" -> "26 (...)", "260 (...)" ... A lone "-" matches every category id (all of them start with it).
        /// </summary>
        private IReadOnlyList<Suggestion> SearchIds(string query, int max)
        {
            var hits = idVocabulary
                .Where(entry => entry.Text.StartsWith(query, StringComparison.Ordinal))
                .OrderBy(entry => entry.Text.Length == query.Length ? 0 : 1)
                .ThenBy(entry => entry.Text.Length)
                .ThenBy(entry => Math.Abs((long)entry.Number))
                .Take(max)
                .ToList();

            var result = new List<Suggestion>(hits.Count);
            foreach (IdEntry entry in hits)
                result.Add(new Suggestion(entry.Text, query.Length, isCategory: false, idLabel: entry.Label));
            return result;
        }

        /// <summary>Quest items and field litter (weeds, twigs, rocks) can never be carried, so they can never be in a chest.</summary>
        private static bool CanBeHeld(string objectId)
        {
            if (!Game1.objectData.TryGetValue(objectId, out ObjectData? data) || data == null)
                return false;

            return !string.Equals(data.Type, "Quest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(data.Type, "Litter", StringComparison.OrdinalIgnoreCase)
                && data.Category != -999;
        }

        /// <summary>Rejects empty names, unresolved text tokens and error items.</summary>
        private static bool IsUsableName([NotNullWhen(true)] string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (name.StartsWith("[", StringComparison.Ordinal) || name.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                return false;
            return name.Any(char.IsLetterOrDigit);
        }

        /// <summary>Remembers the names of the items that are in the player's chests right now (they are ranked first).</summary>
        public void SetOwnedNames(IEnumerable<string> displayNames)
        {
            var byKey = new Dictionary<string, Entry>();

            foreach (string raw in displayNames)
            {
                string text = (raw ?? "").Trim();
                if (!IsUsableName(text))
                    continue;

                string key = MakeKey(text);
                if (key.Length > 0 && !byKey.ContainsKey(key))
                    byKey[key] = new Entry(text, key);
            }

            ownedEntries = byKey.Values.ToArray();
            ownedKeys = new HashSet<string>(byKey.Keys);
        }

        /// <summary>The best <paramref name="max"/> names that start with what the player typed (empty list if nothing fits).</summary>
        public IReadOnlyList<Suggestion> Search(string rawQuery, int max)
        {
            // a number ("26", "-7") completes to the ids that start with it, each with its name; text is completed as before
            // (a lone "-" lists the category ids, the ones closest to zero first: -2, -4, -5 ... -79 ...; the player types on, "-7", to narrow it down)
            string typed = (rawQuery ?? "").Trim();
            if (typed == "-" || IsIdQuery(typed))
                return SearchIds(typed, max);

            string query = MakeKey(rawQuery);

            // nothing typed, or an item / category id like "268" or "-26": ids are not worth completing
            if (query.Length == 0 || query.All(char.IsDigit))
                return Array.Empty<Suggestion>();

            var hits = new List<Hit>();

            foreach (Entry entry in ownedEntries)
            {
                if (entry.Key.StartsWith(query, StringComparison.Ordinal))
                    hits.Add(new Hit(entry, owned: true, exact: entry.Key.Length == query.Length));
            }

            foreach (Entry entry in vocabulary)
            {
                if (ownedKeys.Contains(entry.Key))
                    continue;   // already listed above, with the spelling that is really in the chests
                if (entry.Key.StartsWith(query, StringComparison.Ordinal))
                    hits.Add(new Hit(entry, owned: false, exact: entry.Key.Length == query.Length));
            }

            hits.Sort(CompareHits);

            var result = new List<Suggestion>(Math.Min(max, hits.Count));
            for (int i = 0; i < hits.Count && i < max; i++)
                result.Add(new Suggestion(hits[i].Entry.Text, MatchedLength(hits[i].Entry.Text, query.Length), ModEntry.IsCategoryWord(hits[i].Entry.Text)));
            return result;
        }

        private static int CompareHits(Hit a, Hit b)
        {
            if (a.Exact != b.Exact)
                return a.Exact ? -1 : 1;
            if (a.Owned != b.Owned)
                return a.Owned ? -1 : 1;

            int byLength = a.Entry.Key.Length.CompareTo(b.Entry.Key.Length);
            if (byLength != 0)
                return byLength;

            int byName = string.Compare(a.Entry.Text, b.Entry.Text, StringComparison.CurrentCultureIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Entry.Text, b.Entry.Text);
        }

        /// <summary>How many characters of the displayed name the typed key covers (separators in the name are skipped when counting).</summary>
        private static int MatchedLength(string text, int keyLength)
        {
            int counted = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (IsSeparator(text[i]))
                    continue;
                counted++;
                if (counted == keyLength)
                    return i + 1;
            }
            return text.Length;
        }
    }
}