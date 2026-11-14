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

        /// <summary>
        /// The qualified id of the item this suggestion stands for ("(O)348"), or of the plain base product for a name the game builds on the fly
        /// ("Peach Wine" -> "(O)348"). The drop-down asks the game for the category of this item (hover tip). Null when it is not known.
        /// </summary>
        public readonly string? QualifiedId;

        /// <summary>
        /// Set only for a "popular item of the category" row (typing "fish" also offers Sturgeon, whose name does not contain "fish"): the name of the
        /// category the row was added for ("Fish"). The drop-down writes it dimmed after the name, so the player sees why the row is there. Null for every other row.
        /// </summary>
        public readonly string? ViaCategory;

        public Suggestion(string text, int matchedLength, bool isCategory = false, string? idLabel = null, string? qualifiedId = null, string? viaCategory = null)
        {
            Text = text;
            MatchedLength = matchedLength;
            IsCategory = isCategory;
            IdLabel = idLabel;
            QualifiedId = qualifiedId;
            ViaCategory = viaCategory;
        }
    }

    /// <summary>
    /// The word list behind the search box's auto-complete, and the prefix search over it.
    /// </summary>
    /// <remarks>
    /// Word list = every item the game has that a player can put into a chest (built once from the game's own item data,
    /// rebuilt only if the language or the item data changes) + the processed items the game builds on the fly ("Starfruit Wine", "Sturgeon Roe",
    /// "Aged Carp Roe", ... made up from every ingredient, see AddFlavoredNames, so they are offered whether or not a chest holds them)
    /// + the names of the items that are in the player's chests right now (this also catches items of other mods).
    ///
    /// Matching is by word: the typed text must be the START of the name or the START OF ANY WORD in it ("jel" finds "Jelly" and "Fishing Jelly",
    /// "goods" finds "Artisan Goods", "fish" finds "Smoked Fish"). Names that start with the text come first.
    /// Spaces, hyphens, underscores and case are ignored, exactly like the real search (see Norm in ModEntry), so
    /// "star fruit" finds "Starfruit" and "minif" finds "Mini-Fridge".
    ///
    /// Order of the drop-down:
    ///   1. the name that equals the typed text, then category words
    ///   2. names that START with the text before names where a later word starts with it
    ///   3. at the same way of matching: furniture / decoration / cooking / trash last, late-game items just before them
    ///   4. the items a player uses the most (ItemRanking), then items that are in your chests, then shorter names, then alphabetical
    /// On top of that order, THE GLOBAL SLOT RULE (see below) fills the first three rows: best match, most central item, item held the most of.
    /// "Did you mean" (FindSimilar) uses the same slot rule on its own spelling-based match order.
    /// </remarks>
    internal sealed class SuggestionIndex
    {
        private readonly struct Entry
        {
            public readonly string Text;        // as shown
            public readonly string Key;         // lower-case, without spaces / hyphens / underscores
            public readonly int[] WordStarts;   // index in Key where each word of the name begins ("Fishing Jelly" -> key "fishingjelly" -> 0, 7)
            public readonly string? ItemId;     // qualified id of the item (or of its base product), for the category hover tip; null = unknown
            public readonly bool Built;         // a name the game builds on the fly ("Starfruit Wine"): ItemId is then the id of the BASE PRODUCT (Wine), not of the ingredient
            public readonly bool Unholdable;    // an item the game has but a player can never carry (Weeds, Twig, quest items): kept ONLY to recognise the name and to correct typos of it; never in the drop-down, never in a tip

            public Entry(string text, string key, string? itemId = null, bool built = false, bool unholdable = false)
            {
                Text = text;
                Key = key;
                ItemId = itemId;
                Built = built;
                Unholdable = unholdable;
                WordStarts = FindWordStarts(text);
            }
        }

        private readonly struct Hit
        {
            public readonly Entry Entry;
            public readonly bool Owned;
            public readonly bool Exact;
            public readonly int Rank;   // 0 = the typed text starts the whole name ("jel" -> "Jelly"), 1 = it starts a later word ("jel" -> "Fishing Jelly")
            public readonly bool IsCategory;   // the search treats this name as a whole category ("Roe", "Aged Roe", "Fishing Jelly" ...)
            public readonly int Demote;        // see DemoteLevel: 0 normal, 1 late-game, 2 furniture / decoration / cooking / trash
            public readonly (int Tier, int Progress) Usage;
            public readonly int OwnedCount;    // how many the chests hold (0 = none)

            public Hit(Entry entry, bool owned, bool exact, int rank, int ownedCount)
            {
                Entry = entry;
                Owned = owned;
                Exact = exact;
                Rank = rank;
                bool isCategory = ModEntry.IsCategoryWord(entry.Text);
                IsCategory = isCategory;
                Demote = isCategory ? 0 : DemoteLevel(entry.ItemId);
                Usage = isCategory ? (0, 0) : ItemRanking.Get(entry.ItemId);
                OwnedCount = ownedCount;
            }
        }

        // ===================== THE GLOBAL SLOT RULE (drop-down AND "Did you mean") =====================
        // Every list of suggestions is built from candidates that already passed the match test (the drop-down: the typed text starts the name or one
        // of its words; "Did you mean": the spelling floor). Among those, the first three places are filled by a fixed plan:
        //   slot 1: the closest match (for the drop-down: the exact name first, then the best way of matching);
        //   slot 2: the most central, most used item of the game among the rest (curated CoreItems first: ores, bars, coal, Starfruit ...; then
        //           everyday resources by ItemRanking). NEVER furniture, decoration, cooked dishes or trash (DemoteLevel 2);
        //   slot 3: the related item the chests hold the most of (stacks added up); if none of them is in a chest, simply the next best match.
        // Everything after that follows the plain match order. In that order, at the same match quality, furniture / decoration / cooking / trash
        // always come last ("dead last"), so they only show up when nothing else matches as well.
        // In the three slots, two names that are the same kind of thing ("Iridium Rod" and "Advanced Iridium Rod": the same last word, and that word
        // is not what the player typed) never take two slots while something different is left; the second one simply moves down.

        /// <summary>How many places are filled by the slot plan.</summary>
        private const int PlannedSlots = 3;

        /// <summary>
        /// The core items of the game (internal English names), most central first. Slot 2 picks the first of them that is a candidate, before any
        /// other item. Ores and bars come first on purpose: they are what nearly every typo of "iridium", "gold", "copper" ... is looking for.
        /// </summary>
        private static readonly string[] CoreItems =
        {
            "Iridium Bar", "Iridium Ore", "Gold Bar", "Gold Ore", "Iron Bar", "Iron Ore", "Copper Bar", "Copper Ore", "Radioactive Bar", "Radioactive Ore",
            "Coal", "Refined Quartz", "Battery Pack", "Prismatic Shard", "Diamond", "Starfruit", "Ancient Fruit", "Hardwood", "Wood", "Stone", "Clay", "Fiber",
            "Sturgeon", "Caviar", "Truffle", "Truffle Oil", "Cheese", "Mayonnaise", "Cloth", "Pumpkin", "Cranberries", "Melon", "Blueberry",
            "Sea Cucumber", "Sea Urchin", "Seaweed", "Omni Geode", "Magma Geode", "Frozen Geode", "Geode", "Bone Fragment", "Solar Essence", "Void Essence"
        };

        private Dictionary<string, int>? coreRankCache;
        private object? coreRankBuiltFor;

        /// <summary>Key of a core item -> its place in <see cref="CoreItems"/> (found by the English name and by the game's internal name, so it works in every language).</summary>
        private Dictionary<string, int> CoreRanks
        {
            get
            {
                if (coreRankCache != null && ReferenceEquals(coreRankBuiltFor, itemsByInternalName))
                    return coreRankCache;

                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < CoreItems.Length; i++)
                {
                    map.TryAdd(MakeKey(CoreItems[i]), i);
                    if (itemsByInternalName.TryGetValue(CoreItems[i], out Entry entry))
                        map.TryAdd(entry.Key, i);
                }

                coreRankBuiltFor = itemsByInternalName;
                return coreRankCache = map;
            }
        }

        /// <summary>
        /// How far an item is pushed down at the same match quality: 0 = normal, 1 = late-game / mostly for fun (Qi Fruit, legendary fish ...),
        /// 2 = furniture, hats, clothing, decoration (floors, paths, fences), trash and cooked dishes. Unknown items (another mod's item that only lies in a
        /// chest) are normal: the player really has them.
        /// </summary>
        private static int DemoteLevel(string? qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return 0;
            if (ItemRanking.IsJunk(qualifiedId))
                return 2;
            return ItemRanking.IsLate(qualifiedId) ? 1 : 0;
        }

        /// <summary>The "how central is it" order of slot 2: curated core items first (in their order), then the everyday ranking (tier, then progress).</summary>
        private long CoreScore(Entry entry, (int Tier, int Progress) usage)
        {
            if (CoreRanks.TryGetValue(entry.Key, out int index))
                return index;
            return 1000L + usage.Tier * 1000L + usage.Progress;
        }

        /// <summary>The key of the last word of a name ("Advanced Iridium Rod" -> "rod").</summary>
        private static string LastWordKey(Entry entry)
        {
            int[] starts = entry.WordStarts;
            return starts.Length == 0 ? entry.Key : entry.Key.Substring(starts[starts.Length - 1]);
        }

        /// <summary>
        /// True if two names are the same kind of thing for the slot plan: both are items (no category), their last word is the same (at least three
        /// letters), and that word is not part of what the player typed. "Iridium Rod" / "Advanced Iridium Rod" for "irido" yes; "Bed" / "Child Bed"
        /// for "bedd" no (the player typed "bed", so those ARE what is asked for).
        /// </summary>
        private static bool IsNearDuplicate(Entry a, bool aCategory, Entry b, bool bCategory, string query)
        {
            if (aCategory || bCategory || a.Key == b.Key)
                return false;

            string last = LastWordKey(a);
            return last.Length >= 3 && last == LastWordKey(b) && !query.Contains(last, StringComparison.Ordinal);
        }

        /// <summary>
        /// Fills the planned slots of an ordered candidate list and appends the rest in order. <paramref name="ordered"/> is the match order
        /// (best first). <paramref name="coreEligible"/> says which candidates may take slot 2; <paramref name="coreScore"/> ranks them (smaller =
        /// more central); <paramref name="ownedCount"/> gives the chest count for slot 3. Returns the indices into <paramref name="ordered"/>.
        /// </summary>
        private static List<int> ApplySlotPlan(int count, int max, Func<int, bool> coreEligible, Func<int, long> coreScore, Func<int, int> ownedCount,
            Func<int, int, bool> nearDuplicate)
        {
            var picked = new List<int>(Math.Min(max, count));
            var taken = new bool[count];

            bool Clashes(int i)
            {
                if (picked.Count >= PlannedSlots)
                    return false;
                foreach (int p in picked)
                {
                    if (nearDuplicate(p, i))
                        return true;
                }
                return false;
            }

            // the best candidate that passes the filter, by the given order (null order = list order); duplicates of what is already picked are
            // skipped while anything else is left
            int Pick(Func<int, bool> filter, Comparison<int>? better)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int best = -1;
                    for (int i = 0; i < count; i++)
                    {
                        if (taken[i] || !filter(i) || (pass == 0 && Clashes(i)))
                            continue;
                        if (best < 0 || (better != null && better(i, best) < 0))
                            best = i;
                        if (better == null)
                            break;
                    }
                    if (best >= 0)
                        return best;
                }
                return -1;
            }

            void Take(int i)
            {
                if (i < 0 || taken[i] || picked.Count >= max)
                    return;
                taken[i] = true;
                picked.Add(i);
            }

            // slot 1: the closest match
            Take(Pick(_ => true, null));

            // slot 2: the most central item of the game (never furniture / decoration / cooking / trash)
            if (picked.Count < max && picked.Count < PlannedSlots)
            {
                int core = Pick(coreEligible, (a, b) => { int c = coreScore(a).CompareTo(coreScore(b)); return c != 0 ? c : a.CompareTo(b); });
                Take(core >= 0 ? core : Pick(_ => true, null));
            }

            // slot 3: the related item the chests hold the most of; none in a chest = the next best match
            if (picked.Count < max && picked.Count < PlannedSlots)
            {
                int held = Pick(i => ownedCount(i) > 0, (a, b) => { int c = ownedCount(b).CompareTo(ownedCount(a)); return c != 0 ? c : a.CompareTo(b); });
                Take(held >= 0 ? held : Pick(_ => true, null));
            }

            // the rest, in match order
            for (int i = 0; i < count && picked.Count < max; i++)
                Take(i);

            return picked;
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
        /// The hover tips use the same list to name a category ("fruits" -> "Fruit", "artisan" -> "Artisan Goods", see ModEntry.CategoryDisplayName).
        /// </summary>
        private static string[]? categoryWordsCache;

        /// <summary>
        /// Every category the search understands: the curated spellings below PLUS any category of the search tables that has no curated spelling
        /// yet (e.g. "litter", id -999). One spelling per category, so the drop-down is not flooded with synonyms. This list feeds the auto-complete,
        /// "Did you mean", the partial-category search and the hover tips, so a category can never be understood by the search but invisible to them.
        /// </summary>
        internal static string[] CategoryWords => categoryWordsCache ??= BuildCategoryWords();

        private static string[] BuildCategoryWords()
        {
            var words = new List<string>(CuratedCategoryWords);
            var covered = new HashSet<string>();
            foreach (string word in words)
            {
                string? key = ModEntry.CategoryKey(word);
                if (key != null)
                    covered.Add(key);
            }

            foreach (string word in ModEntry.GetSingleWordCategoryNames().OrderBy(w => w.Length).ThenBy(w => w, StringComparer.Ordinal))
            {
                string? key = ModEntry.CategoryKey(word);
                if (key != null && covered.Add(key))
                    words.Add(word);
            }

            return words.ToArray();
        }

        private static readonly string[] CuratedCategoryWords =
        {
            "aged roe", "animal product", "artisan goods", "bait", "big craftable", "book", "boots", "building resources", "clothing", "cooking", "crafting",
            "decor", "dried fruit", "dried mushrooms", "egg", "equipment", "fertilizer", "fish", "fishing jelly", "fishing tackle", "flower", "forage", "fruit", "furniture", "gem", "hat",
            "honey", "ingredient", "jelly", "juice", "junk",
            "meat", "metal resources", "milk", "mineral", "monster loot", "pickles", "ring", "roe", "seed", "skill book", "smoked fish", "syrup", "tackle", "targeted bait", "tool",
            "trash", "trinket", "vegetable", "weapon", "wine"
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

        // ===================== FAKE ITEMS: NEVER IN THE LIBRARY =====================
        // The game keeps a plain "base product" for every item it builds on the fly: Wine (348) is the data behind "Peach Wine", Jelly (344) behind
        // "Apple Jelly" ... In normal play these plain versions never exist (every keg / jar / dehydrator / smoker / pond output carries its ingredient);
        // only a cheat menu (CJB Item Spawner) can spawn them. So they are not items of the library at all: not in the drop-down, not in "Did you mean",
        // not in the hover tips, not as an id ("348"), and not even when a cheat put one into a chest. Their names stay what they really are in the
        // search: category words ("wine" = every wine). The flavored names built from them ("Peach Wine") still use their id for the icon / category tip.
        // Wild Honey (340) is NOT on this list: a bee house with no flower nearby really makes it.
        // To ban another cheat-only object, add its object id (unqualified, as in Data/Objects) here.
        private static readonly HashSet<string> CheatOnlyObjectIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "348",              // Wine
            "344",              // Jelly
            "350",              // Juice
            "342",              // Pickles
            "812",              // Roe
            "447",              // Aged Roe
            "DriedFruit",       // Dried Fruit
            "DriedMushrooms",   // Dried Mushrooms
            "SmokedFish",       // Smoked Fish
            "SpecificBait"      // Targeted Bait
        };

        /// <summary>True for an object id of <see cref="CheatOnlyObjectIds"/> (qualified "(O)348" or plain "348").</summary>
        internal static bool IsCheatOnlyObject(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            if (id.StartsWith("(O)", StringComparison.Ordinal))
                id = id.Substring(3);
            return CheatOnlyObjectIds.Contains(id);
        }

        /// <summary>Keys of the display names of the cheat-only objects ("wine", "jelly" ...): such a name in a chest is never taken as an item.</summary>
        private HashSet<string> cheatOnlyKeys = new();

        /// <summary>
        /// Items that are only listed when their category is expanded in the drop-down (typing "clothing" lists every shirt and pair of pants):
        /// clothing is left out of the normal word list on purpose (hundreds of look-alike names), but a player who asks for the category wants to see it.
        /// </summary>
        private Entry[] categoryOnlyEntries = Array.Empty<Entry>();

        /// <summary>Item kinds that are only used for the category expansion (see <see cref="categoryOnlyEntries"/>): shirts and pants.</summary>
        private static readonly HashSet<string> CategoryOnlyItemTypes = new() { "(S)", "(P)" };

        private Entry[] vocabulary = Array.Empty<Entry>();
        private HashSet<string> clutterKeys = new();          // keys of the unholdable names that are pure map clutter (Weeds, Twig, Stone): the only unholdable names the drop-down offers
        private HashSet<string> unobtainableKeys = new();      // display names of map nodes and the like (Amethyst Stone ...): not in the library, and never taken from a chest either
        private Entry[] unholdableEntries = Array.Empty<Entry>();   // Weeds, Twig, quest items ...: names the game HAS but no chest can hold (see Entry.Unholdable)
        private Dictionary<string, Entry> itemsByInternalName = new(StringComparer.OrdinalIgnoreCase);   // the game's internal English name ("Lava Eel") -> its entry: language independent, for the popular-item tables
        private readonly Dictionary<string, List<Suggestion>> popularCache = new();                   // category -> all of its items, ordered (cleared when the vocabulary is rebuilt)
        private Dictionary<string, Entry> realItemsByKey = new();   // key -> the real item of that name (no built names, no category words): finds the ingredient of "Starfruit Wine"
        private IdEntry[] idVocabulary = Array.Empty<IdEntry>();
        private Entry[] ownedEntries = Array.Empty<Entry>();
        private HashSet<string> ownedKeys = new();
        private Dictionary<string, int> ownedCounts = new();   // key -> how many of it the chests hold (all stacks added up)

        // what the vocabulary was built from, so it can be rebuilt when the player changes language or a mod reloads the item data
        private object? builtFromObjectData;
        private LocalizedContentManager.LanguageCode builtLanguage;

        /// <summary>
        /// Where each word of a name begins, as an index into its key (the key has no spaces / hyphens / underscores, so those are skipped while counting).
        /// "Artisan Goods" -> key "artisangoods" -> 0, 7.
        /// </summary>
        private static int[] FindWordStarts(string text)
        {
            var starts = new List<int>();
            int keyIndex = 0;
            bool atWordStart = true;

            foreach (char ch in text)
            {
                if (IsSeparator(ch))
                {
                    atWordStart = true;
                    continue;
                }

                if (atWordStart)
                    starts.Add(keyIndex);
                atWordStart = false;
                keyIndex++;
            }

            return starts.ToArray();
        }

        /// <summary>
        /// How the typed key matches an entry: 0 = it is the start of the whole name, 1 = it is the start of a LATER word (or of several words
        /// in a row: "goods" and "ancient fr" both work), -1 = no match.
        /// </summary>
        private static int GetMatchRank(Entry entry, string query)
        {
            if (entry.Key.StartsWith(query, StringComparison.Ordinal))
                return 0;

            foreach (int start in entry.WordStarts)
            {
                if (start > 0 && entry.Key.AsSpan(start).StartsWith(query.AsSpan(), StringComparison.Ordinal))
                    return 1;
            }

            return -1;
        }

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

            clutterCanonicalIds = BuildClutterCanonicalIds();
            var newClutterKeys = new HashSet<string>();
            var newUnobtainableKeys = new HashSet<string>();

            var byKey = new Dictionary<string, Entry>();
            var byInternal = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var unholdableByKey = new Dictionary<string, Entry>();
            var unholdableInternalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // the game's internal English name ("Weeds") -> key of its entry, so the junk blacklist also finds it in other languages
            var newCheatOnlyKeys = new HashSet<string>();
            var clothingByKey = new Dictionary<string, Entry>();

            foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
            {
                if (CategoryOnlyItemTypes.Contains(type.Identifier))
                {
                    // shirts and pants: only for the category expansion ("clothing"), never in the normal word list
                    foreach (string id in type.GetAllIds())
                    {
                        ParsedItemData? clothingData = type.GetData(id);
                        if (clothingData == null || IsNonPlayerItem(clothingData))
                            continue;
                        string? clothingName = clothingData.DisplayName?.Trim();
                        if (!IsUsableName(clothingName))
                            continue;
                        string clothingKey = MakeKey(clothingName);
                        if (clothingKey.Length > 0 && !clothingByKey.ContainsKey(clothingKey))
                            clothingByKey[clothingKey] = new Entry(clothingName, clothingKey, type.Identifier + id);
                    }
                    continue;
                }

                if (!IncludedItemTypes.Contains(type.Identifier))
                    continue;

                bool isObject = type.Identifier == "(O)";

                foreach (string id in type.GetAllIds())
                {
                    if (isObject && CheatOnlyObjectIds.Contains(id))
                    {
                        // a plain base product (Wine, Jelly ...): only a cheat menu can make it, so it is no item of the library (see CheatOnlyObjectIds)
                        string? fakeName = type.GetData(id)?.DisplayName?.Trim();
                        if (IsUsableName(fakeName))
                            newCheatOnlyKeys.Add(MakeKey(fakeName));
                        continue;
                    }

                    if (isObject && !CanBeHeld(id))
                    {
                        // Weeds, Twig, quest items: no chest can hold them, so they are NOT offered by the drop-down and never listed in a tip.
                        // But they ARE words of the game: "weeds" is not gibberish, and a typo like "weds" must be able to find "Weeds".
                        ParsedItemData? unholdableData = type.GetData(id);
                        string? unholdableName = unholdableData?.DisplayName?.Trim();
                        if (IsUsableName(unholdableName))
                        {
                            string unholdableKey = MakeKey(unholdableName);
                            Game1.objectData.TryGetValue(id, out ObjectData? unholdableObject);
                            bool clutter = unholdableObject != null && IsPureMapClutter(id, unholdableObject);
                            bool quest = unholdableObject != null && string.Equals(unholdableObject.Type, "Quest", StringComparison.OrdinalIgnoreCase);
                            if (!clutter && !quest)
                            {
                                // a map node (Amethyst Stone ...) or another tile that is no item: not part of the library at all
                                newUnobtainableKeys.Add(unholdableKey);
                                continue;
                            }

                            if (clutter)
                                newClutterKeys.Add(unholdableKey);

                            string unholdableQualifiedId = clutter ? CanonicalItemId(type.Identifier + id) : type.Identifier + id;
                            if (unholdableKey.Length > 0 && (!unholdableByKey.TryGetValue(unholdableKey, out Entry prevUnholdable) || IsLowerObjectId(unholdableQualifiedId, prevUnholdable.ItemId)))
                                unholdableByKey[unholdableKey] = new Entry(unholdableName, unholdableKey, unholdableQualifiedId, unholdable: true);
                            if (unholdableKey.Length > 0 && !string.IsNullOrEmpty(unholdableData!.InternalName) && !unholdableInternalKeys.ContainsKey(unholdableData.InternalName))
                                unholdableInternalKeys[unholdableData.InternalName] = unholdableKey;
                        }
                        continue;
                    }

                    ParsedItemData? itemData = type.GetData(id);
                    if (itemData == null || IsNonPlayerItem(itemData))
                        continue;   // an id the game cannot resolve into a real item (error / debug / placeholder) is never part of the list
                    string? name = itemData.DisplayName?.Trim();
                    if (!IsUsableName(name))
                        continue;

                    string key = MakeKey(name);
                    if (key.Length > 0 && !byKey.ContainsKey(key))
                    {
                        byKey[key] = new Entry(name, key, type.Identifier + id);
                        if (isObject && !string.IsNullOrEmpty(itemData!.InternalName) && !byInternal.ContainsKey(itemData.InternalName))
                            byInternal[itemData.InternalName] = byKey[key];
                    }
                }
            }

            // names the game builds on the fly ("Sturgeon Roe", "Starfruit Wine", ...): they are in no item list, so they are made up here
            AddFlavoredNames(byKey);

            // category words: the search treats them as whole categories, so they are offered as well (and marked in the drop-down)
            foreach (string word in CategoryWords)
            {
                if (!ModEntry.IsCategoryWord(word))
                    continue;

                string categoryKey = MakeKey(word);
                if (categoryKey.Length > 0 && !byKey.ContainsKey(categoryKey))
                    byKey[categoryKey] = new Entry(word, categoryKey);
            }

            clutterKeys = newClutterKeys;
            unobtainableKeys = newUnobtainableKeys;
            foreach (string key in byKey.Keys)
                unobtainableKeys.Remove(key);   // a name that a carryable item also has ("Stone") is an item

            // THE one Weeds: every weeds variant (Weeds, Green Rain Weeds ...) is merged into the classic Weeds (object 313), which is the only
            // weeds item that is listed and suggested. The other variants stay only as internal known words (see unholdableEntries).
            string weedsKey = MakeKey("Weeds");
            ParsedItemData? weedsData = ItemRegistry.GetData("(O)" + ClassicWeedsObjectId);
            if (weedsData != null && !weedsData.IsErrorItem && !byKey.ContainsKey(weedsKey))
            {
                var weedsEntry = new Entry("Weeds", weedsKey, "(O)" + ClassicWeedsObjectId);
                byKey[weedsKey] = weedsEntry;
                byInternal["Weeds"] = weedsEntry;
            }

            // a name that a holdable item (or a built name / category word) already has is not listed a second time ("Stone" exists both ways)
            unholdableEntries = unholdableByKey.Values.Where(entry => !byKey.ContainsKey(entry.Key)).ToArray();
            foreach (KeyValuePair<string, string> pair in unholdableInternalKeys)
            {
                if (!byInternal.ContainsKey(pair.Key) && unholdableByKey.TryGetValue(pair.Value, out Entry unholdableEntry) && !byKey.ContainsKey(unholdableEntry.Key))
                    byInternal[pair.Key] = unholdableEntry;
            }

            vocabulary = byKey.Values.ToArray();
            cheatOnlyKeys = newCheatOnlyKeys;
            var vocabularyKeys = new HashSet<string>(byKey.Keys);
            categoryOnlyEntries = clothingByKey.Values.Where(e => !vocabularyKeys.Contains(e.Key)).ToArray();
            realItemsByKey = vocabulary.Where(e => !e.Built && e.ItemId != null).ToDictionary(e => e.Key, e => e);
            itemsByInternalName = byInternal;
            popularCache.Clear();
            idVocabulary = BuildIdVocabulary();
            builtFromObjectData = Game1.objectData;
            builtLanguage = LocalizedContentManager.CurrentLanguageCode;
        }

        /// <summary>Fish with these context tags are not pond fish: they never produce roe (and legendary fish are not processed either).</summary>
        private static readonly string[] NoProcessingFishTags = { "fish_legendary", "fish_legendary_family", "fish_crab_pot" };

        /// <summary>Fishing products (1.6) that are Fish-type items but are not fish: no roe, no smoked version.</summary>
        private static readonly HashSet<string> NotRealFishIds = new(StringComparer.OrdinalIgnoreCase) { "SeaJelly", "RiverJelly", "CaveJelly" };

        /// <summary>
        /// Adds the names of the processed items the game builds on the fly. They have no entry in the item data (every Wine is (O)348), so they
        /// are made up from the ingredients, by the same rules the machines use:
        ///   fruit        -> "{X} Wine", "{X} Jelly", "Dried {X}" (not grapes: they become raisins)
        ///   vegetable    -> "{X} Juice", "Pickled {X}"
        ///   flower       -> "{X} Honey"
        ///   mushroom     -> "Dried {X}"   (items with the "mushroom_item" tag)
        ///   fish         -> "{X} Roe", "Aged {X} Roe" (not sturgeon: its aged roe is Caviar, an item of its own), "Smoked {X}", "{X} Bait"
        ///                   (roe and bait: not legendary fish and not crab pot catches)
        /// An item can also opt in with the machine tags keg_wine / keg_juice / preserves_jelly / preserves_pickle (other mods' ingredients).
        /// X is the game's display name of the ingredient in English; in other languages the English item name is used, because the search also
        /// looks at the English name of every item. A name that already exists (real item or category word) is never replaced.
        /// </summary>
        private static void AddFlavoredNames(Dictionary<string, Entry> byKey)
        {
            bool english = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.en;

            // baseId = the plain product the name belongs to ("Peach Wine" -> (O)348), so the hover tip can show its category (Artisan Goods)
            void Add(string text, string baseId)
            {
                if (!IsUsableName(text))
                    return;

                string key = MakeKey(text);
                if (key.Length > 0 && !byKey.ContainsKey(key))
                    byKey[key] = new Entry(text, key, baseId, built: true);
            }

            foreach (KeyValuePair<string, ObjectData> pair in Game1.objectData)
            {
                string id = pair.Key;
                ObjectData? data = pair.Value;
                if (data == null || !CanBeHeld(id))
                    continue;

                string? shown = english ? ItemRegistry.GetData("(O)" + id)?.DisplayName?.Trim() : null;
                if (!IsUsableName(shown))
                    shown = data.Name?.Trim();
                if (!IsUsableName(shown))
                    continue;
                string name = shown;

                List<string> tags = data.ContextTags ?? new List<string>();
                int category = data.Category;
                bool isFruit = category == -79;
                bool isVegetable = category == -75;
                bool isFlower = category == -80;
                bool isFish = category == -4 && string.Equals(data.Type, "Fish", StringComparison.OrdinalIgnoreCase) && !NotRealFishIds.Contains(id);

                if (isFruit || tags.Contains("keg_wine"))
                    Add(name + " Wine", "(O)348");
                if (isFruit || tags.Contains("preserves_jelly"))
                    Add(name + " Jelly", "(O)344");
                if (isVegetable || tags.Contains("keg_juice"))
                    Add(name + " Juice", "(O)350");
                if (isVegetable || tags.Contains("preserves_pickle"))
                    Add("Pickled " + name, "(O)342");
                if (isFlower)
                    Add(name + " Honey", "(O)340");

                bool isGrape = string.Equals(data.Name, "Grape", StringComparison.OrdinalIgnoreCase);
                if (isFruit && !isGrape)
                    Add("Dried " + name, "(O)DriedFruit");
                else if (tags.Contains("mushroom_item"))
                    Add("Dried " + name, "(O)DriedMushrooms");

                if (isFish && !tags.Any(tag => Array.IndexOf(NoProcessingFishTags, tag) >= 0))
                {
                    Add(name + " Roe", "(O)812");
                    if (id != "698")   // sturgeon: its aged roe is Caviar
                        Add("Aged " + name + " Roe", "(O)447");
                    Add(name + " Bait", "(O)SpecificBait");
                }

                if (isFish && !tags.Contains("fish_legendary") && !tags.Contains("fish_legendary_family"))
                    Add("Smoked " + name, "(O)SmokedFish");
            }
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
                        || !CanBeHeld(id)
                        || CheatOnlyObjectIds.Contains(id))   // "348" is the cheat-only plain Wine: not offered as an id
                        continue;

                    string? label = SearchMenu.TryGetIdName(id);
                    if (label != null)
                        entries.Add(new IdEntry(id, number, label));
                }
            }

            foreach (int categoryId in SearchMenu.CategoryNamesById.Keys)
            {

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

        /// <summary>Many sprite variants share one name (dozens of "Weeds"): the lowest numeric object id is the one representative whose icon is shown.</summary>
        private static bool IsLowerObjectId(string qualifiedNew, string? qualifiedOld)
        {
            if (qualifiedOld == null)
                return true;
            return int.TryParse(qualifiedNew.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && (!int.TryParse(qualifiedOld.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out int o) || n < o);
        }

        /// <summary>The game's category id of litter (Object.litterCategory).</summary>
        private const int LitterCategoryId = -999;

        /// <summary>
        /// True for an object that is pure map clutter: weeds, twigs, plain stones. Such an object is "litter" in the game's data (category -999),
        /// which also makes it impossible to carry (see <see cref="CanBeHeld"/>), so anything a player CAN put into the inventory is never litter.
        /// Everything else with the litter category (mine nodes like Amethyst Stone / Aquamarine Stone / Cinder Shard Stone, dig spots ...) is a map
        /// tile, not an item: it is not clutter and not obtainable, so the search library does not contain it at all.
        /// Both names of the object must say clutter (the internal English name AND the display name), so a node that shares the internal name
        /// "Stone" but is shown as "Amethyst Stone" is still recognised as a node.
        /// </summary>
        private static bool IsPureMapClutter(string objectId, ObjectData data)
        {
            if (string.Equals(data.Type, "Quest", StringComparison.OrdinalIgnoreCase) || data.Category != LitterCategoryId)
                return false;

            string? displayName = ItemRegistry.GetData("(O)" + objectId)?.DisplayName;
            return IsClutterName(data.Name) && IsClutterName(displayName);
        }

        /// <summary>Weeds (any variant, "Green Rain Weeds"), Twig, and a plain Stone. Spaces and case are ignored.</summary>
        internal static bool IsClutterName(string? name)
        {
            string key = MakeKey(name ?? "");
            return key.Length > 0
                && (key == "weed" || key.EndsWith("weeds", StringComparison.Ordinal) || key == "twig" || key == "stone");
        }

        /// <summary>object id ("0", "313") -> the qualified id of the ONE representative of its clutter family (Weeds -> "(O)0"). Built with the vocabulary.</summary>
        private static Dictionary<string, string> clutterCanonicalIds = new();

        /// <summary>
        /// The standard sprite of an object whose game data has several look-alike variants (the dozens of Weeds with their own sprites): all variants
        /// of the same internal name map to the one with the lowest numeric id (Weeds -> "(O)0", the classic weed). Anything else is returned unchanged.
        /// </summary>
        public static string CanonicalItemId(string qualifiedId)
        {
            return qualifiedId.StartsWith("(O)", StringComparison.Ordinal)
                && clutterCanonicalIds.TryGetValue(qualifiedId.Substring(3), out string? canonical)
                    ? canonical
                    : qualifiedId;
        }

        /// <summary>
        /// THE Weeds icon: the object id whose sprite is shown for every Weeds in the mod (drop-down, tips, result panel). The game has many Weeds
        /// objects with their own sprites; this is the classic one with the bright yellow center (the second sprite of the Weeds sheet:
        /// first row, second picture). Change this one id to switch the icon everywhere. If no such object exists (another game version),
        /// the Weeds fall back to the lowest id of their family.
        /// </summary>
        internal const string ClassicWeedsObjectId = "313";

        /// <summary>
        /// True for a Weeds variant: the name has the WHOLE word "weed" or "weeds" in it ("Weeds", "Green Rain Weeds"). "Seaweed" does not count
        /// (it is one word), so it is never merged into the weeds.
        /// </summary>
        internal static bool IsWeedsName(string? name)
        {
            foreach (string word in (name ?? "").Split(new[] { ' ', '\t', '_', '-', '\'', '(', ')', ',', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Equals("weed", StringComparison.OrdinalIgnoreCase) || word.Equals("weeds", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Debug / placeholder objects that are in the item data but are not items of the normal game.</summary>
        private static bool IsNonPlayerItem(ParsedItemData data)
        {
            if (data.IsErrorItem)
                return true;
            string internalName = data.InternalName ?? "";
            return internalName.Length == 0
                || internalName.StartsWith("Debug", StringComparison.OrdinalIgnoreCase)
                || internalName.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || internalName.StartsWith("Placeholder", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> BuildClutterCanonicalIds()
        {
            var lowestByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ObjectData> pair in Game1.objectData)
            {
                if (pair.Value == null || !IsPureMapClutter(pair.Key, pair.Value))
                    continue;

                string nameKey = MakeKey(pair.Value.Name ?? "");
                if (!lowestByName.TryGetValue(nameKey, out string? previous) || IsLowerObjectId("(O)" + pair.Key, "(O)" + previous))
                    lowestByName[nameKey] = pair.Key;
            }

            // the Weeds family always shows the one classic sprite
            string weedsKey = MakeKey("Weeds");
            // forced: every Weeds variant draws the sprite of object 314 (springobjects.png), as long as that object exists in the game data
            if (lowestByName.ContainsKey(weedsKey) && Game1.objectData.ContainsKey(ClassicWeedsObjectId))
            {
                lowestByName[weedsKey] = ClassicWeedsObjectId;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ObjectData> pair in Game1.objectData)
            {
                if (pair.Value != null && IsPureMapClutter(pair.Key, pair.Value))
                    map[pair.Key] = "(O)" + lowestByName[MakeKey(pair.Value.Name ?? "")];
            }

            return map;
        }

        private static string? litterCategoryNameCache;

        /// <summary>The name the game gives to its litter category (what a Weeds item reports as its category), or null.</summary>
        internal static string? LitterCategoryName
        {
            get
            {
                if (litterCategoryNameCache != null)
                    return litterCategoryNameCache;

                try
                {
                    foreach (KeyValuePair<string, ObjectData> pair in Game1.objectData)
                    {
                        if (pair.Value == null || pair.Value.Category != LitterCategoryId)
                            continue;
                        string? name = ItemRegistry.Create("(O)" + pair.Key).getCategoryName()?.Trim();
                        if (!string.IsNullOrEmpty(name))
                            return litterCategoryNameCache = name;
                    }
                }
                catch (Exception)
                {
                    // no name rather than a crash
                }

                return null;
            }
        }

        /// <summary>
        /// A name a player can look for: the object of that name can be carried (so it can lie in a chest). Quest items and everything with the
        /// litter category can not. Judged by the game's category id only: a type label alone does not make an item un-carryable.
        /// </summary>
        private static bool CanBeHeld(string objectId)
        {
            if (!Game1.objectData.TryGetValue(objectId, out ObjectData? data) || data == null)
                return false;

            return !string.Equals(data.Type, "Quest", StringComparison.OrdinalIgnoreCase)
                && data.Category != LitterCategoryId;
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

        /// <summary>
        /// True if the text is EXACTLY a name the game has, or exactly one whole word (or a run of whole words) of a longer name: "wedding ring" and
        /// "ore" ("Copper Ore") yes, "wed" no (only a part of "Wedding"). Much stricter than <see cref="AnyNameContains"/>: it is what a singular spelling
        /// that was made by cutting the plural "s" off a typed word has to pass before the search may use it (ModEntry.GetSearchVariants).
        /// Spaces, hyphens and case are ignored.
        /// </summary>
        public bool HasWholeNameOrWord(string rawText)
        {
            string key = MakeKey(rawText ?? "");
            if (key.Length == 0)
                return false;

            foreach (Entry entry in vocabulary)
            {
                if (IsWholeNameOrWords(entry, key))
                    return true;
            }

            foreach (Entry entry in ownedEntries)
            {
                if (IsWholeNameOrWords(entry, key))
                    return true;
            }

            foreach (Entry entry in unholdableEntries)
            {
                if (IsWholeNameOrWords(entry, key))
                    return true;   // "twigs" -> "twig": the shorter word is a real name of the game even though no chest can hold it
            }

            return false;
        }

        /// <summary>
        /// Like <see cref="AnyNameContains"/>, but a one-word text (no space / hyphen in it) only counts when it sits INSIDE one word of a name:
        /// "glasses" in "Broken Glasses" yes, "apples" in "Apple Sapling" no (the plain match only finds it because the spaces are ignored: "applesapling").
        /// A text of several words ("gold bar") is matched across words, like everywhere else. Used to decide whether a typed word "exists" before
        /// its plural "s" may be cut off (ModEntry.GetSearchVariants).
        /// </summary>
        public bool AnyNameContainsInOneWord(string rawText)
        {
            string text = (rawText ?? "").Trim();
            string key = MakeKey(text);
            if (key.Length == 0)
                return false;

            bool severalWords = text.Any(IsSeparator);
            foreach (Entry entry in vocabulary)
            {
                if (severalWords ? entry.Key.Contains(key, StringComparison.Ordinal) : ContainsInOneWord(entry, key))
                    return true;
            }

            foreach (Entry entry in ownedEntries)
            {
                if (severalWords ? entry.Key.Contains(key, StringComparison.Ordinal) : ContainsInOneWord(entry, key))
                    return true;
            }

            foreach (Entry entry in unholdableEntries)
            {
                if (severalWords ? entry.Key.Contains(key, StringComparison.Ordinal) : ContainsInOneWord(entry, key))
                    return true;
            }

            return false;
        }

        private static bool ContainsInOneWord(Entry entry, string key)
        {
            string name = entry.Key;
            int[] starts = entry.WordStarts;
            int from = 0;

            while (from <= name.Length - key.Length)
            {
                int position = name.IndexOf(key, from, StringComparison.Ordinal);
                if (position < 0)
                    return false;

                int word = Array.FindLastIndex(starts, start => start <= position);
                int wordEnd = word >= 0 && word + 1 < starts.Length ? starts[word + 1] : name.Length;
                if (position + key.Length <= wordEnd)
                    return true;   // the whole match lies inside one word

                from = position + 1;
            }

            return false;
        }

        private static bool IsWholeNameOrWords(Entry entry, string key)
        {
            string name = entry.Key;
            if (name == key)
                return true;

            int[] starts = entry.WordStarts;
            for (int s = 0; s < starts.Length; s++)
            {
                int begin = starts[s];
                int end = begin + key.Length;
                if (end > name.Length)
                    break;   // the word starts only get later, so nothing further along can fit either
                if (string.CompareOrdinal(name, begin, key, 0, key.Length) != 0)
                    continue;
                if (end == name.Length || Array.IndexOf(starts, end) >= 0)
                    return true;   // the match ends exactly where a word ends
            }

            return false;
        }

        /// <summary>
        /// True if any name the game has (every item that can lie in a chest, the names the game builds on the fly like "Peach Wine", the category
        /// words) or any name in the chests right now contains the text, anywhere (spaces, hyphens, underscores and case ignored).
        /// Used to tell a word that exists in the game but is in no chest ("an empty result") from a word nothing in the world matches ("Invalid").
        /// </summary>
        public bool AnyNameContains(string rawText)
        {
            string key = MakeKey(rawText ?? "");
            if (key.Length == 0)
                return false;

            foreach (Entry entry in vocabulary)
            {
                if (entry.Key.Contains(key, StringComparison.Ordinal))
                    return true;
            }

            foreach (Entry entry in ownedEntries)
            {
                if (entry.Key.Contains(key, StringComparison.Ordinal))
                    return true;
            }

            foreach (Entry entry in unholdableEntries)
            {
                if (entry.Key.Contains(key, StringComparison.Ordinal))
                    return true;   // Weeds, Twig ...: they exist in the game (an empty result), they are not "Invalid"
            }

            return false;
        }

        /// <summary>
        /// The qualified id of the carryable item whose name is exactly the text (spaces and case ignored), or null. Real items only: no built names, no
        /// category words, no map clutter. The hover tips ask this first, so a name always resolves to the item a player can really own.
        /// </summary>
        public string? GetHoldableItemId(string name)
        {
            return realItemsByKey.TryGetValue(MakeKey(name ?? ""), out Entry entry) ? entry.ItemId : null;
        }

        /// <summary>Remembers the items that are in the player's chests right now, with how many of each (all stacks added up).</summary>
        public void SetOwnedItems(IEnumerable<(string DisplayName, int Stack)> items)
        {
            var counts = new Dictionary<string, int>();
            var names = new List<string>();
            foreach (var (displayName, stack) in items)
            {
                string text = (displayName ?? "").Trim();
                if (!IsUsableName(text))
                    continue;
                string key = MakeKey(text);
                if (key.Length == 0)
                    continue;
                names.Add(text);
                counts[key] = counts.GetValueOrDefault(key) + Math.Max(1, stack);
            }

            SetOwnedNames(names);
            ownedCounts = counts;
        }

        /// <summary>How many of the named item the chests held when <see cref="SetOwnedItems"/> was last called (0 = none, or unknown).</summary>
        public int GetOwnedCount(string displayName)
        {
            return ownedCounts.TryGetValue(MakeKey(displayName ?? ""), out int count) ? count : 0;
        }

        /// <summary>Remembers the names of the items that are in the player's chests right now (they are ranked first).</summary>
        public void SetOwnedNames(IEnumerable<string> displayNames)
        {
            var byKey = new Dictionary<string, Entry>();

            // a name that is also in the word list keeps that entry's item id (for the category hover tip)
            var idByKey = new Dictionary<string, string>();
            foreach (Entry known in vocabulary)
            {
                if (known.ItemId != null)
                    idByKey[known.Key] = known.ItemId;
            }
            foreach (Entry known in unholdableEntries)
            {
                if (known.ItemId != null)
                    idByKey.TryAdd(known.Key, known.ItemId);   // a Weeds lying in a chest still gets its icon and category tip
            }
            var holdableKeys = new HashSet<string>(vocabulary.Select(e => e.Key));

            foreach (string raw in displayNames)
            {
                string text = (raw ?? "").Trim();
                if (!IsUsableName(text))
                    continue;

                string key = MakeKey(text);
                if (unobtainableKeys.Contains(key) && !holdableKeys.Contains(key))
                    continue;   // a map node that a cheat tool put into a chest: not an item of the library, never suggested
                if (cheatOnlyKeys.Contains(key))
                    continue;   // a plain Wine / Jelly ... spawned by a cheat menu (see CheatOnlyObjectIds): never suggested, never in a tip
                if (key.Length > 0 && !byKey.ContainsKey(key))
                    byKey[key] = new Entry(text, key, idByKey.TryGetValue(key, out string? knownId) ? knownId : null);
            }

            ownedEntries = byKey.Values.ToArray();
            ownedKeys = new HashSet<string>(byKey.Keys);
            popularCache.Clear();   // the category lists also hold items that only the chests know (other mods' items)
            ownedCounts = byKey.Keys.ToDictionary(key => key, _ => 1);   // without counts every owned name counts as 1 (SetOwnedItems sets the real numbers)
        }

        /// <summary>The best <paramref name="max"/> names that start with what the player typed (empty list if nothing fits).</summary>
        public IReadOnlyList<Suggestion> Search(string rawQuery, int max)
        {
            // a number ("26", "-7") completes to the ids that start with it, each with its name; text is completed as before
            // (a lone "-" lists the category ids, the ones closest to zero first: -2, -4, -5 ... -79 ...; the player types on, "-7", to narrow it down)
            string typed = (rawQuery ?? "").Trim();
            if (typed == "-" || IsIdQuery(typed))
                return SearchIds(typed, max);

            string query = MakeKey(rawQuery ?? "");

            // nothing typed, or an item / category id like "268" or "-26": ids are not worth completing
            if (query.Length == 0 || query.All(char.IsDigit))
                return Array.Empty<Suggestion>();

            var hits = new List<Hit>();

            foreach (Entry entry in ownedEntries)
            {
                int rank = GetMatchRank(entry, query);
                if (rank >= 0)
                    hits.Add(new Hit(entry, owned: true, exact: entry.Key.Length == query.Length, rank: rank, ownedCount: ownedCounts.GetValueOrDefault(entry.Key)));
            }

            foreach (Entry entry in vocabulary)
            {
                if (ownedKeys.Contains(entry.Key))
                    continue;   // already listed above, with the spelling that is really in the chests
                int rank = GetMatchRank(entry, query);
                if (rank >= 0)
                    hits.Add(new Hit(entry, owned: false, exact: entry.Key.Length == query.Length, rank: rank, ownedCount: 0));
            }

            // items a player cannot carry (Weeds, Twig, Stone nodes, quest items) are NEVER offered by the drop-down: they stay only as internal
            // known words (so that "weeds" is not reported as gibberish), they are not shown anywhere in the list

            hits.Sort(CompareHits);

            // the slot plan (see THE GLOBAL SLOT RULE): slot 1 stays the best match (the exact name, which ModEntry.GetKnownNameCheck relies on);
            // slots 2 and 3 are taken from the items that match in the same, best way (start of the name before a later word), so a far-fetched
            // match never jumps ahead of a good one
            int bestItemRank = int.MaxValue;
            foreach (Hit hit in hits)
            {
                if (!hit.IsCategory && !hit.Exact && hit.Rank < bestItemRank)
                    bestItemRank = hit.Rank;
            }

            Dictionary<string, int> core = CoreRanks;
            List<int> order = ApplySlotPlan(
                hits.Count,
                max,
                coreEligible: i => !hits[i].IsCategory && !hits[i].Exact && hits[i].Rank == bestItemRank && (hits[i].Demote == 0 || core.ContainsKey(hits[i].Entry.Key)),
                coreScore: i => CoreScore(hits[i].Entry, hits[i].Usage),
                ownedCount: i => hits[i].IsCategory || hits[i].Rank != bestItemRank ? 0 : hits[i].OwnedCount,
                nearDuplicate: (a, b) => IsNearDuplicate(hits[a].Entry, hits[a].IsCategory, hits[b].Entry, hits[b].IsCategory, query));

            // the typed text IS a quality word ("gold", "iridium"): the tools of that material come right after the exact / category rows
            // (Gold Pickaxe, Gold Axe, Gold Watering Can ...), see QUALITY WORDS AND TOOLS
            if (Array.IndexOf(QualityWordKeys, query) >= 0)
            {
                string[] family = QualityWordFamily(query);
                List<int> lead = order.TakeWhile(i => hits[i].Exact || hits[i].IsCategory).ToList();
                List<int> tools = order.Skip(lead.Count).Where(i => IsMaterialTool(hits[i].Entry, family)).OrderBy(i => ToolRank(hits[i].Entry)).ToList();
                order = lead.Concat(tools).Concat(order.Skip(lead.Count).Where(i => !tools.Contains(i))).ToList();
            }

            var result = new List<Suggestion>(order.Count);
            foreach (int i in order)
            {
                // the typed text is dimmed only when it is the start of the whole name; a match inside a later word ("Fishing Jelly") is drawn in full color
                int matched = hits[i].Rank == 0 ? MatchedLength(hits[i].Entry.Text, query.Length) : 0;
                result.Add(new Suggestion(hits[i].Entry.Text, matched, hits[i].IsCategory, qualifiedId: hits[i].Entry.ItemId));
            }

            // AUTO SUGGEST ONLY (never used by "Did you mean", see FindSimilar): the player has typed a WHOLE category word ("artisan goods", "fish",
            // "wine"): row 1 is the category itself, then EVERY item of that category (most used first, late-game items last, see CATEGORY EXPANSION),
            // then the other names that contain the word. The drop-down scrolls through all of them.
            if (max > 1)
            {
                List<Suggestion>? expanded = ExpandCategory(typed, result, Math.Max(max, SearchMenu.MaxCategorySuggestions));
                if (expanded != null)
                    return expanded;
            }

            return result;
        }

        // ===================== AUTO SUGGEST: CATEGORY EXPANSION =====================
        // Typing a WHOLE category word lists the category and then ALL of its items, so the player can scroll through the whole category:
        //   "artisan goods" -> artisan goods (category), Cheese, Mayonnaise, Caviar, Truffle Oil ..., Starfruit Wine, Ancient Fruit Wine ...
        //   "fish"          -> fish (category), Sturgeon, Lava Eel ...
        //   "wine"          -> wine (category), Starfruit Wine, Ancient Fruit Wine ... (a "flavored" category: its items are the wines the game builds)
        // Which items belong to a category: the game's own category id of the item (ModEntry.GetStaticCategoryIds), or for a flavored category
        // (wine, jelly, juice, pickles, honey, roe, aged roe, dried fruit / mushrooms, smoked fish, targeted bait) the built names of that product.
        // Every kind of item the library knows counts (objects, big craftables, furniture, hats, tools, weapons, boots, trinkets), and for "clothing"
        // also every shirt and pair of pants (see categoryOnlyEntries). Cheat-only base products (plain Wine ...) are never part of it.
        // Order ("most used first", the community favourites of the Stardew Valley Wiki as curated in PopularByCategory, then the mod's usage ranking):
        //   1. late-game / mostly-for-fun items (Qi Fruit, Radioactive ..., legendary fish) LAST, whatever else
        //   2. the curated favourites of the category, in their order
        //   3. the central items (CoreItems), then the everyday ranking (tier, then progress). A built name ("Starfruit Wine") is ranked by its
        //      INGREDIENT (Starfruit), so the famous wines come first
        //   4. real items before built names, then the base product (Wine before Jelly), then shorter names, then alphabetical

        /// <summary>Category id -> the internal English names of its favourite items, most used first (the community staples).</summary>
        private static readonly Dictionary<int, string[]> PopularByCategory = new()
        {
            [-4] = new[] { "Sturgeon", "Lava Eel", "Blobfish", "Super Cucumber", "Rainbow Trout", "Void Salmon", "Tuna", "Salmon", "Catfish", "Squid", "Octopus" },                       // fish
            [-79] = new[] { "Ancient Fruit", "Starfruit", "Pineapple", "Cranberries", "Blueberry", "Melon", "Pomegranate", "Strawberry", "Orange", "Apple" },                              // fruit
            [-75] = new[] { "Pumpkin", "Cauliflower", "Kale", "Potato", "Garlic", "Tomato", "Corn", "Wheat", "Hops", "Artichoke" },                                                         // vegetable
            [-80] = new[] { "Fairy Rose", "Sunflower", "Poppy", "Tulip", "Blue Jazz", "Summer Spangle" },                                                                                  // flower
            [-81] = new[] { "Wild Horseradish", "Leek", "Dandelion", "Salmonberry", "Blackberry", "Hazelnut", "Morel", "Chanterelle", "Common Mushroom" },                                  // forage
            [-2] = new[] { "Prismatic Shard", "Diamond", "Emerald", "Ruby", "Aquamarine", "Amethyst", "Topaz", "Jade" },                                                                  // gem
            [-12] = new[] { "Earth Crystal", "Frozen Tear", "Fire Quartz", "Ghost Crystal" },                                                                                              // mineral
            [-26] = new[] { "Cheese", "Mayonnaise", "Caviar", "Truffle Oil", "Cloth", "Goat Cheese", "Oil", "Honey", "Duck Mayonnaise", "Beer", "Pale Ale", "Coffee", "Mead", "Green Tea" },   // artisan goods (the plain Wine / Jelly / Juice / Pickles are cheat-only, see CheatOnlyObjectIds)
            [-18] = new[] { "Wool", "Duck Feather", "Rabbit's Foot", "Truffle" },                                                                                                          // animal product
            [-28] = new[] { "Solar Essence", "Void Essence", "Bat Wing", "Slime", "Bug Meat" },                                                                                            // monster loot
            [-74] = new[] { "Rare Seed", "Ancient Seeds", "Mixed Seeds", "Starfruit Seeds", "Cranberry Seeds", "Pumpkin Seeds" },                                                          // seed
            [-15] = new[] { "Iridium Bar", "Gold Bar", "Iron Bar", "Copper Bar", "Coal", "Refined Quartz", "Iridium Ore", "Gold Ore" },                                                    // metal resources
            [-16] = new[] { "Wood", "Stone", "Hardwood", "Clay", "Fiber" },                                                                                                                // building resources
            [-5] = new[] { "Large Egg", "Egg", "Duck Egg", "Void Egg", "Golden Egg" },                                                                                                    // egg
            [-6] = new[] { "Large Milk", "Milk", "Goat Milk", "L. Goat Milk" }                                                                                                            // milk
        };

        /// <summary>
        /// The flavored categories: the category word (as a key) -> the qualified id of the plain base product whose built names are its items.
        /// Matched through ModEntry.CategoryKey, so every spelling the search accepts ("wines", "aged roes" ...) works.
        /// </summary>
        private static readonly (string Word, string BaseId)[] FlavoredCategoryBases =
        {
            ("wine", "(O)348"), ("jelly", "(O)344"), ("juice", "(O)350"), ("pickles", "(O)342"), ("honey", "(O)340"), ("roe", "(O)812"), ("aged roe", "(O)447"),
            ("dried fruit", "(O)DriedFruit"), ("dried mushrooms", "(O)DriedMushrooms"), ("smoked fish", "(O)SmokedFish"), ("targeted bait", "(O)SpecificBait")
        };

        /// <summary>
        /// The finished drop-down for a whole category word, or null when the text is not one (then the normal list is used). Row 1 is the
        /// category, then every item of the category (cached per category), then the other rows of <paramref name="normalRows"/> in their order.
        /// </summary>
        private List<Suggestion>? ExpandCategory(string typed, List<Suggestion> normalRows, int max)
        {
            string plain = (typed ?? "").Trim();
            if (plain.Length < 3 || !ModEntry.IsCategoryWord(plain))
                return null;

            string? categoryKey = ModEntry.CategoryKey(plain);
            string cacheKey = categoryKey ?? MakeKey(plain);
            if (!popularCache.TryGetValue(cacheKey, out List<Suggestion>? members))
            {
                members = BuildCategoryMembers(plain);
                popularCache[cacheKey] = members;
            }

            // row 1: the category itself (the normal search has it as the exact row; for another spelling of it, "fishes", it is made here)
            Suggestion categoryRow;
            int existing = normalRows.FindIndex(r => r.IsCategory && r.IdLabel == null
                && (MakeKey(r.Text) == MakeKey(plain) || (categoryKey != null && ModEntry.CategoryKey(r.Text) == categoryKey)));
            if (existing >= 0)
            {
                categoryRow = normalRows[existing];
            }
            else
            {
                Entry? categoryEntry = null;
                foreach (Entry e in vocabulary)
                {
                    if (e.ItemId == null && ModEntry.IsCategoryWord(e.Text) && categoryKey != null && ModEntry.CategoryKey(e.Text) == categoryKey)
                    {
                        categoryEntry = e;
                        break;
                    }
                }
                categoryRow = categoryEntry is Entry found
                    ? new Suggestion(found.Text, 0, isCategory: true)
                    : new Suggestion(plain, MatchedLength(plain, MakeKey(plain).Length), isCategory: true);
            }

            var result = new List<Suggestion>(Math.Min(max, members.Count + normalRows.Count + 1)) { categoryRow };
            var listed = new HashSet<string>(StringComparer.Ordinal) { MakeKey(categoryRow.Text) };

            foreach (Suggestion member in members)
            {
                if (result.Count >= max)
                    break;
                if (listed.Add(MakeKey(member.Text)))
                    result.Add(member);
            }

            // then the names that only contain the word ("Fishing Rod" for "fish"), in their normal order
            foreach (Suggestion row in normalRows)
            {
                if (result.Count >= max)
                    break;
                if (listed.Add(MakeKey(row.Text)))
                    result.Add(row);
            }

            return result;
        }

        /// <summary>Every item of a category, in the order described above (see CATEGORY EXPANSION). Empty if the category has no items the library knows.</summary>
        private List<Suggestion> BuildCategoryMembers(string categoryWord)
        {
            var rows = new List<Suggestion>();
            string? categoryKey = ModEntry.CategoryKey(categoryWord);
            string via = ModEntry.CategoryDisplayName(categoryWord);

            // a flavored category ("wine"): its items are the built names of one base product (plus the real item of that id, e.g. Wild Honey)
            string? flavoredBase = null;
            foreach (var (word, baseId) in FlavoredCategoryBases)
            {
                if (MakeKey(word) == MakeKey(categoryWord) || (categoryKey != null && ModEntry.CategoryKey(word) == categoryKey))
                {
                    flavoredBase = baseId;
                    break;
                }
            }

            HashSet<int> ids = flavoredBase == null ? ModEntry.GetStaticCategoryIds(categoryWord) : new HashSet<int>();
            if (flavoredBase == null && ids.Count == 0)
                return rows;

            var categoryCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool InCategory(Entry entry)
            {
                if (entry.ItemId == null || entry.Unholdable || IsCheatOnlyObject(entry.Built ? null : entry.ItemId))
                    return false;
                if (flavoredBase != null)
                    return string.Equals(entry.ItemId, flavoredBase, StringComparison.Ordinal);

                if (categoryCache.TryGetValue(entry.ItemId, out bool cached))
                    return cached;
                bool inside;
                try
                {
                    ParsedItemData? data = ItemRegistry.GetData(entry.ItemId);
                    inside = data != null && !data.IsErrorItem && ids.Contains(data.Category);
                }
                catch (Exception)
                {
                    inside = false;
                }
                categoryCache[entry.ItemId] = inside;
                return inside;
            }

            // the curated favourites: key -> place (by the English name and the game's internal name)
            var curated = new Dictionary<string, int>(StringComparer.Ordinal);
            int place = 0;
            foreach (int id in ids.OrderByDescending(i => i))
            {
                if (!PopularByCategory.TryGetValue(id, out string[]? names))
                    continue;
                foreach (string name in names)
                {
                    if (itemsByInternalName.TryGetValue(name, out Entry entry))
                        curated.TryAdd(entry.Key, place);
                    curated.TryAdd(MakeKey(name), place);
                    place++;
                }
            }

            var members = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (IEnumerable<Entry> source in new[] { vocabulary, categoryOnlyEntries, ownedEntries })
            {
                foreach (Entry entry in source)
                {
                    if (!members.ContainsKey(entry.Key) && !(entry.ItemId == null && ModEntry.IsCategoryWord(entry.Text)) && InCategory(entry))
                        members[entry.Key] = entry;
                }
            }

            // the sort key of one member (see the order above)
            (int Late, int Curated, long Core, int Built, int BaseProgress, int Length, string Text) RankOf(Entry entry)
            {
                Entry rankEntry = entry;
                (int Tier, int Progress) usage = ItemRanking.Get(entry.ItemId);
                int baseProgress = 0;
                if (entry.Built)
                {
                    // "Starfruit Wine" is ranked by Starfruit (the tier stays the one of the product, so it sits with the other artisan goods)
                    baseProgress = usage.Progress;
                    if (realItemsByKey.TryGetValue(MakeKey(GuessRoot(entry.Text)), out Entry ingredient))
                    {
                        rankEntry = ingredient;
                        usage = (usage.Tier, ItemRanking.Get(ingredient.ItemId).Progress);
                    }
                }

                int late = DemoteLevel(rankEntry.ItemId) == 1 || DemoteLevel(entry.ItemId) == 1 ? 1 : 0;
                int curatedPlace = !entry.Built && curated.TryGetValue(entry.Key, out int p) ? p : int.MaxValue;
                return (late, curatedPlace, CoreScore(rankEntry, usage), entry.Built ? 1 : 0, baseProgress, entry.Key.Length, entry.Text);
            }

            foreach (Entry entry in members.Values
                .Select(e => (Member: e, Rank: RankOf(e)))
                .OrderBy(x => x.Rank.Late)
                .ThenBy(x => x.Rank.Curated)
                .ThenBy(x => x.Rank.Core)
                .ThenBy(x => x.Rank.Built)
                .ThenBy(x => x.Rank.BaseProgress)
                .ThenBy(x => x.Rank.Length)
                .ThenBy(x => x.Rank.Text, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Member))
            {
                rows.Add(new Suggestion(entry.Text, 0, qualifiedId: entry.ItemId, viaCategory: via));
            }

            return rows;
        }

        /// <summary>The entries of the litter category, built from the game's object data: pure map clutter only (see <see cref="IsPureMapClutter"/>), one per name, lowest id as its icon.</summary>
        private List<Entry> BuildLitterEntries()
        {
            var byKey = new Dictionary<string, Entry>();
            foreach (KeyValuePair<string, ObjectData> pair in Game1.objectData)
            {
                if (pair.Value == null || !IsPureMapClutter(pair.Key, pair.Value))
                    continue;

                string qualifiedId = CanonicalItemId("(O)" + pair.Key);
                ParsedItemData? data = ItemRegistry.GetData(qualifiedId);
                string? name = data?.DisplayName?.Trim();
                if (data == null || data.IsErrorItem || !IsUsableName(name))
                    continue;

                string key = MakeKey(name);
                if (key.Length > 0 && (!byKey.TryGetValue(key, out Entry previous) || IsLowerObjectId(qualifiedId, previous.ItemId)))
                    byKey[key] = new Entry(name, key, qualifiedId);
            }

            return byKey.Values.OrderBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ===================== "DID YOU MEAN" JUNK BLACKLIST =====================
        // Junk items that sit very close to many short typos by edit distance ("frish" ~ "Trash") but are never what a player is looking for.
        // The blacklist works ONLY inside FindSimilar, i.e. only for the "Did you mean" words (the result panel's row and the instant correction row under
        // the search box). It never touches the real search (what matches, what is highlighted: the Contains match, the multi-word split and the
        // space handling are all untouched) and never touches the auto-complete drop-down (Search).
        //
        // A blacklisted name never takes part in the spelling match, EXCEPT through its own tiny typo library: when the typed word is exactly one of the
        // listed misspellings of that item ("sao" -> Sap, "trsh" -> Trash), the item may be offered. A library only holds typos that the real search
        // can NOT find by itself: a typed word that the plain Contains match already finds in the item ("plan" in "Rotting Plant", "weed" in "Weeds")
        // never reaches "Did you mean" at all, so it has no place here. ResolveSpellingBlacklist enforces this: a listed typo that is a part of its own
        // item's name is dropped when the library is built (so a slip in the table below can never turn into a second, hidden match rule).
        // The libraries are small on purpose: just enough to catch an obvious slip of the finger, never a loose match. Only the WHOLE typed word counts
        // (it has to equal the listed spelling), never a part of it. Spellings are written as keys (lower case, no spaces / hyphens).
        // Red Mushroom and Wild Horseradish / Leek are NOT blacklisted.
        private static readonly (string[] EnglishNames, string[] Typos)[] SpellingBlacklist =
        {
            (new[] { "Trash" },           new[] { "trsh", "trashh", "tash", "trsah", "yrash", "trasy", "trast", "trahs", "rtash", "trah" }),
            (new[] { "Sap" },             new[] { "sao", "sapp", "sps", "aps", "dap", "slp", "sxp", "spa", "asps", "ssap", "pas", "sapq", "zap" }),
            (new[] { "Weeds" },           new[] { "wees", "weds", "wesd", "weedz", "qweeds", "eweds", "weedsd", "weedd", "weez" }),
            (new[] { "Driftwood" },       new[] { "driftwod", "diftwood", "drifwood", "fritwood", "drftwood", "driftwodo", "dirftwood", "dritfwood", "drifwod" }),
            (new[] { "Soggy Newspaper" }, new[] { "newspapr", "nespapr", "newspaperr" }),
            (new[] { "Broken Glasses" },  new[] { "glases", "glasees", "gasses", "glase" }),
            (new[] { "Broken CD" },       new[] { "cdd", "dc", "ccd", "ccdd" }),
            (new[] { "Rotting Plant", "Rotten Plant" }, new[] { "plnt", "plaint", "plantt", "plent", "plannt" })
        };

        /// <summary>
        /// The blacklist as lookup: the key of a blacklisted name -> its tiny typo library (keys). Found by the English display name AND by the game's
        /// internal English name, so it also works when the game runs in another language. A typo that is a part of its item's own name is left out
        /// (the real search finds those by itself, see above).
        /// </summary>
        private Dictionary<string, HashSet<string>> ResolveSpellingBlacklist()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (englishNames, typos) in SpellingBlacklist)
            {
                foreach (string englishName in englishNames)
                {
                    string itemKey = MakeKey(englishName);
                    var library = new HashSet<string>(
                        typos.Select(MakeKey).Where(typo => typo.Length > 0 && !itemKey.Contains(typo, StringComparison.Ordinal)),
                        StringComparer.Ordinal);

                    map[itemKey] = library;
                    if (itemsByInternalName.TryGetValue(englishName, out Entry internalEntry))
                        map[internalEntry.Key] = library;
                }
            }

            return map;
        }

        // ===================== "DID YOU MEAN": MATCH CLASSES =====================
        // Every name that passes the spelling floor gets a match SCORE (smaller = closer) and a KIND, the way it was reached:
        //   kind 0  WHOLE NAME   the whole name is spelled like the word: score = edit distance ("bedd" ~ "Bed": 1, "floorr" ~ "Flower": 2)
        //   kind 1  EXACT START  the start of the word IS a whole word of the name, only a few letters too many were typed:
        //                        score = the extra letters ("floorr" -> the "floor" of "Wood Floor": 1, "seaa" -> the "sea" of "Sea Urchin": 1,
        //                        "bedd" -> the "bed" of "Child Bed": 1). What the player typed correctly is the strongest hint there is, so an exact
        //                        start beats a name that only LOOKS similar after letters are moved around ("floorr" finds Wood Floor before Flower).
        //   kind 2  PIECE        the start, the end or one word of the name is spelled like the word: score = distance + 1
        //   kind 3  FILL         looser: the end / one word of the name, the +1 may reach the limit + 1 ("fih" -> Catfish)
        // The match order is: score, then furniture / decoration / cooking / trash last (DemoteLevel), then the kind, then the category word, then
        // (whole names only) the closest length, then the everyday ranking, then the item held the most of, then the shorter name.
        // Furniture / decoration / cooking / trash that are only reached through a PIECE or the FILL ("seaa" ~ the "seat" of "Stump Seat") are not
        // candidates at all as long as anything else is found: nothing in their name was typed, they are noise. They come back only when the list
        // would otherwise be empty. Reached through the whole name or an exact start ("bedd" -> Bed, Child Bed; "floorr" -> Wood Floor) they stay
        // normal candidates: then the player really typed their name.
        // Then THE GLOBAL SLOT RULE (see Search) fills the first three places: closest match, most central item, item held the most of.

        /// <summary>The shortest whole word (in letters) that an exact start may stand on ("sea", "bed", "oil"; never two letters).</summary>
        private const int ExactStartMinWordLength = 3;

        /// <summary>One name found by FindSimilar (see MATCH CLASSES).</summary>
        private readonly struct SpellingCandidate
        {
            public readonly Entry Entry;
            public readonly bool IsCategory;
            public readonly int Score;
            public readonly int Kind;
            public readonly int Demote;
            public readonly int LengthGap;
            public readonly (int Tier, int Progress) Usage;
            public readonly int OwnedCount;

            public SpellingCandidate(Entry entry, bool isCategory, int score, int kind, int demote, int lengthGap, (int Tier, int Progress) usage, int ownedCount)
            {
                Entry = entry;
                IsCategory = isCategory;
                Score = score;
                Kind = kind;
                Demote = demote;
                LengthGap = lengthGap;
                Usage = usage;
                OwnedCount = ownedCount;
            }

            /// <summary>Furniture / decoration / cooking / trash reached only through a piece or the fill: used only when nothing else is found.</summary>
            public bool IsNoise => Demote == 2 && Kind >= 2;
        }

        private static int CompareSpelling(SpellingCandidate a, SpellingCandidate b)
        {
            int c = a.Score.CompareTo(b.Score);
            if (c != 0) return c;
            c = a.Demote.CompareTo(b.Demote);
            if (c != 0) return c;
            c = a.Kind.CompareTo(b.Kind);
            if (c != 0) return c;
            c = (a.IsCategory ? 0 : 1).CompareTo(b.IsCategory ? 0 : 1);   // the category word is the broadest meaning of a typo: first at the same score
            if (c != 0) return c;
            c = (a.Kind == 0 ? a.LengthGap : 0).CompareTo(b.Kind == 0 ? b.LengthGap : 0);
            if (c != 0) return c;
            c = a.Usage.Tier.CompareTo(b.Usage.Tier);
            if (c != 0) return c;
            c = a.Usage.Progress.CompareTo(b.Usage.Progress);
            if (c != 0) return c;
            c = b.OwnedCount.CompareTo(a.OwnedCount);
            if (c != 0) return c;
            c = a.Entry.Key.Length.CompareTo(b.Entry.Key.Length);   // "Child Bed" before "Modern Double Bed"
            if (c != 0) return c;
            return string.Compare(a.Entry.Text, b.Entry.Text, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>The shortest end-of-name piece or single word (in letters) that the looser FILL group of FindSimilar compares with. Keeps 3-letter pieces ("Egg", "Oil") out of it.</summary>
        private const int FillMinPieceLength = 4;

        /// <summary>A blacklisted name of at least this many letters may also be offered for a word that is one slip away from its whole name (not only for the typos of its own library).</summary>
        private const int BlacklistWholeNameMinLength = 4;

        /// <summary>
        /// The spelling floor of "Did you mean": a name (or the piece of it that is compared) is only a candidate when the edit distance is at most
        /// 40% of the longer of the two texts. This is what lets a shared ENDING or START count ("litterr" ~ the "latter" of "Roots Platter": 2 of 7)
        /// while a name with nothing spelled alike ("litterr" ~ "Pizza", "Fish Taco" ~ "starf") never gets in.
        /// Integer test: distance * 5 &lt;= 2 * longer length.
        /// </summary>
        private static bool IsSimilarEnough(int distance, int lengthA, int lengthB)
        {
            return distance * 5 <= 2 * Math.Max(lengthA, lengthB);
        }

        /// <summary>
        /// "Did you mean ...?": the names that are SPELLED most like a word that found nothing (a typo like "frish", "bedd", "floorr").
        /// The allowed distance grows with the length of the word (1 for up to 4 letters, 2 up to 8, then 3), and every candidate must pass the
        /// spelling floor (IsSimilarEnough). How a name is reached and how the list is ordered: see MATCH CLASSES above and THE GLOBAL SLOT RULE.
        /// Spaces, hyphens and case are ignored, like everywhere else in the search.
        /// Names the game builds on the fly ("Spook Fish Roe") only count through their whole name or its start, never through one of their words
        /// (otherwise every fish would bring its Roe, its Aged Roe, its Smoked version ...). A category word ("fish") is a candidate only by its whole
        /// name, and only when <paramref name="includeCategories"/> is set; it is returned as "Fish (category)".
        /// A name that equals the word is never returned (then the word was not a typo), and neither is a name that cannot be typed back into the
        /// search box as one word (it contains a search separator or a bracket).
        /// Junk names (Trash, Sap, Weeds, ... see SpellingBlacklist) are never candidates, unless the word is one of their own tiny typo library,
        /// or (names of BlacklistWholeNameMinLength letters or more) the word is ONE slip away from the WHOLE name. Items no chest can hold
        /// (Weeds, Twig, quest items, see Entry.Unholdable) are candidates by their WHOLE name only.
        /// When the slots are not full yet, the names built on the best whole-name match ("Bed" -> "Child Bed", "Double Bed") fill them up.
        /// </summary>
        /// <param name="includeCategories">True: a category word that is spelled like the word may be returned ("Fish (category)").</param>
        public IReadOnlyList<string> FindSimilar(string rawQuery, int max, bool includeCategories = false)
        {
            // A stray apostrophe inside a word ("co'ok") is a slip of the finger, not a letter: it is taken out before the spelling match, otherwise it
            // counts as one more wrong letter and "co'ok" lands on "Book" (2 slips) instead of "cook". A real possessive ("rabbit's") keeps it.
            string typedKey = MakeKey(rawQuery ?? "");
            string query = StripStrayApostrophes(typedKey);
            bool symbolsStripped = query != typedKey;

            if (max <= 0 || query.Length < 2 || query.All(char.IsDigit))
                return Array.Empty<string>();

            Dictionary<string, HashSet<string>> blacklist = ResolveSpellingBlacklist();

            // under 3 letters only a listed typo of a blacklisted item counts ("dc" -> Broken CD); any other 1-2 letter word is too short to guess from
            if (query.Length < 3 && !blacklist.Values.Any(library => library.Contains(query)))
                return Array.Empty<string>();

            int limit = query.Length <= 4 ? 1 : query.Length <= 8 ? 2 : 3;
            var candidates = new List<SpellingCandidate>();

            void Consider(Entry entry)
            {
                string key = entry.Key;
                if (key.Length == 0 || key == query)
                    return;

                // junk (Trash, Sap, ...): out of the spelling match, unless the word is exactly one of its own listed misspellings, or (long enough
                // names) one slip away from the whole name. An item no chest can hold (Weeds, Twig ...) only ever matches through its whole name.
                bool ownTypo = false;
                bool wholeNameOnly = entry.Unholdable;
                if (blacklist.TryGetValue(key, out HashSet<string>? library))
                {
                    if (library.Contains(query))
                        ownTypo = true;
                    else if (query.Length >= 4 && key.Length >= BlacklistWholeNameMinLength && EditDistance(query, key, 1) <= 1)
                        wholeNameOnly = true;
                    else
                        return;
                }

                if (query.Length < 3 && !ownTypo)
                    return;

                bool isCategory = entry.ItemId == null && ModEntry.IsCategoryWord(entry.Text);
                if (isCategory && !includeCategories)
                    return;
                if (entry.Text.IndexOfAny(ModEntry.QueryCommas) >= 0 || entry.Text.IndexOfAny(new[] { '(', ')', '\uFF08', '\uFF09' }) >= 0)
                    return;

                const int None = int.MaxValue;

                // the word was only wrong because of a stray symbol, and without it the word IS the start of this name ("co'ok" -> "cook" ->
                // Cooking, Cookout Kit): a perfect match, before every spelling guess. Category words count too (they are the broadest meaning).
                if (symbolsStripped && !wholeNameOnly && !ownTypo && query.Length >= 3 && key.Length > query.Length && key.StartsWith(query, StringComparison.Ordinal))
                {
                    int startDemote = isCategory ? 0 : DemoteLevel(entry.ItemId);
                    candidates.Add(new SpellingCandidate(entry, isCategory, 0, 0, startDemote, key.Length - query.Length,
                        isCategory ? (0, 0) : ItemRanking.Get(entry.ItemId), ownedCounts.GetValueOrDefault(key)));
                    return;
                }

                // kind 0: the whole name
                int wholeScore = EditDistance(query, key, limit);
                if (wholeScore > limit || !IsSimilarEnough(wholeScore, query.Length, key.Length))
                    wholeScore = None;

                // the score of a piece of the name (its start, its end, one word): distance + 1, or None if the piece is not spelled like the word
                int PieceScore(string piece)
                {
                    int d = EditDistance(query, piece, limit);
                    return d <= limit && IsSimilarEnough(d, query.Length, piece.Length) ? d + 1 : None;
                }

                bool piecesAllowed = !wholeNameOnly && !isCategory;
                bool wordsAllowed = piecesAllowed && !entry.Built;   // built names ("Sturgeon Roe") never through one of their words

                // kind 1: the start of the word is EXACTLY one whole word of the name, and only a few letters too many were typed after it
                int exactStart = None;
                if (wordsAllowed)
                {
                    for (int w = 0; w < entry.WordStarts.Length; w++)
                    {
                        int start = entry.WordStarts[w];
                        int end = w + 1 < entry.WordStarts.Length ? entry.WordStarts[w + 1] : key.Length;
                        int wordLength = end - start;
                        int extra = query.Length - wordLength;
                        if (wordLength >= ExactStartMinWordLength && extra >= 1 && extra <= limit
                            && string.CompareOrdinal(query, 0, key, start, wordLength) == 0)
                        {
                            exactStart = Math.Min(exactStart, extra);
                        }
                    }
                }

                // kind 2: a piece of the name
                int pieceScore = None;
                if (piecesAllowed && key.Length > query.Length)
                {
                    // the start of the name (same length as the word, give or take one letter)
                    for (int len = Math.Max(1, query.Length - 1); len <= query.Length + 1 && len < key.Length; len++)
                        pieceScore = Math.Min(pieceScore, PieceScore(key.Substring(0, len)));
                }

                if (wordsAllowed)
                {
                    // the end of the name: "frish" ~ "fish" in "Catfish"
                    if (key.Length > query.Length)
                    {
                        for (int len = Math.Max(3, query.Length - 1); len <= query.Length + 1 && len < key.Length; len++)
                            pieceScore = Math.Min(pieceScore, PieceScore(key.Substring(key.Length - len)));
                    }

                    // one single word of a longer name
                    if (query.Length >= 4 && entry.WordStarts.Length > 1)
                    {
                        for (int w = 0; w < entry.WordStarts.Length; w++)
                        {
                            int start = entry.WordStarts[w];
                            int end = w + 1 < entry.WordStarts.Length ? entry.WordStarts[w + 1] : key.Length;
                            pieceScore = Math.Min(pieceScore, PieceScore(key.Substring(start, end - start)));
                        }
                    }
                }

                if (pieceScore > limit)
                    pieceScore = None;   // a piece only counts within the normal slip limit (the +1 included)

                int score;
                int kind;
                if (ownTypo)
                {
                    score = 1;   // a listed misspelling of a blacklisted name counts as a one-slip typo of its whole name
                    kind = 0;
                }
                else if (wholeScore != None && wholeScore <= exactStart && wholeScore <= pieceScore)
                {
                    score = wholeScore;
                    kind = 0;
                }
                else if (exactStart != None && exactStart <= pieceScore)
                {
                    score = exactStart;
                    kind = 1;
                }
                else if (pieceScore != None)
                {
                    score = pieceScore;
                    kind = 2;
                }
                else
                {
                    // kind 3, FILL: real item names may still come in through their END or one of their WORDS (at least FillMinPieceLength letters),
                    // the +1 for not being the whole name being allowed to reach limit + 1 ("fih" ~ the "fish" of "Catfish" / "Fish Stew")
                    if (!wordsAllowed)
                        return;

                    int fillScore = None;
                    if (key.Length > query.Length)
                    {
                        for (int len = Math.Max(FillMinPieceLength, query.Length - 1); len <= query.Length + 1 && len < key.Length; len++)
                            fillScore = Math.Min(fillScore, PieceScore(key.Substring(key.Length - len)));
                    }

                    if (entry.WordStarts.Length > 1)
                    {
                        for (int w = 0; w < entry.WordStarts.Length; w++)
                        {
                            int start = entry.WordStarts[w];
                            int end = w + 1 < entry.WordStarts.Length ? entry.WordStarts[w + 1] : key.Length;
                            if (end - start >= FillMinPieceLength)
                                fillScore = Math.Min(fillScore, PieceScore(key.Substring(start, end - start)));
                        }
                    }

                    if (fillScore > limit + 1)
                        return;

                    score = fillScore;
                    kind = 3;
                }

                int demote = isCategory || ownTypo ? 0 : DemoteLevel(entry.ItemId);
                (int Tier, int Progress) usage = isCategory ? (0, 0) : ItemRanking.Get(entry.ItemId);
                candidates.Add(new SpellingCandidate(entry, isCategory, score, kind, demote, Math.Abs(key.Length - query.Length), usage, ownedCounts.GetValueOrDefault(key)));
            }

            var consideredKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in ownedEntries)
            {
                if (consideredKeys.Add(entry.Key))
                    Consider(entry);
            }

            foreach (Entry entry in vocabulary)
            {
                if (ownedKeys.Contains(entry.Key) || !consideredKeys.Add(entry.Key))
                    continue;   // already considered above, with the spelling that is really in the chests
                Consider(entry);
            }

            // items that cannot be carried (Twig, quest items, weeds variants) are NEVER a "Did you mean" candidate (the one real Weeds is in the vocabulary above)

            // the real candidates, and the noise (furniture / decoration / cooking / trash that only shares a piece of its name) kept apart:
            // the noise is used only when nothing else was found
            candidates.Sort(CompareSpelling);

            // THE ROOT AND ITS VARIANTS: when the best match is a whole name ("mixed seedz" -> Mixed Seeds), the items that share that root
            // ("Mixed Flower Seeds": every word of the root, in order, with more words around it; "Child Bed" for "Bed"; "Catfish" for "Fish") are
            // pulled into the candidates as well, as close as the root itself (kind 1, the root's score). So they compete for the slots like any close
            // match instead of only filling slots nobody else wants. Furniture / decoration / cooking still sort after the rest at the same score.
            var familyKeys = new HashSet<string>(StringComparer.Ordinal);
            SpellingCandidate? root = candidates.Count > 0 && candidates[0].Kind == 0 && !candidates[0].IsCategory && !candidates[0].IsNoise
                ? candidates[0]
                : null;
            if (root is SpellingCandidate anchor)
            {
                familyKeys.Add(anchor.Entry.Key);
                var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < candidates.Count; i++)
                    indexByKey.TryAdd(candidates[i].Entry.Key, i);

                foreach (Entry member in GetFamily(anchor.Entry))
                {
                    if (!familyKeys.Add(member.Key))
                        continue;

                    int demote = DemoteLevel(member.ItemId);
                    var variant = new SpellingCandidate(member, false, anchor.Score, 1, demote, Math.Abs(member.Key.Length - query.Length),
                        ItemRanking.Get(member.ItemId), ownedCounts.GetValueOrDefault(member.Key));

                    if (indexByKey.TryGetValue(member.Key, out int at))
                    {
                        if (CompareSpelling(variant, candidates[at]) < 0)
                            candidates[at] = variant;   // already a candidate, but further down: it moves up next to its root
                    }
                    else
                    {
                        candidates.Add(variant);
                    }
                }
            }

            List<SpellingCandidate> pool = candidates.Where(c => !c.IsNoise).ToList();
            if (pool.Count == 0)
                pool = candidates;
            pool.Sort(CompareSpelling);

            // A loose FILL match that is clearly worse than the best match (more than one point) is a guess, not a correction: "mixee seedz" finds
            // Mixed Seeds (2) and Mixed Flower Seeds, but the "treeseed" end of "Mushroom Tree Seed" (4) is not offered. Fill matches close to the
            // best one stay ("fih" -> Fish, Fig, then Sunfish). Better two good words than a third weak one.
            int bestScore = pool.Count > 0 ? pool[0].Score : 0;
            if (pool.Any(c => c.Kind <= 2))
                pool.RemoveAll(c => c.Kind == 3 && c.Score > bestScore + 1);

            // QUALITY WORDS AND TOOLS: a misspelled "gold" / "iridium" / "silver" ("golld", "iridum") is taken as a search for the TOOLS of that material
            string? qualityWord = QualityWordTypo(query, limit);
            List<Entry> materialTools = new();
            if (qualityWord != null)
            {
                string[] family = QualityWordFamily(qualityWord);
                materialTools = MaterialTools(family);
                pool.RemoveAll(c => !c.IsCategory && !IsMaterialTool(c.Entry, family) && HasQualityWord(c.Entry));   // no Gold Bar, Golden Pumpkin, Iridium Milk ...
            }

            // slot 2 comes from the close matches (whole name, exact start, piece) when there are any; the looser FILL only when there are none
            bool anyClose = pool.Any(c => c.Kind <= 2 && !c.IsCategory);
            Dictionary<string, int> core = CoreRanks;

            List<int> order = ApplySlotPlan(
                pool.Count,
                max,
                coreEligible: i => !pool[i].IsCategory && (pool[i].Kind <= 2 || !anyClose) && (pool[i].Demote == 0 || core.ContainsKey(pool[i].Entry.Key)),
                coreScore: i => CoreScore(pool[i].Entry, pool[i].Usage),
                ownedCount: i => pool[i].IsCategory ? 0 : pool[i].OwnedCount,
                nearDuplicate: (a, b) => !(familyKeys.Contains(pool[a].Entry.Key) && familyKeys.Contains(pool[b].Entry.Key))   // a root and its variants are what was asked for
                    && IsNearDuplicate(pool[a].Entry, pool[a].IsCategory, pool[b].Entry, pool[b].IsCategory, query));

            var result = new List<string>(order.Count);
            var shownKeys = new HashSet<string>(StringComparer.Ordinal);

            // a misspelled quality word: the tools of that material take the first places, the slot plan fills what is left
            foreach (Entry tool in materialTools)
            {
                if (result.Count >= max)
                    break;
                if (shownKeys.Add(tool.Key))
                    result.Add(tool.Text);
            }

            foreach (int i in order)
            {
                if (result.Count >= max)
                    break;
                if (shownKeys.Add(pool[i].Entry.Key))
                    result.Add(pool[i].IsCategory ? ModEntry.CategoryDisplayName(pool[i].Entry.Text) + " (category)" : pool[i].Entry.Text);
            }

            return result;
        }

        /// <summary>The apostrophe characters a player may type: straight, curly and the backtick.</summary>
        private static readonly char[] ApostropheChars = { '\'', '\u2019', '\u2018', '`' };

        /// <summary>
        /// Takes stray apostrophes out of a key ("co'ok" -> "cook"). An apostrophe directly followed by an "s" after at least two letters is kept,
        /// because that is how real names are written ("rabbit's", "farmer's"); every other one is a typo.
        /// </summary>
        private static string StripStrayApostrophes(string key)
        {
            if (key.IndexOfAny(ApostropheChars) < 0)
                return key;

            var builder = new System.Text.StringBuilder(key.Length);
            for (int i = 0; i < key.Length; i++)
            {
                char ch = key[i];
                if (Array.IndexOf(ApostropheChars, ch) >= 0)
                {
                    bool possessive = i >= 2 && i + 1 < key.Length && key[i + 1] == 's';
                    if (!possessive)
                        continue;
                    ch = '\'';
                }
                builder.Append(ch);
            }
            return builder.ToString();
        }

        // ===================== QUALITY WORDS AND TOOLS =====================
        // "gold", "iridium" and "silver" are quality words of the search box ("gold star apple", see SearchMenu.ExtractQuality), but on their own they are
        // mostly typed to find the TOOLS of that material: Gold Pickaxe, Gold Axe, Gold Watering Can, Golden Scythe, Iridium Axe ... So:
        //   - the drop-down for exactly "gold" / "golden" / "iridium" / "silver" lists those tools first (after the exact / category rows);
        //   - "Did you mean" for a misspelled quality word ("golld", "glod", "iridum") offers those tools first, and NO other item that has the quality word
        //     in its name (no Gold Bar, Golden Pumpkin, Iridium Bar ...): the player was not after them. Words that are only CLOSE to such names but are no
        //     misspelling of the quality word itself keep the normal rules ("irido" -> Iridium Ore, Iridium Bar ... as before: "irido" is 3 slips from "iridium").
        // A "tool" is an item of the tool kind (T) or a weapon (W) (the scythes are weapons). Order: pickaxe, axe, watering can, hoe, pan, scythe, the rest.

        /// <summary>The quality words, as keys.</summary>
        private static readonly string[] QualityWordKeys = { "gold", "golden", "iridium", "silver" };

        /// <summary>The order of the tools of one material, by the last word of their name (as keys).</summary>
        private static readonly string[] ToolOrder = { "pickaxe", "axe", "wateringcan", "can", "hoe", "pan", "scythe", "rod" };

        /// <summary>The words that mean the same material: gold = gold, golden; the others only themselves.</summary>
        private static string[] QualityWordFamily(string word)
        {
            return word == "gold" || word == "golden" ? new[] { "gold", "golden" } : new[] { word };
        }

        /// <summary>
        /// The quality word that the typed key is a misspelling of ("golld" -> "gold", "iridum" -> "iridium"), or null. Within the usual slip limit and the
        /// spelling floor; the word itself does not count (it is no typo).
        /// </summary>
        private static string? QualityWordTypo(string query, int limit)
        {
            string? best = null;
            int bestDistance = int.MaxValue;
            foreach (string word in QualityWordKeys)
            {
                if (query == word)
                    return null;
                int d = EditDistance(query, word, limit);
                if (d <= limit && IsSimilarEnough(d, query.Length, word.Length) && d < bestDistance)
                {
                    best = word;
                    bestDistance = d;
                }
            }
            return best;
        }

        /// <summary>True if one whole word of the name is a quality word ("Gold Bar", "Golden Pumpkin", "Iridium Milk"; not "Goldfish").</summary>
        private static bool HasQualityWord(Entry entry)
        {
            foreach (string word in WordsOf(entry))
            {
                if (Array.IndexOf(QualityWordKeys, word) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>True for a tool or weapon with one of the material words as a whole word of its name ("Gold Pickaxe", "Golden Scythe").</summary>
        private static bool IsMaterialTool(Entry entry, string[] family)
        {
            if (entry.Built || entry.ItemId == null || !(entry.ItemId.StartsWith("(T)", StringComparison.Ordinal) || entry.ItemId.StartsWith("(W)", StringComparison.Ordinal)))
                return false;
            return WordsOf(entry).Any(word => Array.IndexOf(family, word) >= 0);
        }

        /// <summary>Place of a tool in <see cref="ToolOrder"/> (tools before weapons of the same place, then the shorter name).</summary>
        private static (int Place, int Weapon, int Length) ToolRank(Entry entry)
        {
            string last = LastWordKey(entry);
            int place = Array.IndexOf(ToolOrder, last);
            if (place < 0 && entry.Key.EndsWith("wateringcan", StringComparison.Ordinal))
                place = Array.IndexOf(ToolOrder, "wateringcan");
            return (place < 0 ? ToolOrder.Length : place, entry.ItemId != null && entry.ItemId.StartsWith("(W)", StringComparison.Ordinal) ? 1 : 0, entry.Key.Length);
        }

        /// <summary>Every tool / weapon of a material that the library knows, in <see cref="ToolRank"/> order.</summary>
        private List<Entry> MaterialTools(string[] family)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return ownedEntries.Concat(vocabulary)
                .Where(e => IsMaterialTool(e, family) && seen.Add(e.Key))
                .OrderBy(ToolRank)
                .ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The names that are BUILT ON a root name: they end with it ("Catfish" for "fish"), contain it as whole words ("Fish Stew", "Starfruit Seeds"),
        /// or (a root of several words) contain all of its words in the same order with other words in between ("Mixed Flower Seeds" for
        /// "Mixed Seeds"). Real items only: no names the game builds on the fly, no items no chest can hold, no category words, never the root itself,
        /// and nothing that could not be typed back into the search box as one word.
        /// </summary>
        private IEnumerable<Entry> GetFamily(Entry rootEntry)
        {
            string rootKey = rootEntry.Key;
            if (rootKey.Length < 2)
                yield break;

            string[] rootWords = WordsOf(rootEntry);

            foreach (IEnumerable<Entry> source in new[] { ownedEntries, vocabulary })
            {
                foreach (Entry e in source)
                {
                    if (e.Built || e.Unholdable || e.Key.Length <= rootKey.Length)
                        continue;
                    if (ReferenceEquals(source, vocabulary) && ownedKeys.Contains(e.Key))
                        continue;   // already seen with the spelling that is really in the chests
                    if (e.ItemId == null && ModEntry.IsCategoryWord(e.Text))
                        continue;
                    if (!e.Key.EndsWith(rootKey, StringComparison.Ordinal) && !IsWholeNameOrWords(e, rootKey) && !ContainsWordsInOrder(e, rootWords))
                        continue;
                    if (e.Text.IndexOfAny(ModEntry.QueryCommas) >= 0 || e.Text.IndexOfAny(new[] { '(', ')', '\uFF08', '\uFF09' }) >= 0)
                        continue;
                    yield return e;
                }
            }
        }

        /// <summary>The words of a name as keys ("Mixed Flower Seeds" -> mixed, flower, seeds).</summary>
        private static string[] WordsOf(Entry entry)
        {
            int[] starts = entry.WordStarts;
            var words = new string[starts.Length];
            for (int w = 0; w < starts.Length; w++)
            {
                int end = w + 1 < starts.Length ? starts[w + 1] : entry.Key.Length;
                words[w] = entry.Key.Substring(starts[w], end - starts[w]);
            }
            return words;
        }

        /// <summary>True if the name has MORE words than <paramref name="rootWords"/> (at least two) and contains every one of them, in the same order.</summary>
        private static bool ContainsWordsInOrder(Entry entry, string[] rootWords)
        {
            if (rootWords.Length < 2)
                return false;

            string[] words = WordsOf(entry);
            if (words.Length <= rootWords.Length)
                return false;

            int next = 0;
            foreach (string word in words)
            {
                if (next < rootWords.Length && word == rootWords[next])
                    next++;
            }
            return next == rootWords.Length;
        }

        // ===================== DEVELOPER AUDIT (console command "find_item_audit") =====================
        // Walks the WHOLE item list of the running game (every language, every mod that adds items) and reports what a unit test would:
        //   A. a word where the auto-complete drop-down offers a name that the "Showing results for:" tip does not list;
        //   B. a one-letter slip of an item name (a missing or a swapped letter) that "Did you mean" does not bring back to that name.
        // It only reads, never changes anything, and can take a little while (it runs on the game thread).

        /// <summary>Runs the audit and returns the report lines (the last line is a summary).</summary>
        public List<string> RunAudit()
        {
            EnsureBuilt();
            var report = new List<string>();

            // ---- A. drop-down vs tip ----
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in vocabulary.Concat(ownedEntries))
            {
                string firstWord = entry.Text.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                string firstWordKey = MakeKey(firstWord);
                if (firstWordKey.Length >= 3)
                    prefixes.Add(firstWordKey);
                if (entry.Key.Length >= 4)
                    prefixes.Add(entry.Key.Substring(0, 4));
            }

            int hidden = 0;
            foreach (string prefix in prefixes)
            {
                if (IsIdQuery(prefix) || prefix.All(char.IsDigit))
                    continue;

                var tipKeys = new HashSet<string>(GetTipNames(new[] { prefix }).Select(MakeKey), StringComparer.Ordinal);
                foreach (Suggestion suggestion in Search(prefix, int.MaxValue))
                {
                    if (suggestion.IsCategory || suggestion.IdLabel != null)
                        continue;

                    string suggestionKey = MakeKey(suggestion.Text);
                    if (!suggestionKey.Contains(prefix, StringComparison.Ordinal))
                        continue;   // a popular-item row of a category word: it does not contain the text, so no tip is expected to list it

                    if (!tipKeys.Contains(suggestionKey))
                    {
                        hidden++;
                        if (hidden <= 100)
                            report.Add($"A: '{prefix}' -> the drop-down offers '{suggestion.Text}', the tip does not list it");
                    }
                }
            }

            // ---- B. one-letter slips of item names ----
            var blacklistKeys = new HashSet<string>(ResolveSpellingBlacklist().Keys, StringComparer.Ordinal);
            var targets = new List<Entry>();
            int counter = 0;
            foreach (Entry entry in vocabulary)
            {
                if (entry.ItemId == null || entry.Built)
                    continue;
                if (blacklistKeys.Contains(entry.Key) || counter++ % 10 == 0)   // every blacklisted name, and every 10th other item
                    targets.Add(entry);
            }
            targets.AddRange(unholdableEntries);   // every item no chest can hold (Weeds, Twig ...)

            int missed = 0;
            int tried = 0;
            foreach (Entry target in targets)
            {
                string key = target.Key;
                if (key.Length < 5)
                    continue;

                int mid = key.Length / 2;
                var typos = new List<string> { key.Remove(mid, 1) };   // a missing letter
                char[] swapped = key.ToCharArray();
                (swapped[mid], swapped[mid + 1]) = (swapped[mid + 1], swapped[mid]);
                typos.Add(new string(swapped));                         // two neighbouring letters swapped

                foreach (string typo in typos.Distinct())
                {
                    if (typo == key || AnyNameContains(typo))
                        continue;   // it is a real part of some name: not a typo (the search finds it by itself)

                    tried++;
                    bool found = FindSimilar(typo, 12, includeCategories: true).Any(name => MakeKey(name) == key);
                    if (!found)
                    {
                        missed++;
                        if (missed <= 100)
                            report.Add($"B: '{typo}' (a slip of '{target.Text}') does not bring '{target.Text}' back in 'Did you mean'");
                    }
                }
            }

            report.Add($"Audit done: {prefixes.Count} words checked ({hidden} drop-down names missing from their tip), {tried} slips checked over {targets.Count} names ({missed} not corrected). At most 100 lines of each kind are listed.");
            return report;
        }

        // ===================== THE ITEM LINES OF A "Showing results for:" TIP =====================
        // Display only (never changes what the search finds). NOTHING is ever filtered out here: every name that contains the typed text is listed,
        // so the tip always holds at least every row the auto-complete drop-down (Search) offers for the same text. The junk blacklist, the
        // junk / late-game demotion and the singular / plural pairing belong to "Did you mean" (FindSimilar) and nowhere else.
        // Furniture, hats, clothing, decoration and dishes are not removed, they only come LAST (rule 2), so a long list that is cut by the tip's
        // fixed height ("... and more", see ModEntry.MaxTipNames) loses them first. Two rules for the order:
        //   1. Items of the same root stay together: "Starfruit", then "Starfruit Wine", "Starfruit Jelly", "Dried Starfruit" ... directly below it
        //      (the root is the name without its Dried / Pickled / Smoked / Aged in front and Wine / Jelly / Juice / Honey / Roe / Bait / Seeds behind it).
        //   2. The groups are ordered by how much a player uses the ROOT item (ItemRanking: tier, then progress; the same order as everywhere else),
        //      then by how well the name fits the text (starts with it, a later word starts with it, contains it), then shorter first.
        //      Inside a group: the root item itself, then the built names (Wine, Jelly ...), then other items (Seeds ...).

        private static readonly string[] RootPrefixes = { "Dried ", "Pickled ", "Smoked ", "Aged " };
        private static readonly string[] RootSuffixes = { " Wine", " Jelly", " Juice", " Honey", " Roe", " Bait", " Seeds", " Seed", " Sapling", " Starter" };

        /// <summary>"Aged Carp Roe" -> "Carp", "Starfruit Wine" -> "Starfruit", "Starfruit Seeds" -> "Starfruit", "Wine" -> "Wine".</summary>
        private static string GuessRoot(string name)
        {
            string root = (name ?? "").Trim();
            foreach (string prefix in RootPrefixes)
            {
                if (root.Length > prefix.Length && root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    root = root.Substring(prefix.Length);
                    break;
                }
            }

            foreach (string suffix in RootSuffixes)
            {
                if (root.Length > suffix.Length && root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    root = root.Substring(0, root.Length - suffix.Length);
                    break;
                }
            }

            return root.Trim();
        }

        private sealed class TipCandidate
        {
            public string Text = "";
            public string Key = "";
            public string? Id;          // the item's own qualified id (for a built name: the id of its base product)
            public bool Built;
            public int Rank;            // 0 = starts with the text, 1 = a later word starts with it, 2 = contains it
            public string RootKey = "";
            public bool IsRoot;         // this item IS the root of its group
        }

        /// <summary>
        /// The names for the item lines of a "Showing results for:" tip, in the order described above. Names come from the whole game (every item a
        /// chest can hold, plus the names the game builds on the fly), the items in the chests right now, and <paramref name="extra"/> (chest items
        /// the search matched). Category words are not returned (the caller lists categories itself, first).
        /// </summary>
        /// <param name="typedKeys">The typed text as keys (spaces / hyphens removed, lower case) in their singular / plural spellings; the first one is the text as typed.</param>
        public IReadOnlyList<string> GetTipNames(IReadOnlyList<string> typedKeys, IEnumerable<(string Name, string? QualifiedId)>? extra = null)
        {
            if (typedKeys == null || typedKeys.Count == 0 || typedKeys[0].Length == 0)
                return Array.Empty<string>();

            var byKey = new Dictionary<string, TipCandidate>();

            void Consider(string text, string? id, bool built)
            {
                string key = MakeKey(text ?? "");
                if (key.Length == 0 || byKey.ContainsKey(key))
                    return;

                int rank = -1;
                foreach (string variant in typedKeys)
                {
                    if (variant.Length == 0 || !key.Contains(variant, StringComparison.Ordinal))
                        continue;

                    int r = key.StartsWith(variant, StringComparison.Ordinal) ? 0
                        : IsWordStart(text!, key, variant) ? 1
                        : 2;
                    if (rank < 0 || r < rank)
                        rank = r;
                }

                if (rank < 0 || ModEntry.IsCategoryWord(text!))
                    return;   // does not match, or a category word (listed as a category by the caller)

                string root = GuessRoot(text!);
                string rootKey = MakeKey(root);
                byKey[key] = new TipCandidate
                {
                    Text = text!,
                    Key = key,
                    Id = id,
                    Built = built,
                    Rank = rank,
                    RootKey = rootKey.Length > 0 ? rootKey : key,
                    IsRoot = rootKey == key
                };
            }

            foreach (Entry entry in ownedEntries)
                Consider(entry.Text, entry.ItemId, entry.Built);
            foreach (Entry entry in vocabulary)
                Consider(entry.Text, entry.ItemId, entry.Built);
            // items that cannot be carried (Weeds, Twig ...) are never listed in a tip
            if (extra != null)
            {
                foreach (var (name, id) in extra)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !IsCheatOnlyObject(id) && !cheatOnlyKeys.Contains(MakeKey(name)))
                        Consider(name.Trim(), id, built: false);   // a cheat-only plain Wine in a chest is never listed
                }
            }

            if (byKey.Count == 0)
                return Array.Empty<string>();

            // 1. group by root (nothing is filtered out: see the block comment above)
            var groups = byKey.Values.GroupBy(c => c.RootKey).Select(g =>
            {
                var members = g.ToList();
                TipCandidate? rootMember = members.FirstOrDefault(m => m.IsRoot);

                // the group is ranked by its ROOT item (Starfruit for "Starfruit Wine"), even when the root itself does not match the text
                string? rootId = rootMember?.Id;
                if (rootId == null && realItemsByKey.TryGetValue(g.Key, out Entry rootEntry))
                    rootId = rootEntry.ItemId;
                (int Tier, int Progress) lead = rootId != null
                    ? ItemRanking.Get(rootId)
                    : members.Select(m => ItemRanking.Get(m.Built ? null : m.Id)).OrderBy(r => r.Tier).ThenBy(r => r.Progress).First();

                var ordered = members
                    .OrderBy(m => m.IsRoot ? 0 : m.Built ? 1 : 2)
                    .ThenBy(m => ItemRanking.Get(m.Id).Tier)
                    .ThenBy(m => ItemRanking.Get(m.Id).Progress)
                    .ThenBy(m => m.Text.Length)
                    .ThenBy(m => m.Text, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                return (Lead: lead, Rank: members.Min(m => m.Rank), Name: (rootMember ?? ordered[0]).Text, Members: ordered);
            });

            // 2. order the groups
            List<TipCandidate> orderedCandidates = groups
                .OrderBy(g => g.Lead.Tier >= ItemRanking.FirstDemotedTier ? 1 : 0)
                .ThenBy(g => g.Lead.Tier)
                .ThenBy(g => g.Lead.Progress)
                .ThenBy(g => g.Rank)
                .ThenBy(g => g.Name.Length)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(g => g.Members)
                .ToList();

            // 3. every name once ("Ancient Seeds" and "Ancient Seed" are two different items and the drop-down offers both, so the tip lists both)
            var names = new List<string>(orderedCandidates.Count);
            foreach (TipCandidate candidate in orderedCandidates)
                names.Add(candidate.Text);

            return names;
        }

        /// <summary>True if the typed key starts one of the words of the name (not the first one): "fruit" in "Crystal Fruit".</summary>
        private static bool IsWordStart(string text, string key, string variant)
        {
            foreach (int start in FindWordStarts(text))
            {
                if (start > 0 && start <= key.Length && key.AsSpan(start).StartsWith(variant.AsSpan(), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Edit distance of two keys (insert, delete, replace, or swap two neighbouring letters = 1 each). Stops early and returns
        /// <c>limit + 1</c> as soon as the distance is certainly bigger than <paramref name="limit"/>.
        /// </summary>
        private static int EditDistance(string a, string b, int limit)
        {
            if (Math.Abs(a.Length - b.Length) > limit)
                return limit + 1;

            int n = a.Length;
            int m = b.Length;
            int[] twoBack = new int[m + 1];
            int[] previous = new int[m + 1];
            int[] current = new int[m + 1];

            for (int j = 0; j <= m; j++)
                previous[j] = j;

            for (int i = 1; i <= n; i++)
            {
                current[0] = i;
                int rowMin = current[0];

                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        value = Math.Min(value, twoBack[j - 2] + 1);   // two neighbouring letters swapped ("satr" -> "star")
                    current[j] = value;
                    if (value < rowMin)
                        rowMin = value;
                }

                if (rowMin > limit)
                    return limit + 1;

                int[] recycled = twoBack;
                twoBack = previous;
                previous = current;
                current = recycled;
            }

            return Math.Min(previous[m], limit + 1);
        }

        private static int CompareHits(Hit a, Hit b)
        {
            // the name that equals the typed text always comes first (ModEntry.GetKnownNameCheck relies on this)
            if (a.Exact != b.Exact)
                return a.Exact ? -1 : 1;

            // all categories come before all items, shortest category name first ("Roe" before "Aged Roe")
            if (a.IsCategory != b.IsCategory)
                return a.IsCategory ? -1 : 1;

            if (!a.IsCategory)
            {
                if (a.Rank != b.Rank)
                    return a.Rank < b.Rank ? -1 : 1;   // names that START with the typed text come before names that only contain it as a later word

                // same way of matching: furniture / decoration / cooking / trash last, late-game items just before them
                if (a.Demote != b.Demote)
                    return a.Demote < b.Demote ? -1 : 1;

                // then the items a player uses the most (the same everyday ranking as everywhere: tier, then progress)
                if (a.Usage.Tier != b.Usage.Tier)
                    return a.Usage.Tier < b.Usage.Tier ? -1 : 1;
                if (a.Usage.Progress != b.Usage.Progress)
                    return a.Usage.Progress < b.Usage.Progress ? -1 : 1;

                if (a.Owned != b.Owned)
                    return a.Owned ? -1 : 1;
            }

            if (a.Entry.Unholdable != b.Entry.Unholdable)
                return a.Entry.Unholdable ? 1 : -1;   // items no chest can hold come last

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

    /// <summary>
    /// How useful / central an item is to a player, for ordering suggestions (the "Showing results for:" tip and "Did you mean"). Display only:
    /// it never changes what the search finds. Two numbers, both "smaller = shown earlier":
    ///   Tier      what kind of thing it is, by how much a player uses that kind:
    ///               1 crops, fruit, vegetables, forage and mushrooms, flowers, fish, gems and minerals, ores, bars, coal, wood, stone, animal products,
    ///                 seeds, monster loot (the everyday resources)
    ///               2 artisan goods and other processed things (Wine, Jelly, Juice, Cheese, Mayonnaise, Honey, Roe, syrups, ingredients)
    ///               3 tools, weapons, machines and other big craftables, crafted items, bait, tackle, fertilizer, rings, boots, trinkets, books, artifacts
    ///               4 cooked dishes                                              } "demoted": the suggestions put these last unless
    ///               5 decoration: paths, floors, fences, gates; trash           } nothing else matches as well
    ///               6 furniture, hats, clothing                                  }
    ///   Progress  0 .. 100: when a player meets it and how much they use it, inside its tier. Early, everyday items first (Crystal Fruit,
    ///             Salmonberry), core late-game items in the middle (Starfruit, Ancient Fruit), late, mostly-for-fun items at the end (Qi Fruit).
    ///             Well-known items have a fixed value (looked up by the game's internal English name, so it works in every language); any other
    ///             item is judged by its sell price (cheap = early), and late-game names (Qi, Radioactive, Prismatic, Galaxy, legendary fish ...)
    ///             go to the end.
    /// Works on the qualified item id, so a flavored item ("Peach Wine", id (O)348) is ranked like its base product.
    /// </summary>
    internal static class ItemRanking
    {
        /// <summary>Tier and progress of an item the game does not know (another mod's item that is only in a chest).</summary>
        public static readonly (int Tier, int Progress) Unknown = (4, 50);

        /// <summary>Tier 4 and higher are pushed to the end of every suggestion list (cooking, decoration, furniture, clothing).</summary>
        public const int FirstDemotedTier = 4;

        /// <summary>Progress from which an item counts as late-game / mostly for fun (Qi Fruit, Radioactive and Prismatic items, legendary fish ...).</summary>
        public const int LateProgress = 88;

        private static readonly Dictionary<string, (int Tier, int Progress)> cache = new();
        private static object? cacheBuiltFor;

        private static readonly HashSet<int> TierOneCategories = new() { -79, -75, -81, -80, -4, -2, -12, -15, -16, -5, -6, -18, -14, -17, -74, -28 };
        private static readonly HashSet<int> TierTwoCategories = new() { -26, -27, -23, -25 };
        private static readonly HashSet<int> TierThreeCategories = new() { -8, -9, -19, -21, -22, -29, -96, -97, -98, -99, -101, -102, -103 };

        /// <summary>Name endings of decoration crafts (paths, floors, fences ...): tier 5, even though the game files them under "crafting".</summary>
        private static readonly string[] DecorNameEndings = { "Path", "Floor", "Flooring", "Fence", "Gate", "Brazier", "Stepping Stone", "Walkway" };

        private static readonly string[] LateNameMarks = { "Radioactive", "Prismatic", "Galaxy", "Infinity", "Dragon", "Magma", "Void", "Golden Walnut", "Mystic", "Legend" };

        public static (int Tier, int Progress) Get(string? qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return Unknown;

            if (!ReferenceEquals(cacheBuiltFor, Game1.objectData))
            {
                cache.Clear();   // the item data was reloaded (another mod changed it)
                cacheBuiltFor = Game1.objectData;
            }

            if (cache.TryGetValue(qualifiedId, out var cached))
                return cached;

            (int Tier, int Progress) result = Unknown;
            try
            {
                ParsedItemData? data = ItemRegistry.GetData(qualifiedId);
                if (data != null && !data.IsErrorItem)
                    result = (GetTier(data), GetProgress(data));
            }
            catch (Exception)
            {
                result = Unknown;
            }

            cache[qualifiedId] = result;
            return result;
        }

        /// <summary>
        /// True for the kinds of items that only belong in a "Showing results for:" tip when the player clearly means them: furniture, hats, clothing,
        /// decoration (paths, floors, fences ...), trash and cooked dishes (tier 5 and 6, and cooking). Null / unknown ids are not junk (another mod's
        /// item that only lies in a chest is something the player really has).
        /// </summary>
        public static bool IsJunk(string? qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return false;

            try
            {
                ParsedItemData? data = ItemRegistry.GetData(qualifiedId);
                if (data == null || data.IsErrorItem)
                    return false;
                return data.Category == -7 || Get(qualifiedId).Tier >= 5;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>True for late-game, mostly-for-fun items (progress <see cref="LateProgress"/> or more). Null / unknown ids are not late.</summary>
        public static bool IsLate(string? qualifiedId)
        {
            return !string.IsNullOrEmpty(qualifiedId) && Get(qualifiedId).Progress >= LateProgress;
        }

        private static int GetTier(ParsedItemData data)
        {
            string type = data.ItemType?.Identifier ?? "";
            int category = data.Category;

            if (type == "(F)" || type == "(H)" || type == "(S)" || type == "(P)" || category == -95 || category == -100 || (category == -24 && type != "(O)"))
                return 6;
            if (type == "(T)" || type == "(W)" || type == "(B)" || type == "(TR)" || type == "(BC)")
                return 3;

            string name = data.InternalName ?? "";
            if (DecorNameEndings.Any(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) || category == -24 || category == -20 || category == -999)
                return 5;

            if (TierOneCategories.Contains(category))
                return 1;
            if (TierTwoCategories.Contains(category))
                return 2;
            if (TierThreeCategories.Contains(category))
                return 3;
            if (category == -7)
                return 4;
            if (string.Equals(data.ObjectType, "Arch", StringComparison.OrdinalIgnoreCase))
                return 3;   // artifacts

            return 4;
        }

        private static int GetProgress(ParsedItemData data)
        {
            string name = data.InternalName ?? "";
            if (ProgressOverrides.TryGetValue(name, out int fixedScore))
                return fixedScore;

            int price = 0;
            List<string>? tags = null;
            if (data.ItemType?.Identifier == "(O)" && Game1.objectData.TryGetValue(data.ItemId, out ObjectData? objectData) && objectData != null)
            {
                price = objectData.Price;
                tags = objectData.ContextTags;
            }

            int score = price <= 0 ? 45
                : price <= 40 ? 15
                : price <= 100 ? 22
                : price <= 200 ? 32
                : price <= 400 ? 44
                : price <= 800 ? 56
                : 68;

            if (data.Category == -74)
                score = Math.Min(score + 25, 85);   // seeds come after what they grow into (Starfruit before Starfruit Seeds)

            bool late = LateNameMarks.Any(mark => name.Contains(mark, StringComparison.OrdinalIgnoreCase))
                || name.StartsWith("Qi ", StringComparison.Ordinal)
                || (tags != null && (tags.Contains("fish_legendary") || tags.Contains("fish_legendary_family")));
            return late ? Math.Max(score, LateProgress) : score;
        }

        /// <summary>Fixed progress values of well-known items (internal English names). Lower = earlier and more used.</summary>
        private static readonly Dictionary<string, int> ProgressOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            // everyday resources
            ["Wood"] = 0,
            ["Stone"] = 0,
            ["Fiber"] = 1,
            ["Sap"] = 2,
            ["Coal"] = 3,
            ["Clay"] = 4,
            ["Hardwood"] = 6,
            ["Copper Ore"] = 4,
            ["Iron Ore"] = 8,
            ["Gold Ore"] = 14,
            ["Iridium Ore"] = 40,
            ["Copper Bar"] = 5,
            ["Iron Bar"] = 9,
            ["Gold Bar"] = 15,
            ["Iridium Bar"] = 42,
            ["Refined Quartz"] = 10,
            ["Quartz"] = 5,
            ["Earth Crystal"] = 6,
            ["Frozen Tear"] = 10,
            ["Fire Quartz"] = 14,
            ["Amethyst"] = 6,
            ["Topaz"] = 6,
            ["Aquamarine"] = 10,
            ["Jade"] = 10,
            ["Emerald"] = 12,
            ["Ruby"] = 12,
            ["Diamond"] = 16,
            ["Prismatic Shard"] = 80,
            ["Egg"] = 2,
            ["Large Egg"] = 4,
            ["Brown Egg"] = 2,
            ["Large Brown Egg"] = 4,
            ["Milk"] = 3,
            ["Large Milk"] = 5,
            ["Goat Milk"] = 8,
            ["L. Goat Milk"] = 10,
            ["Wool"] = 10,
            ["Duck Egg"] = 9,
            ["Duck Feather"] = 12,
            ["Rabbit's Foot"] = 20,
            ["Truffle"] = 14,
            ["Void Egg"] = 60,
            ["Dinosaur Egg"] = 70,
            ["Golden Egg"] = 80,
            ["Ostrich Egg"] = 65,
            // forage and mushrooms
            ["Wild Horseradish"] = 1,
            ["Daffodil"] = 1,
            ["Leek"] = 1,
            ["Dandelion"] = 1,
            ["Salmonberry"] = 2,
            ["Spring Onion"] = 2,
            ["Grape"] = 4,
            ["Spice Berry"] = 3,
            ["Sweet Pea"] = 3,
            ["Blackberry"] = 3,
            ["Hazelnut"] = 4,
            ["Wild Plum"] = 4,
            ["Common Mushroom"] = 3,
            ["Crystal Fruit"] = 5,
            ["Crocus"] = 5,
            ["Holly"] = 5,
            ["Snow Yam"] = 6,
            ["Winter Root"] = 6,
            ["Chanterelle"] = 6,
            ["Morel"] = 6,
            ["Red Mushroom"] = 8,
            ["Purple Mushroom"] = 12,
            ["Cave Carrot"] = 8,
            ["Coconut"] = 30,
            ["Cactus Fruit"] = 32,
            ["Magma Cap"] = 82,
            // crops
            ["Parsnip"] = 0,
            ["Potato"] = 1,
            ["Green Bean"] = 1,
            ["Cauliflower"] = 2,
            ["Kale"] = 2,
            ["Garlic"] = 3,
            ["Strawberry"] = 4,
            ["Rhubarb"] = 20,
            ["Blueberry"] = 3,
            ["Melon"] = 4,
            ["Tomato"] = 3,
            ["Hot Pepper"] = 3,
            ["Corn"] = 4,
            ["Wheat"] = 3,
            ["Radish"] = 4,
            ["Hops"] = 6,
            ["Red Cabbage"] = 10,
            ["Poppy"] = 6,
            ["Summer Spangle"] = 6,
            ["Sunflower"] = 6,
            ["Pumpkin"] = 4,
            ["Cranberries"] = 4,
            ["Eggplant"] = 5,
            ["Yam"] = 5,
            ["Bok Choy"] = 5,
            ["Amaranth"] = 6,
            ["Artichoke"] = 12,
            ["Beet"] = 20,
            ["Fairy Rose"] = 8,
            ["Tulip"] = 2,
            ["Blue Jazz"] = 2,
            ["Coffee Bean"] = 14,
            ["Rice"] = 10,
            ["Unmilled Rice"] = 10,
            ["Tea Leaves"] = 16,
            ["Starfruit"] = 45,
            ["Ancient Fruit"] = 52,
            ["Sweet Gem Berry"] = 66,
            ["Taro Root"] = 62,
            ["Pineapple"] = 64,
            ["Ginger"] = 58,
            ["Powdermelon"] = 20,
            ["Carrot"] = 3,
            ["Summer Squash"] = 5,
            ["Broccoli"] = 6,
            ["Qi Fruit"] = 96,
            ["Qi Bean"] = 96,
            // fruit trees
            ["Cherry"] = 8,
            ["Apricot"] = 8,
            ["Orange"] = 9,
            ["Peach"] = 9,
            ["Apple"] = 9,
            ["Pomegranate"] = 10,
            ["Banana"] = 60,
            ["Mango"] = 62,
            // fish (the common ones; legendary fish are late via their tag)
            ["Sardine"] = 2,
            ["Anchovy"] = 2,
            ["Carp"] = 2,
            ["Bream"] = 2,
            ["Sunfish"] = 2,
            ["Herring"] = 3,
            ["Chub"] = 3,
            ["Smallmouth Bass"] = 3,
            ["Largemouth Bass"] = 4,
            ["Rainbow Trout"] = 4,
            ["Catfish"] = 5,
            ["Salmon"] = 4,
            ["Perch"] = 5,
            ["Pike"] = 5,
            ["Walleye"] = 6,
            ["Tuna"] = 6,
            ["Red Snapper"] = 5,
            ["Tilapia"] = 6,
            ["Eel"] = 6,
            ["Squid"] = 6,
            ["Halibut"] = 6,
            ["Flounder"] = 7,
            ["Red Mullet"] = 6,
            ["Shad"] = 6,
            ["Pufferfish"] = 12,
            ["Tiger Trout"] = 10,
            ["Ghostfish"] = 12,
            ["Stonefish"] = 20,
            ["Ice Pip"] = 24,
            ["Lava Eel"] = 40,
            ["Sandfish"] = 30,
            ["Scorpion Carp"] = 34,
            ["Sturgeon"] = 14,
            ["Dorado"] = 12,
            ["Albacore"] = 8,
            ["Midnight Carp"] = 10,
            ["Woodskip"] = 10,
            ["Blobfish"] = 50,
            ["Midnight Squid"] = 50,
            ["Spook Fish"] = 50,
            ["Stingray"] = 60,
            ["Lionfish"] = 60,
            ["Blue Discus"] = 60,
            // processed goods
            ["Wine"] = 2,
            ["Jelly"] = 2,
            ["Juice"] = 3,
            ["Pickles"] = 3,
            ["Cheese"] = 2,
            ["Goat Cheese"] = 5,
            ["Mayonnaise"] = 1,
            ["Duck Mayonnaise"] = 8,
            ["Honey"] = 4,
            ["Beer"] = 4,
            ["Pale Ale"] = 5,
            ["Coffee"] = 6,
            ["Cloth"] = 8,
            ["Oil"] = 6,
            ["Truffle Oil"] = 10,
            ["Maple Syrup"] = 8,
            ["Oak Resin"] = 8,
            ["Pine Tar"] = 8,
            ["Mead"] = 8,
            ["Roe"] = 10,
            ["Aged Roe"] = 12,
            ["Caviar"] = 30,
            ["Green Tea"] = 16,
            ["Void Mayonnaise"] = 60,
            ["Dinosaur Mayonnaise"] = 70
        };
    }
}