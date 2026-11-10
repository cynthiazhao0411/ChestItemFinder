using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
        private List<(int Start, int Length)> notifyEmphasis = new();   // the parts of notifyTitle that are drawn bold (every searched word of the "No item" title and every item name of the "Found ..." title, each without its quotes), as character ranges; empty = nothing is bold
        private int notifyInvalidStart = -1;    // the "(Invalid: 'ere', 'df')" part of notifyTitle (from its opening bracket to its closing bracket), drawn in dark red; Length 0 = the title has no such part
        private int notifyInvalidLength = 0;
        private string notifyBody = "";
        // Real-world clock: not affected by in-game time (CJB Cheats Menu, TimeSpeed, Time Master,
        // Turbo, ...), so freezing, slowing or speeding up game time never changes these timers.
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long notifyUntilMs = 0;
        private long highlightUntilMs = -1;   // -1 = highlights never expire on their own (the countdown is frozen while the result panel is hovered, see OnRenderedHud)
        private Vector2 lastGrabTile = new Vector2(-1, -1);
        private int lastSearchKeyTick = -1;   // game tick in which the search key was last handled (open or close), see OnButtonPressed
        private ModConfig Config = new();

        // --- search history (saved per save file, newest first) ---
        private const string SearchHistorySaveKey = "search-history";
        private readonly List<SearchHistoryEntry> searchHistory = new();   // every word with the star filter it was searched with

        // --- search suggestions (auto-complete): the word list is built the first time the search box opens ---
        private SuggestionIndex? suggestionIndex;

        // --- hover interaction on the result panel ---
        // While the mouse is over the panel its countdown is frozen. After the mouse leaves, the countdown
        // resumes, but never from less than this, so a panel that was almost gone does not vanish instantly.
        private const long HoverMinRemainingMs = 2000;
        private const long MaxFrameDeltaMs = 250;
        private long lastHudFrameMs = -1;

        // Chest highlights follow the result panel while it is hovered: as long as the mouse is over the panel (or the "Keep highlights" box),
        // the highlight countdown is frozen too, exactly like the panel's own. This is the real-clock time of the last HUD frame in which that
        // was the case; OnUpdateTicked will not let the highlights expire while it is this recent (a frame that has not been drawn yet must
        // never be able to sneak an expiry in between two frozen frames).
        private long highlightHoverFrameMs = -1000000;
        private const long HighlightHoverFreshMs = 300;

        // --- feedback for a finished search (sound + sparkles), see PlaySearchFeedback ---
        private readonly List<(string Cue, long AtMs)> pendingSounds = new();   // the later sounds of a successful search, waiting for their moment
        private long sparkleStartMs = -1000000;               // real-clock time the sparkles started (long ago = none)
        private const long SearchSecondSoundDelayMs = 300;    // pause between the "coin" and the "pick-up" sound
        private const long SearchThirdSoundDelayMs = 650;     // after the "coin": when the long-ringing finishing chime ("reward") starts
        private const long SparkleHoldMs = 2600;              // the stars stay fully visible this long...
        private const long SparkleFadeMs = 900;               // ...then fade away smoothly over this long

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
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => { IsKeepHighlightsLocked = false; EndKeepFade(); ClearHighlights(); };
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += (_, _) => SaveSearchHistory();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => searchHistory.Clear();   // history belongs to a save file: do not leak it into the next one
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.Input.ButtonPressed += OnKeepBoxClicked;
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
                if (data == null)
                    return;

                // Entries = word + star filter. Save files from older versions only have the plain words (Items): those come back with "any quality".
                IEnumerable<SearchHistoryEntry> saved = data.Entries is { Count: > 0 }
                    ? data.Entries.Where(x => x != null)
                    : (data.Items ?? new List<string>()).Select(word => new SearchHistoryEntry { Word = word });

                foreach (SearchHistoryEntry entry in saved)
                {
                    string item = (entry.Word ?? "").Trim();
                    if (item.Length == 0 || searchHistory.Count >= SearchMenu.MaxHistory)
                        continue;
                    if (searchHistory.Any(h => string.Equals(h.Word, item, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    searchHistory.Add(new SearchHistoryEntry { Word = item, Quality = entry.Quality });
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
                Helper.Data.WriteSaveData(SearchHistorySaveKey, new SearchHistoryData
                {
                    Items = searchHistory.Select(h => h.Word).ToList(),   // words only: what older versions of the mod read
                    Entries = searchHistory.Select(h => new SearchHistoryEntry { Word = h.Word, Quality = h.Quality }).ToList()
                });
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

        // ===================== TOGGLE: WHICH SEARCH KEYS MAY CLOSE AN OPEN SEARCH BOX =====================
        // The search key closes the open box only if pressing it types nothing into the box:
        //   closes:       any combo with Shift / Ctrl / Alt (LeftShift + T, Ctrl + M, Alt + A, Shift + 3 ...), and keys that are not typing keys (F1-F12, Insert, mouse buttons ...)
        //   never closes: a key that is just a typing key when it is bound without a modifier (c, 3, #, space ...) or one that the box itself uses
        //                 (Enter, Esc, Tab, arrows, Backspace ...): the player is typing, and "catfish" must not close the box at the first "c".
        //                 Hidden exception: such a key pressed together with Shift / Ctrl / Alt closes the box too (LeftShift + T when the search key is just T).
        // Opening is not affected: when the box is closed, every configured search key opens it.

        private static readonly HashSet<SButton> ModifierButtons = new()
        {
            SButton.LeftShift, SButton.RightShift, SButton.LeftControl, SButton.RightControl, SButton.LeftAlt, SButton.RightAlt
        };

        // True for keys that type a character into the search box, or that the box (or its drop-down) uses itself.
        private static bool IsTextBoxKey(SButton button)
        {
            string name = button.ToString();

            if (name.Length == 1) return true;                                              // A-Z
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return true;   // 0-9 on the top row
            if (name.StartsWith("NumPad", StringComparison.Ordinal)) return true;           // NumPad0-9
            if (name.StartsWith("Oem", StringComparison.Ordinal)) return true;              // ; = , - . / ` [ \ ] ' and the like

            switch (name)
            {
                case "Space":
                case "Add":
                case "Subtract":
                case "Multiply":
                case "Divide":
                case "Decimal":
                case "Separator":   // number pad symbols
                case "Back":
                case "Delete":
                case "Enter":
                case "Escape":
                case "Tab":                           // keys the box / drop-down uses
                case "Up":
                case "Down":
                case "Left":
                case "Right":
                case "Home":
                case "End":
                    return true;
                default:
                    return false;
            }
        }

        // Looks at the keybind(s) of the search key that were just pressed: true if at least one of them is safe to use as "close".
        private bool SearchKeyMayCloseMenu()
        {
            foreach (var keybind in Config.SearchKey.Keybinds)
            {
                if (keybind.GetState() != SButtonState.Pressed)
                    continue;

                SButton[] buttons = keybind.Buttons;
                bool hasModifier = buttons.Any(button => ModifierButtons.Contains(button));
                bool hasOtherKey = buttons.Any(button => !ModifierButtons.Contains(button));

                if (!hasOtherKey)
                    continue;   // only modifiers (e.g. just LeftShift): they are held all the time while typing capitals, so never a close key
                if (hasModifier)
                    return true;   // Shift / Ctrl / Alt + something
                if (!buttons.Any(IsTextBoxKey))
                    return true;   // no typing key at all, e.g. F5

                // hidden shortcut: a search key that is a typing key (just "T") also closes the box when it is pressed while Shift / Ctrl / Alt
                // is held, so players used to LeftShift + T keep their muscle memory. Without a modifier the key simply types.
                if (IsAnyModifierDown())
                    return true;
            }

            return false;
        }

        // True while any Shift / Ctrl / Alt key is held down.
        private bool IsAnyModifierDown()
        {
            foreach (SButton modifier in ModifierButtons)
            {
                if (Helper.Input.IsDown(modifier))
                    return true;
            }

            return false;
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // remember which tile the player is interacting with (used to find the opened chest)
            if (Game1.activeClickableMenu == null)
                lastGrabTile = e.Cursor.GrabTile;

            if (!Config.SearchKey.JustPressed()) return;

            // A combo like LeftShift + T raises ButtonPressed once per key, and when both keys go down in the same tick, JustPressed is
            // true for each of those events. Without this check the second event would see the box the first one just opened and close it again.
            if (lastSearchKeyTick == Game1.ticks) return;

            // Toggle: the search key also closes the search box (like E closes the inventory). exitThisMenu is the same exit the Esc key
            // and the red X use: it plays the menu-close sound and lets the box release the keyboard.
            // The whole key combo is suppressed, so neither the game nor the text box reacts to this press any further.
            if (Game1.activeClickableMenu is SearchMenu openSearchMenu)
            {
                // a search key that is just a typing key (c, 3, #, ...) must reach the text box: no close, no suppress, the character is typed
                if (!SearchKeyMayCloseMenu())
                    return;

                lastSearchKeyTick = Game1.ticks;
                Helper.Input.SuppressActiveKeybinds(Config.SearchKey);
                openSearchMenu.exitThisMenu();
                return;
            }

            if (Game1.activeClickableMenu != null) return;

            // "Keep highlights" is locked: opening the search box releases the lock, removes the box and clears the kept highlights right away
            if (IsKeepHighlightsLocked)
                ReleaseKeepLock(fadeOut: false);
            else if (keepBoxDismissed)
                EndKeepFade();   // a box that is still fading out is gone now, and can no longer be revived

            lastSearchKeyTick = Game1.ticks;
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
                // "fruit (category)" (or the start of it) is completed like "fruit", so the drop-down does not go blank while the tip is typed
                return query => index.Search(StripCategorySuffixWhileTyping(query), SearchMenu.MaxSuggestions);
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
            if (IsKeepHighlightsLocked) return;   // locked ("Keep highlights" checked): opening chests never clears the highlights
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
            long nowMs = clock.ElapsedMilliseconds;

            // the second sound of a successful search (see PlaySearchFeedback), a moment after the first
            for (int i = pendingSounds.Count - 1; i >= 0; i--)
            {
                if (nowMs < pendingSounds[i].AtMs)
                    continue;
                string cue = pendingSounds[i].Cue;
                pendingSounds.RemoveAt(i);
                Game1.playSound(cue);
            }

            bool hoverFreezesHighlights = nowMs - highlightHoverFrameMs <= HighlightHoverFreshMs;   // the mouse is on the result panel right now
            if (!IsKeepHighlightsLocked && !hoverFreezesHighlights && highlightedChests.Count > 0 && highlightUntilMs >= 0 && nowMs >= highlightUntilMs)
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

        // The game's own category names as shown on item tooltips (Wiki, Modding:Items, column "English translation": Mineral, Fish, Animal Product,
        // Cooking, Crafting, Resource, Fertilizer, Trash, Bait, Fishing Tackle, Decor, Artisan Goods, Monster Loot, Seed, Vegetable, Fruit, Flower,
        // Forage) are all searchable below, next to the constant-style names.
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
            ["cooking"] = new[] { -7, -25 },         // Object.CookingCategory + ingredientsCategory (both shown as "Cooking")
            ["crafting"] = new[] { -8 },                                                // Object.CraftingCategory
            ["mineral"] = new[] { -2, -12 },
            ["minerals"] = new[] { -2, -12 },        // gems (-2) + minerals (-12), both shown as "Mineral"
            ["meat"] = new[] { -14 },                                                   // Object.meatCategory
            ["fertilizer"] = new[] { -19 },
            ["fertilizers"] = new[] { -19 },         // Object.fertilizerCategory
            ["junk"] = new[] { -20 },                                                   // Object.junkCategory
            ["trash"] = new[] { -20 },               // the game shows Object.junkCategory as "Trash"
            ["bait"] = new[] { -21 },                                                   // Object.baitCategory
            ["tackle"] = new[] { -22 },                                                 // Object.tackleCategory
            ["furniture"] = new[] { -24 },                                              // Object.furnitureCategory
            ["decor"] = new[] { -24 },               // the game shows Object.furnitureCategory as "Decor"
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
            (new[] { -5, -6, -14, -18 }, new HashSet<string> { "animalproduct", "animalproducts" }),                       // shown as "Animal Product": eggs, milk, meat, wool & co. (-5, -6, -14, -18)
            (new[] { -9 },        new HashSet<string> { "big", "craftable", "craftables", "bigcraftable", "bigcraftables" }),   // Object.BigCraftableCategory
            (new[] { -15 },       new HashSet<string> { "metal", "resource", "resources", "metalresources" }),                  // Object.metalResources
            (new[] { -16 },       new HashSet<string> { "building", "resource", "resources", "buildingresources" }),            // Object.buildingResources
            (new[] { -17 },       new HashSet<string> { "sellatpierres" }),                                                     // Object.sellAtPierres
            (new[] { -18 },       new HashSet<string> { "sellatpierresandmarnies" }),                                           // Object.sellAtPierresAndMarnies
            (new[] { -23 },       new HashSet<string> { "sellatfishshop" }),                                                    // Object.sellAtFishShopCategory
            (new[] { -26, -27 },  new HashSet<string> { "artisan", "goods", "artisangoods" }),                                  // artisanGoodsCategory + syrupCategory (both shown as "Artisan Goods")
            (new[] { -28 },       new HashSet<string> { "monster", "loot", "monsterloot" }),                                    // Object.monsterLootCategory
            (new[] { -22 },       new HashSet<string> { "fishingtackle" }),                                                     // Object.tackleCategory, shown as "Fishing Tackle"
            (new[] { -103 },      new HashSet<string> { "skill", "book", "books", "skillbook", "skillbooks" })                  // Object.skillBooksCategory
        };

        private static readonly char[] QuerySeparators = { ' ', '\t', '_', '-' };

        // lower-case and drop spaces, underscores and hyphens: "Star Fruit" -> "starfruit"
        private static string Norm(string s)
        {
            return new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-').ToArray()).ToLowerInvariant();
        }

        // The search box tip (and the drop-down) show category words as "fruit (category)". A player who types exactly that must find the
        // same thing as "fruit", so the label is cut off before searching. Case is ignored, spaces around the brackets are optional, and
        // half-width ( ) as well as full-width brackets are accepted.
        private static readonly Regex CategorySuffixPattern = new(
            @"\s*[(\uFF08]\s*category\s*[)\uFF09]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The same, but also for a label that is still being typed: "fruit (", "fruit (cat", "fruit (category" (used by the auto-complete only).
        private static readonly Regex PartialCategorySuffixPattern = new(
            @"\s*[(\uFF08]\s*(?:category|categor|catego|cate|cat|ca|c)?\s*[)\uFF09]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Removes a trailing " (category)" label: "fruit (category)" -> "fruit", "Artisan Goods (Category)" -> "Artisan Goods".
        /// Text without the label comes back trimmed but otherwise unchanged. If nothing would be left (the text is only "(category)"),
        /// the text is returned as it is, so an empty query can never turn into "match everything".
        /// </summary>
        internal static string StripCategorySuffix(string? rawText)
        {
            string text = StripWith(CategorySuffixPattern, rawText);

            // an id with the name label the title and the history show: "268 (Starfruit)" / "-79 (fruit)" is searched as "268" / "-79"
            Match idLabel = IdLabelPattern.Match(text);
            return idLabel.Success ? idLabel.Groups[1].Value : text;
        }

        // a number followed by a "(name)" label: the way the history and the result title write an id search word
        private static readonly Regex IdLabelPattern = new(
            @"^\s*(-?\d+)\s*[(\uFF08][^()\uFF08\uFF09]*[)\uFF09]\s*$",
            RegexOptions.CultureInvariant);

        /// <summary>Like <see cref="StripCategorySuffix"/>, but also accepts a label that is only partly typed ("fruit (cat"). For the auto-complete.</summary>
        internal static string StripCategorySuffixWhileTyping(string? rawText)
        {
            return StripWith(PartialCategorySuffixPattern, rawText);
        }

        private static string StripWith(Regex pattern, string? rawText)
        {
            string text = (rawText ?? "").Trim();
            string current = text;

            // a few rounds, so even "fruit (category) (category)" ends up as "fruit"
            for (int round = 0; round < 3; round++)
            {
                string next = pattern.Replace(current, "").Trim();
                if (next.Length == 0 || next == current)
                    break;
                current = next;
            }

            return current;
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

        // ===================== MULTI-TERM SEARCH =====================
        // The search box accepts several words at once. An item is highlighted when it matches ANY of them (OR):
        //   "fruit, fish"   a comma is an explicit separator. Each part is ONE term and may contain spaces: "ancient fruit, fish".
        //   "fruit fish"    without a comma, spaces separate the words, but only where the text is not already ONE thing the search knows
        //                   (see SplitBySpaces): "artisan goods", "star fruit" and "sea urch" keep working exactly as before.
        // Every term then goes through BuildSingleMatcher, i.e. the unchanged N / P1 / P2 / P3 / P4 rules, one term at a time.
        //
        // Separators: the explicit ones (QueryCommas: , ， ; ； 、 / \ & +) always cut. A hyphen or underscore between two letters cuts too ("fruit-fish"),
        // unless the text is one known name ("star-fruit", "Auto-Grabber"); a leading minus ("-26") never cuts (see SplitSoft).
        // History: only the terms that found something are remembered (see SearchReport / SearchMenu.RecordHistory).
        //
        // A term that cannot be used never stops the search, it is only reported: if at least one term finds something, the search is a success
        // (sounds, stars, panel) and the title lists the useless terms in dark red at its end: Found 15 chests containing 'fruit' (Invalid: 'ere', 'df').
        // A term is "invalid" when it cannot be searched at all (too short, only symbols, a character that cannot belong to an item name:
        // SearchMenu.IsTermSearchable) or when no item in any chest matches it. If NO term finds anything, the title is the usual
        // "No item 'a', 'b' found in any chest." (see ScanChests).

        private static readonly char[] SpaceSeparators = { ' ', '\t' };

        /// <summary>
        /// The explicit separators between search words (they always separate, like the comma always did): the half-width and full-width comma,
        /// semicolon (half / full width), the Chinese enumeration comma, slash, backslash, ampersand and plus.
        /// (The name is kept from the time when only the two commas were separators.)
        /// The hyphen and the underscore are NOT in this list: they only separate two words when they sit between two letters and the text is not
        /// already one name the game knows ("star-fruit", "Auto-Grabber" stay whole), see <see cref="SplitSoft"/>.
        /// </summary>
        internal static readonly char[] QueryCommas = { ',', '\uFF0C', ';', '\uFF1B', '\u3001', '/', '\\', '&', '+' };

        /// <summary>True if the text contains an explicit separator of any kind (see <see cref="QueryCommas"/>).</summary>
        internal static bool HasCommas(string? text)
        {
            return (text ?? "").IndexOfAny(QueryCommas) >= 0;
        }

        private static readonly char[] SoftSeparators = { '-', '_' };

        /// <summary>True for a token that is nothing but hyphens / underscores ("-", "_"): a lone dash between two words is a separator, not a word.</summary>
        private static bool IsOnlySoftSeparators(string token)
        {
            return token.Length > 0 && token.All(ch => ch == '-' || ch == '_');
        }

        /// <summary>
        /// Cuts a text at every hyphen / underscore that has a letter on both sides ("fruit-fish" -> fruit | fish). A leading minus ("-26"),
        /// a minus before a digit and a dash at the end of the text never cut. One piece = no cut was possible.
        /// </summary>
        private static List<string> SplitAtSoftSeparators(string text)
        {
            var pieces = new List<string>();
            int start = 0;

            for (int i = 1; i < text.Length - 1; i++)
            {
                if (Array.IndexOf(SoftSeparators, text[i]) < 0)
                    continue;
                if (!char.IsLetter(text[i - 1]) || !char.IsLetter(text[i + 1]))
                    continue;

                string piece = text.Substring(start, i - start).Trim();
                if (piece.Length > 0)
                    pieces.Add(piece);
                start = i + 1;
            }

            string last = text.Substring(start).Trim();
            if (last.Length > 0)
                pieces.Add(last);

            return pieces.Count > 0 ? pieces : new List<string> { text };
        }

        /// <summary>
        /// Splits a query at its commas (half-width or full-width). Every part loses its surrounding spaces and a trailing " (category)" label; empty parts and
        /// repeats (case, spaces and hyphens ignored) are dropped. "fruit, , Fish (category)" -> fruit | Fish.
        /// </summary>
        internal static List<string> GetCommaTerms(string? rawText)
        {
            var terms = new List<string>();
            var seen = new HashSet<string>();

            foreach (string part in (rawText ?? "").Split(QueryCommas))
            {
                string term = StripCategorySuffix(part);
                if (term.Length == 0 || !seen.Add(Norm(term)))
                    continue;
                terms.Add(term);
            }

            return terms;
        }

        /// <summary>
        /// The query as it is searched and remembered in the history. Without a comma: StripCategorySuffix, exactly as before.
        /// With commas: the terms of <see cref="GetCommaTerms"/> joined by ", " ("fruit,fish" -> "fruit, fish", "fruit," -> "fruit").
        /// </summary>
        internal static string NormalizeQueryText(string? rawText)
        {
            string text = (rawText ?? "").Trim();
            return HasCommas(text) ? string.Join(", ", GetCommaTerms(text)) : StripCategorySuffix(text);
        }

        /// <summary>The separate words of a normalized query: split at the commas if there are any, otherwise at the spaces (see SplitBySpaces).</summary>
        private List<string> ResolveTerms(List<Item> allItems, string queryText)
        {
            List<string> coarse = HasCommas(queryText)
                ? GetCommaTerms(queryText)
                : SplitBySpaces(allItems, queryText);

            // then every term may still be cut at a hyphen / underscore between two letters ("fruit-fish"), unless it is one known name ("star-fruit")
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (string term in coarse)
            {
                foreach (string piece in SplitSoft(allItems, term))
                {
                    if (seen.Add(Norm(piece)))
                        result.Add(piece);
                }
            }

            return result.Count > 0 ? result : coarse;
        }

        /// <summary>
        /// Splits one term at the hyphens / underscores between two letters, but never through something the search already understands as ONE
        /// thing: a category word, the name of an item the game has ("star-fruit", "Auto-Grabber", "Mini-Fridge") or the start of a name some chest
        /// item has ("auto-gra"). Otherwise "fruit-fish" -> fruit | fish.
        /// </summary>
        private List<string> SplitSoft(List<Item> allItems, string term)
        {
            List<string> pieces = SplitAtSoftSeparators(term);
            if (pieces.Count < 2)
                return new List<string> { term };

            if (IsKnownThing(allItems, GetKnownNameCheck(), term))
                return new List<string> { term };

            return pieces;
        }

        /// <summary>True if the text is ONE thing the search knows: a category, an item name the game has, or the start of a name that a chest item has.</summary>
        private static bool IsKnownThing(List<Item> allItems, Func<string, bool> isKnownName, string phrase)
        {
            return IsCategoryWord(phrase)
                || isKnownName(phrase)
                || allItems.Any(i => NameContains(i, phrase, Norm(phrase)));
        }

        /// <summary>
        /// Splits a comma-free query at its spaces, but never through something the search already understands as one thing. A text stays
        /// ONE term (so the old behavior is untouched) when it is a category ("artisan goods"), the name of an item ("star fruit", whether it is in
        /// a chest or not) or the start of a name that some chest item has ("sea urch"). Otherwise it is cut into words ("fruit fish" -> fruit | fish),
        /// where a run of words that forms such a known phrase is kept together ("fruit artisan goods" -> fruit | artisan goods).
        /// </summary>
        private List<string> SplitBySpaces(List<Item> allItems, string text)
        {
            // a lone "-" or "_" between two words ("fruit - fish") is a separator, not a word
            string[] words = text.Split(SpaceSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !IsOnlySoftSeparators(w))
                .ToArray();
            if (words.Length < 2)
                return new List<string> { words.Length == 1 ? words[0] : text };

            Func<string, bool> isKnownName = GetKnownNameCheck();
            bool IsOneThing(string phrase) => IsKnownThing(allItems, isKnownName, phrase);

            if (IsOneThing(text))
                return new List<string> { text };

            var terms = new List<string>();
            var seen = new HashSet<string>();
            int index = 0;

            while (index < words.Length)
            {
                // the longest run of words from here on that is one known thing, or just this one word
                int take = 1;
                for (int length = words.Length - index; length >= 2; length--)
                {
                    if (IsOneThing(string.Join(" ", words, index, length)))
                    {
                        take = length;
                        break;
                    }
                }

                string term = string.Join(" ", words, index, take);
                index += take;

                // a lone short word ("of", "a") is NOT dropped any more: it stays a term and is reported as invalid by ScanChests
                // (a number is an item / category id and is a valid term)

                if (seen.Add(Norm(term)))
                    terms.Add(term);
            }

            return terms.Count > 0 ? terms : new List<string> { text };
        }

        // Tells whether a text is exactly the name of an item the game has (the auto-complete's word list, so items that are not in any chest
        // count too). If the word list cannot be built, nothing is "known" and only the other checks of SplitBySpaces apply.
        private Func<string, bool> GetKnownNameCheck()
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();

                SuggestionIndex index = suggestionIndex;
                return phrase =>
                {
                    IReadOnlyList<Suggestion> best = index.Search(phrase, 1);   // the name that equals the text always comes first
                    return best.Count > 0 && SuggestionIndex.MakeKey(best[0].Text) == SuggestionIndex.MakeKey(phrase);
                };
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check item names for the multi-word search: {ex.Message}", LogLevel.Trace);
                return _ => false;
            }
        }

        /// <summary>One matcher for all terms: an item matches when it matches at least one of them. One term = exactly the old single search.</summary>
        private static Func<Item, bool> CombineMatchers(Func<Item, bool>[] termMatchers)
        {
            if (termMatchers.Length == 1)
                return termMatchers[0];

            Func<Item, bool>[] matchers = (Func<Item, bool>[])termMatchers.Clone();   // a copy, so later changes to the array can never change this matcher
            return item => matchers.Any(matcher => matcher(item));
        }

        // The title text with every name in single quotes, separated by ", ": prefix 'a', 'b' suffix. Also fills notifyEmphasis with the
        // character range of each name (the quotes are not part of it), so every name is drawn bold on its own.
        // shorten: cut a very long name short ("...") so the title stays readable.
        // invalidNames: the words of the search that found nothing (or could not be searched). They are added after the suffix as
        // " (Invalid: 'a', 'b')" (always shortened, they are whatever the player typed). That whole part, brackets included, is remembered in
        // notifyInvalidStart / notifyInvalidLength so it can be drawn in dark red, and the invalid names are bold like every other name.
        private string BuildQuotedTitle(string prefix, IReadOnlyList<string> names, bool shorten, string suffix, IReadOnlyList<string>? invalidNames = null)
        {
            var title = new System.Text.StringBuilder(prefix);
            var ranges = new List<(int Start, int Length)>();

            void AppendNames(IReadOnlyList<string> list, bool cutLongNames)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string shown = list[i];
                    if (cutLongNames && shown.Length > MaxEchoedQueryLength)
                        shown = shown.Substring(0, MaxEchoedQueryLength).TrimEnd() + "...";

                    if (i > 0)
                        title.Append(", ");
                    title.Append('\'');
                    ranges.Add((title.Length, shown.Length));   // the name itself: after the opening quote, before the closing one
                    title.Append(shown).Append('\'');

                    // an id ("-79", "268") gets its real name after it, in brackets and in normal weight: '-79' (fruit, category), '268' (Starfruit)
                    string? idName = SearchMenu.TryGetIdName(list[i]);
                    if (idName != null)
                    {
                        // everything in the brackets is plain text (only the quoted word is bold): '-79' (fruit, category)
                        title.Append(" (").Append(idName);

                        // a negative id is a category: the title says so
                        if (list[i].TrimStart().StartsWith('-'))
                            title.Append(", category");

                        title.Append(')');
                    }
                }
            }

            AppendNames(names, shorten);
            title.Append(suffix);

            notifyInvalidStart = -1;
            notifyInvalidLength = 0;
            if (invalidNames != null && invalidNames.Count > 0)
            {
                title.Append(' ');
                int invalidStart = title.Length;
                title.Append("(Invalid: ");
                AppendNames(invalidNames, true);
                title.Append(')');
                notifyInvalidStart = invalidStart;
                notifyInvalidLength = title.Length - invalidStart;
            }

            notifyEmphasis = ranges;
            return title.ToString();
        }

        // One search term: the N / P1 / P2 / P3 / P4 rules above (this is the whole search of the versions before multi-term search).
        private static Func<Item, bool> BuildSingleMatcher(List<Item> allItems, string rawQuery)
        {
            // "fruit (category)" (as shown by the tip and the drop-down) is searched exactly like "fruit"
            string query = StripCategorySuffix(rawQuery);
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
        /// True if the search treats this text as a category word (rules P1 and P2 of <see cref="BuildSingleMatcher"/>, same tables).
        /// Used only to label the auto-complete suggestions and the title of the result panel; it does not take part in searching.
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

        // the "No item '...' found" title repeats what the player typed, up to this many characters
        private const int MaxEchoedQueryLength = 40;

        // ===================== QUALITY ICON IN THE RESULT PANEL =====================
        // The result panel shows the star filter of the search it belongs to, as a framed icon at the right end of its title row,
        // looking exactly like the star button of the search box (SearchMenu.DrawQualityButton draws both):
        //   outline star = "Any quality", silver / gold / iridium star = only that quality, an empty frame = "No quality" (only items without a star).
        // It is always shown (for category searches too, because the star filter applies to them as well).
        // Hovering it shows the game's own hover tip with the name of the filter ("Silver Star" ...). Display only: nothing happens when it is clicked
        // (the only click handling on the panel is the "Keep highlights" checkbox).

        /// <summary>The star filter of the result that is on the panel (AnyQuality = the outline star). Set only when a search is accepted, so a refused search leaves it alone.</summary>
        private int resultQuality = AnyQuality;

        /// <summary>
        /// True when the search only refers to items that can never carry a star (Wood, Stone, cooked dishes, ...), whatever star filter is selected
        /// and whether or not anything was found. Such items are never blocked by the filter (see qualityOk in ScanChests), so the filter does not apply:
        /// the panel then shows the outline "Any Quality" star and the hover tip "This item has no quality." instead of the selected star.
        /// Found: decided by the matched chest items. Not found: decided by the game's own item list (see SearchRefersOnlyToNoQualityItems).
        /// A search that also refers to a star-capable item (Corn, ...) is not affected: it keeps its normal icon and tip.
        /// </summary>
        private bool resultHasNoQualityItemsOnly = false;
        private const string NoQualityItemTip = "This item has no quality.";

        // The warning icon of a failed search (a warning, not a close button): a red "no entry" sign (a ring with a slash), drawn pixel by pixel in the
        // game's pixel-art style (red with a dark red outline, no box, no bubble), at the LEFT end of the title row, in front of the title text.
        // Display only: NO click, NO hover.
        private bool resultFailed;
        private const int WarningBadgeSize = 44;        // the room reserved for the icon (the title text starts after it)
        private const int WarningBadgeLeftInset = 26;   // panel edge -> icon slot (the frame art itself is about 20px thick)
        private const int WarningBadgeGap = 14;         // icon slot -> title text
        private const int WarningSignCells = 11;        // the sign is 11 x 11 game pixels...
        private const int WarningSignPixel = 3;         // ...of 3 screen pixels each = 33 px, centered in the 44 px slot (a smaller sign keeps its round edge clear of the slot's edge)
        private static readonly Color WarningSignRed = new(215, 35, 30);
        private static readonly Color WarningSignOutline = new(105, 15, 12);
        private static byte[,]? warningSignMask;   // 0 = empty, 1 = red, 2 = outline

        private static byte[,] GetWarningSignMask()
        {
            if (warningSignMask != null)
                return warningSignMask;

            const int n = WarningSignCells;
            const int c = n / 2;
            var mask = new byte[n, n];

            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < n; col++)
                {
                    int dx = col - c;
                    int dy = row - c;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    bool ring = dist >= 4.2 && dist <= 5.5;
                    bool slash = dist <= 5.5 && Math.Abs(dx - dy) <= 1;   // from the upper left to the lower right
                    if (ring || slash)
                        mask[row, col] = 1;
                }
            }

            // a dark outline around everything red (the neighbors above, below, left and right)
            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < n; col++)
                {
                    if (mask[row, col] != 0)
                        continue;
                    bool touchesRed = (row > 0 && mask[row - 1, col] == 1) || (row < n - 1 && mask[row + 1, col] == 1)
                        || (col > 0 && mask[row, col - 1] == 1) || (col < n - 1 && mask[row, col + 1] == 1);
                    if (touchesRed)
                        mask[row, col] = 2;
                }
            }

            warningSignMask = mask;
            return mask;
        }

        private static void DrawWarningBadge(SpriteBatch b, int x, int y, float alpha)
        {
            byte[,] mask = GetWarningSignMask();
            int inset = (WarningBadgeSize - WarningSignCells * WarningSignPixel) / 2;   // centers the sign in its slot, both ways
            x += inset;
            y += inset;
            for (int row = 0; row < WarningSignCells; row++)
            {
                for (int col = 0; col < WarningSignCells; col++)
                {
                    if (mask[row, col] == 0)
                        continue;
                    Color color = mask[row, col] == 1 ? WarningSignRed : WarningSignOutline;
                    b.Draw(Game1.staminaRect, new Rectangle(x + col * WarningSignPixel, y + row * WarningSignPixel, WarningSignPixel, WarningSignPixel), color * alpha);
                }
            }
        }

        private static readonly Rectangle QualityGoldStarSource = new(346, 400, 8, 8);   // also used by the gold stars of a successful search

        private const int QualityBoxSize = 44;                            // the framed icon, a little smaller than the 48px star button so it fits the title row
        private const int QualityIconRightInset = 24;                     // panel edge -> icon (the frame art itself is about 20px thick)
        private const int QualityIconGap = 16;                            // minimum space between the title text and the icon

        /// <summary>The hover tip of the quality icon.</summary>
        private static string GetQualityTip(int quality)
        {
            return quality switch
            {
                1 => "Silver Star",
                2 => "Gold Star",
                4 => "Iridium Star",
                0 => "No Quality",
                _ => "Any Quality"
            };
        }

        // Quality values in Stardew: 0 = normal, 1 = silver, 2 = gold, 4 = iridium.
        private static bool CanHaveQuality(Item item)
        {
            return item.Quality > 0 || QualityCategories.Contains(item.Category);
        }

        /// <param name="quality">Star filter: -1 = any (no filtering), 0 = no star, 1 = silver, 2 = gold, 4 = iridium.</param>
        /// <returns>
        /// The outcome, plus the search words that really found something in a chest (with the chosen star). The search menu remembers exactly
        /// these words in the history: a word that found nothing (or could not be searched) is never remembered.
        /// </returns>
        private SearchReport ScanChests(string rawQuery, int quality = AnyQuality)
        {
            string itemName = NormalizeQueryText(rawQuery);   // "fruit (category)" -> "fruit"; "fruit,fish" -> "fruit, fish" (the separate terms are worked out below)
            if (itemName.Length == 0)
            {
                IsKeepHighlightsLocked = false;
                highlightedChests.Clear();
                placeItems.Clear();
                placeEntries = new List<PlaceEntry>();
                return new SearchReport(SearchOutcome.Done);
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

            // one term = the search as it always was; several terms (commas or spaces) = an item counts if it matches ANY of them
            List<string> terms = ResolveTerms(allItems, itemName);

            // every term has its own matcher. With several terms, a term that cannot be searched at all (too short, only symbols, a character that
            // cannot belong to an item name) gets a matcher that matches nothing: it never stops the search, it is reported as invalid below.
            // (A single term was already checked by the search menu, so it is searched exactly as it always was.)
            var termMatchers = new Func<Item, bool>[terms.Count];
            for (int t = 0; t < terms.Count; t++)
            {
                bool searchable = terms.Count == 1 || SearchMenu.IsTermSearchable(terms[t]);
                termMatchers[t] = searchable ? BuildSingleMatcher(allItems, terms[t]) : (_ => false);
            }
            Func<Item, bool> matches = CombineMatchers(termMatchers);

            // star filter: items that can never have stars (Wood, Stone, cooked dishes, ...) ignore it, for every choice including "no star";
            // star-capable items must match the chosen quality exactly (0 = normal quality, i.e. no star)
            Func<Item, bool> qualityOk = i => quality == AnyQuality || !CanHaveQuality(i) || i.Quality == quality;
            if (quality != AnyQuality)
            {
                Func<Item, bool> nameMatches = matches;
                Func<Item, bool> withQuality = i => nameMatches(i) && qualityOk(i);

                // the item exists, but not in this quality: keep the previous result untouched and tell the menu to stay open
                if (allItems.Any(nameMatches) && !allItems.Any(withQuality))
                    return new SearchReport(SearchOutcome.NoMatchingQuality);

                matches = withQuality;
            }

            // sort the terms: "invalid" = no item in any chest matches it (or it could not be searched at all), "valid" = it finds something with the
            // chosen star. A term that matches items by name, but none of them with the chosen star, is neither: it is not invalid (the word is fine),
            // and the title does not claim a chest contains it; the star icon of the panel shows which filter was used.
            var validTerms = new List<string>();
            var invalidTerms = new List<string>();
            for (int t = 0; t < terms.Count; t++)
            {
                Func<Item, bool> byName = termMatchers[t];
                if (!allItems.Any(byName))
                    invalidTerms.Add(terms[t]);
                else if (allItems.Any(i => byName(i) && qualityOk(i)))
                    validTerms.Add(terms[t]);
            }

            IsKeepHighlightsLocked = false;   // a new result: its "Keep highlights" box starts unchecked (and is shown again)
            keepBoxDismissed = false;
            EndKeepFade();                    // nothing of the old box (fade, saved chests) is left
            resultQuality = quality;   // the panel's quality icon (every search shows one, "any quality" included)
            resultFailed = false;
            resultHasNoQualityItemsOnly = false;
            highlightedChests.Clear();
            placeItems.Clear();
            placeEntries = new List<PlaceEntry>();

            bool anyQualityCapableMatch = false;   // at least one matched item can carry a star (Corn, Fish, ...)
            var mainCounts = new Dictionary<string, int>();
            var islandCounts = new Dictionary<string, int>();
            int found = 0;

            foreach (var (location, position, chest, label) in allChests)
            {
                bool chestHasItem = chest.Items.Any(i => i != null && matches(i));
                if (!chestHasItem) continue;

                highlightedChests.Add((location, position, chest));
                if (!anyQualityCapableMatch && chest.Items.Any(i => i != null && matches(i) && CanHaveQuality(i)))
                    anyQualityCapableMatch = true;
                string? islandArea = GetIslandArea(location.Name);
                AddMatchingItems(islandArea != null ? IslandKeyPrefix + islandArea : label, chest, matches);
                if (islandArea != null)
                    islandCounts[islandArea] = islandCounts.GetValueOrDefault(islandArea) + 1;
                else
                    mainCounts[label] = mainCounts.GetValueOrDefault(label) + 1;
                found++;
            }

            // the search only refers to items without quality (found: every matched chest item has none; not found: every game item the words
            // match has none): the star filter does not apply, and the panel says so (see resultHasNoQualityItemsOnly)
            resultHasNoQualityItemsOnly = found > 0
                ? !anyQualityCapableMatch
                : SearchRefersOnlyToNoQualityItems(terms);

            if (found == 0)
            {
                // say what was searched for, so a typo ("satrfruit") is easy to spot; a very long word is cut short so the title stays readable.
                // Every word is quoted and drawn bold on its own: No item 'fruit', 'fish' found in any chest.
                // (Nothing was found, so every word is listed here, and there is no separate "(Invalid: ...)" part.)
                notifyTitle = BuildQuotedTitle("No item ", terms, shorten: true, " found in any chest.");
                notifyBody = "";
                resultFailed = true;         // the warning icon at the left end of the title row
                PlaySearchFeedback(false);   // nothing found: one short "dead end" sound
            }
            else
            {
                // Something was found, so the search is a success, however many of the words found nothing. The title lists the words that found
                // something, each in its own quotes and bold; the words that found nothing (or could not be searched) follow in dark red:
                // Found 15 chests containing 'fruit' (Invalid: 'ere', 'df')
                string chestWord = found == 1 ? "chest" : "chests";
                string foundPrefix = $"Found {found} {chestWord} containing ";
                // a single category search ("fish", "fruit", "artisan goods" ...) gets the same " (category)" label as the auto-complete, after the
                // closing quote: 'fish' (category); a real item name gets none. With several words the title lists them, each in its own quotes and
                // bold, and if at least one of them is a category the title gets ONE " (categories)" (a label after every word would make
                // the title too long): Found 15 chests containing 'fruit', 'fish' (categories)
                string categoryLabel = validTerms.Count == 1
                    ? (IsCategoryWord(validTerms[0]) ? " (category)" : "")
                    : (validTerms.Any(IsCategoryWord) ? " (categories)" : "");
                notifyTitle = BuildQuotedTitle(foundPrefix, validTerms, shorten: false, categoryLabel, invalidTerms);
                notifyBody = BuildPlaceSummary(mainCounts, islandCounts);
                placeEntries = ParsePlaceEntries(notifyBody);
                PlaySearchFeedback(true);    // found: coin + pick-up sounds and a few gold stars beside the panel
            }

            notifyUntilMs = clock.ElapsedMilliseconds + 8000;
            highlightUntilMs = (found > 0 && Config.HighlightTimeoutSeconds > 0)
                ? clock.ElapsedMilliseconds + Config.HighlightTimeoutSeconds * 1000L
                : -1;
            Monitor.Log(notifyTitle + (notifyBody.Length > 0 ? " | " + notifyBody : ""), LogLevel.Info);

            // history: only the words that found something (validTerms); nothing found = nothing to remember
            return new SearchReport(SearchOutcome.Done, found > 0 ? validTerms : new List<string>());
        }

        // ===================== GAME ITEM CATALOG (only used to tell whether a search is about items without quality) =====================
        // One sample item of everything the game has that can lie in a chest, built the first time a search finds nothing (rebuilt if the language
        // or the item data changes). Lets "No item 'Wood' found" still know that Wood has no quality, although no chest holds any.
        private List<Item>? gameItemCatalog;
        private object? catalogBuiltFromObjectData;
        private LocalizedContentManager.LanguageCode catalogBuiltLanguage;

        private static readonly HashSet<string> CatalogItemTypes = new() { "(O)", "(BC)", "(F)", "(H)", "(W)", "(T)", "(B)", "(TR)" };

        private List<Item> GetGameItemCatalog()
        {
            if (gameItemCatalog != null
                && ReferenceEquals(catalogBuiltFromObjectData, Game1.objectData)
                && catalogBuiltLanguage == LocalizedContentManager.CurrentLanguageCode)
                return gameItemCatalog;

            var catalog = new List<Item>();
            foreach (var type in ItemRegistry.ItemTypes)
            {
                if (!CatalogItemTypes.Contains(type.Identifier))
                    continue;

                foreach (string id in type.GetAllIds())
                {
                    try
                    {
                        Item? item = ItemRegistry.Create(type.Identifier + id, 1, 0, true);
                        if (item != null)
                            catalog.Add(item);
                    }
                    catch (Exception)
                    {
                        // an item that cannot be created is simply left out
                    }
                }
            }

            gameItemCatalog = catalog;
            catalogBuiltFromObjectData = Game1.objectData;
            catalogBuiltLanguage = LocalizedContentManager.CurrentLanguageCode;
            return catalog;
        }

        /// <summary>
        /// True if the search words match at least one item the game has, and NONE of the matched items can carry a star (same matching rules as the
        /// real search, run over the game's item list instead of the chests). Any problem or no match at all = false (the normal star icon is kept).
        /// </summary>
        private bool SearchRefersOnlyToNoQualityItems(List<string> terms)
        {
            try
            {
                List<Item> catalog = GetGameItemCatalog();
                if (catalog.Count == 0 || terms.Count == 0)
                    return false;

                var matchers = new Func<Item, bool>[terms.Count];
                for (int t = 0; t < terms.Count; t++)
                {
                    bool searchable = terms.Count == 1 || SearchMenu.IsTermSearchable(terms[t]);
                    matchers[t] = searchable ? BuildSingleMatcher(catalog, terms[t]) : (_ => false);
                }
                Func<Item, bool> combined = CombineMatchers(matchers);

                bool anyMatch = false;
                foreach (Item item in catalog)
                {
                    if (!combined(item))
                        continue;
                    if (CanHaveQuality(item))
                        return false;
                    anyMatch = true;
                }

                return anyMatch;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check whether the searched item has quality: {ex.Message}", LogLevel.Trace);
                return false;
            }
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

        // For every non-space character of notifyTitle: its line and column in the wrapped title (Game1.parseText only adds line breaks and
        // spaces, so the letters can be matched up one by one; spaces are ignored because the wrapper may keep, drop or add them).
        // False if the wrapped text is not the title (then nothing may be drawn from the result).
        private bool TryMapTitleLetters(string wrappedTitle, out int[] lineOf, out int[] colOf)
        {
            lineOf = new int[notifyTitle.Length];
            colOf = new int[notifyTitle.Length];

            int o = 0, line = 0, col = 0;
            foreach (char ch in wrappedTitle)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    line++;
                    col = 0;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    while (o < notifyTitle.Length && char.IsWhiteSpace(notifyTitle[o]))
                        o++;
                    if (o >= notifyTitle.Length || notifyTitle[o] != ch)
                        return false;
                    lineOf[o] = line;
                    colOf[o] = col;
                    o++;
                }
                col++;
            }

            while (o < notifyTitle.Length && char.IsWhiteSpace(notifyTitle[o]))
                o++;
            return o == notifyTitle.Length;   // every letter of the title was found
        }

        /// <summary>The color of the "(Invalid: ...)" part of the result title: the words of the search that found nothing.</summary>
        private static readonly Color InvalidTitleColor = Color.DarkRed;

        private bool IsInInvalidPart(int titleIndex)
        {
            return notifyInvalidLength > 0 && titleIndex >= notifyInvalidStart && titleIndex < notifyInvalidStart + notifyInvalidLength;
        }

        // Draws the title text (with the game's text shadow). A title without an "(Invalid: ...)" part is drawn in one go in the normal color.
        // With one, every wrapped line is cut at the first and the last letter of that part and drawn in up to three pieces (normal / invalid /
        // normal), each placed by measuring the text before it, the same way DrawTitleEmphasis places its bold copies.
        private void DrawTitleText(SpriteBatch b, string wrappedTitle, Vector2 titlePos, float scale, Color color, Color invalidColor)
        {
            if (notifyInvalidLength <= 0 || !TryMapTitleLetters(wrappedTitle, out int[] lineOf, out int[] colOf))
            {
                Utility.drawTextWithShadow(b, wrappedTitle, Game1.dialogueFont, titlePos, color, scale);
                return;
            }

            string[] lines = wrappedTitle.Replace("\r", "").Split('\n');
            SpriteFont font = Game1.dialogueFont;
            float lineHeight = font.LineSpacing * scale;

            // for every line: the first and the last column that belongs to the invalid part (-1 = none on this line)
            int[] firstCol = new int[lines.Length];
            int[] lastCol = new int[lines.Length];
            Array.Fill(firstCol, -1);
            Array.Fill(lastCol, -1);
            for (int idx = notifyInvalidStart; idx < notifyInvalidStart + notifyInvalidLength && idx < notifyTitle.Length; idx++)
            {
                if (char.IsWhiteSpace(notifyTitle[idx]))
                    continue;
                int line = lineOf[idx];
                if (line >= lines.Length)
                    continue;
                int col = colOf[idx];
                if (firstCol[line] < 0 || col < firstCol[line])
                    firstCol[line] = col;
                if (col > lastCol[line])
                    lastCol[line] = col;
            }

            for (int line = 0; line < lines.Length; line++)
            {
                string text = lines[line];
                if (text.Length == 0)
                    continue;

                float y = titlePos.Y + line * lineHeight;
                void DrawPart(string part, string textBefore, Color partColor)
                {
                    if (part.Length == 0)
                        return;
                    var pos = new Vector2(titlePos.X + (textBefore.Length == 0 ? 0f : font.MeasureString(textBefore).X * scale), y);
                    Utility.drawTextWithShadow(b, part, font, pos, partColor, scale);
                }

                if (firstCol[line] < 0 || firstCol[line] >= text.Length)
                {
                    DrawPart(text, "", color);
                    continue;
                }

                int start = firstCol[line];
                int end = Math.Min(lastCol[line] + 1, text.Length);
                string before = text.Substring(0, start);
                string middle = text.Substring(start, end - start);
                string after = text.Substring(end);

                DrawPart(before, "", color);
                DrawPart(middle, before, invalidColor);
                DrawPart(after, before + middle, color);
            }
        }

        // How far the extra copies of a bold word are moved to the left and to the right, in screen pixels (the pixel font has no bold version).
        // One copy at -N and one at +N, on top of the normal text, make every stroke 2 * N pixels thicker and keep the word centered where it was.
        private const int TitleEmphasisBoldSpread = 1;

        // Draws the emphasized parts of the title (notifyEmphasis: every searched word of the "No item 'a', 'b' found" title, or every item name of
        // the "Found ... containing 'a', 'b'" title, quotes not included) in bold, on top of the normal title text: each word is drawn two more
        // times in the same color, one copy a pixel to the left and one a pixel to the right. wrappedTitle is the title as drawn (after
        // Game1.parseText wrapped it), so every piece of a word is found again on its own line even when the wrapping splits a search word with
        // spaces over two lines. Only the letters are matched up with the original text (spaces are ignored, since the wrapper may keep, drop or add
        // them), and only letters are drawn, so a space at a line break can never put a piece in a wrong spot.
        private void DrawTitleEmphasis(SpriteBatch b, string wrappedTitle, Vector2 titlePos, float scale, Color color, Color invalidColor)
        {
            if (notifyEmphasis.Count == 0)
                return;

            // line and column (in the wrapped text) of every non-space character of the original title
            if (!TryMapTitleLetters(wrappedTitle, out int[] lineOf, out int[] colOf))
                return;   // the wrapped text is not the title: draw nothing extra rather than something misplaced

            string[] lines = wrappedTitle.Replace("\r", "").Split('\n');
            SpriteFont font = Game1.dialogueFont;
            float lineHeight = font.LineSpacing * scale;
            float layerDepth = titlePos.Y / 10000f;   // the same depth the normal title text uses

            foreach ((int emphasisStart, int emphasisLength) in notifyEmphasis)
            {
                Color rangeColor = IsInInvalidPart(emphasisStart) ? invalidColor : color;   // the invalid words are bold in the dark red of their part
                int first = emphasisStart;
                int last = emphasisStart + emphasisLength - 1;
                if (emphasisLength <= 0 || first < 0 || last >= notifyTitle.Length)
                    continue;

                int idx = first;
                while (idx <= last)
                {
                    if (char.IsWhiteSpace(notifyTitle[idx]))
                    {
                        idx++;
                        continue;
                    }

                    // one run of letters on one line
                    int runLine = lineOf[idx];
                    int runEnd = idx;
                    while (runEnd + 1 <= last && !char.IsWhiteSpace(notifyTitle[runEnd + 1]) && lineOf[runEnd + 1] == runLine)
                        runEnd++;

                    if (runLine < lines.Length)
                    {
                        string text = lines[runLine];
                        int colStart = colOf[idx];
                        int colEnd = Math.Min(colOf[runEnd] + 1, text.Length);
                        if (colStart < colEnd)
                        {
                            string piece = text.Substring(colStart, colEnd - colStart);
                            var pos = new Vector2(
                                titlePos.X + font.MeasureString(text.Substring(0, colStart)).X * scale,
                                titlePos.Y + runLine * lineHeight);

                            b.DrawString(font, piece, pos + new Vector2(-TitleEmphasisBoldSpread, 0f), rangeColor, 0f, Vector2.Zero, scale, SpriteEffects.None, layerDepth);
                            b.DrawString(font, piece, pos + new Vector2(TitleEmphasisBoldSpread, 0f), rangeColor, 0f, Vector2.Zero, scale, SpriteEffects.None, layerDepth);
                        }
                    }

                    idx = runEnd + 1;
                }
            }
        }

        // The item list that pops up under the result panel. Same parchment box, fonts, shadow and divider as the panel,
        // but display-only: nothing in it can be clicked or picked up.
        // avoid: a screen rectangle the list must never cover (the "Keep highlights" box). Normally the list opens below it anyway; if the two
        // would overlap, the list slides to the left until it clears the rectangle (or, with no room on the left, drops below it).
        // Header row: place name on the left, "Total: N" (sum of all matching stacks at that place) on the right.
        // above: the panel is at the bottom of the screen, so the list opens ABOVE it: topY is then the list's BOTTOM edge instead of its top edge.
        private void DrawPlaceTooltip(SpriteBatch b, string title, Dictionary<string, int> items, int anchorX /* left edge of the text */, int topY, Rectangle? avoid = null, bool above = false)
        {
            const float titleScale = 0.7f;
            const int padX = 28;
            const int padY = 22;

            // The font's measured height already contains its own top/bottom spacing, so these gaps are small on purpose.
            const int gapTitleToDivider = 0;    // title text -> divider (was 8 + font spacing: looked too airy)
            const int gapDividerToList = 2;     // divider -> first item line (was 8)
            const int dividerHeight = 2;
            const int titleTotalGap = 24;       // minimum space between the title and the total

            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            int lineHeight = Game1.smallFont.LineSpacing;

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            int headerHeight = (int)titleSize.Y + gapTitleToDivider + dividerHeight + gapDividerToList;

            // total of ALL matching items at this place (not just the lines that fit on screen)
            string totalText = $"Total: {items.Values.Sum()}";
            Vector2 totalSize = Game1.smallFont.MeasureString(totalText);

            // most-numerous items first; if the list is too tall for the screen the rest is summarised as "+N more"
            var ordered = items
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            int availableHeight = (above ? topY : screenHeight - topY) - 16 - padY * 2 - headerHeight;
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

            // the header row must fit the title AND the total; the item lines may be wider still
            int headerRowWidth = (int)Math.Ceiling(titleSize.X + titleTotalGap + totalSize.X);
            int textWidth = Math.Max(headerRowWidth, (int)lines.Max(l => Game1.smallFont.MeasureString(l).X));
            int boxWidth = textWidth + padX * 2;
            int boxHeight = padY * 2 + headerHeight + lines.Count * lineHeight;

            int boxX = Math.Clamp(anchorX - padX, 8, Math.Max(8, screenWidth - boxWidth - 8));
            int boxY = above ? topY - boxHeight : topY;

            // never cover the "Keep highlights" box: slide the list left until its right edge is clear of the box
            if (avoid is Rectangle keepRect && new Rectangle(boxX, boxY, boxWidth, boxHeight).Intersects(keepRect))
            {
                int clearX = keepRect.X - KeepBoxPanelGap - boxWidth;
                if (clearX >= 8)
                    boxX = clearX;
                else
                    boxY = above
                        ? keepRect.Y - KeepBoxPanelGap - boxHeight   // no room on the left (a tiny window): go above the box instead
                        : keepRect.Bottom + KeepBoxPanelGap;         // no room on the left (a tiny window): go below the box instead
            }

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White);

            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, textPos, Game1.textColor, titleScale);

            // total: right-aligned to the divider's right end, vertically centered on the title, a bit quieter than the title
            Vector2 totalPos = new Vector2(textPos.X + textWidth - totalSize.X, textPos.Y + (titleSize.Y - totalSize.Y) / 2f);
            Utility.drawTextWithShadow(b, totalText, Game1.smallFont, totalPos, Game1.textColor * 0.75f);

            int dividerY = (int)(textPos.Y + titleSize.Y) + gapTitleToDivider;
            b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, dividerHeight), Game1.textColor * 0.35f);

            Vector2 linePos = new Vector2(textPos.X, dividerY + dividerHeight + gapDividerToList);
            foreach (string line in lines)
            {
                Utility.drawTextWithShadow(b, line, Game1.smallFont, linePos, Game1.textColor);
                linePos.Y += lineHeight;
            }
        }

        // True while the game's toolbar is at the TOP of the screen (it jumps there when the player stands in the lower half of the screen; the
        // option "pin toolbar" keeps it at the bottom). The toolbar's own position is read, so this stays right whatever the reason it is up there.
        // No toolbar on screen (hidden HUD, cutscene) = false: the panel keeps its normal place.
        private static bool IsToolbarAtTop()
        {
            foreach (IClickableMenu menu in Game1.onScreenMenus)
            {
                if (menu is Toolbar toolbar)
                    return toolbar.yPositionOnScreen < Game1.uiViewport.Height / 2;
            }
            return false;
        }

        // ===================== "KEEP HIGHLIGHTS" BOX (beside the result panel) =====================
        // A small parchment box next to the result panel with the game's own checkbox and the words "Keep highlights".
        //   unchecked (default): the box belongs to the result panel: it appears, fades and disappears together with it, and hovering it
        //                        pauses the panel's countdown. The chest highlights run on their own timer (highlightUntilMs, GMCM timeout), which is frozen
        //                        while the mouse is over the panel or this box and runs on when the mouse leaves.
        //   checked (IsKeepHighlightsLocked): the box stays on the HUD even after the panel has faded out (same spot, fully opaque), the chest
        //                        highlights never expire and opening a found chest does not clear them. Like the rest of the HUD it is hidden while
        //                        a menu (inventory, CJB, ...) is open and comes back when the menu closes.
        //   released by: clicking the checkbox again, or pressing the search key to open the search box (also by a new search or going back
        //                to the title). Releasing clears the highlights right away. After a click the box fades out over KeepBoxFadeMs;
        //                hovering it pauses the fade, clicking it meanwhile revives it (locked again, highlights back). When the search box
        //                opens it is simply gone. A new search brings a fresh box.

        private const string KeepBoxLabel = "Keep highlights";
        private const string KeepBoxTip = "Check to keep highlights on; uncheck to clear.";

        // the game's own checkbox sprites (the ones its options menu uses), in Game1.mouseCursors
        private static readonly Rectangle CheckboxEmptySource = new(227, 425, 9, 9);
        private static readonly Rectangle CheckboxFilledSource = new(236, 425, 9, 9);
        private const int CheckboxScale = 4;                        // 9px sprite * 4 = 36px
        private const int CheckboxSize = 9 * CheckboxScale;
        private const int CheckboxHitPad = 2;                       // a little clickable room around the checkbox; nothing else in the box is clickable

        private const int KeepBoxPadX = 24;                         // box frame -> checkbox / words
        private const int KeepBoxPadY = 20;
        private const int KeepBoxLabelGap = 8;                      // checkbox -> words
        private const int KeepBoxPanelGap = 12;                     // result panel -> box: the horizontal space between the panel's right edge and the box
        private const int KeepBoxBottomOffset = 0;                  // panel bottom edge -> box bottom edge (0 = the two bottom edges are level; raise it to move the box up)
        private const int KeepBoxHudReserve = 300;                  // width of the game's clock / gold block at the top right (used if the real one cannot be read)
        private const int KeepBoxHudGap = 4;                        // the box always stays at least this far left of the clock block (the block's own frame adds a bit more room)
        private const int KeepBoxScreenMargin = 8;                  // the box never gets closer than this to the left screen edge
        private const int KeepBoxToolbarBand = 140;                 // height of the strip at the top / bottom screen edge that the game's toolbar (96 px + its margin) may occupy; the box never enters it
        private const int KeepBoxHudFallbackHeight = 260;           // height of the clock / gold block (used if the real one cannot be read)
        private const long KeepBoxFreshMs = 200;                    // the box counts as "on screen" if it was drawn this recently (real clock)
        private const long KeepBoxFadeMs = 4000;                    // how long the box takes to fade out after the checkbox was unchecked (real clock, like the result panel's countdown)

        /// <summary>True while "Keep highlights" is checked: the box stays on screen and the chest highlights never expire.</summary>
        private bool IsKeepHighlightsLocked = false;

        // Lifecycle of the box after the checkbox was unchecked (a small state machine; the box is "dismissed" from that moment on):
        //   LOCKED    IsKeepHighlightsLocked, not dismissed: box fully visible, highlights never expire.
        //   FADING    dismissed, keepFadeUntilMs in the future: highlights are already gone, the box fades out over KeepBoxFadeMs. Hovering it
        //             freezes the countdown (keepFadeUntilMs is pushed back by the frame time) and shows it fully opaque. Clicking it
        //             revives it: back to LOCKED, with the chest highlights from keepSavedChests lit again.
        //   GONE      dismissed, countdown over (or the fade was ended on purpose): nothing is drawn and nothing can be clicked.
        // A new result (ScanChests) starts everything over with a fresh, unchecked box.
        private bool keepBoxDismissed = false;
        private long keepFadeUntilMs = 0;                           // real-clock end of the fade-out (in the past = not fading)

        // The highlighted chests at the moment of unchecking, so a click during the fade can light them again. Dropped as soon as the box is GONE.
        private List<(GameLocation location, Vector2 position, Chest chest)>? keepSavedChests;
        private Rectangle keepBoxBounds;                            // the whole box: hovering it shows the tip and pauses the countdown
        private Rectangle keepCheckBounds;                          // the checkbox sprite
        private Rectangle keepCheckHit;                             // the clickable area: the checkbox plus a tiny margin
        private long keepBoxDrawnMs = -1000000;                     // real-clock time of the last frame the box was drawn

        // Releases the lock and clears the highlights at once (the box itself disappears when the result panel is already gone).
        // The chest highlights stop at once. With fadeOut true (unchecking with a click) the box fades away over KeepBoxFadeMs and can still be
        // clicked back to life meanwhile; with fadeOut false it is simply gone (the search box is opening over it).
        private void ReleaseKeepLock(bool fadeOut)
        {
            IsKeepHighlightsLocked = false;
            keepBoxDismissed = true;
            keepBoxDrawnMs = -1000000;

            if (fadeOut)
            {
                keepSavedChests = new List<(GameLocation location, Vector2 position, Chest chest)>(highlightedChests);
                keepFadeUntilMs = clock.ElapsedMilliseconds + KeepBoxFadeMs;
            }
            else
            {
                keepSavedChests = null;
                keepFadeUntilMs = 0;
            }

            ClearHighlights();
        }

        // Ends a running fade on purpose: the box is GONE and can no longer be revived.
        private void EndKeepFade()
        {
            keepFadeUntilMs = 0;
            keepSavedChests = null;
        }

        // Brings a fading box back: locked again, and the chests that were highlighted at the moment of unchecking light up again.
        private void ReviveKeepLock()
        {
            highlightedChests = keepSavedChests != null
                ? new List<(GameLocation location, Vector2 position, Chest chest)>(keepSavedChests)
                : new List<(GameLocation location, Vector2 position, Chest chest)>();
            keepSavedChests = null;
            keepFadeUntilMs = 0;
            keepBoxDismissed = false;
            IsKeepHighlightsLocked = true;
        }

        // True while the box of a released lock is still fading out.
        private bool IsKeepBoxFading(long now)
        {
            return keepBoxDismissed && keepFadeUntilMs > now;
        }

        // The box is part of the HUD: it is only drawn while the HUD itself is shown (no inventory / CJB / dialogue menu, no cutscene).
        private static bool IsKeepBoxHudVisible()
        {
            return Context.IsWorldReady
                && Game1.activeClickableMenu == null
                && Game1.displayHUD
                && !Game1.eventUp;
        }

        // Works out where the box goes: to the RIGHT of the result panel, KeepBoxPanelGap pixels from its right edge, with its BOTTOM edge level
        // with the panel's bottom edge (so it sits beside the lower part of the panel). The panel's position and size are passed in every frame,
        // so the box follows the panel when it gets taller.
        // The box must never cover the game's clock / gold block at the top right, so the room on the right ends KeepBoxHudGap pixels left of
        // that block. When the window is so narrow (or the panel so wide) that the box does not fit there, it goes to the LEFT of the panel
        // instead (same height), and only if that does not fit either, directly under the panel at its right end (the old place).
        // The box and the panel stay diagonal to each other: with the panel at the top of the screen the box sits at the panel's lower right
        // (bottom edges level); with the panel flipped to the bottom (toolbar at the top) it sits at the panel's upper right (top edges level).
        // In both cases it stays clear of the clock / gold block (only a concern where their rows meet) and out of the toolbar's strip.
        // Its hover tip (see DrawKeepBox) opens below the mouse, so it does not matter where the box is.
        private void LayoutKeepBox(int panelX, int panelY, int panelWidth, int panelHeight, bool panelAtBottom = false)
        {
            int labelWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(KeepBoxLabel).X);
            int width = KeepBoxPadX * 2 + CheckboxSize + KeepBoxLabelGap + labelWidth;
            int height = Math.Max(CheckboxSize, Game1.smallFont.LineSpacing) + KeepBoxPadY * 2;

            // where the clock / gold block starts: the real position when the game has it, never further right than the reserved width
            int hudLeft = Game1.uiViewport.Width - KeepBoxHudReserve;
            if (Game1.dayTimeMoneyBox != null)
                hudLeft = Math.Min(hudLeft, Game1.dayTimeMoneyBox.xPositionOnScreen);
            hudLeft -= KeepBoxHudGap;

            // the rows the clock / gold block covers (with a gap), for the panel at the bottom: far from the block the box may use the whole width
            int hudTop = Game1.dayTimeMoneyBox?.yPositionOnScreen ?? 0;
            int hudBottom = Game1.dayTimeMoneyBox != null ? Game1.dayTimeMoneyBox.yPositionOnScreen + DayTimeMoneyBox.height : KeepBoxHudFallbackHeight;   // height is a static field of the class

            int rightX = panelX + panelWidth + KeepBoxPanelGap;
            int leftX = panelX - KeepBoxPanelGap - width;

            // top edge for a box beside the panel:
            //   panel at the top:    its bottom edge level with the panel's bottom edge (never higher than the panel's top edge)
            //   panel at the bottom: its top edge level with the panel's top edge (never lower than the panel's bottom edge)
            int sideY = panelAtBottom
                ? Math.Min(panelY + KeepBoxBottomOffset, panelY + panelHeight - height)
                : Math.Max(panelY, panelY + panelHeight - height - KeepBoxBottomOffset);

            // how far right the box may reach: always left of the clock block when the panel is at the top (as before); at the bottom only when
            // the box's rows would meet the block's rows
            bool meetsHud = sideY < hudBottom + KeepBoxHudGap && sideY + height > hudTop - KeepBoxHudGap;
            int rightLimit = panelAtBottom && !meetsHud ? Game1.uiViewport.Width - KeepBoxScreenMargin : hudLeft;

            int x, y;
            if (rightX + width <= rightLimit)
            {
                x = rightX;                                   // beside the panel, on its right (the normal case)
                y = sideY;
            }
            else if (leftX >= KeepBoxScreenMargin)
            {
                x = leftX;                                    // no room on the right: beside the panel, on its left
                y = sideY;
            }
            else
            {
                x = Math.Max(panelX, panelX + panelWidth - width);   // no room at either side: under the panel, right-aligned
                y = panelAtBottom
                    ? panelY - KeepBoxPanelGap - height             // the panel is at the bottom of the screen: the box goes above it instead
                    : panelY + panelHeight + KeepBoxPanelGap;
            }

            // never inside the toolbar's strip: the toolbar is at the top when the panel is at the bottom, and at the bottom otherwise
            if (panelAtBottom)
                y = Math.Max(y, KeepBoxToolbarBand);
            else
                y = Math.Min(y, Math.Max(0, Game1.uiViewport.Height - KeepBoxToolbarBand - height));

            keepBoxBounds = new Rectangle(x, y, width, height);
            keepCheckBounds = new Rectangle(x + KeepBoxPadX, y + (height - CheckboxSize) / 2, CheckboxSize, CheckboxSize);

            keepCheckHit = keepCheckBounds;
            keepCheckHit.Inflate(CheckboxHitPad, CheckboxHitPad);
        }

        // Draws the box (parchment frame, the game's checkbox, the words) and, last, the tip while the mouse is over the box.
        private void DrawKeepBox(SpriteBatch b, float alpha)
        {
            keepBoxDrawnMs = clock.ElapsedMilliseconds;
            Point mouse = Game1.getMousePosition();

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), keepBoxBounds.X, keepBoxBounds.Y, keepBoxBounds.Width, keepBoxBounds.Height, Color.White * alpha);

            // the mouse is over the checkbox itself: a soft highlight behind it (the words are not clickable, so they get none)
            if (keepCheckHit.Contains(mouse))
                b.Draw(Game1.staminaRect, keepCheckHit, Game1.textColor * 0.12f * alpha);

            b.Draw(
                Game1.mouseCursors,
                new Vector2(keepCheckBounds.X, keepCheckBounds.Y),
                IsKeepHighlightsLocked ? CheckboxFilledSource : CheckboxEmptySource,
                Color.White * alpha,
                0f,
                Vector2.Zero,
                (float)CheckboxScale,
                SpriteEffects.None,
                1f);

            int lineHeight = Game1.smallFont.LineSpacing;
            var textPos = new Vector2(keepCheckBounds.Right + KeepBoxLabelGap, keepBoxBounds.Y + (keepBoxBounds.Height - lineHeight) / 2f);
            Utility.drawTextWithShadow(b, KeepBoxLabel, Game1.smallFont, textPos, Game1.textColor * alpha);

            // the game's own hover tip, the same kind the search box shows on its magnifier, star and Clear History button.
            // The game puts a tip 32 px right of the mouse (using the old mouse position); the x offset moves it back so that its left edge
            // lies exactly on the left edge of the box, whatever part of the box the mouse is over.
            if (keepBoxBounds.Contains(mouse))
                IClickableMenu.drawHoverText(b, KeepBoxTip, Game1.smallFont, xOffset: keepBoxBounds.X - (Game1.getOldMouseX() + 32));
        }

        // A left click on the checkbox flips it. Only the checkbox counts (the words and the rest of the box do nothing), and the click is
        // swallowed, so it does not also swing a tool or interact with whatever lies under the box.
        private void OnKeepBoxClicked(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.MouseLeft)
                return;
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;
            long now = clock.ElapsedMilliseconds;
            if (now - keepBoxDrawnMs > KeepBoxFreshMs)
                return;   // the box is not on screen right now
            if (keepBoxDismissed && !IsKeepBoxFading(now))
                return;   // GONE: nothing to click
            if (!keepCheckHit.Contains(Game1.getMousePosition()))
                return;

            if (keepBoxDismissed)
            {
                // FADING -> LOCKED: checked again during the fade: the fade stops, the lock is back and the highlights light up again
                ReviveKeepLock();
            }
            else if (IsKeepHighlightsLocked)
            {
                // LOCKED -> FADING: unchecking: the highlights stop at once, the box starts its fade-out
                ReleaseKeepLock(fadeOut: true);
            }
            else
            {
                // nothing left to keep (e.g. the found chest was already opened): the box stays unchecked, the click is not used
                if (highlightedChests.Count == 0)
                    return;

                IsKeepHighlightsLocked = true;
            }

            Game1.playSound("drumkit6");   // the sound the game's own checkboxes make
            Helper.Input.Suppress(e.Button);
        }

        // ===================== FEEDBACK FOR A FINISHED SEARCH =====================
        // Called once per search that really produced a result (ScanChests, after the title is built): never when the search box opens, while
        // typing, when the text is too short, or when the star filter asks the player to choose again. All sounds are the game's own cues.
        //   found:     "coin", then "pickUpItem" (SearchSecondSoundDelayMs) and a long-ringing "reward" (SearchThirdSoundDelayMs), and a few gold stars beside the result panel
        //   not found: "fishingRodBend" (a short, low creak: the search hit a wall), no stars, and a red "no entry" sign left of the title
        private void PlaySearchFeedback(bool found)
        {
            pendingSounds.Clear();
            sparkleStartMs = -1000000;

            if (found)
            {
                long now = clock.ElapsedMilliseconds;
                Game1.playSound("coin");
                pendingSounds.Add(("pickUpItem", now + SearchSecondSoundDelayMs));
                pendingSounds.Add(("reward", now + SearchThirdSoundDelayMs));   // the longest-ringing of the three: the "satisfying" tail
                sparkleStartMs = now;
            }
            else
            {
                Game1.playSound("fishingRodBend");
            }
        }

        // One star: which side of the panel it starts at (0 = left, 1 = right), where along the title row (0 to 1), how far it drifts out,
        // how far it rises, and a phase that varies its twinkle and spin. Fixed values, so every search looks the same.
        private const float StarScale = 3.5f;   // 8px sprite * 3.5 = 28 px: round and clearly visible

        private static readonly (int Side, float YFrac, float Out, float Rise, float Phase)[] SparkleSeeds =
        {
            (0, 0.15f, 1.0f, 1.0f, 0.0f), (1, 0.20f, 0.9f, 0.8f, 0.4f),
            (0, 0.50f, 0.6f, 0.5f, 0.8f), (1, 0.55f, 1.0f, 1.0f, 0.2f),
            (0, 0.85f, 0.8f, 0.7f, 0.6f), (1, 0.90f, 0.7f, 0.9f, 1.0f),
            (0, 0.33f, 0.4f, 1.0f, 0.5f), (1, 0.38f, 0.5f, 0.6f, 0.9f),
            (0, 0.70f, 1.0f, 0.4f, 0.3f), (1, 0.72f, 0.6f, 0.8f, 0.7f)
        };

        // Draws the gold stars of a successful search: they pop out at the two sides of the panel (next to the title), drift outwards and up,
        // stay fully visible for SparkleHoldMs and then fade away smoothly. A star is only drawn where it lies completely on the screen and
        // clear of the result panel, the "Keep highlights" box, the clock / gold block and the toolbar's strip, so it never covers any of them.
        private void DrawSearchSparkles(SpriteBatch b, Rectangle panel, bool panelAtBottom, float panelAlpha, Rectangle? keepBox)
        {
            long age = clock.ElapsedMilliseconds - sparkleStartMs;
            long total = SparkleHoldMs + SparkleFadeMs;
            if (age < 0 || age >= total)
                return;

            float fade = age <= SparkleHoldMs ? 1f : 1f - (age - SparkleHoldMs) / (float)SparkleFadeMs;
            fade = fade * fade * (3f - 2f * fade);                      // smooth in and out
            float grow = Math.Min(1f, age / 180f);                      // the stars pop up in the first moments
            float t = age / (float)total;
            float drift = 1f - (1f - t) * (1f - t) * (1f - t);           // starts moving, then slows down gently (the stars now stay about 3.5 s)

            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            var screen = new Rectangle(0, 0, screenWidth, screenHeight);

            // the places a star must keep out of (same sizes the "Keep highlights" box keeps clear of)
            int hudLeft = screenWidth - KeepBoxHudReserve;
            if (Game1.dayTimeMoneyBox != null)
                hudLeft = Math.Min(hudLeft, Game1.dayTimeMoneyBox.xPositionOnScreen);
            var clockBlock = new Rectangle(hudLeft - KeepBoxHudGap, 0, screenWidth, KeepBoxHudFallbackHeight + KeepBoxHudGap);
            var toolbarStrip = panelAtBottom
                ? new Rectangle(0, 0, screenWidth, KeepBoxToolbarBand)
                : new Rectangle(0, Math.Max(0, screenHeight - KeepBoxToolbarBand), screenWidth, KeepBoxToolbarBand);
            Rectangle keepArea = keepBox ?? Rectangle.Empty;
            if (keepBox != null)
                keepArea.Inflate(8, 8);

            int rowHeight = Math.Max(1, Math.Min(panel.Height - 28, 70));   // the stars start along the first (title) row of the panel

            for (int i = 0; i < SparkleSeeds.Length; i++)
            {
                var seed = SparkleSeeds[i];

                float x = seed.Side == 0
                    ? panel.X - 14 - 12 - 26f * seed.Out * drift
                    : panel.Right + 14 + 12 + 26f * seed.Out * drift;
                float y = panel.Y + 14 + seed.YFrac * rowHeight - 18f * seed.Rise * t;

                // Panel at the bottom: BOTH sides use the same scatter as the panel at the top (same spacing, same outward drift, same rise), but the
                // stars stand on the panel's bottom edge instead of its title row: at the start the lowest star (YFrac 0.90) has its bottom edge level
                // with the panel's bottom edge, and the others lie above it with the same spacing as at the top.
                if (panelAtBottom)
                {
                    float halfStar = 8 * StarScale / 2f;
                    y = panel.Bottom - halfStar - (0.90f - seed.YFrac) * rowHeight - 18f * seed.Rise * t;

                    // The "Keep highlights" box (upper right of the panel, or upper left if it had to move): a star that would lie over it
                    // (at the start, or while it rises) is held just under the box instead, so it never covers it and nothing is squeezed together.
                    if (keepBox != null)
                    {
                        bool sharesColumn = x + halfStar > keepArea.Left && x - halfStar < keepArea.Right;
                        if (sharesColumn && y - halfStar < keepArea.Bottom + 1f && y + halfStar > keepArea.Top)
                            y = keepArea.Bottom + halfStar + 1f;
                    }
                }

                float scale = StarScale * grow;   // every star the same fixed size (only the quick pop-in at the start changes it)
                int size = (int)Math.Ceiling(8 * scale);
                var box = new Rectangle((int)(x - size / 2f), (int)(y - size / 2f), size, size);

                if (size <= 0 || !screen.Contains(box)
                    || box.Intersects(panel) || box.Intersects(clockBlock) || box.Intersects(toolbarStrip)
                    || (keepBox != null && box.Intersects(keepArea)))
                    continue;

                b.Draw(
                    Game1.mouseCursors,
                    new Vector2(x, y),
                    QualityGoldStarSource,
                    Color.White * (fade * panelAlpha),
                    seed.Phase * 0.5f + t * 0.8f,
                    new Vector2(4f, 4f),
                    scale,
                    SpriteEffects.None,
                    1f);
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
            bool panelVisible = remaining > 0f;
            if (string.IsNullOrEmpty(notifyTitle)) return;
            bool keepBoxFading = IsKeepBoxFading(now);
            if (keepBoxDismissed && !keepBoxFading && keepSavedChests != null)
                keepSavedChests = null;   // GONE: nothing to revive any more
            if (!panelVisible && !IsKeepHighlightsLocked && !keepBoxFading) return;   // panel gone, nothing locked, no box fading out: nothing to draw
            if (!panelVisible && !IsKeepBoxHudVisible()) return;                  // only the locked box is left, and a menu is hiding the HUD

            SpriteBatch b = e.SpriteBatch;
            float alpha = panelVisible ? Math.Min(1f, remaining / 0.6f) : 0f;   // fade out during the last 0.6 seconds

            const float titleScale = 0.8f;
            const int padX = 32;
            const int padY = 26;
            const int gap = 10;

            int screenWidth = Game1.uiViewport.Width;
            int maxTextWidth = Math.Min(760, screenWidth - 160) - padX * 2;

            // quality icon at the right end of the title row: the title gets a little less room, so it never runs under the icon
            int iconReserve = QualityBoxSize + QualityIconGap - (padX - QualityIconRightInset);

            // a failed search: the warning badge sits at the left end of the title row, so the title starts a little further right
            bool showFailIcon = resultFailed;
            int leftReserve = showFailIcon ? WarningBadgeSize + WarningBadgeGap - (padX - WarningBadgeLeftInset) : 0;

            string title = Game1.parseText(notifyTitle, Game1.dialogueFont, (int)((maxTextWidth - iconReserve - leftReserve) / titleScale));
            string body = notifyBody.Length > 0 ? Game1.parseText(notifyBody, Game1.smallFont, maxTextWidth) : "";

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            Vector2 bodySize = body.Length > 0 ? Game1.smallFont.MeasureString(body) : Vector2.Zero;

            int textWidth = (int)Math.Max(titleSize.X + iconReserve + leftReserve, bodySize.X);
            int boxWidth = textWidth + padX * 2;
            int boxHeight = (int)titleSize.Y + padY * 2;
            if (body.Length > 0)
                boxHeight += gap * 2 + 2 + (int)bodySize.Y;

            int boxX = (screenWidth - boxWidth) / 2;

            // The game moves its toolbar between the bottom and the top of the screen depending on where the player stands. The panel normally
            // sits at the top; while the toolbar is at the top it moves to the bottom instead, so it never covers the toolbar. The "Keep highlights"
            // box and the item list of a hovered place are laid out relative to the panel, so they follow it.
            bool panelAtBottom = IsToolbarAtTop();
            int boxY = panelAtBottom ? Math.Max(20, Game1.uiViewport.Height - boxHeight - 20) : 20;

            // the "Keep highlights" box beside the panel, only when something was found (placed here because hovering it pauses the countdown too).
            // It lives as long as the panel, or, while locked, until the lock is released; a menu hides it like the rest of the HUD.
            bool showKeepBox = body.Length > 0
                && (!keepBoxDismissed || keepBoxFading)
                && (panelVisible || IsKeepHighlightsLocked || keepBoxFading)
                && IsKeepBoxHudVisible();
            if (showKeepBox)
                LayoutKeepBox(boxX, boxY, boxWidth, boxHeight, panelAtBottom);

            // hover pause: while the mouse is over the panel the countdown does not run (the real clock keeps going,
            // so the end time is pushed back by the same amount) and the panel stays fully visible
            // (the same goes for the mouse being over the "Keep highlights" box)
            Point mouse = Game1.getMousePosition();
            bool overPanel = panelVisible && new Rectangle(boxX, boxY, boxWidth, boxHeight).Contains(mouse);
            bool overKeepBox = showKeepBox && !keepBoxDismissed && keepBoxBounds.Contains(mouse);

            // a FADING box: hovering it (clicking is not needed) freezes its own countdown and shows it fully opaque again, like the result
            // panel; moving the mouse away lets the remaining countdown and the fade carry on
            bool overFadingKeepBox = keepBoxFading && showKeepBox && keepBoxBounds.Contains(mouse);
            if (overFadingKeepBox)
                keepFadeUntilMs += frameDelta;
            if (panelVisible && (overPanel || overKeepBox))   // a panel that is already gone is never revived by hovering the locked box
            {
                notifyUntilMs = Math.Max(notifyUntilMs + frameDelta, now + HoverMinRemainingMs);
                alpha = 1f;

                // the chest highlights stand still with the panel: their end time is pushed back by the same frame time, so their remaining time
                // is exactly what it was when the mouse arrived, and it runs on from there when the mouse leaves. (Locked highlights never
                // expire, so there is nothing to freeze then; "never expire" (-1) stays as it is.)
                highlightHoverFrameMs = now;
                if (!IsKeepHighlightsLocked && highlightedChests.Count > 0 && highlightUntilMs >= 0)
                    highlightUntilMs += frameDelta;
            }

            (string Title, Dictionary<string, int> Items, int AnchorX)? pendingTooltip = null;   // drawn after the keep box, see below
            Rectangle qualityIconBounds = Rectangle.Empty;   // where the quality icon is on screen (for its hover tip)

            if (panelVisible)
            {
                // the game's standard menu box
                IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White * alpha);

                Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
                // the whole title in the normal text color (its "(Invalid: ...)" part, if it has one, in dark red), then the emphasized parts (the quoted
                // item names of "Found ... containing 'name'", or the searched words of "No item 'name' found") drawn bold on top of it in the very same color
                Vector2 titlePos = new Vector2(textPos.X + leftReserve, textPos.Y);
                DrawTitleText(b, title, titlePos, titleScale, Game1.textColor * alpha, InvalidTitleColor * alpha);
                DrawTitleEmphasis(b, title, titlePos, titleScale, Game1.textColor * alpha, InvalidTitleColor * alpha);

                // the quality icon: right end of the title row, inside the panel's frame, centered on the first title line (display only)
                {
                    float firstLineHeight = Game1.dialogueFont.LineSpacing * titleScale;
                    qualityIconBounds = new Rectangle(
                        boxX + boxWidth - QualityIconRightInset - QualityBoxSize,
                        (int)Math.Round(textPos.Y + (firstLineHeight - QualityBoxSize) / 2f),
                        QualityBoxSize,
                        QualityBoxSize);
                    SearchMenu.DrawQualityButton(b, qualityIconBounds, resultHasNoQualityItemsOnly ? AnyQuality : resultQuality, alpha);
                }

                // the warning icon of a failed search: left end of the title row, display only (no click, no hover)
                if (showFailIcon)
                {
                    float firstLineHeight = Game1.dialogueFont.LineSpacing * titleScale;
                    DrawWarningBadge(
                        b,
                        boxX + WarningBadgeLeftInset,
                        Math.Max(boxY + 22, (int)Math.Round(textPos.Y + (firstLineHeight - WarningBadgeSize) / 2f)),
                        alpha);
                }

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

                            pendingTooltip = (hovered.Title, items, pieces[0].X);
                        }
                    }
                }
            }

            // the gold stars of a successful search, beside the panel (under the keep box and the item list, which are drawn after them)
            if (panelVisible)
                DrawSearchSparkles(b, new Rectangle(boxX, boxY, boxWidth, boxHeight), panelAtBottom, alpha, showKeepBox ? keepBoxBounds : (Rectangle?)null);

            // the "Keep highlights" box (drawn last, so its tip lies on top)
            if (showKeepBox)
            {
                // locked: stays fully visible even after the panel has faded out; just unchecked: fades out over KeepBoxFadeMs (it was fully visible)
                float keepAlpha = keepBoxFading
                    ? (overFadingKeepBox ? 1f : Math.Clamp((keepFadeUntilMs - now) / (float)KeepBoxFadeMs, 0f, 1f))
                    : (IsKeepHighlightsLocked ? 1f : alpha);
                DrawKeepBox(b, keepAlpha);
            }

            // the item list of a hovered place opens right under the panel (from the hovered word to the right). The keep box now sits beside the
            // panel, so the two normally do not meet; the box's rectangle is handed over anyway, so the list can never cover it
            // (the list needs the mouse on a place word, the box tip needs the mouse on the box: the two tips never show at once)
            if (pendingTooltip != null)
            {
                var tip = pendingTooltip.Value;
                Rectangle? keepAvoid = showKeepBox ? keepBoxBounds : (Rectangle?)null;
                DrawPlaceTooltip(b, tip.Title, tip.Items, tip.AnchorX, panelAtBottom ? boxY - 8 : boxY + boxHeight + 8, keepAvoid, panelAtBottom);
            }

            // hovering the quality icon: the game's own hover tip names the filter (drawn last, so it lies on top of everything else).
            // The mouse over the icon is also over the panel, so the countdown is paused like anywhere else on the panel.
            if (panelVisible && qualityIconBounds.Contains(mouse))
                IClickableMenu.drawHoverText(b, resultHasNoQualityItemsOnly ? NoQualityItemTip : GetQualityTip(resultQuality), Game1.smallFont);
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