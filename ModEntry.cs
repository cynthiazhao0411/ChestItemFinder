using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestItemFinder
{
    public class ModEntry : Mod
    {
        private List<(GameLocation location, Vector2 position, Chest chest)> highlightedChests = new();
        private string notifyTitle = "";
        private string notifyBody = "";
        // Real-world clock: not affected by in-game time (CJB Cheats Menu, TimeSpeed, Time Master,
        // Turbo, ...), so freezing, slowing or speeding up game time never changes these timers.
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long notifyUntilMs = 0;
        private long highlightUntilMs = -1;   // -1 = highlights never expire on their own
        private Vector2 lastGrabTile = new Vector2(-1, -1);
        private ModConfig Config = new();

        // --- search history (saved per save file, newest first) ---
        private const string SearchHistorySaveKey = "search-history";
        private readonly List<string> searchHistory = new();

        // --- search suggestions (auto-complete): the word list is built the first time the search box opens ---
        private SuggestionIndex? suggestionIndex;

        // --- hover interaction on the result panel ---
        // While the mouse is over the panel its countdown is frozen. After the mouse leaves, the countdown
        // resumes, but never from less than this, so a panel that was almost gone does not vanish instantly.
        private const long HoverMinRemainingMs = 2000;
        private const long MaxFrameDeltaMs = 250;
        private long lastHudFrameMs = -1;

        // Prefix of the keys used for Ginger Island areas. The bare prefix means "all island areas together".
        private const string IslandKeyPrefix = "\u0001island:";

        // What was found per place at the time of the last search: place key -> (item display name -> total stack count).
        // Kept separately from highlightedChests, so the list still works after a chest is opened and the highlights are cleared.
        private readonly Dictionary<string, Dictionary<string, int>> placeItems = new();

        // The hoverable place words inside notifyBody (positions are character offsets into notifyBody).
        private List<PlaceEntry> placeEntries = new();

        private readonly struct PlaceEntry
        {
            public readonly string Key;     // lookup key into placeItems
            public readonly string Title;   // heading shown in the item list
            public readonly int Start;      // first character in notifyBody
            public readonly int Length;     // number of characters

            public PlaceEntry(string key, string title, int start, int length)
            {
                Key = key;
                Title = title;
                Start = start;
                Length = length;
            }
        }

        // internal location name -> readable name
        private static readonly Dictionary<string, string> FriendlyNames = new()
        {
            ["Farm"] = "Farm",
            ["FarmHouse"] = "Farmhouse",
            ["FarmCave"] = "Farm Cave",
            ["Greenhouse"] = "Greenhouse",
            ["Cellar"] = "Cellar",
            ["Town"] = "Town",
            ["BusStop"] = "Bus Stop",
            ["Forest"] = "Cindersap Forest",
            ["Mountain"] = "Mountain",
            ["Backwoods"] = "Backwoods",
            ["Railroad"] = "Railroad",
            ["Beach"] = "Beach",
            ["Desert"] = "Calico Desert",
            ["Woods"] = "Secret Woods",
            ["Sewer"] = "Sewers",
            ["IslandSouth"] = "Ginger Island (South)",
            ["IslandSouthEast"] = "Ginger Island (Southeast)",
            ["IslandEast"] = "Ginger Island (East)",
            ["IslandWest"] = "Ginger Island (West)",
            ["IslandNorth"] = "Ginger Island (North)",
            ["IslandFarmHouse"] = "Ginger Island Farmhouse",
            ["IslandFarmCave"] = "Ginger Island Farm Cave",
            ["IslandHut"] = "Ginger Island Hut",
            ["IslandFieldOffice"] = "Ginger Island Field Office"
        };

        // highlight colors offered in the config menu (the first one is the default)
        private static readonly string[] HighlightColorNames =
        {
            "Yellow", "Red", "Orange", "Green", "Blue", "Purple", "Black", "White", "Gray", "Brown"
        };

        private static readonly Dictionary<string, Color> HighlightColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Yellow"] = Color.Yellow,
            ["Red"] = Color.Red,
            ["Orange"] = Color.Orange,
            ["Green"] = Color.LimeGreen,   // brighter than XNA's very dark Color.Green, so it shows on grass
            ["Blue"] = Color.Blue,
            ["Purple"] = Color.Purple,
            ["Black"] = Color.Black,
            ["White"] = Color.White,
            ["Gray"] = Color.Gray,
            ["Brown"] = Color.Brown
        };

        /// <summary>Returns the matching color name from the list above (fixing the casing), or "Yellow" if it is unknown.</summary>
        private static string NormalizeColorName(string? name)
        {
            string trimmed = name?.Trim() ?? "";
            foreach (string known in HighlightColorNames)
            {
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return "Yellow";
        }

        // highlight timeout choices offered in the config menu (seconds, and the text shown for each)
        private static readonly int[] TimeoutSecondsChoices = { 0, 30, 60, 120, 180, 300 };

        private static readonly string[] TimeoutLabels =
        {
            "Never", "30 seconds", "1 minute", "2 minutes", "3 minutes", "5 minutes"
        };

        /// <summary>Returns the closest allowed timeout in seconds (fixes hand-edited config.json values).</summary>
        private static int NormalizeTimeoutSeconds(int seconds)
        {
            return TimeoutSecondsChoices.OrderBy(choice => Math.Abs(choice - seconds)).First();
        }

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            Config.HighlightTimeoutSeconds = NormalizeTimeoutSeconds(Config.HighlightTimeoutSeconds);
            // opacity is 0-100 in steps of 20; fix up hand-edited config.json values
            Config.HighlightOpacityPercent = (int)Math.Round(Math.Clamp(Config.HighlightOpacityPercent, 0, 100) / 20.0) * 20;
            Config.HighlightColor = NormalizeColorName(Config.HighlightColor);

            helper.ConsoleCommands.Add("find_item", "Search for an item in all chests. Usage: find_item <item name>", OnFindItem);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearHighlights();
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += (_, _) => SaveSearchHistory();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => searchHistory.Clear();   // history belongs to a save file: do not leak it into the next one
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.RenderedHud += OnRenderedHud;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        // Optional integration: lets players change the key in-game via Generic Mod Config Menu.
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null) return;

            api.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            api.AddSectionTitle(ModManifest, () => "ChestItemFinder");

            api.AddKeybindList(
                mod: ModManifest,
                getValue: () => Config.SearchKey,
                setValue: value => Config.SearchKey = value,
                name: () => "Search key",
                tooltip: () => "Press this key (or key combination) in-game to open the chest search box. Default: LeftShift + T."
            );

            // a slider that follows the mouse smoothly (the knob is not forced onto a step while dragging); the nearest of the fixed timeouts
            // is shown next to it (Never, 30 seconds, 1 minute, ...) and is what gets saved
            var timeoutSlider = new SmoothSlider(
                readConfig: () => Array.IndexOf(TimeoutSecondsChoices, NormalizeTimeoutSeconds(Config.HighlightTimeoutSeconds)),
                writeConfig: step => Config.HighlightTimeoutSeconds = TimeoutSecondsChoices[Math.Clamp(step, 0, TimeoutSecondsChoices.Length - 1)],
                stopCount: TimeoutSecondsChoices.Length,
                formatStop: step => TimeoutLabels[Math.Clamp(step, 0, TimeoutLabels.Length - 1)]
            );

            api.AddComplexOption(
                mod: ModManifest,
                name: () => "Highlight timeout",
                draw: timeoutSlider.Draw,
                tooltip: () => "Real-world time before chest highlights disappear by themselves. Never = they stay until you open a found chest. Not affected by in-game time speed. Default: 3 minutes.",
                beforeMenuOpened: timeoutSlider.Reload,
                beforeSave: timeoutSlider.Commit,
                afterReset: timeoutSlider.Reload,
                beforeMenuClosed: timeoutSlider.CancelDrag,
                height: () => SmoothSlider.RowHeight
            );

            // slider with visible tick marks and labels at the six fixed stops (0, 20, 40, 60, 80, 100)
            var opacitySlider = new TickSlider(
                readConfig: () => Config.HighlightOpacityPercent,
                writeConfig: value => Config.HighlightOpacityPercent = value,
                min: 0,
                max: 100,
                step: 20
            );

            api.AddComplexOption(
                mod: ModManifest,
                name: () => "Highlight opacity",
                draw: opacitySlider.Draw,
                tooltip: () => "How strong the flashing highlight on chests is. 0% = no highlight is drawn, 100% = fully opaque at the brightest point. Default: 60%.",
                beforeMenuOpened: opacitySlider.Reload,
                beforeSave: opacitySlider.Commit,
                afterReset: opacitySlider.Reload,
                beforeMenuClosed: opacitySlider.CancelDrag,
                height: () => TickSlider.RowHeight
            );

            api.AddTextOption(
                mod: ModManifest,
                getValue: () => Config.HighlightColor,
                setValue: value => Config.HighlightColor = NormalizeColorName(value),
                name: () => "Highlight color",
                tooltip: () => "Color of the flashing highlight on chests. Works together with the opacity setting. Default: Yellow.",
                allowedValues: HighlightColorNames
            );
        }

        // Loads this save file's search history (at most 5 words, no duplicates).
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            searchHistory.Clear();

            try
            {
                SearchHistoryData? data = Helper.Data.ReadSaveData<SearchHistoryData>(SearchHistorySaveKey);
                if (data?.Items == null)
                    return;

                foreach (string raw in data.Items)
                {
                    string item = (raw ?? "").Trim();
                    if (item.Length == 0 || searchHistory.Count >= SearchMenu.MaxHistory)
                        continue;
                    if (searchHistory.Any(h => string.Equals(h, item, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    searchHistory.Add(item);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not read the search history: {ex.Message}", LogLevel.Warn);
            }
        }

        // Writes the search history into the current save's mod data. Called after every change and again when the game saves.
        private void SaveSearchHistory()
        {
            if (!Context.IsWorldReady)
                return;

            try
            {
                Helper.Data.WriteSaveData(SearchHistorySaveKey, new SearchHistoryData { Items = new List<string>(searchHistory) });
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not save the search history: {ex.Message}", LogLevel.Warn);
            }
        }

        // Letter and number keys type a character into the text box when they open it.
        private static string? GetLeakedChar(SButton key)
        {
            string name = key.ToString();
            if (name.Length == 1)
                return name;
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                return name.Substring(1);
            return null;
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // remember which tile the player is interacting with (used to find the opened chest)
            if (Game1.activeClickableMenu == null)
                lastGrabTile = e.Cursor.GrabTile;

            if (!Config.SearchKey.JustPressed()) return;
            if (Game1.activeClickableMenu != null) return;

            Helper.Input.SuppressActiveKeybinds(Config.SearchKey);
            // e.Button is the key that completed the combo (e.g. T in LeftShift + T, or LeftShift if T was already held)
            Game1.activeClickableMenu = new SearchMenu(ScanChests, searchHistory, SaveSearchHistory, GetLeakedChar(e.Button), CreateSuggestionProvider());
        }

        // Prepares the auto-complete for one opening of the search box. If anything goes wrong this returns null and the
        // search box simply works without a drop-down, so a problem here can never break searching itself.
        private Func<string, IReadOnlyList<Suggestion>>? CreateSuggestionProvider()
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();                          // game item names (built once, rebuilt if the language or item data changed)
                suggestionIndex.SetOwnedNames(GetChestItemNames());     // what is in the chests right now (ranked first)

                SuggestionIndex index = suggestionIndex;
                return query => index.Search(query, SearchMenu.MaxSuggestions);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Search suggestions are unavailable: {ex.Message}", LogLevel.Warn);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
                return null;
            }
        }

        // The display name of every item in every chest (same chests as ScanChests looks at).
        private IEnumerable<string> GetChestItemNames()
        {
            foreach (var (location, _) in GetAllLocations())
            {
                foreach (var obj in location.objects.Pairs)
                {
                    if (obj.Value is not Chest chest)
                        continue;

                    foreach (Item? item in chest.Items)
                    {
                        if (item != null)
                            yield return item.DisplayName;
                    }
                }
            }
        }

        // Opening ANY chest that contains the target clears ALL highlights.
        // Opening a chest that is not a target does nothing.
        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (highlightedChests.Count == 0) return;
            if (e.NewMenu is not ItemGrabMenu menu) return;

            bool openedTarget;
            if (menu.context is Chest opened)
                openedTarget = highlightedChests.Any(h => ReferenceEquals(h.chest, opened));
            else
                openedTarget = highlightedChests.Any(h => h.location == Game1.currentLocation && h.position == lastGrabTile);

            if (openedTarget)
                ClearHighlights();
        }

        private void ClearHighlights()
        {
            highlightedChests.Clear();
            highlightUntilMs = -1;
        }

        // Highlights fade away on their own after the configured number of REAL minutes.
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (highlightedChests.Count > 0 && highlightUntilMs >= 0 && clock.ElapsedMilliseconds >= highlightUntilMs)
            {
                ClearHighlights();
                Monitor.Log("Chest highlights expired.", LogLevel.Debug);
            }
        }

        private void OnFindItem(string command, string[] args)
        {
            if (args.Length == 0)
            {
                Monitor.Log("Usage: find_item <item name>", LogLevel.Info);
                return;
            }

            ScanChests(string.Join(" ", args));
        }

        // Game1.locations only holds top-level locations; building interiors
        // (sheds, barns, coops, cabins) live inside each building, so walk those too.
        private IEnumerable<(GameLocation location, string label)> GetAllLocations()
        {
            var seen = new HashSet<GameLocation>();

            foreach (GameLocation root in Game1.locations)
            {
                if (seen.Add(root))
                    yield return (root, GetFriendlyName(root.Name));

                foreach (var building in root.buildings)
                {
                    GameLocation? indoors = building.indoors.Value;
                    if (indoors != null && seen.Add(indoors))
                        yield return (indoors, building.buildingType.Value);
                }
            }
        }

        // Ginger Island areas are shown as compact compass letters, e.g. "Ginger Island (E, W x2)".
        private static readonly Dictionary<string, string> IslandAreaCodes = new()
        {
            ["IslandNorth"] = "N",
            ["IslandEast"] = "E",
            ["IslandSouth"] = "S",
            ["IslandSouthEast"] = "SE",
            ["IslandWest"] = "W"
        };

        private static readonly string[] IslandAreaOrder = { "N", "E", "SE", "S", "W" };

        // Returns the island area label for a Ginger Island location, or null for any other location.
        private static string? GetIslandArea(string internalName)
        {
            if (!internalName.StartsWith("Island", StringComparison.OrdinalIgnoreCase))
                return null;
            if (IslandAreaCodes.TryGetValue(internalName, out string? code))
                return code;
            if (FriendlyNames.TryGetValue(internalName, out string? friendly))
                return friendly.Replace("Ginger Island ", "");
            return internalName.Substring(6);
        }

        private static string FormatCount(string name, int count)
        {
            return count > 1 ? $"{name} x{count}" : name;
        }

        private static string BuildPlaceSummary(Dictionary<string, int> mainCounts, Dictionary<string, int> islandCounts)
        {
            var parts = mainCounts.Select(p => FormatCount(p.Key, p.Value)).ToList();

            if (islandCounts.Count > 0)
            {
                var areas = islandCounts
                    .OrderBy(p => { int i = Array.IndexOf(IslandAreaOrder, p.Key); return i < 0 ? IslandAreaOrder.Length : i; })
                    .ThenBy(p => p.Key)
                    .Select(p => FormatCount(p.Key, p.Value));
                parts.Add("Ginger Island (" + string.Join(", ", areas) + ")");
            }

            return string.Join(", ", parts);
        }

        private static string GetFriendlyName(string internalName)
        {
            if (FriendlyNames.TryGetValue(internalName, out string? friendly))
                return friendly;
            if (internalName.StartsWith("Island", StringComparison.OrdinalIgnoreCase))
                return "Ginger Island (" + internalName.Substring(6) + ")";
            return internalName;
        }

        // ===================== SEARCH MATCHING =====================
        // Priority (the first rule that applies wins):
        //   N  number    "-26" = every item whose category id is -26 (Artisan Goods)
        //                "268" = the object with item id 268 (Starfruit)
        //   P1 category  the exact category name: "fruit", "fish", "gem", "seeds" ...
        //                "fruit" lights Starfruit / Ancient Fruit, never Starfruit Wine (that is Artisan Goods)
        //   P2 category  multi-word categories, any whole word or combination:
        //                "artisan" / "goods" / "artisan goods" -> Artisan Goods (Mayonnaise, Cheese ...)
        //   P3 item name exact name only: "star fruit" or "Starfruit" -> only Starfruit, not Starfruit Wine
        //   P4 contains  fallback: "star" -> Starfruit, Starfruit Wine, ...
        // Category ids come from the Stardew Valley Wiki, page Modding:Items (section "Categories").

        // P1: single-word category names. A word can map to several ids when the game shows
        // them under the same in-game name (e.g. gems -2 and minerals -12 are both "Mineral").
        private static readonly Dictionary<string, int[]> SingleWordCategories = new()
        {
            ["gem"] = new[] { -2 },
            ["gems"] = new[] { -2 },                 // Object.GemCategory
            ["fish"] = new[] { -4 },                                                    // Object.FishCategory
            ["egg"] = new[] { -5 },
            ["eggs"] = new[] { -5 },                 // Object.EggCategory
            ["milk"] = new[] { -6 },                                                    // Object.MilkCategory
            ["cooking"] = new[] { -7 },                                                 // Object.CookingCategory
            ["crafting"] = new[] { -8 },                                                // Object.CraftingCategory
            ["mineral"] = new[] { -2, -12 },
            ["minerals"] = new[] { -2, -12 },        // gems (-2) + minerals (-12), both shown as "Mineral"
            ["meat"] = new[] { -14 },                                                   // Object.meatCategory
            ["fertilizer"] = new[] { -19 },
            ["fertilizers"] = new[] { -19 },         // Object.fertilizerCategory
            ["junk"] = new[] { -20 },                                                   // Object.junkCategory
            ["bait"] = new[] { -21 },                                                   // Object.baitCategory
            ["tackle"] = new[] { -22 },                                                 // Object.tackleCategory
            ["furniture"] = new[] { -24 },                                              // Object.furnitureCategory
            ["ingredient"] = new[] { -25 },
            ["ingredients"] = new[] { -25 },         // Object.ingredientsCategory
            ["syrup"] = new[] { -27 },                                                  // Object.syrupCategory
            ["equipment"] = new[] { -29 },                                              // Object.equipmentCategory
            ["seed"] = new[] { -74 },
            ["seeds"] = new[] { -74 },               // Object.SeedsCategory
            ["vegetable"] = new[] { -75 },
            ["vegetables"] = new[] { -75 },          // Object.VegetableCategory
            ["fruit"] = new[] { -79 },
            ["fruits"] = new[] { -79 },              // Object.FruitsCategory
            ["flower"] = new[] { -80 },
            ["flowers"] = new[] { -80 },             // Object.flowersCategory
            ["greens"] = new[] { -81 },
            ["forage"] = new[] { -81 },              // Object.GreensCategory (shown as "Forage")
            ["hat"] = new[] { -95 },
            ["hats"] = new[] { -95 },                // Object.hatCategory
            ["ring"] = new[] { -96 },
            ["rings"] = new[] { -96 },               // Object.ringCategory
            ["boots"] = new[] { -97 },                                                  // Object.bootsCategory
            ["weapon"] = new[] { -98 },
            ["weapons"] = new[] { -98 },             // Object.weaponCategory
            ["tool"] = new[] { -99 },
            ["tools"] = new[] { -99 },               // Object.toolCategory
            ["clothing"] = new[] { -100 },                                              // Object.clothingCategory
            ["trinket"] = new[] { -101 },
            ["trinkets"] = new[] { -101 },           // Object.trinketCategory
            ["book"] = new[] { -102 },
            ["books"] = new[] { -102 },              // Object.booksCategory
            ["litter"] = new[] { -999 }                                                 // Object.litterCategory
        };

        // P2: multi-word categories. Each entry lists every word that triggers it (lower-case).
        // A query matches an entry when ALL of its words are in the list, or when the whole
        // query with separators removed is in the list ("artisan_goods" -> "artisangoods").
        private static readonly (int[] Ids, HashSet<string> Words)[] MultiWordCategories =
        {
            (new[] { -9 },        new HashSet<string> { "big", "craftable", "craftables", "bigcraftable", "bigcraftables" }),   // Object.BigCraftableCategory
            (new[] { -15 },       new HashSet<string> { "metal", "resource", "resources", "metalresources" }),                  // Object.metalResources
            (new[] { -16 },       new HashSet<string> { "building", "resource", "resources", "buildingresources" }),            // Object.buildingResources
            (new[] { -17 },       new HashSet<string> { "sellatpierres" }),                                                     // Object.sellAtPierres
            (new[] { -18 },       new HashSet<string> { "sellatpierresandmarnies" }),                                           // Object.sellAtPierresAndMarnies
            (new[] { -23 },       new HashSet<string> { "sellatfishshop" }),                                                    // Object.sellAtFishShopCategory
            (new[] { -26, -27 },  new HashSet<string> { "artisan", "goods", "artisangoods" }),                                  // artisanGoodsCategory + syrupCategory (both shown as "Artisan Goods")
            (new[] { -28 },       new HashSet<string> { "monster", "loot", "monsterloot" }),                                    // Object.monsterLootCategory
            (new[] { -103 },      new HashSet<string> { "skill", "book", "books", "skillbook", "skillbooks" })                  // Object.skillBooksCategory
        };

        private static readonly char[] QuerySeparators = { ' ', '\t', '_', '-' };

        // lower-case and drop spaces, underscores and hyphens: "Star Fruit" -> "starfruit"
        private static string Norm(string s)
        {
            return new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-').ToArray()).ToLowerInvariant();
        }

        // P3: the whole name equals the query (spaces / case ignored)
        private static bool NameEquals(Item item, string normQuery)
        {
            return Norm(item.DisplayName) == normQuery
                || Norm(item.Name) == normQuery;
        }

        // P4: the name contains the query (literal, case-insensitive; also ignoring spaces)
        private static bool NameContains(Item item, string query, string normQuery)
        {
            return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Norm(item.DisplayName).Contains(normQuery)
                || Norm(item.Name).Contains(normQuery);
        }

        // Singular and plural spellings of a normalized query (see Norm), so "cranberry" finds "Cranberries" and the other way round.
        // The first entry is always the query as typed. Only the end of the text is changed, i.e. the last word.
        private static List<string> PluralVariants(string q)
        {
            var result = new List<string> { q };
            void Add(string s)
            {
                if (s.Length >= 2 && !result.Contains(s))
                    result.Add(s);
            }

            if (q.Length > 3 && q.EndsWith("ies"))
            {
                Add(q[..^3] + "y");                  // cranberries -> cranberry
            }
            else if (q.Length > 3 && q.EndsWith("ves"))
            {
                Add(q[..^3] + "f");                  // leaves -> leaf
                Add(q[..^3] + "fe");                 // knives -> knife
                Add(q[..^1]);                        // leaves -> leave
            }
            else if (q.Length > 3 && q.EndsWith("es"))
            {
                Add(q[..^2]);                        // tomatoes -> tomato, boxes -> box
                Add(q[..^1]);                        // apples -> apple, grapes -> grape
            }
            else if (q.Length > 3 && q.EndsWith("s") && !q.EndsWith("ss") && !q.EndsWith("us") && !q.EndsWith("is"))
            {
                Add(q[..^1]);                        // starfruits -> starfruit
            }
            else
            {
                if (q.Length >= 3 && q.EndsWith("y") && !"aeiou".Contains(q[^2]))
                    Add(q[..^1] + "ies");            // cranberry -> cranberries
                if (q.EndsWith("f"))
                    Add(q[..^1] + "ves");            // leaf -> leaves
                else if (q.EndsWith("fe"))
                    Add(q[..^2] + "ves");            // knife -> knives

                Add(q + "s");                        // apple -> apples
                if (q.EndsWith("s") || q.EndsWith("x") || q.EndsWith("z") || q.EndsWith("ch") || q.EndsWith("sh") || q.EndsWith("o"))
                    Add(q + "es");                   // tomato -> tomatoes, glass -> glasses
            }

            return result;
        }

        private static Func<Item, bool> BuildMatcher(List<Item> allItems, string rawQuery)
        {
            string query = rawQuery.Trim();
            string normQuery = Norm(query);

            // N: pure number. Negative = category id, otherwise object item id ("(O)268").
            // Note: flavored goods share the id of their base product (every Wine is (O)348).
            if (int.TryParse(query, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            {
                if (number < 0)
                    return i => i.Category == number;

                string qualifiedId = "(O)" + number.ToString(CultureInfo.InvariantCulture);
                return i => i.QualifiedItemId == qualifiedId;
            }

            // P1: exact category word
            if (SingleWordCategories.TryGetValue(query.ToLowerInvariant(), out int[]? ids1))
                return i => ids1.Contains(i.Category);

            // P2: multi-word category
            string[] tokens = query.ToLowerInvariant().Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            var ids2 = new HashSet<int>();
            foreach (var (ids, words) in MultiWordCategories)
            {
                if ((tokens.Length > 0 && tokens.All(words.Contains)) || words.Contains(normQuery))
                    foreach (int id in ids)
                        ids2.Add(id);
            }
            if (ids2.Count > 0)
                return i => ids2.Contains(i.Category);

            // singular / plural spellings of the query ("cranberry" <-> "cranberries"); the first one is the query as typed
            List<string> variants = PluralVariants(normQuery);

            // P3: exact item name -> only that item (the typed spelling is tried first, then its singular / plural)
            foreach (string variant in variants)
            {
                string exact = variant;
                if (allItems.Any(i => NameEquals(i, exact)))
                    return i => NameEquals(i, exact);
            }

            // P4: contains (fallback); the singular / plural spellings only add matches, they never remove any
            return i => NameContains(i, query, normQuery)
                || variants.Skip(1).Any(v => NameContains(i, v, v));
        }

        /// <summary>
        /// True if the search treats this text as a category word (rules P1 and P2 of <see cref="BuildMatcher"/>, same tables).
        /// Used only to label the auto-complete suggestions; it does not take part in searching.
        /// </summary>
        internal static bool IsCategoryWord(string rawText)
        {
            string query = (rawText ?? "").Trim();
            if (query.Length == 0 || int.TryParse(query, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                return false;

            if (SingleWordCategories.ContainsKey(query.ToLowerInvariant()))
                return true;

            string normQuery = Norm(query);
            string[] tokens = query.ToLowerInvariant().Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var (_, words) in MultiWordCategories)
            {
                if ((tokens.Length > 0 && tokens.All(words.Contains)) || words.Contains(normQuery))
                    return true;
            }

            return false;
        }

        // Categories whose items can carry a quality star (silver / gold / iridium). Items outside these
        // categories (Wood, Stone, ...) have no star, so the star filter is ignored for them.
        private static readonly HashSet<int> QualityCategories = new()
        {
            -4,    // fish
            -5,    // eggs
            -6,    // milk
            -18,   // animal products (wool, ...)
            -26,   // artisan goods
            -27,   // syrup
            -75,   // vegetables
            -79,   // fruit
            -80,   // flowers
            -81    // forage / greens
        };

        private const int AnyQuality = -1;

        // Quality values in Stardew: 0 = normal, 1 = silver, 2 = gold, 4 = iridium.
        private static bool CanHaveQuality(Item item)
        {
            return item.Quality > 0 || QualityCategories.Contains(item.Category);
        }

        /// <param name="quality">Star filter: -1 = any (no filtering), 0 = no star, 1 = silver, 2 = gold, 4 = iridium.</param>
        private SearchOutcome ScanChests(string rawQuery, int quality = AnyQuality)
        {
            string itemName = rawQuery.Trim();
            if (itemName.Length == 0)
            {
                highlightedChests.Clear();
                placeItems.Clear();
                placeEntries = new List<PlaceEntry>();
                return SearchOutcome.Done;
            }

            var locations = GetAllLocations().ToList();

            // collect every chest once
            var allChests = new List<(GameLocation location, Vector2 position, Chest chest, string label)>();
            foreach (var (location, label) in locations)
            {
                foreach (var obj in location.objects.Pairs)
                {
                    if (obj.Value is Chest chest)
                        allChests.Add((location, obj.Key, chest, label));
                }
            }

            var allItems = allChests
                .SelectMany(c => c.chest.Items)
                .Where(i => i != null)
                .ToList();

            Func<Item, bool> matches = BuildMatcher(allItems, itemName);

            // star filter: items that can never have stars (Wood, Stone, cooked dishes, ...) ignore it, for every choice including "no star";
            // star-capable items must match the chosen quality exactly (0 = normal quality, i.e. no star)
            if (quality != AnyQuality)
            {
                Func<Item, bool> nameMatches = matches;
                Func<Item, bool> withQuality = i => nameMatches(i) && (!CanHaveQuality(i) || i.Quality == quality);

                // the item exists, but not in this quality: keep the previous result untouched and tell the menu to stay open
                if (allItems.Any(nameMatches) && !allItems.Any(withQuality))
                    return SearchOutcome.NoMatchingQuality;

                matches = withQuality;
            }

            highlightedChests.Clear();
            placeItems.Clear();
            placeEntries = new List<PlaceEntry>();

            var mainCounts = new Dictionary<string, int>();
            var islandCounts = new Dictionary<string, int>();
            int found = 0;

            foreach (var (location, position, chest, label) in allChests)
            {
                bool chestHasItem = chest.Items.Any(i => i != null && matches(i));
                if (!chestHasItem) continue;

                highlightedChests.Add((location, position, chest));
                string? islandArea = GetIslandArea(location.Name);
                AddMatchingItems(islandArea != null ? IslandKeyPrefix + islandArea : label, chest, matches);
                if (islandArea != null)
                    islandCounts[islandArea] = islandCounts.GetValueOrDefault(islandArea) + 1;
                else
                    mainCounts[label] = mainCounts.GetValueOrDefault(label) + 1;
                found++;
            }

            if (found == 0)
            {
                notifyTitle = "No item found in any chests.";
                notifyBody = "";
            }
            else
            {
                string chestWord = found == 1 ? "chest" : "chests";
                notifyTitle = $"Found {found} {chestWord} containing '{itemName}'";
                notifyBody = BuildPlaceSummary(mainCounts, islandCounts);
                placeEntries = ParsePlaceEntries(notifyBody);
            }

            notifyUntilMs = clock.ElapsedMilliseconds + 8000;
            highlightUntilMs = (found > 0 && Config.HighlightTimeoutSeconds > 0)
                ? clock.ElapsedMilliseconds + Config.HighlightTimeoutSeconds * 1000L
                : -1;
            Monitor.Log(notifyTitle + (notifyBody.Length > 0 ? " | " + notifyBody : ""), LogLevel.Info);
            return SearchOutcome.Done;
        }

        // Adds the matching items of one chest to the running totals of its place (same item name = one line, counts add up).
        private void AddMatchingItems(string placeKey, Chest chest, Func<Item, bool> matches)
        {
            if (!placeItems.TryGetValue(placeKey, out Dictionary<string, int>? totals))
                placeItems[placeKey] = totals = new Dictionary<string, int>();

            foreach (Item? item in chest.Items)
            {
                if (item == null || !matches(item)) continue;
                string name = item.DisplayName;
                totals[name] = totals.GetValueOrDefault(name) + item.Stack;
            }
        }

        // Item totals for a place key; the bare island prefix adds up every Ginger Island area.
        private Dictionary<string, int>? GetPlaceItems(string key)
        {
            if (key == IslandKeyPrefix)
            {
                var total = new Dictionary<string, int>();
                foreach (var pair in placeItems)
                {
                    if (!pair.Key.StartsWith(IslandKeyPrefix, StringComparison.Ordinal)) continue;
                    foreach (var item in pair.Value)
                        total[item.Key] = total.GetValueOrDefault(item.Key) + item.Value;
                }
                return total.Count > 0 ? total : null;
            }

            return placeItems.TryGetValue(key, out Dictionary<string, int>? items) && items.Count > 0 ? items : null;
        }

        // "Cindersap Forest x3" -> "Cindersap Forest"
        private static string StripCount(string part)
        {
            int idx = part.LastIndexOf(" x", StringComparison.Ordinal);
            if (idx > 0 && idx + 2 < part.Length && part.Substring(idx + 2).All(char.IsDigit))
                return part.Substring(0, idx);
            return part;
        }

        // Splits the summary text built by BuildPlaceSummary into hoverable place words, e.g.
        // "Farm, Cindersap Forest x3, Ginger Island (S, W x2)" -> Farm | Cindersap Forest x3 | Ginger Island | S | W x2
        private static List<PlaceEntry> ParsePlaceEntries(string body)
        {
            var entries = new List<PlaceEntry>();
            const string groupPrefix = "Ginger Island (";

            int i = 0;
            while (i < body.Length)
            {
                // find the end of this top-level part (commas inside parentheses do not split it)
                int depth = 0;
                int j = i;
                while (j < body.Length)
                {
                    char ch = body[j];
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    else if (ch == ',' && depth <= 0) break;
                    j++;
                }

                string part = body.Substring(i, j - i);
                if (part.StartsWith(groupPrefix, StringComparison.Ordinal) && part.EndsWith(")", StringComparison.Ordinal))
                {
                    // the words "Ginger Island" show all island areas together; each area letter shows just that area
                    entries.Add(new PlaceEntry(IslandKeyPrefix, "Ginger Island", i, "Ginger Island".Length));

                    int k = i + groupPrefix.Length;
                    int innerEnd = i + part.Length - 1;   // index of the closing ')'
                    while (k < innerEnd)
                    {
                        int comma = body.IndexOf(", ", k, innerEnd - k, StringComparison.Ordinal);
                        int end = comma < 0 ? innerEnd : comma;
                        string code = StripCount(body.Substring(k, end - k));
                        entries.Add(new PlaceEntry(IslandKeyPrefix + code, "Ginger Island (" + code + ")", k, end - k));
                        k = end + 2;
                    }
                }
                else if (part.Length > 0)
                {
                    string name = StripCount(part);
                    entries.Add(new PlaceEntry(name, name, i, part.Length));
                }

                i = j + 2;   // skip the ", " separator
            }

            return entries;
        }

        // Game1.parseText only inserts line breaks (and a trailing space), so every character of the original text can be
        // found again in the wrapped text. Returns, for each original character, its line and column in the wrapped text.
        private static bool TryMapChars(string original, string wrapped, out int[] lineOf, out int[] colOf)
        {
            lineOf = new int[original.Length];
            colOf = new int[original.Length];

            int o = 0, line = 0, col = 0;
            bool afterBreak = false;
            foreach (char ch in wrapped)
            {
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    line++;
                    col = 0;
                    afterBreak = true;
                    continue;
                }

                // some wrappers drop the space at a line break instead of keeping it
                while (afterBreak && o < original.Length && original[o] == ' ' && ch != ' ')
                    o++;
                afterBreak = false;

                if (o < original.Length && ch == original[o])
                {
                    lineOf[o] = line;
                    colOf[o] = col;
                    o++;
                }
                col++;
            }

            return o == original.Length;
        }

        // Finds the place word under the mouse. Also returns the on-screen rectangle of each line piece of that word
        // (a word like "Cindersap Forest" can be split over two lines by the wrapping).
        private bool TryGetHoveredPlace(string wrappedBody, Vector2 bodyPos, Point mouse, out PlaceEntry hovered, out List<Rectangle> rects)
        {
            hovered = default;
            rects = new List<Rectangle>();
            if (placeEntries.Count == 0 || !TryMapChars(notifyBody, wrappedBody, out int[] lineOf, out int[] colOf))
                return false;

            string[] lines = wrappedBody.Replace("\r", "").Split('\n');
            SpriteFont font = Game1.smallFont;
            int lineHeight = font.LineSpacing;

            foreach (PlaceEntry entry in placeEntries)
            {
                int first = entry.Start;
                int last = entry.Start + entry.Length - 1;
                if (entry.Length <= 0 || last >= notifyBody.Length) continue;

                var pieces = new List<Rectangle>();
                int idx = first;
                while (idx <= last)
                {
                    int line = lineOf[idx];
                    int pieceEnd = idx;
                    while (pieceEnd + 1 <= last && lineOf[pieceEnd + 1] == line) pieceEnd++;

                    if (line < lines.Length)
                    {
                        string text = lines[line];
                        int colStart = colOf[idx];
                        int colEnd = Math.Min(colOf[pieceEnd] + 1, text.Length);
                        if (colStart < colEnd)
                        {
                            float x = bodyPos.X + font.MeasureString(text.Substring(0, colStart)).X;
                            float w = font.MeasureString(text.Substring(colStart, colEnd - colStart)).X;
                            pieces.Add(new Rectangle((int)x, (int)bodyPos.Y + line * lineHeight, (int)Math.Ceiling(w), lineHeight));
                        }
                    }

                    idx = pieceEnd + 1;
                }

                foreach (Rectangle piece in pieces)
                {
                    Rectangle hit = piece;
                    hit.Inflate(3, 0);   // a little slack, so one-letter words like "S" are easy to hit
                    if (hit.Contains(mouse))
                    {
                        hovered = entry;
                        rects = pieces;
                        return true;
                    }
                }
            }

            return false;
        }

        // The item list that pops up under the result panel. Same parchment box, fonts, shadow and divider as the panel,
        // but display-only: nothing in it can be clicked or picked up.
        private void DrawPlaceTooltip(SpriteBatch b, string title, Dictionary<string, int> items, int anchorX /* left edge of the text */, int topY)
        {
            const float titleScale = 0.7f;
            const int padX = 28;
            const int padY = 22;
            const int gap = 8;

            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            int lineHeight = Game1.smallFont.LineSpacing;

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            int headerHeight = (int)titleSize.Y + gap * 2 + 2;

            // most-numerous items first; if the list is too tall for the screen the rest is summarised as "+N more"
            var ordered = items
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            int availableHeight = screenHeight - topY - 16 - padY * 2 - headerHeight;
            int maxLines = Math.Max(3, availableHeight / lineHeight);

            var lines = new List<string>();
            if (ordered.Count <= maxLines)
            {
                lines.AddRange(ordered.Select(p => $"{p.Key} x{p.Value}"));
            }
            else
            {
                lines.AddRange(ordered.Take(maxLines - 1).Select(p => $"{p.Key} x{p.Value}"));
                lines.Add($"+{ordered.Count - (maxLines - 1)} more");
            }

            int textWidth = (int)Math.Max(titleSize.X, lines.Max(l => Game1.smallFont.MeasureString(l).X));
            int boxWidth = textWidth + padX * 2;
            int boxHeight = padY * 2 + headerHeight + lines.Count * lineHeight;

            int boxX = Math.Clamp(anchorX - padX, 8, Math.Max(8, screenWidth - boxWidth - 8));
            int boxY = topY;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White);

            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, textPos, Game1.textColor, titleScale);

            int dividerY = (int)(textPos.Y + titleSize.Y) + gap;
            b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, 2), Game1.textColor * 0.35f);

            Vector2 linePos = new Vector2(textPos.X, dividerY + 2 + gap);
            foreach (string line in lines)
            {
                Utility.drawTextWithShadow(b, line, Game1.smallFont, linePos, Game1.textColor);
                linePos.Y += lineHeight;
            }
        }

        // Draws the result as ONE compact parchment panel (the same box the game's own menus use).
        // The box is sized exactly around the text: title, a thin divider, then the wrapped location list.
        // Hovering the panel freezes its countdown; hovering a place word also pops up that place's item list.
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            long frameDelta = lastHudFrameMs < 0 ? 0 : Math.Min(now - lastHudFrameMs, MaxFrameDeltaMs);
            lastHudFrameMs = now;

            float remaining = (notifyUntilMs - now) / 1000f;
            if (remaining <= 0f || string.IsNullOrEmpty(notifyTitle)) return;

            SpriteBatch b = e.SpriteBatch;
            float alpha = Math.Min(1f, remaining / 0.6f);   // fade out during the last 0.6 seconds

            const float titleScale = 0.8f;
            const int padX = 32;
            const int padY = 26;
            const int gap = 10;

            int screenWidth = Game1.uiViewport.Width;
            int maxTextWidth = Math.Min(760, screenWidth - 160) - padX * 2;

            string title = Game1.parseText(notifyTitle, Game1.dialogueFont, (int)(maxTextWidth / titleScale));
            string body = notifyBody.Length > 0 ? Game1.parseText(notifyBody, Game1.smallFont, maxTextWidth) : "";

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            Vector2 bodySize = body.Length > 0 ? Game1.smallFont.MeasureString(body) : Vector2.Zero;

            int textWidth = (int)Math.Max(titleSize.X, bodySize.X);
            int boxWidth = textWidth + padX * 2;
            int boxHeight = (int)titleSize.Y + padY * 2;
            if (body.Length > 0)
                boxHeight += gap * 2 + 2 + (int)bodySize.Y;

            int boxX = (screenWidth - boxWidth) / 2;
            int boxY = 20;

            // hover pause: while the mouse is over the panel the countdown does not run (the real clock keeps going,
            // so the end time is pushed back by the same amount) and the panel stays fully visible
            Point mouse = Game1.getMousePosition();
            bool overPanel = new Rectangle(boxX, boxY, boxWidth, boxHeight).Contains(mouse);
            if (overPanel)
            {
                notifyUntilMs = Math.Max(notifyUntilMs + frameDelta, now + HoverMinRemainingMs);
                alpha = 1f;
            }

            // the game's standard menu box
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White * alpha);

            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, textPos, Game1.textColor * alpha, titleScale);

            if (body.Length > 0)
            {
                int dividerY = (int)(textPos.Y + titleSize.Y) + gap;
                b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, 2), Game1.textColor * 0.35f * alpha);

                Vector2 bodyPos = new Vector2(textPos.X, dividerY + 2 + gap);
                Utility.drawTextWithShadow(b, body, Game1.smallFont, bodyPos, Game1.textColor * alpha);

                // hovering a place word: underline it and show what was found there
                if (overPanel && TryGetHoveredPlace(body, bodyPos, mouse, out PlaceEntry hovered, out List<Rectangle> pieces))
                {
                    Dictionary<string, int>? items = GetPlaceItems(hovered.Key);
                    if (items != null && pieces.Count > 0)
                    {
                        foreach (Rectangle piece in pieces)
                            b.Draw(Game1.staminaRect, new Rectangle(piece.X, piece.Bottom - 4, piece.Width, 2), Game1.textColor * 0.8f);

                        DrawPlaceTooltip(b, hovered.Title, items, pieces[0].X, boxY + boxHeight + 8);
                    }
                }
            }
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (highlightedChests.Count == 0) return;
            if (Config.HighlightOpacityPercent <= 0) return;   // 0% = do not draw the highlight at all

            // the flash pulses between 40% and 100% of the chosen opacity
            float peak = Config.HighlightOpacityPercent / 100f;
            float pulse = (float)(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4) + 1) / 2f;
            float alpha = peak * (0.4f + pulse * 0.6f);
            Color baseColor = HighlightColors.TryGetValue(Config.HighlightColor ?? "", out Color chosen) ? chosen : Color.Yellow;

            foreach (var (location, position, _) in highlightedChests)
            {
                if (location != Game1.currentLocation) continue;

                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, position * 64f);

                e.SpriteBatch.Draw(
                    Game1.fadeToBlackRect,
                    new Rectangle((int)screenPos.X - 4, (int)screenPos.Y - 4, 72, 72),
                    baseColor * alpha
                );
            }
        }
    }
}