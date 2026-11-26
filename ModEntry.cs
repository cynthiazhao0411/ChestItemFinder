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
using StardewValley.Buildings;
using StardewValley.TokenizableStrings;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestItemFinder
{
    public class ModEntry : Mod
    {
        private List<(GameLocation location, Vector2 position, Chest chest)> highlightedChests = new();

        // The player's backpack is a place like any other (see BACKPACK AS A PLACE): when it holds something of the search, the player flashes like a
        // chest. highlightBackpack = the flash is on; backpackWordSlots = the colors of the words the backpack holds (several words search, like
        // chestWordSlots); keepSavedBackpack = the flash at the moment "Keep highlights" was unchecked (a click during the fade brings it back).
        private bool highlightBackpack;
        private int[] backpackWordSlots = Array.Empty<int>();
        private bool keepSavedBackpack;

        /// <summary>Something is highlighted: a chest, or the backpack (the player).</summary>
        private bool AnyHighlight => highlightedChests.Count > 0 || highlightBackpack;

        // Hover tip of a highlighted chest in the world (see OnRenderedHudChestTip): the matcher of the last search (what counts as "found"), so the
        // tip can list what ONE chest holds of it, live; an icon sample per item name; and the screen rectangles of the result panel, its red X and the
        // "Keep highlights" box in the last HUD frame, which the tip must never be drawn over.
        private Func<Item, bool>? highlightMatcher;
        private readonly Dictionary<string, Item> chestTipSamples = new();
        private readonly List<Rectangle> hudBlockRects = new();
        private string notifyTitle = "";
        private List<(int Start, int Length)> notifyEmphasis = new();   // the parts of notifyTitle that are drawn bold (every searched word of the "No item" title and every item name of the "Found ..." title, each without its quotes), as character ranges; empty = nothing is bold
        private List<string> notifyEmphasisTerms = new();     // the searched word behind each notifyEmphasis range (same order)
        private List<string?> notifyEmphasisTips = new();   // hover tip of each notifyEmphasis range (same order; null = no tip), worked out once per search in BuildEmphasisTips
        private int notifyInvalidStart = -1;    // the "(Invalid: 'ere', 'df')" part of notifyTitle (or the whole "Invalid: ..." title), drawn in dark red; Length 0 = the title has no such part
        private int notifyInvalidLength = 0;
        private int notifyTailStart = -1;       // where the bracketed groups after the main sentence start: the title breaks onto a new line there (-1 = no groups)
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

        // the order of the items in the item list of a place: by the search word that found them (in the order typed: egg first, then fish,
        // then strawberry), and inside one word in the order of its tip / drop-down (items of one root together: Starfruit, Starfruit Wine)
        private Dictionary<string, (int Term, int Position)> placeItemOrder = new();

        // What the highlights are for, as shown in the corner status line ("Highlighting: 'Apple' (12 chests)"): the searched words that found
        // something, quoted. Set by a successful search; the line itself only shows while there are highlighted chests (see DrawHighlightStatus).
        private string highlightLabel = "";

        // Per place key: how many found containers of each kind (for the brackets of the place list: "Farmhouse x2 (fridges)"), and the matching items
        // per section ("chest" = every chest size, or a special kind's key) with how many containers each section has (for the sections of a place's
        // item list when a place mixes kinds: "Chest / Milk ×3", "Fridge / Milk ×5").
        private readonly Dictionary<string, Dictionary<ContainerKind, int>> placeKindCounts = new();
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> placeSectionItems = new();

        // One sample item (stack 1, no star) for every item name in placeItems, so the item list of a hovered place can draw the real icon of each item.
        private readonly Dictionary<string, Dictionary<string, Item>> placeSamples = new();

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
            ["IslandSouth"] = "Ginger Island (Docks)",
            ["IslandSouthEast"] = "Ginger Island (Tide Pool)",
            ["IslandEast"] = "Ginger Island (Jungle)",
            ["IslandWest"] = "Ginger Island (Farm)",
            ["IslandNorth"] = "Ginger Island (Dig Site)",
            ["IslandFarmHouse"] = "Ginger Island Farmhouse",
            ["IslandFarmCave"] = "Ginger Island Farm Cave",
            ["IslandHut"] = "Ginger Island Leo's Hut",
            ["IslandFieldOffice"] = "Ginger Island Field Office",
            ["ManorHouse"] = "Mayor's Manor"
        };

        // the colors offered in the config menu, as a row of boxes (see ColorSwatchPicker), left to right
        private static readonly string[] HighlightColorNames =
        {
            "Yellow", "Orange", "Red", "Pink", "Purple", "Blue", "Sky Blue", "Green", "White", "Black"
        };

        // how every color is drawn (the flash on the chests, the marks behind the words, the words of the status line). Gold, Brown and Gray
        // are no longer offered, but a config.json that still names them keeps working.
        private static readonly Dictionary<string, Color> HighlightColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Yellow"] = Color.Yellow,
            ["Orange"] = Color.Orange,
            ["Red"] = Color.Red,
            ["Pink"] = new Color(255, 64, 192),
            ["Purple"] = Color.Purple,
            ["Blue"] = Color.Blue,
            ["Sky Blue"] = new Color(30, 144, 255),
            ["Green"] = Color.LimeGreen,   // brighter than XNA's very dark Color.Green, so it shows on grass
            ["White"] = Color.White,
            ["Black"] = Color.Black,
            ["Gold"] = new Color(255, 212, 0),
            ["Brown"] = Color.Brown,
            ["Gray"] = Color.Gray
        };

        /// <summary>Returns the matching color name (fixing the casing), or the fallback ("Yellow") if it is unknown.</summary>
        private static string NormalizeColorName(string? name, string fallback = "Yellow")
        {
            string trimmed = name?.Trim() ?? "";
            foreach (string known in HighlightColors.Keys)
            {
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return fallback;
        }

        // ===================== SEVERAL WORDS, SEVERAL COLORS =====================
        // A search for two or three words ("fruit, fish") gives every word that found something a flash color of its own, in the order typed:
        // word 1 the main color (the one a one-word search uses, default Yellow), word 2 the secondary color 1 (Pink), word 3 the secondary
        // color 2 (Sky Blue). The color of a word shows:
        //   - on the chests that hold it (the flash). A chest that holds several words is cut into upright strips, one per word, left to right in
        //     the order of the color numbers (word 1, 2, 3); only a strip set with all three colors is laid out as secondary 1, main, secondary 2
        //     (default: pink, yellow, blue), see StripOrder. The colors are never mixed (yellow + blue would look green, a fourth word that was never searched);
        //   - as a highlighter mark behind the word in the panel title, and behind the item lines of the item lists (a hovered place, a hovered
        //     chest: an item has the color of the first word that found it). The text itself keeps the game's normal color;
        //   - on the word itself in the status line of the lower left corner (on the world, where the bright color reads well).
        // Every color is the same everywhere, so a word, its chests and its items are matched at a glance.

        private static readonly string[] DefaultWordColors = { "Yellow", "Pink", "Sky Blue" };

        /// <summary>How strong the highlighter mark behind a word or an item line is (of the panel's own fade).</summary>
        private const float WordMarkAlpha = 0.42f;

        // how many words of the last search have a color (2 or 3); 0 = a one-word search (or nothing found): one color, as always
        private int highlightWordCount;

        // per search word (index in the search's word list): its color number (0 = word 1 ...), -1 = no color (it found nothing)
        private int[] termWordSlot = Array.Empty<int>();

        // the color numbers of every highlighted chest (the words it holds, in the order typed); kept when the highlights are cleared, so a
        // "Keep highlights" box that is checked again brings the chests back in their colors
        private readonly Dictionary<Chest, int[]> chestWordSlots = new(ReferenceEqualityComparer.Instance);

        // the words of the status line, in color order ("Highlighting: 'fruit', 'fish' (12 chests)")
        private List<string> highlightLabelWords = new();

        // the color number of every quoted word of the title (same order as notifyEmphasis; -1 = normal color), and the title it belongs to:
        // a title built by anything else (a failed search) is never colored
        private int[] titleEmphasisSlots = Array.Empty<int>();
        private string coloredTitle = "";

        // the color number of every item name found by the last search (see OwnerSlot in ScanChests): an item that a category word AND an item
        // word of the same search both find ("fruit, mango": Mango) has the color of the item word, everywhere (its chests, its item line)
        private Dictionary<string, int> itemWordSlots = new();

        /// <summary>
        /// The left-to-right order of the color strips of a chest (or of the player, the backpack): the color numbers ascending (word 1, word 2, word 3),
        /// except a strip set that has all three colors: secondary 1, main, secondary 2 (default pink, yellow, blue). One or two colors keep the
        /// plain order (two: main, secondary: default yellow, pink).
        /// </summary>
        private static int[] StripOrder(IEnumerable<int> slots)
        {
            int[] ordered = slots.Distinct().OrderBy(slot => slot).ToArray();
            return ordered.Length == 3 && ordered[0] == 0 && ordered[1] == 1 && ordered[2] == 2
                ? new[] { 1, 0, 2 }
                : ordered;
        }

        private string WordColorName(int slot)
        {
            string? name = slot switch { 0 => Config.HighlightColor, 1 => Config.SecondaryColor1, 2 => Config.SecondaryColor2, _ => null };
            return NormalizeColorName(name, DefaultWordColors[Math.Clamp(slot, 0, DefaultWordColors.Length - 1)]);
        }

        /// <summary>The flash color of a word (chests, marks, status line).</summary>
        private Color WordFlashColor(int slot)
        {
            return HighlightColors.TryGetValue(WordColorName(slot), out Color color) ? color : Color.Yellow;
        }

        /// <summary>The mark behind an item line of an item list (the flash color of the first word that found the item), or null for none.</summary>
        private Color? ItemLineMark(string rowKey)
        {
            if (highlightWordCount <= 0)
                return null;
            if (itemWordSlots.TryGetValue(RowName(rowKey), out int owner))
                return WordFlashColor(owner) * WordMarkAlpha;
            int term = PlaceItemOrderOf(rowKey).Term;   // an item the search did not see in a chest (put in later): the first word that found it
            if (term < 0 || term >= termWordSlot.Length || termWordSlot[term] < 0)
                return null;
            return WordFlashColor(termWordSlot[term]) * WordMarkAlpha;
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

        /// <summary>The running mod (the search box asks it how a text splits into search words, see GetIgnoredSearchTerms).</summary>
        internal static ModEntry? Instance { get; private set; }

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();
            Config.HighlightTimeoutSeconds = NormalizeTimeoutSeconds(Config.HighlightTimeoutSeconds);
            // opacity is 0-100 in steps of 20; fix up hand-edited config.json values
            Config.HighlightOpacityPercent = (int)Math.Round(Math.Clamp(Config.HighlightOpacityPercent, 0, 100) / 20.0) * 20;
            Config.HighlightColor = NormalizeColorName(Config.HighlightColor, DefaultWordColors[0]);
            Config.SecondaryColor1 = NormalizeColorName(Config.SecondaryColor1, DefaultWordColors[1]);
            Config.SecondaryColor2 = NormalizeColorName(Config.SecondaryColor2, DefaultWordColors[2]);

            helper.ConsoleCommands.Add("find_item", "Search your chests like the search box and list where everything is (place, container, tile, count, stars). " + FindItemUsage, OnFindItem);
            helper.ConsoleCommands.Add("find_item_audit", "Developer check over the whole item list: drop-down names missing from their hover tip, and item-name typos that 'Did you mean' cannot correct.", OnAudit);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => { IsKeepHighlightsLocked = false; EndKeepFade(); ClearHighlights(); };
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.Saving += (_, _) => SaveSearchHistory();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => searchHistory.Clear();   // history belongs to a save file: do not leak it into the next one
            helper.Events.Input.ButtonPressed += OnHudCopyInput;     // FIRST: the "Copy" bubble of the result panel's title words takes its clicks before anything else
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.Input.ButtonPressed += OnKeepBoxClicked;
            helper.Events.Input.ButtonPressed += OnSuggestionClicked;
            helper.Events.Input.ButtonPressed += OnResultCloseClicked;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.RenderedHud += OnRenderedHud;
            helper.Events.Display.RenderedHud += OnRenderedHudChestTip;   // after OnRenderedHud: its tip is drawn on top of the panel
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Display.RenderedActiveMenu += OnRenderedFoundToast;   // the "Found 'Apple'!" note over the opened chest
            helper.Events.Display.RenderedHud += OnRenderedFoundToastHud;        // ... and for its last moments after the chest is closed again
            helper.Events.Display.RenderedHud += OnRenderedHudEdgeArrow;         // red arrow at the screen edge when every highlighted chest here is off screen
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        private const string SearchKeyFieldId = "SearchKey";   // GMCM field id of the search key, for OnFieldChanged

        // Optional integration: lets players change the key in-game via Generic Mod Config Menu.
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // a search key that clashes with a game control or another mod's key: a warning in the SMAPI console (the red lines of the config page
            // are only seen by players with GMCM). Same rules as those lines, see KeyConflictChecker.
            try
            {
                foreach (string conflict in new KeyConflictChecker(Helper, ModManifest.UniqueID).CheckNow(Config.SearchKey))
                    Monitor.Log($"Search key ({Config.SearchKey}): {conflict} Change it in config.json or in Generic Mod Config Menu.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check the search key for conflicts: {ex.Message}", LogLevel.Trace);
            }

            var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null) return;

            api.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            api.AddSectionTitle(ModManifest, () => "General");

            api.AddKeybindList(
                mod: ModManifest,
                getValue: () => Config.SearchKey,
                setValue: value => Config.SearchKey = value,
                name: () => "Search key",
                tooltip: () => "Press this key (or key combination) in-game to open the chest search box. Default: LeftShift + T.",
                fieldId: SearchKeyFieldId
            );

            // red lines under the search key when it is the same key as a game control or another mod's key (nothing shown otherwise)
            var keyConflicts = new KeyConflictChecker(Helper, ModManifest.UniqueID);
            api.AddComplexOption(
                mod: ModManifest,
                name: () => "",
                draw: keyConflicts.Draw,
                beforeMenuOpened: () => keyConflicts.Reload(Config.SearchKey),
                afterReset: () => keyConflicts.SetKey(Config.SearchKey),
                height: () => keyConflicts.Height
            );
            api.OnFieldChanged(ModManifest, (fieldId, value) =>
            {
                if (fieldId == SearchKeyFieldId && value is StardewModdingAPI.Utilities.KeybindList key)
                    keyConflicts.SetKey(key);   // check the new key at once, before it is saved
            });

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
                tooltip: () => "Real-world time before chest highlights disappear by themselves. Never: they stay until you open a found chest. Not affected by in-game time speed. Default: 3 minutes.",
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
            // at 0% nothing is drawn: say so right under the slider, so the player does not think the search is broken
            opacitySlider.Note = value => value <= 0 ? "0%: nothing flashes. The arrows still show the way." : null;

            api.AddComplexOption(
                mod: ModManifest,
                name: () => "Highlight opacity",
                draw: opacitySlider.Draw,
                tooltip: () => "How strong the flashing highlight on chests is. At 0% no highlight is drawn, at 100% it is fully opaque at the brightest point. Default: 60%.",
                beforeMenuOpened: opacitySlider.Reload,
                beforeSave: opacitySlider.Commit,
                afterReset: opacitySlider.Reload,
                beforeMenuClosed: opacitySlider.CancelDrag,
                height: () => opacitySlider.Height
            );

            // the three flash colors: a row of color boxes each (one click picks a color, the name shows beside the row)
            var colorChoices = HighlightColorNames.Select(name => (name, HighlightColors[name])).ToList();
            string[] colorNames = { "Flash color", "Secondary color 1", "Secondary color 2" };
            string[] colorTips =
            {
                "Flash color of the found chests. When you search for several words (\"fruit, fish\"), the first word uses this color. Default: Yellow.",
                "Flash color of the second word when you search for several words (\"fruit, fish\"): its chests, and the marks behind it and its items in the result panel. Default: Pink.",
                "Flash color of the third word when you search for three words (\"fruit, fish, gem\"): its chests, and the marks behind it and its items in the result panel. Default: Sky Blue."
            };
            var pickers = new ColorSwatchPicker[3];
            for (int slot = 0; slot < 3; slot++)
            {
                int colorSlot = slot;
                var picker = pickers[slot] = new ColorSwatchPicker(
                    readConfig: () => WordColorName(colorSlot),
                    writeConfig: value =>
                    {
                        string name = NormalizeColorName(value, DefaultWordColors[colorSlot]);
                        if (colorSlot == 0) Config.HighlightColor = name;
                        else if (colorSlot == 1) Config.SecondaryColor1 = name;
                        else Config.SecondaryColor2 = name;
                    },
                    choices: colorChoices
                );

                // two words with the same color cannot be told apart: a small note under the row names the other one (only a note, nothing is blocked)
                picker.SameAsNote = () =>
                {
                    for (int other = 0; other < pickers.Length; other++)
                    {
                        if (other != colorSlot && pickers[other] != null && string.Equals(pickers[other].Picked, picker.Picked, StringComparison.OrdinalIgnoreCase))
                            return "Same as " + colorNames[other];
                    }
                    return null;
                };

                api.AddComplexOption(
                    mod: ModManifest,
                    name: () => colorNames[colorSlot],
                    draw: picker.Draw,
                    tooltip: () => colorTips[colorSlot],
                    beforeMenuOpened: picker.Reload,
                    beforeSave: picker.Commit,
                    afterReset: picker.Reload,
                    height: () => picker.Height
                );
            }

            api.AddSectionTitle(
                ModManifest,
                () => "Backpack search",
                () => "Your backpack is searched just like a chest. If it holds a match, you can make your character flash and show an arrow above your head.");

            // the backpack is searched like a chest (the player flashes, an arrow stands above the player's head, hovering the player lists the
            // items): the flash and the arrow can each be switched off; the search itself and the hover list stay
            api.AddBoolOption(
                mod: ModManifest,
                getValue: () => Config.FlashPlayer,
                setValue: value => Config.FlashPlayer = value,
                name: () => "Flash for backpack match",
                tooltip: () => "When your backpack holds something you searched for, your character flashes like a chest does. Turn this off to stop the flashing. Hovering the mouse over your character still lists the matching items in your backpack while the highlights last. The flash strength is the same as the chests' (Highlight opacity). Default: On."
            );

            api.AddBoolOption(
                mod: ModManifest,
                getValue: () => Config.ShowPlayerArrow,
                setValue: value => Config.ShowPlayerArrow = value,
                name: () => "Arrow for backpack match",
                tooltip: () => "When your backpack holds something you searched for, a red arrow with the word \"Backpack\" stands above your character's head. Turn this off to hide the arrow and its text. The arrows that show the way to chests are not affected. Default: On."
            );
        }

        // A new day (the player slept, passed out, or a save was loaded): the result panel of yesterday's search is closed and its highlights are
        // gone, whatever their countdown said. EXCEPT a checked "Keep highlights": then the highlights and the checked box stay exactly as they
        // were and go on flashing today, until the player unchecks the box (or opens the search box, or starts a new search).
        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            CloseResultPanel();
            fastFlashPhaseOffset = null;

            if (IsKeepHighlightsLocked)
                return;   // kept on purpose: the lock, the box and every highlighted chest stay

            keepBoxDismissed = true;   // no box of yesterday's result comes back
            EndKeepFade();             // a box that was still fading out is gone too
            ClearHighlights();
        }

        // Loads this save file's search history (at most 5 unpinned words plus every pinned one, no duplicates).
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            searchHistory.Clear();

            // build the game's item list now, while the save is loading, so the first "Showing results for:" tip does not make the game stutter
            try
            {
                GetGameItemCatalog();

                // the item names that have brackets of their own ("Crop Tank Top (F)"): typed whole, their brackets are allowed (see IsBracketItemName)
                bracketItemNames = null;
                List<string> withBrackets = GetBracketItemNames();
                if (withBrackets.Count > 0)
                    Monitor.Log($"Item names with brackets (searchable as they are): {string.Join(", ", withBrackets)}", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not prepare the item list for the tips: {ex.Message}", LogLevel.Trace);
            }

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
                    if (item.Length == 0)
                        continue;
                    if (!entry.Pinned && searchHistory.Count(h => !h.Pinned) >= SearchMenu.MaxHistory)
                        continue;   // only unpinned words count towards the limit; pinned ones are always kept
                    if (searchHistory.Any(h => string.Equals(h.Word, item, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    searchHistory.Add(new SearchHistoryEntry { Word = item, Quality = entry.Quality, Pinned = entry.Pinned });
                }

                SearchMenu.SortPinnedFirst(searchHistory);   // pinned words always come first
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
                    Entries = searchHistory.Select(h => new SearchHistoryEntry { Word = h.Word, Quality = h.Quality, Pinned = h.Pinned }).ToList()
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

            // Opening the search box changes NOTHING of the current result: the highlights, the "Keep highlights" lock and its box all stay as they are.
            // Only a search that really runs (Enter, the magnifier, a clicked word: ScanChests) replaces them, found or not. Esc / the red X / the
            // search key close the box and the old highlights are still there. (A search refused by the box's checks, or one that stays open with
            // "no matching quality", does not run, so it does not touch them either.)

            lastSearchKeyTick = Game1.ticks;
            Helper.Input.SuppressActiveKeybinds(Config.SearchKey);
            // e.Button is the key that completed the combo (e.g. T in LeftShift + T, or LeftShift if T was already held)
            Game1.activeClickableMenu = new SearchMenu(ScanChests, searchHistory, SaveSearchHistory, GetLeakedChar(e.Button), CreateSuggestionProvider(), getWordTip: GetTermTip, getSpelling: CreateSpellingProvider(), textHasNoQuality: TextRefersOnlyToNoQualityItems);
        }

        // Prepares the auto-complete for one opening of the search box. If anything goes wrong this returns null and the
        // search box simply works without a drop-down, so a problem here can never break searching itself.
        private Func<string, IReadOnlyList<Suggestion>>? CreateSuggestionProvider()
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();                          // game item names (built once, rebuilt if the language or item data changed)
                suggestionIndex.SetOwnedItems(GetChestItemStacks());    // what is in the chests right now, with how many of each (slot 3 of every suggestion list: "the item you hold the most of")
                SearchMenu.SetOwnedIconSamples(GetChestIconSamples());   // and how each of them really looks (the drop-down draws a held item with its own icon)

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

        // ===================== WHAT IS SEARCHED: EVERY CONTAINER THE PLAYER CAN OPEN AND STORE IN =====================
        // Everything the search looks into comes from ONE place, GetAllContainers. Per location:
        //   - every placed chest (Chest, Stone Chest, Big Chest, Big Stone Chest, Junimo Chest, Mini-Fridge ...: all Chest objects in location.objects);
        //   - every Auto-Grabber (its storage is the Chest it holds);
        //   - the built-in fridge of a house (farmhouse, cabins, Ginger Island farmhouse);
        //   - the storage chests of buildings that the player can open and take from: the Mill's output box, the Junimo Hut's bag (and the same
        //     kind of chest of a building added by another mod). The Mill's INPUT (where wheat is dropped in) is not storage and is left out;
        //   - every dresser (they keep clothing, hats, shoes and rings). A dresser is no Chest: its items are mirrored into a stand-in Chest that the
        //     player never sees (see dresserProxies), so the rest of the mod can treat it like any other container. Fish tanks are not storage.
        // Machines that only LOOK like storage (kegs, preserves jars, crystalariums ...) are never containers.
        // Nothing is "checked for" in advance: a location without a fridge, a farm without a mill simply yields nothing, at no extra cost.
        // Everything after the collection (matching, highlights, arrow, place list, hover tips, "Found" note, status line) works with
        // "location + tile + chest". What differs per kind is kept in containerInfo: the kind (for the words) and the footprint (the tiles the
        // container covers: a fridge is two tiles tall, a building several tiles wide). Highlight, hover tip, "is it on screen" and "was it opened"
        // all use the footprint.

        /// <summary>One kind of container and its words. Chest and big chest share the arrow word and the hover section ("Chest").</summary>
        private sealed class ContainerKind
        {
            public string Key { get; }
            public string Singular { get; }      // in titles: "Found 1 fridge", "Found 1 Auto-Grabber"
            public string Plural { get; }        // "Found 2 fridges"
            public string ArrowName { get; }     // the arrow's label: "Fridge: Farmhouse"; chests of every size are just "Chest"
            public bool IsSpecial { get; }       // not a chest: named in brackets in the place list, its own section in the place's item list
            public int Order { get; }            // order of the sections in a place's item list (chests first)

            public ContainerKind? Family { get; }   // the wider kind it belongs to: a mini-fridge is a fridge (null = its own family)

            public ContainerKind(string key, string singular, string plural, string arrowName, bool isSpecial, int order, ContainerKind? family = null)
            {
                Key = key;
                Singular = singular;
                Plural = plural;
                ArrowName = arrowName;
                IsSpecial = isSpecial;
                Order = order;
                Family = family;
            }

            /// <summary>The kind that names a mix of this kind with others of its family (fridge + mini-fridge -> fridge).</summary>
            public ContainerKind FamilyKind => Family ?? this;

            /// <summary>The section key in a place's item list: every chest size is one section ("Chest"); fridge and mini-fridge are two ("Fridge" first).</summary>
            public string SectionKey => IsSpecial ? Key : "chest";
        }

        private static readonly ContainerKind ChestKind = new("chest", "chest", "chests", "Chest", false, 0);
        private static readonly ContainerKind BigChestKind = new("bigchest", "big chest", "big chests", "Chest", false, 0);
        private static readonly ContainerKind FridgeKind = new("fridge", "fridge", "fridges", "Fridge", true, 1);
        private static readonly ContainerKind MiniFridgeKind = new("minifridge", "mini-fridge", "mini-fridges", "Mini-Fridge", true, 2, FridgeKind);
        private static readonly ContainerKind AutoGrabberKind = new("autograbber", "Auto-Grabber", "Auto-Grabbers", "Auto-Grabber", true, 3);
        private static readonly ContainerKind JunimoHutKind = new("junimohut", "Junimo Hut", "Junimo Huts", "Junimo Hut", true, 4);
        private static readonly ContainerKind MillKind = new("mill", "Mill", "Mills", "Mill", true, 5);
        private static readonly ContainerKind DresserKind = new("dresser", "Dresser", "Dressers", "Dresser", true, 6);
        private static readonly ContainerKind MannequinKind = new("mannequin", "mannequin", "mannequins", "Mannequin", true, 7);
        private static readonly ContainerKind LostAndFoundKind = new("lostandfound", "Lost and Found box", "Lost and Found boxes", "Lost and Found", true, 8);

        /// <summary>The kinds of the storage chests of other mods' buildings, one per building type (named like the building).</summary>
        private readonly Dictionary<string, ContainerKind> buildingKinds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What is known about one container: its kind, the tiles it covers, and its own full name (the title of its hover tip).</summary>
        private readonly struct ContainerInfo
        {
            public readonly ContainerKind Kind;
            public readonly Rectangle Footprint;   // in tiles
            public readonly string FullName;

            public ContainerInfo(ContainerKind kind, Rectangle footprint, string fullName)
            {
                Kind = kind;
                Footprint = footprint;
                FullName = fullName;
            }
        }

        /// <summary>Every container met by GetAllContainers -> what is known about it (a chest that was never met is a plain one-tile chest).</summary>
        private readonly Dictionary<Chest, ContainerInfo> containerInfo = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every dresser -> the stand-in Chest that mirrors its items (refreshed whenever the containers are listed or the dresser is opened).</summary>
        private readonly Dictionary<StorageFurniture, Chest> dresserProxies = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every mannequin -> the stand-in Chest that mirrors what it wears (hat, shirt, pants, boots), like a dresser's.</summary>
        private readonly Dictionary<Mannequin, Chest> mannequinProxies = new(ReferenceEqualityComparer.Instance);

        /// <summary>The stand-in Chest of the Lost and Found box in the Mayor's Manor (team.returnedDonations), null until it is first needed.</summary>
        private Chest? lostAndFoundProxy;

        /// <summary>The Lost and Found box: the tile the player clicks in the Mayor's Manor (the map's "LostAndFound" action), and the box drawn above it.</summary>
        private static readonly Vector2 LostAndFoundTile = new(4, 5);

        /// <summary>
        /// Every searchable container of the given locations, each once, with its tile (for a building or a dresser: its top left tile) and the
        /// location's place label.
        /// </summary>
        private IEnumerable<(GameLocation location, Vector2 position, Chest chest, string label)> GetAllContainers(IEnumerable<(GameLocation location, string label)> locations)
        {
            var seen = new HashSet<Chest>(ReferenceEqualityComparer.Instance);
            var found = new List<(GameLocation location, Vector2 position, Chest chest, string label)>();

            void Add(GameLocation location, Vector2 tile, Chest chest, string label, ContainerKind kind, Rectangle footprint, string fullName)
            {
                if (!seen.Add(chest))
                    return;
                containerInfo[chest] = new ContainerInfo(kind, footprint, fullName);
                found.Add((location, tile, chest, label));
            }

            foreach (var (location, label) in locations)
            {
                found.Clear();
                try
                {
                    // placed chests (every size, Junimo chests, mini-fridges: a placed chest that keeps food cold is a mini-fridge) and Auto-Grabbers
                    foreach (var pair in location.objects.Pairs)
                    {
                        var oneTile = new Rectangle((int)pair.Key.X, (int)pair.Key.Y, 1, 1);
                        if (pair.Value is Chest chest)
                        {
                            ContainerKind kind = chest.fridge.Value ? MiniFridgeKind : IsBigChest(chest) ? BigChestKind : ChestKind;
                            Add(location, pair.Key, chest, label, kind, oneTile, string.IsNullOrWhiteSpace(chest.DisplayName) ? "Chest" : chest.DisplayName);
                        }
                        else if (pair.Value.QualifiedItemId == AutoGrabberId && pair.Value.heldObject.Value is Chest grabbed)
                        {
                            Add(location, pair.Key, grabbed, label, AutoGrabberKind, oneTile,
                                string.IsNullOrWhiteSpace(pair.Value.DisplayName) ? "Auto-Grabber" : pair.Value.DisplayName);
                        }
                    }

                    // the built-in fridge of a house: its tile is the lower half (the half the player clicks), the upper half is the tile above
                    Chest? fridge = location.GetFridge();   // null where there is no (unlocked) kitchen fridge
                    Point? fridgeTile = location.GetFridgePosition();
                    if (fridge != null && fridgeTile is Point tile)
                        Add(location, new Vector2(tile.X, tile.Y), fridge, label, FridgeKind, new Rectangle(tile.X, tile.Y - 1, 1, 2), "Fridge");

                    // the storage chests of buildings: the Mill's output box, the Junimo Hut's bag ...
                    foreach (Building building in location.buildings)
                    {
                        foreach (Chest storage in GetBuildingStorage(building))
                        {
                            ContainerKind kind = GetBuildingKind(building);
                            var footprint = new Rectangle(building.tileX.Value, building.tileY.Value, Math.Max(1, building.tilesWide.Value), Math.Max(1, building.tilesHigh.Value));
                            Add(location, new Vector2(footprint.X, footprint.Y), storage, label, kind, footprint, kind.Singular);
                        }
                    }

                    // dressers (not fish tanks): their items, mirrored into a stand-in Chest
                    foreach (Furniture furniture in location.furniture)
                    {
                        if (furniture is not StorageFurniture dresser || furniture is FishTankFurniture)
                            continue;

                        Chest proxy = GetDresserProxy(dresser);
                        Rectangle box = dresser.GetBoundingBox();
                        var footprint = new Rectangle(box.X / 64, box.Y / 64, Math.Max(1, (box.Width + 63) / 64), Math.Max(1, (box.Height + 63) / 64));
                        Add(location, new Vector2(footprint.X, footprint.Y), proxy, label, DresserKind, footprint,
                            string.IsNullOrWhiteSpace(dresser.DisplayName) ? "Dresser" : dresser.DisplayName);
                    }

                    // mannequins (1.6): what they wear (hat, shirt, pants, boots), mirrored into a stand-in Chest like a dresser's. One tile, two tall.
                    foreach (var pair in location.objects.Pairs)
                    {
                        if (pair.Value is not Mannequin mannequin)
                            continue;
                        var footprint = new Rectangle((int)pair.Key.X, (int)pair.Key.Y - 1, 1, 2);
                        Add(location, pair.Key, GetMannequinProxy(mannequin), label, MannequinKind, footprint,
                            string.IsNullOrWhiteSpace(mannequin.DisplayName) ? "Mannequin" : mannequin.DisplayName);
                    }

                    // the Lost and Found box in the Mayor's Manor (items left at the fair's grange display and the like): a container while it holds something
                    if (location.Name == "ManorHouse" && Game1.player?.team.returnedDonations.Any(i => i != null) == true)
                    {
                        Vector2 boxTile = LostAndFoundTile;
                        Add(location, boxTile, GetLostAndFoundProxy(), label, LostAndFoundKind, new Rectangle((int)boxTile.X, (int)boxTile.Y - 1, 1, 2), "Lost and Found");
                    }
                }
                catch (Exception ex)
                {
                    Monitor.LogOnce($"Could not list every container of {location.NameOrUniqueName}: {ex.Message}", LogLevel.Trace);
                }

                foreach (var container in found)
                    yield return container;
            }
        }

        /// <summary>Every searchable container of every location (see GetAllContainers).</summary>
        private IEnumerable<(GameLocation location, Vector2 position, Chest chest, string label)> GetAllContainers()
        {
            return GetAllContainers(GetAllLocations());
        }

        /// <summary>The qualified item id of the Auto-Grabber.</summary>
        private const string AutoGrabberId = "(BC)165";

        /// <summary>True for a big chest (Big Chest, Big Stone Chest): only these are "big chests" in a title.</summary>
        private static bool IsBigChest(Chest chest)
        {
            string id = chest.ItemId ?? "";
            return id.Equals("BigChest", StringComparison.OrdinalIgnoreCase) || id.Equals("BigStoneChest", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The chests of a building the player can open and take from: the ones its data marks as storage or as an output to collect (the Mill's box,
        /// the Junimo Hut's bag), never an input (the Mill's hopper). A Junimo Hut's bag is also asked for directly, in case its data says nothing.
        /// </summary>
        private static IEnumerable<Chest> GetBuildingStorage(Building building)
        {
            var result = new List<Chest>();
            var data = building.GetData();
            if (data?.Chests != null)
            {
                foreach (var chestData in data.Chests)
                {
                    if (chestData == null || chestData.Type == StardewValley.GameData.Buildings.BuildingChestType.Load)
                        continue;
                    Chest? chest = building.GetBuildingChest(chestData.Id);
                    if (chest != null && !result.Contains(chest))
                        result.Add(chest);
                }
            }

            if (building is JunimoHut hut && hut.GetOutputChest() is Chest bag && !result.Contains(bag))
                result.Add(bag);

            return result;
        }

        /// <summary>The kind of a building's storage: Mill, Junimo Hut, or (another mod's building) a kind named like the building.</summary>
        private ContainerKind GetBuildingKind(Building building)
        {
            string type = building.buildingType.Value ?? "";
            if (building is JunimoHut || type.Equals("Junimo Hut", StringComparison.OrdinalIgnoreCase))
                return JunimoHutKind;
            if (type.Equals("Mill", StringComparison.OrdinalIgnoreCase))
                return MillKind;

            if (!buildingKinds.TryGetValue(type, out ContainerKind? kind))
            {
                string name = type;
                try
                {
                    string? dataName = building.GetData()?.Name;
                    if (!string.IsNullOrWhiteSpace(dataName))
                        name = TokenParser.ParseText(dataName);
                }
                catch (Exception)
                {
                    // keep the building type as its name
                }

                if (string.IsNullOrWhiteSpace(name))
                    name = "Building";
                buildingKinds[type] = kind = new ContainerKind("building:" + type, name, name + "s", name, true, 9);
            }
            return kind;
        }

        /// <summary>The stand-in Chest of a dresser, with the dresser's items copied in (the same item objects, nothing is moved or cloned).</summary>
        private Chest GetDresserProxy(StorageFurniture dresser)
        {
            if (!dresserProxies.TryGetValue(dresser, out Chest? proxy))
                dresserProxies[dresser] = proxy = new Chest(false);

            proxy.Items.Clear();
            foreach (Item? item in dresser.heldItems)
            {
                if (item != null)
                    proxy.Items.Add(item);
            }
            return proxy;
        }

        /// <summary>The dresser a stand-in Chest belongs to (null for every real container).</summary>
        private StorageFurniture? GetDresserOf(Chest chest)
        {
            foreach (var pair in dresserProxies)
            {
                if (ReferenceEquals(pair.Value, chest))
                    return pair.Key;
            }
            return null;
        }

        /// <summary>The stand-in Chest of a mannequin, with what it wears copied in (the same item objects, nothing is moved or cloned).</summary>
        private Chest GetMannequinProxy(Mannequin mannequin)
        {
            if (!mannequinProxies.TryGetValue(mannequin, out Chest? proxy))
                mannequinProxies[mannequin] = proxy = new Chest(false);

            proxy.Items.Clear();
            foreach (Item? item in new Item?[] { mannequin.hat.Value, mannequin.shirt.Value, mannequin.pants.Value, mannequin.boots.Value })
            {
                if (item != null)
                    proxy.Items.Add(item);
            }
            return proxy;
        }

        /// <summary>The mannequin a stand-in Chest belongs to (null for every other container).</summary>
        private Mannequin? GetMannequinOf(Chest chest)
        {
            foreach (var pair in mannequinProxies)
            {
                if (ReferenceEquals(pair.Value, chest))
                    return pair.Key;
            }
            return null;
        }

        /// <summary>The stand-in Chest of the Lost and Found box, with its items copied in (the same item objects, nothing is moved or cloned).</summary>
        private Chest GetLostAndFoundProxy()
        {
            lostAndFoundProxy ??= new Chest(false);
            lostAndFoundProxy.Items.Clear();
            if (Game1.player != null)
            {
                foreach (Item? item in Game1.player.team.returnedDonations)
                {
                    if (item != null)
                        lostAndFoundProxy.Items.Add(item);
                }
            }
            return lostAndFoundProxy;
        }

        /// <summary>The items a container holds right now (the stand-in of a dresser, a mannequin or the Lost and Found box is refreshed first).</summary>
        private IEnumerable<Item> CurrentItems(Chest chest)
        {
            StorageFurniture? dresser = GetDresserOf(chest);
            if (dresser != null)
                GetDresserProxy(dresser);
            else if (GetMannequinOf(chest) is Mannequin mannequin)
                GetMannequinProxy(mannequin);
            else if (lostAndFoundProxy != null && ReferenceEquals(chest, lostAndFoundProxy))
                GetLostAndFoundProxy();
            return chest.Items.Where(i => i != null).Select(i => i!);
        }

        /// <summary>What is known about a container (a chest that was never listed counts as a plain one-tile chest at <paramref name="position"/>).</summary>
        private ContainerInfo InfoOf(Chest chest, Vector2 position)
        {
            if (containerInfo.TryGetValue(chest, out ContainerInfo info))
                return info;
            return new ContainerInfo(ChestKind, new Rectangle((int)position.X, (int)position.Y, 1, 1), string.IsNullOrWhiteSpace(chest.DisplayName) ? "Chest" : chest.DisplayName);
        }

        /// <summary>The kind of a container (plain chest if it was never listed).</summary>
        private ContainerKind KindOf(Chest chest)
        {
            return containerInfo.TryGetValue(chest, out ContainerInfo info) ? info.Kind : ChestKind;
        }

        /// <summary>
        /// The ONE kind that names a set of kinds: the kind itself when there is one; the family when they are all of one family (a fridge and a
        /// mini-fridge -> fridge); otherwise chest (every other mix, a chest and a big chest included).
        /// </summary>
        private static ContainerKind NamingKind(IEnumerable<ContainerKind> kinds)
        {
            var distinct = kinds.Distinct().ToList();
            if (distinct.Count == 1)
                return distinct[0];
            var families = distinct.Select(k => k.FamilyKind).Distinct().ToList();
            return distinct.Count > 1 && families.Count == 1 && families[0].IsSpecial ? families[0] : ChestKind;
        }

        /// <summary>
        /// The word for a number of containers in a title or the status line: when ALL of them are of the same kind, that kind's word (singular for
        /// one, plural for more: "fridge", "2 fridges", "mini-fridge", "Auto-Grabber", "big chests" only when every one is a big chest); fridges
        /// and mini-fridges together are "fridges"; any other mix (a chest and a big chest included) simply "chest" / "chests".
        /// </summary>
        private string ContainerWord(IReadOnlyCollection<Chest> containers)
        {
            ContainerKind kind = containers.Count == 0 ? ChestKind : NamingKind(containers.Select(KindOf));
            return containers.Count == 1 ? kind.Singular : kind.Plural;
        }

        /// <summary>The arrow's label word for the containers of one target: the arrow name of their naming kind ("Fridge", "Mini-Fridge", "Chest" ...).</summary>
        private string ArrowWord(IEnumerable<Chest> containers)
        {
            var list = containers.ToList();
            return list.Count == 0 ? ChestKind.ArrowName : NamingKind(list.Select(KindOf)).ArrowName;
        }

        /// <summary>The tiles a highlighted container covers (see ContainerInfo.Footprint).</summary>
        private Rectangle FootprintOf(Chest chest, Vector2 position)
        {
            return InfoOf(chest, position).Footprint;
        }

        /// <summary>The same tiles in world pixels.</summary>
        private Rectangle FootprintPixels(Chest chest, Vector2 position)
        {
            Rectangle tiles = FootprintOf(chest, position);
            return new Rectangle(tiles.X * 64, tiles.Y * 64, tiles.Width * 64, tiles.Height * 64);
        }

        /// <summary>The middle of a container's footprint, as a tile position (what the arrow points at).</summary>
        private Vector2 FootprintCenterTile(Chest chest, Vector2 position)
        {
            Rectangle tiles = FootprintOf(chest, position);
            return new Vector2(tiles.X + tiles.Width / 2f - 0.5f, tiles.Y + tiles.Height / 2f - 0.5f);
        }

        // The display name and stack size of every item in every chest (same containers as ScanChests). The suggestion index adds the stacks up
        // per name, so "Did you mean" and the drop-down know which related item the player holds the most of.
        /// <summary>
        /// One real item of every name the chests hold (suggestion key -> a one-piece copy without star), for the icons of the drop-down: a held item is
        /// drawn exactly as it lies in the chest, like the hover tips of the places and chests do. That is what gives a built item its colour
        /// ("Apple Wine" is a red bottle, not the plain purple Wine): the game makes these from an ingredient, and only the real item knows it.
        /// The same goes for jelly, juice, pickles, roe, honey, dried fruit, smoked fish, specific bait, dyed clothes and items of other mods.
        /// </summary>
        private Dictionary<string, Item> GetChestIconSamples()
        {
            var samples = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var (_, _, chest, _) in GetAllContainers())
            {
                foreach (Item item in ItemsWithNested(CurrentItems(chest)))
                {
                    string key = SuggestionIndex.MakeKey(GetShownItemName(item));
                    if (key.Length > 0 && !samples.ContainsKey(key))
                        samples[key] = MakeIconSample(item);   // the first one found (two different items of one name, e.g. both Eggs: one of them, as before)
                }
            }
            return samples;
        }

        private IEnumerable<(string DisplayName, int Stack)> GetChestItemStacks()
        {
            foreach (var (_, _, chest, _) in GetAllContainers())
            {
                foreach (Item item in ItemsWithNested(chest.Items))
                    yield return (GetShownItemName(item), Math.Max(1, item.Stack));
            }
        }

        // Opening ANY container that contains the target clears ALL highlights (unless "Keep highlights" is locked), and shows a small "Found 'Apple'!"
        // note naming what THIS container really holds of the search (see ShowFoundToast). Opening one that is not a target only shows the light
        // "No target items here" note (if it really holds nothing of the search). Which container was opened:
        //   - a chest / fridge / building chest opens the game's chest menu (ItemGrabMenu), usually with the container itself as its context;
        //   - otherwise (an Auto-Grabber, a building ...) the tile the player clicked tells: the container whose footprint holds that tile;
        //   - a dresser opens its own clothing menu (a ShopMenu whose source is the dresser).
        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (!AnyHighlight) return;

            // the backpack is "opened" by opening the inventory page of the game menu, and it ends like a chest does: not locked, every highlight
            // goes; locked ("Keep highlights" checked), only the backpack's flash goes and the chests stay
            if (e.NewMenu is GameMenu gameMenu && gameMenu.currentTab == GameMenu.inventoryTab)
            {
                if (!highlightBackpack)
                    return;
                if (IsKeepHighlightsLocked)
                {
                    highlightBackpack = false;
                    if (highlightedChests.Count == 0)
                        ClearHighlights();   // that was the last one
                    return;
                }
                ClearHighlights();
                return;
            }

            Chest? opened = null;
            if (e.NewMenu is ShopMenu shop && shop.source is StorageFurniture dresser)
            {
                opened = dresserProxies.TryGetValue(dresser, out Chest? proxy) ? proxy : GetDresserProxy(dresser);
            }
            else if (e.NewMenu is ShopMenu lostAndFound && lostAndFound.ShopId == "ReturnedDonations" && lostAndFoundProxy != null)
            {
                opened = lostAndFoundProxy;   // the Lost and Found box opens as a shop of its items
            }
            else if (e.NewMenu is ItemGrabMenu menu)
            {
                if (menu.context is Chest context && (containerInfo.ContainsKey(context) || highlightedChests.Any(h => ReferenceEquals(h.chest, context))))
                    opened = context;
                else
                    opened = FindContainerAtTile(Game1.currentLocation, lastGrabTile);
            }

            if (opened == null)
                return;   // not one of the containers the search looks into (a shipping bin, a fishing treasure ...)

            bool isTarget = highlightedChests.Any(h => ReferenceEquals(h.chest, opened));
            if (!isTarget)
            {
                // not a target: a short grey "No target items here" in the lower left corner, if it really holds nothing of the search (a container
                // that got a matching item after the search is not "empty-handed": then no note). The highlights stay.
                Func<Item, bool>? matcher = highlightMatcher;
                bool holdsTarget = matcher != null && ItemsWithNested(CurrentItems(opened)).Any(i => matcher(i));
                if (!holdsTarget)
                {
                    ShowLightNote("No target items here");
                    // the soft "invalid" blip, a moment AFTER the container's own opening sound (played in the same frame, it would be drowned out);
                    // queued like the second sound of a successful search, so the open menu cannot swallow it
                    pendingSounds.Add((NoTargetSound, clock.ElapsedMilliseconds + NoTargetSoundDelayMs));
                }
                return;
            }

            ShowFoundToast(opened);   // also while the highlights are locked: the note only confirms, it changes nothing

            // whether something was taken out cannot be told (one item, a stack, taken and put back ...), so OPENING decides:
            //   not locked: the search did its job, every highlight goes;
            //   locked ("Keep highlights" checked, the player is collecting from several containers): only THIS container's highlight goes, the others
            //   stay and the arrow turns to the next nearest one. Unchecking "Keep highlights" clears the rest at any time.
            if (IsKeepHighlightsLocked)
            {
                highlightedChests.RemoveAll(h => ReferenceEquals(h.chest, opened));
                if (highlightedChests.Count == 0 && !highlightBackpack)
                    ClearHighlights();   // that was the last one
                return;
            }
            ClearHighlights();
        }

        /// <summary>
        /// The container whose footprint holds this tile of the location: first among the highlighted ones (they are known), else among all
        /// containers of the location (only listed when needed: a menu was opened and its context did not say which container it belongs to).
        /// </summary>
        private Chest? FindContainerAtTile(GameLocation? location, Vector2 tile)
        {
            if (location == null)
                return null;

            var point = new Point((int)tile.X, (int)tile.Y);
            foreach (var (place, position, chest) in highlightedChests)
            {
                if (place == location && FootprintOf(chest, position).Contains(point))
                    return chest;
            }

            string label = GetAllLocations().FirstOrDefault(l => l.location == location).label ?? "";
            foreach (var (_, position, chest, _) in GetAllContainers(new[] { (location, label) }))
            {
                if (FootprintOf(chest, position).Contains(point))
                    return chest;
            }
            return null;
        }

        // ===================== "FOUND" NOTE WHEN A TARGET CHEST IS OPENED =====================
        // A light confirmation: a small parchment box in the lower left corner, over the chest menu, for a moment, then it fades; with one soft
        // soft confirmation tick (FoundToastSound). No hover, no click. It is drawn by the mod itself (not as a game HUD message), because the game does not draw its
        // HUD messages while a menu (the chest) is open.
        //   Found 'Apple' ×5, 'Starfruit' ×12!          (normal search)
        //   Found [gold star] 'Apple' ×5!                (a star filter: from the star button, or typed, "gold star apple")
        // The names and counts come from the opened chest itself, checked with the matcher of the last search (the star filter included), so the note
        // can never name an item the chest does not hold. The count is everything of that name the chest holds that the search matched (all stacks).
        // The quality is shown ONLY as the game's own star sprite, in front of each item that can carry a star, and only while a silver / gold / iridium
        // filter is active; items that can never have a star (Wood ...) get none. There is no quality word in the text.

        private const long FoundToastMs = 2800;
        private const long FoundToastFadeMs = 450;
        private const int FoundToastMaxNames = 3;

        /// <summary>Text size of the note compared to the normal small font (a little larger, so it reads at a glance).</summary>
        private const float FoundToastTextScale = 1.25f;

        /// <summary>Zoom of the quality star in the note: 8 px sprite * 3 = 24 px, about the height of the letters.</summary>
        private const float FoundToastStarScale = 3f;

        /// <summary>The game's sound cue of the note: a soft confirmation that the item was SEEN, not taken. Change it here (any cue name of the game; "drumkit6" is a softer alternative).</summary>
        private const string FoundToastSound = "smallSelect";   // a soft UI tick, like a radar "ping": nothing like picking an item up (coin / pickUpItem)

        private List<(string Name, int Count, int Star)> foundToastItems = new();   // Star: the quality whose star is drawn in front (only 1 / 2 / 4 draw one)
        private int foundToastMore;      // how many more names there are than shown ("and 2 more")
        private long foundToastUntilMs;

        private void ShowFoundToast(Chest chest)
        {
            try
            {
                Func<Item, bool>? matcher = highlightMatcher;
                if (matcher == null)
                    return;

                // name -> (count, can it carry a star), in the order the chest holds them
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var starCapable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                foreach (Item item in ItemsWithNested(CurrentItems(chest)))   // a dresser: its items right now; carried containers inside: theirs too
                {
                    if (!matcher(item))
                        continue;
                    if (string.IsNullOrWhiteSpace(GetShownItemName(item)))
                        continue;
                    string name = RowKey(item);   // the item name, or name + quality (see ONE ROW PER QUALITY)
                    if (!counts.ContainsKey(name))
                    {
                        order.Add(name);
                        counts[name] = 0;
                        starCapable[name] = false;
                    }
                    counts[name] += Math.Max(1, item.Stack);
                    starCapable[name] |= CanHaveQuality(item);
                }

                if (order.Count == 0)
                    return;   // the chest holds nothing of the search any more: no note (it must never name something that is not there)

                bool starFilter = SearchMenu.HasStarSprite(resultQuality);

                // with a star filter the starred items come first (they are what the filter was about), the never-starred ones after them
                List<string> ordered = starFilter
                    ? order.Where(n => starCapable[n]).Concat(order.Where(n => !starCapable[n])).ToList()
                    : order;

                // one row per quality: every quality of the item gets its own entry with its own star, plain to iridium (at most four, all shown)
                if (singleItemQualityRows)
                    ordered = ordered.OrderBy(QualityRowOrder).ToList();
                int maxNames = singleItemQualityRows ? Math.Max(FoundToastMaxNames, 4) : FoundToastMaxNames;

                foundToastItems = ordered
                    .Take(maxNames)
                    .Select(n => (RowName(n), counts[n], singleItemQualityRows ? RowQuality(n) : (starFilter && starCapable[n] ? resultQuality : 0)))
                    .ToList();
                foundToastMore = ordered.Count - foundToastItems.Count;
                foundToastUntilMs = clock.ElapsedMilliseconds + FoundToastMs;
                Game1.playSound(FoundToastSound);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not show the found note: {ex.Message}", LogLevel.Trace);   // no note rather than a crash
            }
        }

        private void OnRenderedFoundToast(object? sender, RenderedActiveMenuEventArgs e)
        {
            DrawFoundToast(e.SpriteBatch);
            DrawLightNote(e.SpriteBatch);
        }

        private void OnRenderedFoundToastHud(object? sender, RenderedHudEventArgs e)
        {
            if (Game1.activeClickableMenu != null)
                return;
            DrawHighlightStatus(e.SpriteBatch);   // first: the notes above it are placed relative to it
            DrawFoundToast(e.SpriteBatch);
            DrawLightNote(e.SpriteBatch);
        }

        // ===================== LOWER LEFT CORNER: STATUS LINE AND LIGHT NOTES =====================
        // Two very quiet texts in the lower left corner, grey and half transparent, no box, no click, no hover:
        //   - the STATUS LINE "Highlighting: 'Apple' (12 chests)", always while chests are highlighted. It follows the highlights exactly: the count is
        //     the live number of highlighted chests, it goes when they expire or are cleared, it changes with a
        //     new search, and it stays as long as a checked "Keep highlights" keeps them. Hidden while a menu, an event or a hidden HUD is up.
        //   - a LIGHT NOTE for two seconds, e.g. "No target items here" when a chest without anything of the search is opened (it shows over the
        //     open chest, and on the HUD for what is left of the two seconds after it is closed).
        // The "Found 'Apple' ×5!" box of a target chest and the light note sit just above the status line while it shows. On narrow screens, where
        // the corner would reach under the toolbar at the bottom, everything is lifted above the toolbar's strip.

        /// <summary>The sound of "No target items here": exactly the sound of a search that found nothing (see PlaySearchFeedback), a little after the chest opens.</summary>
        private const string NoTargetSound = "fishingRodBend";
        private const long NoTargetSoundDelayMs = 180;

        private const long LightNoteMs = 2000;
        private const long LightNoteFadeMs = 400;
        private const int CornerMargin = 16;
        private static readonly Color CornerTextColor = new(235, 235, 235);
        private const float CornerTextAlpha = 0.72f;

        private string lightNoteText = "";
        private long lightNoteUntilMs;

        private void ShowLightNote(string text)
        {
            lightNoteText = text;
            lightNoteUntilMs = clock.ElapsedMilliseconds + LightNoteMs;
        }

        /// <summary>True while the status line is drawn (chests are highlighted, the HUD is up and no menu is open).</summary>
        private bool IsHighlightStatusShown()
        {
            return AnyHighlight && highlightLabel.Length > 0 && Context.IsWorldReady
                && Game1.activeClickableMenu == null && !Game1.eventUp && Game1.displayHUD;
        }

        /// <summary>The y where the lower left corner stack ends: the bottom of the screen, or above the toolbar strip when the corner would reach under it.</summary>
        private int CornerBottom(int textWidth)
        {
            int bottom = Game1.uiViewport.Height - CornerMargin;
            if (IsToolbarShown() && !IsToolbarAtTop())
            {
                int toolbarLeft = Game1.uiViewport.Width / 2 - 400;   // the toolbar is centered, about 800 px wide
                if (CornerMargin + textWidth > toolbarLeft)
                    bottom = Game1.uiViewport.Height - KeepBoxToolbarBand;
            }
            return bottom;
        }

        private string HighlightStatusText()
        {
            int count = highlightedChests.Count;
            string what = count > 0 ? $"{count} {ContainerWord(highlightedChests.Select(h => h.chest).ToList())}" : "";
            if (highlightBackpack)
                what = what.Length > 0 ? what + ", backpack" : "backpack";
            return $"Highlighting: {highlightLabel} ({what})";
        }

        /// <summary>Where the notes above the status line end (their bottom edge): just above the status line while it shows, else the corner bottom.</summary>
        private int BottomLeftStackBottom()
        {
            if (!IsHighlightStatusShown())
                return Game1.activeClickableMenu == null ? CornerBottom(420) : Game1.uiViewport.Height - CornerMargin;

            SpriteFont font = Game1.smallFont;
            Vector2 size = font.MeasureString(HighlightStatusText());
            return CornerBottom((int)Math.Ceiling(size.X)) - (int)Math.Ceiling(size.Y) - 6;
        }

        /// <summary>One quiet corner text: light grey, half transparent, with a soft dark edge so it reads on bright and dark ground.</summary>
        private static void DrawCornerText(SpriteBatch b, string text, Vector2 position, float alpha)
        {
            SpriteFont font = Game1.smallFont;
            position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
            Color edge = Color.Black * (0.45f * alpha);
            b.DrawString(font, text, position + new Vector2(2, 0), edge);
            b.DrawString(font, text, position + new Vector2(0, 2), edge);
            b.DrawString(font, text, position + new Vector2(2, 2), edge);
            b.DrawString(font, text, position, CornerTextColor * (CornerTextAlpha * alpha));
        }

        private void DrawHighlightStatus(SpriteBatch b)
        {
            try
            {
                if (!IsHighlightStatusShown())
                    return;

                string text = HighlightStatusText();
                Vector2 size = Game1.smallFont.MeasureString(text);
                int bottom = CornerBottom((int)Math.Ceiling(size.X));
                var position = new Vector2(CornerMargin, bottom - size.Y);
                DrawCornerText(b, text, position, 1f);

                // several words: each word once more on top, in its bright color ("Highlighting: 'fruit', 'fish' (12 chests)")
                string expectedLabel = string.Join(", ", highlightLabelWords);
                if (highlightLabelWords.Count > 0 && expectedLabel == highlightLabel)
                {
                    const string lead = "Highlighting: ";
                    string before = lead;
                    for (int w = 0; w < highlightLabelWords.Count; w++)
                    {
                        if (w > 0)
                            before += ", ";
                        Color wordColor = WordColorName(w) == "Black" ? CornerTextColor : WordFlashColor(w);
                        var wordPos = new Vector2(MathF.Round(position.X + Game1.smallFont.MeasureString(before).X), MathF.Round(position.Y));
                        b.DrawString(Game1.smallFont, highlightLabelWords[w], wordPos, wordColor);
                        before += highlightLabelWords[w];
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not draw the highlight status: {ex.Message}", LogLevel.Trace);
            }
        }

        private void DrawLightNote(SpriteBatch b)
        {
            long left = lightNoteUntilMs - clock.ElapsedMilliseconds;
            if (lightNoteText.Length == 0 || left <= 0)
                return;

            try
            {
                float alpha = left < LightNoteFadeMs ? left / (float)LightNoteFadeMs : 1f;
                Vector2 size = Game1.smallFont.MeasureString(lightNoteText);
                int bottom = BottomLeftStackBottom();
                DrawCornerText(b, lightNoteText, new Vector2(CornerMargin, bottom - size.Y), alpha);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not draw the note: {ex.Message}", LogLevel.Trace);
            }
        }

        /// <summary>The multiplication sign of the counts ("×5"), or a plain "x" if the game's font cannot draw it (some languages).</summary>
        private static string TimesSign(SpriteFont font)
        {
            return font.Characters.Contains('\u00D7') ? "\u00D7" : "x";
        }

        private void DrawFoundToast(SpriteBatch b)
        {
            long left = foundToastUntilMs - clock.ElapsedMilliseconds;
            if (foundToastItems.Count == 0 || left <= 0)
                return;

            try
            {
                float alpha = left < FoundToastFadeMs ? left / (float)FoundToastFadeMs : 1f;
                SpriteFont font = Game1.smallFont;
                float scale = FoundToastTextScale;
                string times = TimesSign(font);
                int starSize = (int)(8 * FoundToastStarScale);
                const int starGap = 4;

                // the pieces of the line, left to right: text, or a star (null text)
                var pieces = new List<(string? Text, int Quality)> { ("Found ", 0) };
                for (int i = 0; i < foundToastItems.Count; i++)
                {
                    var (name, count, star) = foundToastItems[i];
                    if (i > 0)
                        pieces.Add((", ", 0));
                    if (SearchMenu.HasStarSprite(star))
                        pieces.Add((null, star));
                    pieces.Add(($"'{name}' {times}{count}", 0));
                }
                if (foundToastMore > 0)
                    pieces.Add(($" and {foundToastMore} more", 0));
                pieces.Add(("!", 0));

                float Width((string? Text, int Quality) piece) => piece.Text == null ? starSize + starGap : font.MeasureString(piece.Text).X * scale;

                float lineWidth = pieces.Sum(Width);
                float lineHeight = Math.Max(font.LineSpacing * scale, starSize);

                const int padX = 22;
                const int padY = 16;
                int boxWidth = (int)Math.Ceiling(lineWidth) + padX * 2;
                int boxHeight = (int)Math.Ceiling(lineHeight) + padY * 2;
                int x = 16;
                int y = BottomLeftStackBottom() - boxHeight;   // above the corner status line while it shows

                IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, boxWidth, boxHeight, Color.White * alpha, 1f, false);

                float cursor = x + padX;
                float textY = y + padY + (lineHeight - font.LineSpacing * scale) / 2f;
                float starY = y + padY + (lineHeight - starSize) / 2f;
                foreach (var piece in pieces)
                {
                    if (piece.Text == null)
                        SearchMenu.DrawQualityStar(b, new Vector2(cursor, starY), piece.Quality, FoundToastStarScale, alpha);
                    else
                        Utility.drawTextWithShadow(b, piece.Text, font, new Vector2(cursor, textY), Game1.textColor * alpha, scale);
                    cursor += Width(piece);
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not draw the found note: {ex.Message}", LogLevel.Trace);
            }
        }

        // ===================== EDGE ARROW: THE WAY TO THE NEAREST HIGHLIGHTED CHEST =====================
        // A red arrow with a black outline and a small "Chest" label (white letters, black edge), so it reads on every ground, also dark floors.
        // It does not flash. It lives exactly as long as the highlights: it goes away when they expire on their own (the countdown), and stays as long
        // as "Keep highlights" keeps them. No arrow while a menu, an event or a hidden HUD is up.
        //   - The arrow always shows the way to the NEAREST target, measured like the order of the place list (see WALKING DISTANCE): a chest in this
        //     location by its tiles, a container behind a door (a barn, a house ...) by the walk to that door plus the way inside.
        //   - The nearest target is in THIS location: while none of the highlighted containers here is on screen, the arrow sits at the edge of the
        //     screen on the line from the screen center to it and points at it; as soon as one is on screen it disappears (the flash shows the way).
        //   - The nearest target is behind a door / in another location: the arrow points at the exit of THIS location that starts the way there (at
        //     the screen edge while the exit is off screen, right above it, pointing down, while it is on screen), even when highlighted chests of this
        //     location are on screen: standing at the barn door, the arrow says "Auto-Grabber: Deluxe Barn". The label names the place.
        //   - No route at all (Ginger Island needs the boat, a cave needs the elevator ...): the arrow sits at the top of the screen with the label
        //     "Chest: Ginger Island", so the player still knows where the chests are.

        /// <summary>Distance of the arrow's center from the screen edge (UI pixels).</summary>
        private const int EdgeArrowMargin = 40;

        /// <summary>Zoom of the arrow sprite (15 x 15 pixels): 3 = 45 px, the pixel size of the game's own HUD icons.</summary>
        private const float EdgeArrowScale = 3f;

        /// <summary>Gap between the arrow's center and the center of its label (UI pixels), towards the middle of the screen.</summary>
        private const int EdgeArrowLabelGap = 46;

        /// <summary>The route to another location is worked out again this often (game ticks), or at once when the player changes location.</summary>
        private const int EdgeRouteRefreshTicks = 60;

        private static Texture2D? edgeArrowTexture;

        // Avoiding the result panel and the "Keep highlights" box: their screen rectangles of the last frame in which they were drawn are REMEMBERED
        // (until the highlights end or a new search runs), so an arrow that moved out of the panel's way stays in that safe place after the panel has
        // faded, instead of jumping back to the very edge. The way out of the way is eased in over a few frames (edgeArrowShownShift, one per arrow), so it never jerks.
        private readonly List<Rectangle> edgeArrowObstacles = new();
        // One slot per arrow that can be on screen at the same time: 0 = the arrow to the nearest chest / exit, 1 = the "Backpack" arrow above the player.
        private readonly Vector2[] edgeArrowShownShift = new Vector2[2];   // the shift that is drawn right now (eased towards the wanted one)
        private readonly int[] edgeArrowShiftEdge = { -1, -1 };   // the edge the shift belongs to (0 top / bottom, 1 left / right, 2 above an exit); another edge starts fresh
        private readonly int[] edgeArrowShiftTick = { int.MinValue, int.MinValue };

        // Where the "Backpack" arrow (with its label) was drawn in the last frame: the chest / exit arrow keeps out of it, so the two never lie on each other.
        private Rectangle backpackArrowBox;
        private int backpackArrowTick = int.MinValue;

        /// <summary>How much of the remaining way the arrow moves per frame while it gets out of the way (0..1): about a fifth of a second in all.</summary>
        private const float EdgeArrowEase = 0.25f;

        // the last worked-out route to a chest in another location (see FindRouteToNearestChest)
        private GameLocation? edgeRouteFrom;
        private int edgeRouteTick = int.MinValue;
        private int edgeRouteChestCount = -1;
        private Point? edgeRouteExit;
        private string edgeRouteLabel = "";
        private string edgeRouteWord = "Chest";   // the kind of the route target's containers ("Fridge", "Auto-Grabber" ...) if they are all one kind
        private float edgeRouteCost = float.MaxValue;   // the walking distance of that target (see WALKING DISTANCE), to compare with the targets here

        /// <summary>The tick in which the item list of a hovered place was last drawn, and where it was (the arrow hides only if it would lie on it).</summary>
        private int placeTooltipShownTick = -1;
        private Rectangle placeTooltipBounds;

        private void OnRenderedHudEdgeArrow(object? sender, RenderedHudEventArgs e)
        {
            try
            {
                if (!AnyHighlight)
                {
                    edgeArrowObstacles.Clear();   // no highlights, no arrow: the next result starts with a clean slate
                    return;
                }
                if (!Context.IsWorldReady || Game1.activeClickableMenu != null || Game1.eventUp || !Game1.displayHUD)
                    return;

                // the result panel, its X and the "Keep highlights" box of THIS frame (OnRenderedHud runs first); remembered while they are gone
                if (hudBlockRects.Count > 0)
                {
                    edgeArrowObstacles.Clear();
                    edgeArrowObstacles.AddRange(hudBlockRects);
                }

                GameLocation? here = Game1.currentLocation;
                Farmer? player = Game1.player;
                if (here == null || player == null)
                    return;

                // the backpack is a place like a chest: whenever it holds something of the search (also while chests are highlighted), an arrow stands
                // above the player's head and says "Backpack". It is drawn first; the arrow to the nearest chest below keeps out of it.
                if (highlightBackpack && Config.ShowPlayerArrow)
                    DrawArrowAboveWorldPoint(e.SpriteBatch, new Vector2(player.Position.X + 32f, player.Position.Y - 40f), BackpackPlaceKey, 1);
                if (highlightedChests.Count == 0)
                    return;

                var view = new Rectangle(Game1.viewport.X, Game1.viewport.Y, Game1.viewport.Width, Game1.viewport.Height);   // world pixels on screen
                bool anyHere = false;
                bool anyHereOnScreen = false;
                Vector2? nearest = null;
                Chest? nearestChest = null;
                float nearestDistance = float.MaxValue;
                foreach (var (location, position, chest) in highlightedChests)
                {
                    if (location != here)
                        continue;
                    anyHere = true;

                    if (view.Intersects(FootprintPixels(chest, position)))
                        anyHereOnScreen = true;   // one of them is on screen (any part of a fridge, a building ...): its flash shows the way

                    float distance = Vector2.Distance(position, player.Tile);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = position;
                        nearestChest = chest;
                    }
                }

                // the nearest target in ANOTHER location (behind a door ...): worked out again now and then, or at once on a new location / highlight set
                if (!ReferenceEquals(edgeRouteFrom, here) || edgeRouteChestCount != highlightedChests.Count || Game1.ticks - edgeRouteTick >= EdgeRouteRefreshTicks)
                {
                    edgeRouteFrom = here;
                    edgeRouteChestCount = highlightedChests.Count;
                    edgeRouteTick = Game1.ticks;
                    (edgeRouteExit, edgeRouteLabel, GameLocation? routeTarget, edgeRouteCost) = FindRouteToNearestChest(here, player.Tile);

                    // "Fridge: Farmhouse" when everything highlighted there is a fridge, "Auto-Grabber: Barn" ..., else "Chest: Farm"
                    edgeRouteWord = ArrowWord(highlightedChests.Where(h => h.location == routeTarget).Select(h => h.chest));
                }

                bool routeIsNearer = edgeRouteExit != null && (!anyHere || edgeRouteCost < nearestDistance);
                if (anyHere && !routeIsNearer)
                {
                    if (!anyHereOnScreen && nearest is Vector2 target && nearestChest != null)
                        DrawEdgeArrowTowards(e.SpriteBatch, FootprintCenterTile(nearestChest, target), KindOf(nearestChest).ArrowName);   // "Fridge", "Auto-Grabber", "Chest" ...
                    return;
                }

                string label = edgeRouteLabel.Length > 0 ? edgeRouteWord + ": " + edgeRouteLabel : edgeRouteWord;
                if (edgeRouteExit is Point exit)
                {
                    var exitTile = new Rectangle(exit.X * 64, exit.Y * 64, 64, 64);
                    if (view.Intersects(exitTile))
                        DrawArrowAboveTile(e.SpriteBatch, exit, label);   // the door / exit is on screen: the arrow stands right above it
                    else
                        DrawEdgeArrowTowards(e.SpriteBatch, new Vector2(exit.X, exit.Y), label);
                }
                else
                {
                    // no way there by walking: the arrow stays at the top of the screen and names the place
                    var spot = new Vector2(Game1.uiViewport.Width / 2f, EdgeArrowMargin);
                    DrawEdgeArrow(e.SpriteBatch, spot, -MathHelper.PiOver2, label, new Vector2(0f, 1f));
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not draw the edge arrow: {ex.Message}", LogLevel.Trace);   // no arrow rather than a crash
            }
        }

        /// <summary>
        /// The nearest highlighted container in ANOTHER location (by WALKING DISTANCE), the exit of <paramref name="here"/> that starts the way there,
        /// the place name of its location and its walking distance. Exit null = no highlighted container elsewhere can be reached on foot (the label
        /// then still names the place of the first one; the distance is float.MaxValue).
        /// </summary>
        private (Point? Exit, string Label, GameLocation? Target, float Cost) FindRouteToNearestChest(GameLocation here, Vector2 playerTile)
        {
            ExitGraph graph = GetExitGraph();
            var reach = ComputeReach(graph, here, playerTile);

            GameLocation? best = null;
            float bestCost = float.MaxValue;
            foreach (var (location, position, _) in highlightedChests)
            {
                if (location == here || !reach.TryGetValue(location, out var way))
                    continue;
                float cost = way.Cost + (way.Arrival is Vector2 arrival ? Vector2.Distance(arrival, position) : 0f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = location;
                }
            }

            string LabelOf(GameLocation location) => graph.Labels.TryGetValue(location, out string? label) ? label : GetFriendlyName(location.Name);

            if (best != null)
                return (reach[best].FirstExit, LabelOf(best), best, bestCost);

            GameLocation? anyTarget = highlightedChests.Select(h => h.location).FirstOrDefault(l => l != here);
            return (null, anyTarget != null ? LabelOf(anyTarget) : "", anyTarget, float.MaxValue);
        }

        // ===================== WALKING DISTANCE (order of the place list AND the target of the arrow) =====================
        // How far a container is from the player, roughly in tiles walked:
        //   - in the player's location: the straight distance between the two tiles;
        //   - in another location: the walk to the exit (door, map edge ...) that starts the way there, plus WalkingHopCost for every location
        //     change, plus the walks inside the locations on the way (from where the player comes in to the next exit), plus the walk from where the
        //     player comes into the last location to the container. Standing at a barn door, the Auto-Grabber inside is a few tiles away, nearer than a
        //     chest at the other end of the farm.
        //   - a location that cannot be reached on foot (Ginger Island needs the boat ...): after every reachable one, in the old rough order of map
        //     weights (see GetMapWeight).
        // The ways between locations (ExitGraph): their warps (map edges, cave mouths), their doors, the doors of their buildings, and, for a door the
        // game handles in its own way (the Ginger Island farmhouse ...), the way back: a location B whose warp leads to A at a tile has a door in A
        // right there. Where somebody arrives after a door is taken from the warp that leads back.

        /// <summary>What one location change costs, in tiles (going through a door is a little more than walking past it).</summary>
        private const float WalkingHopCost = 5f;

        /// <summary>The sort key of a place that cannot be reached on foot starts here (every reachable place is nearer).</summary>
        private const float UnreachableDistance = 1_000_000f;

        /// <summary>The ways out of every location (see WALKING DISTANCE), and the place label of every location.</summary>
        private sealed class ExitGraph
        {
            public readonly Dictionary<GameLocation, List<(Point Exit, GameLocation To, Vector2? Arrival)>> Exits = new(ReferenceEqualityComparer.Instance);
            public readonly Dictionary<GameLocation, string> Labels = new(ReferenceEqualityComparer.Instance);
            public readonly Dictionary<string, GameLocation> ByName = new(StringComparer.OrdinalIgnoreCase);
        }

        private ExitGraph? exitGraph;
        private int exitGraphTick = int.MinValue;

        /// <summary>How long a worked-out exit graph is used before it is worked out again (game ticks): buildings can be built or moved.</summary>
        private const int ExitGraphRefreshTicks = 600;

        /// <summary>The ways between all locations (worked out again now and then, see ExitGraphRefreshTicks).</summary>
        private ExitGraph GetExitGraph()
        {
            if (exitGraph != null && Game1.ticks - exitGraphTick < ExitGraphRefreshTicks && Game1.ticks >= exitGraphTick)
                return exitGraph;

            var graph = new ExitGraph();
            foreach (var (location, label) in GetAllLocations())
            {
                graph.ByName.TryAdd(location.NameOrUniqueName, location);
                graph.ByName.TryAdd(location.Name, location);
                graph.Labels.TryAdd(location, label);
                graph.Exits.TryAdd(location, new List<(Point, GameLocation, Vector2?)>());
            }

            foreach (var (location, exits) in graph.Exits)
                exits.AddRange(ReadExits(graph, location));

            // the way back: B's warp leads to A at a tile, so A has a door to B right there (if A knows no way to B yet). Arrival points that are
            // still unknown (doors, building doors) come from the warp that leads back
            foreach (var (location, exits) in graph.Exits)
            {
                foreach (var (warpTile, to, arrival) in exits.ToList())
                {
                    if (arrival is not Vector2 at || !graph.Exits.TryGetValue(to, out var back))
                        continue;
                    if (!back.Any(x => ReferenceEquals(x.To, location)))
                        back.Add((new Point((int)at.X, (int)at.Y), location, new Vector2(warpTile.X, warpTile.Y)));
                }
            }
            foreach (var (location, exits) in graph.Exits)
            {
                for (int i = 0; i < exits.Count; i++)
                {
                    var (exit, to, arrival) = exits[i];
                    if (arrival != null || !graph.Exits.TryGetValue(to, out var back))
                        continue;
                    foreach (var (backExit, backTo, _) in back)
                    {
                        if (ReferenceEquals(backTo, location))
                        {
                            exits[i] = (exit, to, new Vector2(backExit.X, backExit.Y));   // comes in next to the door that leads back
                            break;
                        }
                    }
                }
            }

            exitGraph = graph;
            exitGraphTick = Game1.ticks;
            return graph;
        }

        /// <summary>The ways out of one location: its warps (with where they arrive), its doors and the doors of its buildings.</summary>
        private List<(Point Exit, GameLocation To, Vector2? Arrival)> ReadExits(ExitGraph graph, GameLocation location)
        {
            var exits = new List<(Point Exit, GameLocation To, Vector2? Arrival)>();
            try
            {
                foreach (Warp warp in location.warps)
                {
                    if (warp != null && !string.IsNullOrEmpty(warp.TargetName) && graph.ByName.TryGetValue(warp.TargetName, out GameLocation? to))
                        exits.Add((new Point(warp.X, warp.Y), to, new Vector2(warp.TargetX, warp.TargetY)));
                }

                foreach (KeyValuePair<Point, string> door in location.doors.Pairs)
                {
                    if (!string.IsNullOrEmpty(door.Value) && graph.ByName.TryGetValue(door.Value, out GameLocation? to))
                        exits.Add((door.Key, to, null));
                }

                foreach (var building in location.buildings)
                {
                    GameLocation? indoors = building.indoors.Value;
                    if (indoors != null)
                        exits.Add((new Point(building.tileX.Value + building.humanDoor.X, building.tileY.Value + building.humanDoor.Y), indoors, null));
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not read the exits of {location.NameOrUniqueName}: {ex.Message}", LogLevel.Trace);
            }
            return exits;
        }

        /// <summary>
        /// Every location that can be reached on foot from <paramref name="here"/>: its walking distance (to where the player comes in), where the
        /// player comes in (null = unknown) and the exit of <paramref name="here"/> that starts the way.
        /// </summary>
        private Dictionary<GameLocation, (float Cost, Vector2? Arrival, Point FirstExit)> ComputeReach(ExitGraph graph, GameLocation here, Vector2 playerTile)
        {
            // a location that is not in the list of searched locations (a mine level ...) still has its ways out
            List<(Point Exit, GameLocation To, Vector2? Arrival)>? hereExits = graph.Exits.TryGetValue(here, out var listed) ? listed : ReadExits(graph, here);

            var best = new Dictionary<GameLocation, (float Cost, Vector2? Arrival, Point FirstExit)>(ReferenceEqualityComparer.Instance);
            var done = new HashSet<GameLocation>(ReferenceEqualityComparer.Instance);
            var queue = new PriorityQueue<GameLocation, float>();

            best[here] = (0f, playerTile, Point.Zero);
            queue.Enqueue(here, 0f);
            while (queue.TryDequeue(out GameLocation? location, out float cost))
            {
                if (!done.Add(location))
                    continue;
                var exits = ReferenceEquals(location, here) ? hereExits : graph.Exits.TryGetValue(location, out var more) ? more : null;
                if (exits == null)
                    continue;
                var (_, arrival, firstExit) = best[location];

                foreach (var (exit, to, toArrival) in exits)
                {
                    if (done.Contains(to))
                        continue;
                    float walk = arrival is Vector2 from ? Vector2.Distance(from, new Vector2(exit.X, exit.Y)) : 0f;
                    float next = cost + walk + WalkingHopCost;
                    if (!best.TryGetValue(to, out var known) || next < known.Cost)
                    {
                        best[to] = (next, toArrival, ReferenceEquals(location, here) ? exit : firstExit);
                        queue.Enqueue(to, next);
                    }
                }
            }

            best.Remove(here);
            return best;
        }

        /// <summary>The arrow at the screen edge, on the line from the screen center to a tile of the current location, pointing at it (the old placement).</summary>
        private void DrawEdgeArrowTowards(SpriteBatch b, Vector2 targetTile, string label)
        {
            // the target's center in UI pixels (the HUD is drawn with the UI scale, the world with the zoom)
            Vector2 world = targetTile * 64f + new Vector2(32f, 32f);
            Vector2 screen = Utility.ModifyCoordinatesForUIScale(Game1.GlobalToLocal(Game1.viewport, world));

            var center = new Vector2(Game1.uiViewport.Width / 2f, Game1.uiViewport.Height / 2f);
            Vector2 direction = screen - center;
            if (direction.LengthSquared() < 1f)
                return;

            // where the line from the center to the target leaves the screen (EdgeArrowMargin inside the edge)
            float halfWidth = Math.Max(1f, center.X - EdgeArrowMargin);
            float halfHeight = Math.Max(1f, center.Y - EdgeArrowMargin);
            float t = Math.Min(
                Math.Abs(direction.X) > 0.001f ? halfWidth / Math.Abs(direction.X) : float.MaxValue,
                Math.Abs(direction.Y) > 0.001f ? halfHeight / Math.Abs(direction.Y) : float.MaxValue);
            Vector2 spot = center + direction * Math.Min(t, 1f);
            float angle = (float)Math.Atan2(direction.Y, direction.X);

            Vector2 inward = -Vector2.Normalize(direction);   // the label sits on the screen side of the arrow
            DrawEdgeArrow(b, spot, angle, label, inward);
        }

        /// <summary>The arrow standing right above an on-screen tile (an exit of the current location), pointing down at it.</summary>
        private void DrawArrowAboveTile(SpriteBatch b, Point tile, string label)
        {
            DrawArrowAboveWorldPoint(b, new Vector2(tile.X * 64f + 32f, tile.Y * 64f - 40f), label, 0);
        }

        /// <summary>The arrow standing right above a point of the world (pixels), pointing down at it. slot: see edgeArrowShownShift.</summary>
        private void DrawArrowAboveWorldPoint(SpriteBatch b, Vector2 world, string label, int slot)
        {
            Vector2 spot = Utility.ModifyCoordinatesForUIScale(Game1.GlobalToLocal(Game1.viewport, world));
            spot.X = Math.Clamp(spot.X, EdgeArrowMargin, Game1.uiViewport.Width - EdgeArrowMargin);
            spot.Y = Math.Clamp(spot.Y, EdgeArrowMargin + EdgeArrowLabelGap, Game1.uiViewport.Height - EdgeArrowMargin);
            DrawEdgeArrow(b, spot, MathHelper.PiOver2, label, new Vector2(0f, -1f), slot);
        }

        /// <summary>Free space between the arrow and its label, and around the player and the toolbar (UI pixels).</summary>
        private const int EdgeArrowSpacing = 10;

        /// <summary>
        /// Draws the arrow (rotated to <paramref name="angle"/>) and its label, laid out so that nothing overlaps:
        ///   - the label is NEVER on the arrow: at the left / right screen edge it sits beside the arrow towards the middle of the screen; at the top /
        ///     bottom edge it sits beside the arrow on the side towards the middle (so it stays in the same thin strip along the edge and does not reach
        ///     into the screen); for an arrow standing above an exit (<paramref name="labelSide"/> pointing up) it sits above the arrow;
        ///   - arrow and label never lie on the game's toolbar: while the toolbar is at the bottom both are lifted above its strip, while it is at the top
        ///     both are kept below it (the same strip the "Keep highlights" box keeps out of, KeepBoxToolbarBand);
        ///   - arrow and label never cover the player: if they would, they slide along their edge (sideways at the top / bottom, up or down at the
        ///     left / right) until they are clear of the player's sprite; an arrow above an exit moves up above the player instead.
        /// The arrow keeps its direction; only its place moves a little.
        /// </summary>
        private void DrawEdgeArrow(SpriteBatch b, Vector2 spot, float angle, string label, Vector2 labelSide, int slot = 0)
        {
            Texture2D arrow = GetEdgeArrowTexture(b.GraphicsDevice);
            SpriteFont font = Game1.smallFont;
            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            int arrowHalf = (int)Math.Ceiling(arrow.Width * EdgeArrowScale / 2f);
            Vector2 labelSize = string.IsNullOrEmpty(label) ? Vector2.Zero : font.MeasureString(label);
            int labelWidth = (int)Math.Ceiling(labelSize.X);
            int labelHeight = (int)Math.Ceiling(labelSize.Y);

            // the area arrow and label may use: the screen minus the toolbar's strip (only where the toolbar really is)
            int safeTop = 8;
            int safeBottom = screenHeight - 8;
            if (IsToolbarShown())
            {
                if (IsToolbarAtTop())
                    safeTop = KeepBoxToolbarBand + EdgeArrowSpacing;
                else
                    safeBottom = screenHeight - KeepBoxToolbarBand - EdgeArrowSpacing;
            }

            bool aboveExit = labelSide.Y < -0.5f && Math.Abs(labelSide.X) < 0.5f;

            // which edge the arrow is at (the nearest one)
            float toLeft = spot.X, toRight = screenWidth - spot.X, toTop = spot.Y, toBottom = screenHeight - spot.Y;
            bool sideEdge = Math.Min(toLeft, toRight) < Math.Min(toTop, toBottom);   // left / right edge, else top / bottom

            // where the label goes, relative to the arrow center (top left corner of the label)
            Vector2 LabelOffset()
            {
                if (aboveExit)
                    return new Vector2(-labelWidth / 2f, -arrowHalf - EdgeArrowSpacing - labelHeight);
                if (sideEdge)
                    return toLeft < toRight
                        ? new Vector2(arrowHalf + EdgeArrowSpacing, -labelHeight / 2f)                 // left edge: right of the arrow
                        : new Vector2(-arrowHalf - EdgeArrowSpacing - labelWidth, -labelHeight / 2f);  // right edge: left of the arrow
                return spot.X < screenWidth / 2f
                    ? new Vector2(arrowHalf + EdgeArrowSpacing, -labelHeight / 2f)                     // top / bottom edge, left half: right of the arrow
                    : new Vector2(-arrowHalf - EdgeArrowSpacing - labelWidth, -labelHeight / 2f);      // ... right half: left of the arrow
            }

            Vector2 labelOffset = LabelOffset();

            // the box of arrow + label for a given arrow center
            Rectangle GroupBox(Vector2 center)
            {
                var arrowBox = new Rectangle((int)(center.X - arrowHalf), (int)(center.Y - arrowHalf), arrowHalf * 2, arrowHalf * 2);
                if (labelWidth == 0)
                    return arrowBox;
                var labelBox = new Rectangle((int)(center.X + labelOffset.X), (int)(center.Y + labelOffset.Y), labelWidth, labelHeight);
                return Rectangle.Union(arrowBox, labelBox);
            }

            // keep the whole group on screen and out of the toolbar's strip
            Vector2 KeepInside(Vector2 center)
            {
                Rectangle box = GroupBox(center);
                float dx = 0f, dy = 0f;
                if (box.Left < 8) dx = 8 - box.Left;
                else if (box.Right > screenWidth - 8) dx = screenWidth - 8 - box.Right;
                if (box.Top < safeTop) dy = safeTop - box.Top;
                else if (box.Bottom > safeBottom) dy = safeBottom - box.Bottom;
                return center + new Vector2(dx, dy);
            }

            Vector2 position = KeepInside(spot);

            // never on the player
            Rectangle player = GetPlayerScreenBox();
            if (player.Width > 0)
            {
                Rectangle avoid = player;
                avoid.Inflate(EdgeArrowSpacing, EdgeArrowSpacing);
                Rectangle box = GroupBox(position);
                if (box.Intersects(avoid))
                {
                    Vector2 moved;
                    if (aboveExit)
                    {
                        moved = position + new Vector2(0f, avoid.Top - box.Bottom);   // up, above the player's head
                    }
                    else if (sideEdge)
                    {
                        Vector2 up = position + new Vector2(0f, avoid.Top - box.Bottom);
                        Vector2 down = position + new Vector2(0f, avoid.Bottom - box.Top);
                        moved = Math.Abs(up.Y - position.Y) <= Math.Abs(down.Y - position.Y) && GroupBox(up).Top >= safeTop ? up : down;
                    }
                    else
                    {
                        Vector2 left = position + new Vector2(avoid.Left - box.Right, 0f);
                        Vector2 right = position + new Vector2(avoid.Right - box.Left, 0f);
                        bool leftFits = GroupBox(left).Left >= 8;
                        bool rightFits = GroupBox(right).Right <= screenWidth - 8;
                        moved = leftFits && (!rightFits || Math.Abs(left.X - position.X) <= Math.Abs(right.X - position.X)) ? left : right;
                    }

                    position = KeepInside(moved);
                }
            }

            // never on the result panel, its X or the "Keep highlights" box (also after they have faded: see edgeArrowObstacles). The group moves the
            // shortest way out (usually down, below the panel), and stays clear of the player and the toolbar while doing so.
            List<Rectangle> arrowObstacles = edgeArrowObstacles;
            if (slot == 0 && backpackArrowTick == Game1.ticks)
                arrowObstacles = new List<Rectangle>(edgeArrowObstacles) { backpackArrowBox };   // the "Backpack" arrow above the player
            if (arrowObstacles.Count > 0)
            {
                for (int round = 0; round < 4; round++)
                {
                    Rectangle box = GroupBox(position);
                    Rectangle? hit = null;
                    foreach (Rectangle obstacle in arrowObstacles)
                    {
                        Rectangle grown = obstacle;
                        grown.Inflate(EdgeArrowSpacing, EdgeArrowSpacing);
                        if (box.Intersects(grown))
                        {
                            hit = grown;
                            break;
                        }
                    }
                    if (hit is not Rectangle blocked)
                        break;

                    Vector2 best = position;
                    float bestDistance = float.MaxValue;
                    foreach (Vector2 shift in new[]
                    {
                        new Vector2(0f, blocked.Bottom - box.Top),   // below it
                        new Vector2(0f, blocked.Top - box.Bottom),   // above it
                        new Vector2(blocked.Right - box.Left, 0f),   // right of it
                        new Vector2(blocked.Left - box.Right, 0f)    // left of it
                    })
                    {
                        Vector2 candidate = KeepInside(position + shift);
                        Rectangle candidateBox = GroupBox(candidate);
                        if (candidateBox.Intersects(blocked))
                            continue;   // pushed back into it by the screen edge / the toolbar: no way out on this side
                        if (player.Width > 0 && candidateBox.Intersects(player))
                            continue;
                        float distance = (candidate - position).LengthSquared();
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = candidate;
                        }
                    }

                    if (bestDistance == float.MaxValue)
                        break;   // boxed in on every side (tiny window): leave it where it is
                    position = best;
                }
            }

            // ease the shift away from the plain edge spot in, so getting out of the way never jerks; the plain spot itself (the direction to the
            // target) follows at once, as before
            Vector2 plain = KeepInside(spot);
            Vector2 wantedShift = position - plain;
            int edgeKind = aboveExit ? 2 : sideEdge ? 1 : 0;
            if (edgeKind != edgeArrowShiftEdge[slot] || Game1.ticks - edgeArrowShiftTick[slot] > 2)
                edgeArrowShownShift[slot] = wantedShift;   // another edge, or the arrow was not drawn a moment ago: start at the wanted place
            else
                edgeArrowShownShift[slot] += (wantedShift - edgeArrowShownShift[slot]) * EdgeArrowEase;
            edgeArrowShiftEdge[slot] = edgeKind;
            edgeArrowShiftTick[slot] = Game1.ticks;
            position = plain + edgeArrowShownShift[slot];

            if (slot == 1)
            {
                backpackArrowBox = GroupBox(position);   // the chest / exit arrow keeps out of this box
                backpackArrowTick = Game1.ticks;
            }

            // the item list of a hovered place (the mouse is on a place word of the panel) lies where arrow or label would be: both stay away while
            // the list is up. The list changes size with every place and comes and goes as the mouse moves, so moving the arrow around it would only
            // jump. Where they do not meet, the arrow stays as it is.
            if (placeTooltipShownTick == Game1.ticks)
            {
                Rectangle list = placeTooltipBounds;
                list.Inflate(EdgeArrowSpacing, EdgeArrowSpacing);
                if (GroupBox(position).Intersects(list))
                    return;
            }

            b.Draw(arrow, position, null, Color.White, angle, new Vector2(arrow.Width / 2f, arrow.Height / 2f), EdgeArrowScale, SpriteEffects.None, 1f);

            if (labelWidth == 0)
                return;

            Vector2 textPosition = position + labelOffset;
            textPosition = new Vector2(MathF.Round(textPosition.X), MathF.Round(textPosition.Y));

            // white letters with a 2 px black edge: readable on bright grass and on dark floors alike
            for (int dy = -2; dy <= 2; dy += 2)
            {
                for (int dx = -2; dx <= 2; dx += 2)
                {
                    if (dx != 0 || dy != 0)
                        b.DrawString(font, label, textPosition + new Vector2(dx, dy), Color.Black);
                }
            }
            b.DrawString(font, label, textPosition, Color.White);
        }

        /// <summary>True while the game's toolbar is on screen (it is part of the HUD; some locations and mods hide it).</summary>
        private static bool IsToolbarShown()
        {
            foreach (IClickableMenu menu in Game1.onScreenMenus)
            {
                if (menu is Toolbar)
                    return Game1.displayHUD;
            }
            return false;
        }

        /// <summary>
        /// The player's sprite on screen, in UI pixels: the 64 px wide standing tile and the body above it (the farmer is about two tiles tall).
        /// Empty if there is no player.
        /// </summary>
        private static Rectangle GetPlayerScreenBox()
        {
            Farmer? farmer = Game1.player;
            if (farmer == null)
                return Rectangle.Empty;

            Vector2 topLeftWorld = farmer.Position + new Vector2(-8f, -112f);       // the head is about 1.75 tiles above the standing position
            Vector2 bottomRightWorld = farmer.Position + new Vector2(72f, 64f);     // the feet tile
            Vector2 topLeft = Utility.ModifyCoordinatesForUIScale(Game1.GlobalToLocal(Game1.viewport, topLeftWorld));
            Vector2 bottomRight = Utility.ModifyCoordinatesForUIScale(Game1.GlobalToLocal(Game1.viewport, bottomRightWorld));
            return new Rectangle((int)topLeft.X, (int)topLeft.Y, (int)Math.Max(1f, bottomRight.X - topLeft.X), (int)Math.Max(1f, bottomRight.Y - topLeft.Y));
        }

        /// <summary>
        /// A small pixel-art triangle pointing right: a bright red fill, a thin dark red rim inside, and a black outline around it, so it stands out on
        /// every ground (bright grass, dark floors, water). Built once.
        /// </summary>
        private static Texture2D GetEdgeArrowTexture(GraphicsDevice device)
        {
            if (edgeArrowTexture != null && !edgeArrowTexture.IsDisposed && edgeArrowTexture.GraphicsDevice == device)
                return edgeArrowTexture;

            const int size = 15;
            const int middle = size / 2;
            var fill = new bool[size, size];
            for (int x = 2; x <= 12; x++)
            {
                float half = (12 - x) * 5f / 10f;   // 5 pixels up and down at the back, a point at the front
                for (int y = 0; y < size; y++)
                    fill[x, y] = Math.Abs(y - middle) <= half + 0.01f;
            }

            bool Filled(int x, int y) => x >= 0 && y >= 0 && x < size && y < size && fill[x, y];

            Color red = new(235, 40, 40);
            Color rim = new(150, 15, 15);
            var data = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool fillHere = fill[x, y];
                    bool touchesOutside = !Filled(x - 1, y) || !Filled(x + 1, y) || !Filled(x, y - 1) || !Filled(x, y + 1);
                    bool nearFill = false;
                    for (int dy = -1; dy <= 1 && !nearFill; dy++)
                    {
                        for (int dx = -1; dx <= 1 && !nearFill; dx++)
                            nearFill = Filled(x + dx, y + dy);
                    }

                    data[y * size + x] = fillHere
                        ? (touchesOutside ? rim : red)                 // the fill, with a darker rim along its edge
                        : nearFill ? Color.Black : Color.Transparent;  // a black outline all around (also at the corners)
                }
            }

            edgeArrowTexture = new Texture2D(device, size, size);
            edgeArrowTexture.SetData(data);
            return edgeArrowTexture;
        }

        private void ClearHighlights()
        {
            highlightedChests.Clear();
            highlightBackpack = false;   // backpackWordSlots stay, like chestWordSlots: a revived "Keep highlights" box brings the flash back in its colors
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

            // the highlighted containers may have been moved, destroyed or picked up since the search (see TARGET MOVED OR GONE)
            if (Context.IsWorldReady && e.IsMultipleOf(ContainerCheckTicks) && (highlightedChests.Count > 0 || keepSavedChests is { Count: > 0 }))
                CheckHighlightedContainers();

            bool hoverFreezesHighlights = nowMs - highlightHoverFrameMs <= HighlightHoverFreshMs;   // the mouse is on the result panel right now
            if (!IsKeepHighlightsLocked && !hoverFreezesHighlights && AnyHighlight && highlightUntilMs >= 0 && nowMs >= highlightUntilMs)
            {
                ClearHighlights();
                Monitor.Log("Chest highlights expired.", LogLevel.Debug);
            }
        }

        // ===================== TARGET MOVED OR GONE =====================
        // A highlight belongs to the CONTAINER, not to the tile it stood on at the time of the search. A few times a second every highlighted
        // container is looked up again where it lives (CheckHighlightedContainers):
        //   moved     a chest kicked by a villager, a chest or mini-fridge pushed one tile with a tool, a Mill / Junimo Hut moved by Robin, a dresser
        //             moved: the highlight follows it (flash, hover tip, "was it opened", the arrow), with a light note in the lower left corner;
        //   gone      destroyed, demolished by Robin, or its building demolished (a barn, a cabin: every container inside goes with it): its highlight
        //             ends at once, with a light note; when nothing is left, every highlight ends (the arrow, the status line). A placed chest a
        //             villager bumps into is shoved onto a free tile nearby (Chest.TryMoveToSafePosition: land only, never water, up to three tiles
        //             through passable ground); when there is none, next to water for example, the game destroys it and drops what it held on the
        //             ground there (GameLocation.checkDestroyItem -> Chest.destroyAndDropContents), where items can roll into the water and sink.
        //             That counts as destroyed: the note says the items were dropped nearby, so the player goes and picks them up;
        //   picked up into the backpack or into a chest (a dresser keeps its clothes): its old place ends ("picked up"); in the backpack, when what it
        //             holds belongs to the search, the backpack flashes instead (see CONTAINERS INSIDE CONTAINERS).
        // A note is shown at most once every ContainerNoteRepeatMs for the same container, and only one at a time (the newest wins). The result panel
        // is NOT updated: it is the picture of the moment of the search and fades away on its own. The chests saved for a "Keep highlights" box
        // that is fading out are kept up to date the same way (without notes), so checking the box again never brings back a stale place.
        // Moves made by other mods (a chest carried to another location ...) are left to the mod compatibility version: such a chest counts as gone.

        private const int ContainerCheckTicks = 15;              // about four times a second
        private const long ContainerNoteRepeatMs = 5000;
        private readonly Dictionary<Chest, long> containerNoteMs = new(ReferenceEqualityComparer.Instance);

        private enum ContainerFate { Same, Moved, Gone, Destroyed, PickedUp, IntoBackpack }

        private void CheckHighlightedContainers()
        {
            try
            {
                var live = new HashSet<GameLocation>(GetAllLocations().Select(l => l.location), ReferenceEqualityComparer.Instance);
                var moved = new List<(Chest Chest, string Name)>();
                var ended = new List<(Chest Chest, string Name, ContainerFate Fate)>();
                bool changed = false;

                for (int i = highlightedChests.Count - 1; i >= 0; i--)
                {
                    var (location, position, chest) = highlightedChests[i];
                    string name = InfoOf(chest, position).FullName;   // before it is updated
                    ContainerFate fate = LocateContainer(location, position, chest, live, out Vector2 now);
                    switch (fate)
                    {
                        case ContainerFate.Same:
                            continue;
                        case ContainerFate.Moved:
                            highlightedChests[i] = (location, now, chest);
                            moved.Add((chest, name));
                            break;
                        default:
                            highlightedChests.RemoveAt(i);
                            ended.Add((chest, name, fate));
                            break;
                    }
                    changed = true;
                }

                // the chests of a "Keep highlights" box that is fading out: the same, without notes
                if (keepSavedChests != null)
                {
                    for (int i = keepSavedChests.Count - 1; i >= 0; i--)
                    {
                        var (location, position, chest) = keepSavedChests[i];
                        ContainerFate fate = LocateContainer(location, position, chest, live, out Vector2 now);
                        if (fate == ContainerFate.Moved)
                            keepSavedChests[i] = (location, now, chest);
                        else if (fate != ContainerFate.Same)
                            keepSavedChests.RemoveAt(i);
                    }
                }

                if (!changed)
                    return;

                // a container that went into the backpack and holds something of the search: the backpack is highlighted now (in that container's colors)
                bool toBackpack = false;
                foreach (var (chest, _, fate) in ended)
                {
                    if (fate != ContainerFate.IntoBackpack || highlightMatcher is not Func<Item, bool> matcher)
                        continue;
                    if (!ItemsWithNested(GetBackpackItems()).Any(i => matcher(i)))
                        continue;
                    highlightBackpack = true;
                    toBackpack = true;
                    if (chestWordSlots.TryGetValue(chest, out int[]? slots))
                        backpackWordSlots = StripOrder(backpackWordSlots.Concat(slots));
                }

                edgeRouteFrom = null;           // the arrow works out its target again at once
                exitGraph = null;               // a building may have moved or gone: the ways between the locations are read again

                if (!AnyHighlight)
                    ClearHighlights();          // that was the last one: the arrow and the status line go too

                ShowContainerNote(moved, ended, toBackpack);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not check the highlighted containers: {ex.Message}", LogLevel.Trace);   // the highlights simply stay as they are
            }
        }

        /// <summary>One light note for what happened in one check (see TARGET MOVED OR GONE); nothing when every container concerned had a note a moment ago.</summary>
        private void ShowContainerNote(List<(Chest Chest, string Name)> moved, List<(Chest Chest, string Name, ContainerFate Fate)> ended, bool toBackpack)
        {
            long now = clock.ElapsedMilliseconds;
            bool Fresh(Chest chest)
            {
                if (containerNoteMs.TryGetValue(chest, out long last) && now - last < ContainerNoteRepeatMs)
                    return false;
                containerNoteMs[chest] = now;
                return true;
            }

            var newEnded = ended.Where(x => Fresh(x.Chest)).ToList();
            var newMoved = moved.Where(x => Fresh(x.Chest)).ToList();

            string? note = null;
            if (newEnded.Count > 1)
            {
                string what = ContainerWord(newEnded.Select(x => x.Chest).ToList());
                note = $"{newEnded.Count} {what} are gone: their highlights ended.";
            }
            else if (newEnded.Count == 1)
            {
                var (_, name, fate) = newEnded[0];
                note = fate switch
                {
                    ContainerFate.IntoBackpack when toBackpack => $"{name} is in your backpack now: your backpack is highlighted.",
                    ContainerFate.IntoBackpack or ContainerFate.PickedUp => $"{name} was picked up: its highlight ended.",
                    ContainerFate.Destroyed => $"{name} was destroyed: its items were dropped nearby.",
                    _ => $"{name} is gone: its highlight ended."
                };
            }
            else if (newMoved.Count > 0)
            {
                note = newMoved.Count == 1
                    ? $"{newMoved[0].Name} was moved: its highlight follows it."
                    : $"{newMoved.Count} {ContainerWord(newMoved.Select(x => x.Chest).ToList())} were moved: their highlights follow them.";
            }

            if (note != null)
                ShowLightNote(note);
        }

        /// <summary>
        /// Where a highlighted container is now (see TARGET MOVED OR GONE). Moved: <paramref name="newPosition"/> is its new tile and its footprint is
        /// updated. Only the container's own location is searched (vanilla moves never leave it); a container it cannot find there is gone, unless it is
        /// in the backpack or (one level) in a chest.
        /// </summary>
        private ContainerFate LocateContainer(GameLocation location, Vector2 position, Chest chest, HashSet<GameLocation> live, out Vector2 newPosition)
        {
            newPosition = position;
            if (!live.Contains(location))
                return ContainerFate.Gone;   // its building was demolished: everything inside went with it

            ContainerInfo info = InfoOf(chest, position);
            ContainerFate Follow(Rectangle footprint, out Vector2 to)
            {
                containerInfo[chest] = new ContainerInfo(info.Kind, footprint, info.FullName);
                to = new Vector2(footprint.X, footprint.Y);
                return to == position && footprint == info.Footprint ? ContainerFate.Same : ContainerFate.Moved;
            }

            // a dresser (its stand-in chest)
            StorageFurniture? dresser = GetDresserOf(chest);
            if (dresser != null)
            {
                if (location.furniture.Contains(dresser))
                {
                    Rectangle box = dresser.GetBoundingBox();
                    return Follow(new Rectangle(box.X / 64, box.Y / 64, Math.Max(1, (box.Width + 63) / 64), Math.Max(1, (box.Height + 63) / 64)), out newPosition);
                }
                return WhereDidItGo(dresser);
            }

            // a mannequin (its stand-in chest): it can be picked up and put somewhere else like a chest
            Mannequin? mannequin = GetMannequinOf(chest);
            if (mannequin != null)
            {
                foreach (var pair in location.objects.Pairs)
                {
                    if (ReferenceEquals(pair.Value, mannequin))
                        return Follow(new Rectangle((int)pair.Key.X, (int)pair.Key.Y - 1, 1, 2), out newPosition);
                }
                return WhereDidItGo(mannequin);
            }

            // the Lost and Found box never moves (taking everything out of it only leaves it empty)
            if (info.Kind == LostAndFoundKind)
                return ContainerFate.Same;

            // the built-in fridge of a house: it never moves; it only goes with its house
            if (info.Kind == FridgeKind)
                return ReferenceEquals(location.GetFridge(), chest) ? ContainerFate.Same : ContainerFate.Gone;

            // the storage of a building (Mill, Junimo Hut ...): follows its building
            if (info.Kind == MillKind || info.Kind == JunimoHutKind || info.Kind.Key.StartsWith("building:", StringComparison.Ordinal))
            {
                foreach (Building building in location.buildings)
                {
                    if (GetBuildingStorage(building).Any(c => ReferenceEquals(c, chest)))
                        return Follow(new Rectangle(building.tileX.Value, building.tileY.Value, Math.Max(1, building.tilesWide.Value), Math.Max(1, building.tilesHigh.Value)), out newPosition);
                }
                return ContainerFate.Gone;
            }

            // an Auto-Grabber (its storage is the chest it holds)
            if (info.Kind == AutoGrabberKind)
            {
                if (location.objects.TryGetValue(position, out StardewValley.Object? here) && ReferenceEquals(here.heldObject.Value, chest))
                    return ContainerFate.Same;
                foreach (var pair in location.objects.Pairs)
                {
                    if (ReferenceEquals(pair.Value.heldObject.Value, chest))
                        return Follow(new Rectangle((int)pair.Key.X, (int)pair.Key.Y, 1, 1), out newPosition);
                }
                StardewValley.Object? grabber = Game1.player?.Items.OfType<StardewValley.Object>().FirstOrDefault(o => ReferenceEquals(o.heldObject.Value, chest));
                return grabber != null ? ContainerFate.PickedUp : ContainerFate.Gone;
            }

            // a placed chest of any size, a mini-fridge
            if (location.objects.TryGetValue(position, out StardewValley.Object? atTile) && ReferenceEquals(atTile, chest))
                return ContainerFate.Same;
            foreach (var pair in location.objects.Pairs)
            {
                if (ReferenceEquals(pair.Value, chest))
                    return Follow(new Rectangle((int)pair.Key.X, (int)pair.Key.Y, 1, 1), out newPosition);
            }
            ContainerFate fate = WhereDidItGo(chest);

            // shoved by a villager with no free tile around it (next to water ...): the game destroyed it and dropped its items where it stood
            if (fate == ContainerFate.Gone && location.debris.Any(d => d.item != null && d.Chunks.Count > 0 && Vector2.Distance(d.Chunks[0].position.Value / 64f, position) <= 4f))
                return ContainerFate.Destroyed;
            return fate;
        }

        /// <summary>A container that left its place: in the backpack, in a chest (one level), or gone.</summary>
        private ContainerFate WhereDidItGo(Item container)
        {
            if (Game1.player != null && Game1.player.Items.Any(i => ReferenceEquals(i, container)))
                return ContainerFate.IntoBackpack;

            foreach (var (_, _, chest, _) in GetAllContainers())
            {
                if (chest.Items.Any(i => ReferenceEquals(i, container)))
                    return ContainerFate.PickedUp;
            }
            return ContainerFate.Gone;
        }

        private void OnAudit(string command, string[] args)
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();

                Monitor.Log("Running the item audit, this can take a little while ...", LogLevel.Info);
                foreach (string line in suggestionIndex.RunAudit())
                    Monitor.Log(line, line.StartsWith("Audit done", StringComparison.Ordinal) ? LogLevel.Info : LogLevel.Warn);
            }
            catch (Exception ex)
            {
                Monitor.Log($"The item audit failed: {ex.Message}", LogLevel.Error);
            }
        }

        // ===================== CONSOLE SEARCH (find_item) =====================
        // The SMAPI console command. It is the search box without the box: the same checks (SearchMenu.CheckQuery, and a product the game never
        // makes), the same search (ScanChests), so the panel, the highlights and the arrows in the game are exactly the same. On top of that it
        // writes a plain text list to the console that can be read and copied: the panel's title, then one line per container, nearest first
        // (walking distance), with its place, its name, its tile and the matching items (count, and the stars of items that can have them).
        // Mostly used by players looking for a lost item. Read only: nothing is created, moved or changed, and nothing is added to the search
        // history (that belongs to the search box). A quality is typed into the text like in the box: "gold star apple".
        private const string FindItemUsage = "Usage: find_item <search>, e.g. find_item starfruit / find_item fruit, fish / find_item gold star apple";

        /// <summary>True while find_item runs its search: ScanChests then leaves the console to it (no short one-line log of its own).</summary>
        private bool consoleSearchRunning;

        private void OnFindItem(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Monitor.Log("Load a save first. " + FindItemUsage, LogLevel.Info);
                return;
            }

            string text = string.Join(" ", args).Trim();
            if (text.Length == 0)
            {
                Monitor.Log(FindItemUsage, LogLevel.Info);
                return;
            }

            try
            {
                CreateSuggestionProvider();   // the word lists the search needs (aliases, spellings, "Did you mean"), made ready as when the box opens

                // the same refusals as Enter in the search box, with the same red hint text
                string? refusal = SearchMenu.CheckQuery(text, out string query, out int? qualityInText);
                if (refusal != null)
                {
                    Monitor.Log(refusal, LogLevel.Info);
                    return;
                }

                // a product the game never makes ("corn jelly"): the box's two lines, nothing is searched
                string plainQuery = NormalizeQueryText(query);
                if (!HasCommas(query) && GetImpossibleProduct(plainQuery) is SuggestionIndex.ImpossibleProduct impossible)
                {
                    Monitor.Log($"There's no {impossible.Missing}. {impossible.Subject}{impossible.Middle}{impossible.Link}{impossible.Tail}", LogLevel.Info);
                    var others = impossible.Alternatives.Select(StripCategorySuffix).ToList();
                    if (impossible.Split != null)
                        others.Add(impossible.Split);
                    if (others.Count > 0)
                        Monitor.Log(DidYouMeanLabel + string.Join(", ", others), LogLevel.Info);
                    return;
                }

                SearchReport report;
                consoleSearchRunning = true;
                try
                {
                    report = ScanChests(query, qualityInText ?? AnyQuality);
                }
                finally
                {
                    consoleSearchRunning = false;
                }

                if (report.Outcome == SearchOutcome.NoMatchingQuality)
                {
                    Monitor.Log("Item found, but no matching quality. Try another quality.", LogLevel.Info);
                    return;
                }

                foreach (string line in BuildConsoleResult())
                    Monitor.Log(line, LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"The search failed: {ex.Message}", LogLevel.Error);
                Monitor.Log(ex.ToString(), LogLevel.Trace);
            }
        }

        /// <summary>
        /// The console text of the search that just ran: the panel's title; then, when something was found, one numbered line per container,
        /// nearest first ("1. Backpack: Apple x2", "2. Farm, Chest at tile (64, 15): Apple x12 (gold 3, no star 9)"); when nothing was found, the
        /// panel's "Did you mean" words. A fourth word that was not searched gets the panel's note at the end.
        /// </summary>
        private List<string> BuildConsoleResult()
        {
            var lines = new List<string> { notifyTitle };

            if (resultFailed)
            {
                if (resultSuggestions.Count > 0)
                    lines.Add(resultHintLead + string.Join(", ", resultSuggestions.Select(s => s.Label)));
            }
            else
            {
                Func<Item, bool> matches = highlightMatcher ?? (_ => false);
                var rows = new List<(float Distance, string Text)>();

                if (highlightBackpack)
                    rows.Add((-1f, "Backpack: " + DescribeConsoleItems(GetBackpackItems(), matches)));

                // walking distances from where the player stands now (the same order as the place list of the panel)
                GameLocation? here = Game1.currentLocation;
                Vector2 playerTile = Game1.player?.Tile ?? Vector2.Zero;
                int playerWeight = GetMapWeight(here);
                Dictionary<GameLocation, (float Cost, Vector2? Arrival, Point FirstExit)>? reach = null;
                try
                {
                    if (here != null)
                        reach = ComputeReach(GetExitGraph(), here, playerTile);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Could not work out the walking distances: {ex.Message}", LogLevel.Trace);   // the rough map order is used
                }

                var placeLabels = new Dictionary<GameLocation, string>(ReferenceEqualityComparer.Instance);
                foreach (var (location, label) in GetAllLocations())
                    placeLabels[location] = label;

                foreach (var (location, position, chest) in highlightedChests)
                {
                    string? islandArea = GetIslandArea(location.Name);
                    string place = islandArea != null ? $"Ginger Island ({islandArea})"
                        : placeLabels.TryGetValue(location, out string? label) ? label
                        : GetFriendlyName(location.Name);
                    string name = InfoOf(chest, position).FullName;
                    float distance = GetPlaceSortKey(location, position, here, playerTile, playerWeight, reach);
                    rows.Add((distance, $"{place}, {name} at tile ({(int)position.X}, {(int)position.Y}): {DescribeConsoleItems(CurrentItems(chest), matches)}"));
                }

                int number = 1;
                foreach (var row in rows.OrderBy(r => r.Distance))   // a stable sort: the same distance keeps the order the containers were found in
                    lines.Add($"  {number++}. {row.Text}");
            }

            if (lastIgnoredTerms.Count > 0)
            {
                lines.Add($"Only {MaxSearchTerms} at a time: {string.Join(", ", lastIgnoredTerms.Select(t => "\"" + t + "\""))} "
                    + (lastIgnoredTerms.Count == 1 ? "was" : "were") + " not searched.");
            }
            if (lostOnDeathNote.Length > 0)
                lines.Add(lostOnDeathNote);

            return lines;
        }

        /// <summary>
        /// The matching items of one container for the console: "Apple x12 (gold 3, no star 9), Starfruit x5". An item inside a carried container
        /// gets its name in front ("Birch Dresser > Shirt x5"); the stars are only written for an item that can have them and has at least one.
        /// </summary>
        private static string DescribeConsoleItems(IEnumerable<Item?> items, Func<Item, bool> matches)
        {
            var names = new List<string>();
            var byQuality = new Dictionary<string, SortedDictionary<int, int>>();
            var canHaveStars = new HashSet<string>();

            foreach (var (item, inside, _) in WithNested(items))
            {
                if (!matches(item))
                    continue;
                string name = GetShownItemName(item);
                if (string.IsNullOrWhiteSpace(name))
                    name = item.DisplayName;
                if (inside != null)
                    name = inside + " > " + name;

                if (!byQuality.TryGetValue(name, out SortedDictionary<int, int>? counts))
                {
                    byQuality[name] = counts = new SortedDictionary<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));   // best star first
                    names.Add(name);
                }
                counts[item.Quality] = counts.GetValueOrDefault(item.Quality) + item.Stack;
                if (CanHaveQuality(item))
                    canHaveStars.Add(name);
            }

            return string.Join(", ", names.Select(name =>
            {
                SortedDictionary<int, int> counts = byQuality[name];
                string text = $"{name} x{counts.Values.Sum()}";
                if (canHaveStars.Contains(name) && counts.Keys.Any(q => q > 0))
                    text += " (" + string.Join(", ", counts.Select(pair => $"{ConsoleStarName(pair.Key)} {pair.Value}")) + ")";
                return text;
            }));
        }

        private static string ConsoleStarName(int quality) => quality switch
        {
            1 => "silver",
            2 => "gold",
            4 => "iridium",
            _ => "no star"
        };

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
            // the names players know (the Parrot Express stops: Docks, Farm, Dig Site, Jungle), not N / S / E / W. An area is only ever listed when a
            // container was found there, so a place no chest can stand on (the volcano ...) never shows up
            ["IslandNorth"] = "Dig Site",
            ["IslandEast"] = "Jungle",
            ["IslandSouth"] = "Docks",
            ["IslandSouthEast"] = "Tide Pool",
            ["IslandWest"] = "Farm"
        };

        private static readonly string[] IslandAreaOrder = { "Dig Site", "Jungle", "Tide Pool", "Docks", "Farm" };

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

        // The places of the panel, nearest to the player first. The distances come from ScanChests (GetPlaceSortKey): a place is as far as its nearest
        // matching chest. Places with the same distance (or without one) keep the order the chests were found in. Ginger Island is ONE entry,
        // "Ginger Island (Docks, Farm x2)", sorted by its nearest area, so it is last unless the player is on the island; inside it the areas are
        // nearest first too, then in their usual order (Dig Site, Jungle, Tide Pool, Docks, Farm).
        // noteOf / islandNoteOf: the note of a place / of an island area ("fridge", "Auto-Grabbers", see PlaceNote): "Farmhouse (fridge)",
        // "Barn x2 (Auto-Grabbers)", and on the island right after the area: "Ginger Island (Farm, Farmhouse: fridge)". Most places get none.
        private static string BuildPlaceSummary(Dictionary<string, int> mainCounts, Dictionary<string, int> islandCounts,
            Dictionary<string, float>? mainDistances = null, Dictionary<string, float>? islandDistances = null,
            Func<string, string>? noteOf = null, Func<string, string>? islandNoteOf = null)
        {
            float DistanceOf(Dictionary<string, float>? distances, string key)
            {
                return distances != null && distances.TryGetValue(key, out float value) ? value : float.MaxValue;
            }

            var parts = new List<(float Distance, int Order, string Text)>();
            int order = 0;

            foreach (var pair in mainCounts)
            {
                string note = noteOf?.Invoke(pair.Key) ?? "";
                string text = FormatCount(pair.Key, pair.Value) + (note.Length > 0 ? " (" + note + ")" : "");
                parts.Add((DistanceOf(mainDistances, pair.Key), order++, text));
            }

            if (islandCounts.Count > 0)
            {
                var orderedAreas = islandCounts
                    .OrderBy(p => DistanceOf(islandDistances, p.Key))
                    .ThenBy(p => { int i = Array.IndexOf(IslandAreaOrder, p.Key); return i < 0 ? IslandAreaOrder.Length : i; })
                    .ThenBy(p => p.Key)
                    .ToList();
                var areas = orderedAreas.Select(p =>
                {
                    string note = islandNoteOf?.Invoke(p.Key) ?? "";
                    return FormatCount(p.Key, p.Value) + (note.Length > 0 ? ": " + note : "");
                });
                float nearestArea = orderedAreas.Min(p => DistanceOf(islandDistances, p.Key));
                parts.Add((nearestArea, order++, "Ginger Island (" + string.Join(", ", areas) + ")"));
            }

            return string.Join(", ", parts.OrderBy(p => p.Distance).ThenBy(p => p.Order).Select(p => p.Text));
        }

        // ===================== DISTANCE OF A PLACE FROM THE PLAYER (order of the places in the result panel) =====================
        // The sort key of one chest, smaller = nearer: its WALKING DISTANCE (the same distance the arrow uses: a barn whose door is next to the
        // player comes before a chest at the other end of the farm). Only a place that cannot be reached on foot falls back to the map weights below,
        // after every reachable place. The map weights are a rough "walking distance from the farm", so such places are in a sensible order from wherever the
        // player is: standing on the farm, the order is Farm buildings, Backwoods, Bus Stop, Cindersap Forest, Town, Mountain, Beach, Railroad, Mines,
        // Desert, unknown maps and Ginger Island last; standing in town, the Bus Stop and the Forest come before the farm. A map of another mod
        // that has no weight counts as UnknownMapWeight. Every Ginger Island map weighs IslandWeight, so the island is the farthest of all; a player
        // standing on the island sees its other areas before the mainland.
        private const int FarmBuildingWeight = 20;   // the inside of a barn, coop, shed, cabin ... (they stand on the farm)
        private const int UnknownMapWeight = 1200;
        private const int IslandWeight = 100000;

        private static readonly Dictionary<string, int> MapWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Farm"] = 0,
            ["FarmHouse"] = 10,
            ["Cellar"] = 12,
            ["Greenhouse"] = 15,
            ["FarmCave"] = 40,
            ["Backwoods"] = 100,
            ["BusStop"] = 250,
            ["Forest"] = 300,
            ["Town"] = 500,
            ["Mountain"] = 600,
            ["Beach"] = 700,
            ["Railroad"] = 800,
            ["Mine"] = 1000,
            ["Desert"] = 1500
        };

        private static int GetMapWeight(GameLocation? location)
        {
            if (location == null)
                return UnknownMapWeight;
            if (GetIslandArea(location.Name) != null)
                return IslandWeight;
            if (location.ParentBuilding != null)
                return FarmBuildingWeight;
            if (MapWeights.TryGetValue(location.Name, out int weight))
                return weight;
            if (location.Name.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase))
                return 1100;
            return UnknownMapWeight;
        }

        // The sort key of one container: its WALKING DISTANCE (see there) when it can be reached on foot; otherwise after every reachable one, in the
        // order of the map weights above. reach = ComputeReach for the player's location (null: no player location, only the weights count).
        private static float GetPlaceSortKey(GameLocation location, Vector2 chestTile, GameLocation? playerLocation, Vector2 playerTile, int playerMapWeight,
            Dictionary<GameLocation, (float Cost, Vector2? Arrival, Point FirstExit)>? reach)
        {
            if (ReferenceEquals(location, playerLocation))
                return Vector2.Distance(chestTile, playerTile);

            if (reach != null && reach.TryGetValue(location, out var way))
                return way.Cost + (way.Arrival is Vector2 arrival ? Vector2.Distance(arrival, chestTile) : 0f);

            return UnreachableDistance + Math.Abs(GetMapWeight(location) - playerMapWeight);
        }

        // ===================== "DID YOU MEAN" (failed search) =====================
        // For every misspelled word (the words of the "(Invalid: ...)" kind: not a category, not a name the game has) the closest real item names;
        // the best name of each word comes first, then the second best of each, up to MaxSpellingSuggestions in all. A word that IS an item or a
        // category (it only has no chest) or a number gets nothing: it was not a typo.
        // THE one place that works out the correction names of a word. The instant line under the search box (GetInstantSpelling) and the result
        // panel after Enter (BuildSpellingSuggestions) both call it, so the same word always gets the same names, in the same order.
        // The names come back like FindSimilar writes them ("Fish (category)"); the callers only differ in how they show them.
        private IReadOnlyList<string> FindSpellingNames(string word)
        {
            suggestionIndex ??= new SuggestionIndex();
            suggestionIndex.EnsureBuilt();
            return suggestionIndex.FindSimilar(StripCategorySuffix(word ?? ""), MaxSpellingSuggestions, includeCategories: true);
        }

        private List<(string Label, string Query)> BuildSpellingSuggestions(List<string> terms, List<string> invalidTerms, string queryText)
        {
            var result = new List<(string Label, string Query)>();

            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();

                // Several words typed without a comma ("Iridium Sprinkleh"): the WHOLE phrase is corrected first, as one name. The words that are
                // already right ("Iridium") are part of the match, so "Iridium Sprinkler" wins; correcting the broken word alone would only find
                // "Sprinkler" or "Quality Sprinkler", which ignore what the player already typed correctly. A click searches the whole name.
                if (invalidTerms.Count > 0 && terms.Count >= 2 && !HasCommas(queryText ?? ""))
                {
                    IReadOnlyList<string> joint = FindSpellingNames(queryText ?? "");
                    if (joint.Count > 0)
                    {
                        foreach (string name in joint)
                        {
                            string shown = StripCategorySuffix(name);
                            if (!result.Any(r => string.Equals(r.Label, shown, StringComparison.OrdinalIgnoreCase)))
                                result.Add((shown, name));
                        }
                        return result;
                    }
                }

                var similar = new IReadOnlyList<string>[terms.Count];
                for (int t = 0; t < terms.Count; t++)
                {
                    string term = terms[t];
                    // a typo = a word that nothing in the game matches (the same test that makes the title say Invalid). A half-typed word that
                    // exists in the game ("artisa", "starf") is never a typo, however close it is spelled to something else.
                    bool typo = invalidTerms.Contains(term)
                        && !int.TryParse(term, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                        && !IsCategoryWord(term)
                        && !ExistsInGame(term);
                    similar[t] = typo ? FindSpellingNames(term) : Array.Empty<string>();
                }

                result = AssembleSuggestions(terms, similar);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not work out the spelling suggestions: {ex.Message}", LogLevel.Trace);
                result.Clear();   // no suggestions rather than a crash: the failed search is reported as before
            }

            return result;
        }

        // The instant correction under the search box (before Enter is pressed): for the word being typed, the names spelled most like it, but ONLY when
        // nothing in the game matches that word at all (the same test that makes a finished search say Invalid: not a number, not a category, not a name
        // or part of a name). A word that exists, even half typed ("starf", "artisa"), gets nothing. This is the same spelling match as the result
        // panel's "Did you mean" row (SuggestionIndex.FindSimilar, junk blacklist included) and has nothing to do with the auto-complete drop-down or
        // the search itself: it only reads, never changes anything. Names come back like FindSimilar writes them ("Fish (category)").
        private IReadOnlyList<string> GetInstantSpelling(string typedWord)
        {
            try
            {
                string text = StripCategorySuffixWhileTyping(typedWord ?? "").Trim();
                if (text.Length < 3
                    || int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                    || !SearchMenu.IsTermSearchable(text)
                    || IsCategoryWord(text)
                    || ExistsInGame(text))
                    return Array.Empty<string>();

                return FindSpellingNames(text);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not work out the instant spelling suggestions: {ex.Message}", LogLevel.Trace);
                return Array.Empty<string>();   // no row rather than a crash
            }
        }

        // Prepares the instant correction for one opening of the search box (null = the box simply has no such row).
        private Func<string, IReadOnlyList<string>>? CreateSpellingProvider()
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();
                return GetInstantSpelling;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Instant spelling suggestions are unavailable: {ex.Message}", LogLevel.Trace);
                return null;
            }
        }

        // Turns the names found for every word into the words of the row: the best name of each word first, then the second best of each, up to
        // MaxSpellingSuggestions in all. Each word also carries the whole query that a click searches: the terms with the misspelled one replaced.
        private static List<(string Label, string Query)> AssembleSuggestions(List<string> terms, IReadOnlyList<string>[] similar)
        {
            var result = new List<(string Label, string Query)>();
            var shownNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            for (int rank = 0; rank < MaxSpellingSuggestions && result.Count < MaxSpellingSuggestions; rank++)
            {
                for (int t = 0; t < terms.Count && result.Count < MaxSpellingSuggestions; t++)
                {
                    if (rank >= similar[t].Count)
                        continue;

                    string name = similar[t][rank];   // "Fish (category)" for a category word: the searched query keeps it
                    int replaced = t;
                    string query = string.Join(", ", terms.Select((word, index) => index == replaced ? name : word));
                    string shown = StripCategorySuffix(name);   // the row only shows "Fish"; the "(category)" label waits for the hover tip (GetTermTip)
                    if (shownNames.Add(shown))
                        result.Add((shown, query));
                }
            }

            return result;
        }

        /// <summary>
        /// The "Did you mean" words that fit into the panel: what is shown, what a click searches, the pixel width of the shown text and its hover tip.
        /// <paramref name="rowWidth"/> is the width of the whole row: the label, the words and the commas between them.
        /// Empty (and width 0) unless the panel has suggestions (only a failed search has them).
        /// </summary>
        private List<(string Label, string Query, int Width, string? Tip)> LayoutSuggestionChips(int maxWidth, out int rowWidth)
        {
            var chips = new List<(string Label, string Query, int Width, string? Tip)>();
            rowWidth = 0;
            if (resultSuggestions.Count == 0)
                return chips;

            SpriteFont font = Game1.smallFont;
            int commaWidth = (int)Math.Ceiling(font.MeasureString(", ").X);
            int leadWidth = (int)Math.Ceiling(font.MeasureString(resultHintLead).X);

            // A word is only ever shown COMPLETE (the same rule as the line under the search box). The words come best first, so one that does not fit
            // into what is left of the row is left out (a later, shorter one may still fit): better two whole words than a third one cut to "...".
            // Only a first word that is wider than the whole row on its own is shortened.
            int used = leadWidth;
            for (int i = 0; i < resultSuggestions.Count; i++)
            {
                string label = resultSuggestions[i].Label;
                int width = (int)Math.Ceiling(font.MeasureString(label).X);
                int separator = chips.Count > 0 ? commaWidth : 0;

                if (used + separator + width > maxWidth)
                {
                    if (chips.Count > 0)
                        continue;   // would be cut: left out

                    label = FitText(label, Math.Max(40, maxWidth - used), font);
                    width = (int)Math.Ceiling(font.MeasureString(label).X);
                }

                string? tip = i < resultSuggestionTips.Count ? resultSuggestionTips[i] : null;
                chips.Add((label, resultSuggestions[i].Query, width, tip));
                used += separator + width;
            }

            rowWidth = used;
            return chips;
        }

        // A left click on one of the "Did you mean" words: the search box opens with that word typed in and searches it at once (the star filter of the
        // search the words belong to is kept). The click is swallowed, so it does not also swing a tool.
        // The mouse is read in UI coordinates (getMousePosition(true)) because the words are drawn by the HUD, which uses the UI scale; the plain
        // getMousePosition() would use the world zoom in this event and miss the words whenever the two scales differ.
        private void OnSuggestionClicked(object? sender, ButtonPressedEventArgs e)
        {
            if (IsHudClickTaken(e.Button))
                return;   // the "Copy" bubble used this click
            if (e.Button != SButton.MouseLeft)
                return;
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;

            long now = clock.ElapsedMilliseconds;
            if (now - suggestionDrawnMs > SuggestionFreshMs)
                return;   // the words are not on screen right now
            if (resultSuggestions.Count == 0 || now >= notifyUntilMs)
                return;   // the panel is gone, or it has no "Did you mean" words

            Point mouse = Game1.getMousePosition(true);
            foreach (var (bounds, query) in suggestionHitBoxes.ToList())
            {
                if (!bounds.Contains(mouse))
                    continue;

                Helper.Input.Suppress(e.Button);
                Game1.playSound("smallSelect");

                // the old highlights stay until the clicked word's search really runs (it starts at once, see SearchMenu autoSearchText; ScanChests
                // replaces them, and releases the "Keep highlights" lock, only then)

                Game1.activeClickableMenu = new SearchMenu(ScanChests, searchHistory, SaveSearchHistory, null, CreateSuggestionProvider(), query, resultQuality, GetTermTip, CreateSpellingProvider(), TextRefersOnlyToNoQualityItems);
                return;
            }
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
            ["clothes"] = new[] { -100 },            // hidden second spelling: searched and named as "clothing", never shown as "clothes"
            ["trinket"] = new[] { -101 },
            ["trinkets"] = new[] { -101 },           // Object.trinketCategory
            ["book"] = new[] { -102 },
            ["books"] = new[] { -102 }               // Object.booksCategory
            // "litter" is deliberately NOT a category any more: typing it only does the literal name match
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

        // P1b: dynamic ("flavored") items. The game builds these on the fly (FLAVORED_ITEM, Wiki: Modding:Item queries; types Wine, Jelly, Pickle,
        // Juice, Roe, AgedRoe, Honey, Bait, DriedFruit, DriedMushroom, SmokedFish): "Peach Wine", "Wild Honey", "Aged Sturgeon Roe" ... all share the item id
        // of their plain base product ("Wine" is (O)348). Searching the base word finds the plain item AND every flavored variant.
        //
        // Matching is BY ITEM ID ONLY for the real categories (no name matching), because a name match would also catch unrelated items: the 1.6 fishing
        // products "Cave Jelly", "River Jelly" and "Sea Jelly" have "jelly" in their name but are NOT (O)344 jelly. Ids used (Wiki, Modding:Items):
        //   Wine (O)348, Juice (O)350, Jelly (O)344, Pickles (O)342, Honey (O)340, Roe (O)812 (fish pond), Aged Roe (O)447 (processed; separate from Roe),
        //   Smoked Fish (O)SmokedFish, Dried Fruit (O)DriedFruit, Dried Mushrooms (O)DriedMushrooms, Targeted Bait (O)SpecificBait.
        // The fishing jellies are (O)CaveJelly, (O)RiverJelly and (O)SeaJelly; as a safety net they are also matched by their exact (not partial) name.
        //
        // IsCategory = true : a real category. The drop-down and the result title label it "(category)".
        // IsCategory = false: a plain search word for one specific kind ("cave jelly", ...): same id matching, but no "(category)" label.
        // Words are stored without spaces / hyphens (see Norm), so "aged roe", "Aged_Roe" and "agedroe" are the same word.
        private sealed class DynamicCategory
        {
            public readonly string[] Words;           // the words that trigger it (Norm form)
            public readonly string[] QualifiedIds;    // base item ids of the product
            public readonly string[] ExactNames;      // whole item names (Norm form) that also match; never a partial name match
            public readonly bool IsCategory;          // true = labeled "(category)"

            public DynamicCategory(string[] words, string[] qualifiedIds, string[] exactNames, bool isCategory)
            {
                Words = words;
                QualifiedIds = qualifiedIds;
                ExactNames = exactNames;
                IsCategory = isCategory;
            }

            public bool Matches(Item item)
            {
                if (Array.IndexOf(QualifiedIds, item.QualifiedItemId) >= 0)
                    return true;
                if (ExactNames.Length == 0)
                    return false;

                return Array.IndexOf(ExactNames, Norm(item.DisplayName)) >= 0
                    || Array.IndexOf(ExactNames, Norm(item.Name)) >= 0;
            }
        }

        private static readonly char[] NameWordSeparators = { ' ', '\t', '_', '-', '\'' };

        // ===================== WORD FAMILIES (clothing and furniture) =====================
        // Normally a whole item name finds only that item ("Coal", "Stone"). Clothing and furniture are different: the game has a plain "Shirt"
        // (dozens of them), "Pants" and "Bed", and next to them hundreds of "... Shirt", "... Pants", "... Bed". A player who types "shirt" means
        // shirts, not only the plain one. So when the whole name is a clothing / furniture item AND other clothing / furniture of the game have that
        // name as whole words in theirs, the word finds the whole family: the plain item and every clothing / furniture with those words
        // ("Shirt" -> Shirt, Bridal Shirt, Flannel Shirt ...; "tank top" -> Tank Top, Crop Tank Top (F) ...). "Bridal Shirt" has no family (no other
        // name holds it), so it still finds only itself. In every list (drop-down, tip, "Did you mean") the plain item comes first.
        // Honey and Bait need none of this: they are category words of their own (every honey, every bait).

        private readonly Dictionary<string, bool> wordFamilyCache = new();

        /// <summary>Clothing (shirts, pants) and furniture (beds included): the kinds that have word families.</summary>
        private static bool IsWordFamilyKind(Item item)
        {
            return item is Clothing || item is Furniture;
        }

        /// <summary>True if the name holds the normalized words as one run of whole words ("Crop Tank Top (F)" holds "tanktop", not "anktop").</summary>
        private static bool HasWholeWordRun(string? name, string normWords)
        {
            string[] words = SplitNameWords(name).Select(Norm).Where(w => w.Length > 0).ToArray();
            for (int start = 0; start < words.Length; start++)
            {
                string run = "";
                for (int end = start; end < words.Length && run.Length < normWords.Length; end++)
                {
                    run += words[end];
                    if (run == normWords)
                        return true;
                }
            }
            return false;
        }

        /// <summary>True if the item belongs to the word family of the normalized name (the plain item itself included).</summary>
        private static bool IsInWordFamily(Item item, string normName)
        {
            return IsWordFamilyKind(item) && (HasWholeWordRun(item.DisplayName, normName) || HasWholeWordRun(item.Name, normName));
        }

        /// <summary>
        /// True if the normalized name is a clothing / furniture item of the game that has a word family: some OTHER clothing / furniture of the game
        /// holds it as whole words ("shirt", "pants", "bed", "tanktop"). Worked out once per name.
        /// </summary>
        internal bool IsWordFamilyName(string normName)
        {
            if (string.IsNullOrEmpty(normName))
                return false;
            if (wordFamilyCache.TryGetValue(normName, out bool known))
                return known;

            bool result = false;
            try
            {
                List<Item> catalog = GetGameItemCatalog();
                bool plainIsFamilyKind = catalog.Any(i => IsWordFamilyKind(i) && NameEquals(i, normName));
                result = plainIsFamilyKind && catalog.Any(i => !NameEquals(i, normName) && IsInWordFamily(i, normName));
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check the word family of '{normName}': {ex.Message}", LogLevel.Trace);
            }

            wordFamilyCache[normName] = result;
            return result;
        }

        /// <summary>The names of a word family, as the game shows them (the plain name first is up to the caller), each once.</summary>
        private HashSet<string> GetWordFamilyNames(string normName)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Item item in GetGameItemCatalog())
            {
                if (IsInWordFamily(item, normName) && !string.IsNullOrWhiteSpace(item.DisplayName))
                    names.Add(Norm(item.DisplayName));
            }
            return names;
        }

        private static IEnumerable<string> SplitNameWords(string? name)
        {
            return (name ?? "").ToLowerInvariant().Split(NameWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        private static readonly string[] NoNames = Array.Empty<string>();

        private static readonly DynamicCategory[] DynamicCategories =
        {
            // --- real categories (labeled "(category)"), matched by item id only ---
            new(new[] { "wine", "wines" },                          new[] { "(O)348" },           NoNames, isCategory: true),
            new(new[] { "juice", "juices" },                        new[] { "(O)350" },           NoNames, isCategory: true),
            new(new[] { "jelly", "jellies" },                       new[] { "(O)344" },           NoNames, isCategory: true),   // ONLY (O)344: never Cave / River / Sea Jelly
            new(new[] { "fishingjelly", "fishingjellies" },         new[] { "(O)CaveJelly", "(O)RiverJelly", "(O)SeaJelly" },
                                                                                                  new[] { "cavejelly", "riverjelly", "seajelly" }, isCategory: true),
            new(new[] { "pickles", "pickle" },                      new[] { "(O)342" },           NoNames, isCategory: true),
            new(new[] { "honey", "honeys" },                        new[] { "(O)340" },           NoNames, isCategory: true),   // every honey variant (Wild, Tulip, Blue Jazz, ...)
            new(new[] { "roe", "roes" },                            new[] { "(O)812" },           NoNames, isCategory: true),   // roe from the fish pond (plain and flavored): NOT aged roe
            new(new[] { "agedroe", "agedroes" },                    new[] { "(O)447" },           NoNames, isCategory: true),   // roe aged in a preserves jar / keg: a separate thing from roe
            new(new[] { "smokedfish" },                             new[] { "(O)SmokedFish" },    NoNames, isCategory: true),
            new(new[] { "driedfruit", "driedfruits" },              new[] { "(O)DriedFruit" },    NoNames, isCategory: true),
            new(new[] { "driedmushroom", "driedmushrooms" },        new[] { "(O)DriedMushrooms" }, NoNames, isCategory: true),
            new(new[] { "targetedbait", "specificbait" },           new[] { "(O)SpecificBait" },  NoNames, isCategory: true),

            // --- independent search words (no "(category)" label): the three single fishing jellies ---
            new(new[] { "cavejelly", "cavejellies" },               new[] { "(O)CaveJelly" },     new[] { "cavejelly" },  isCategory: false),
            new(new[] { "riverjelly", "riverjellies" },             new[] { "(O)RiverJelly" },    new[] { "riverjelly" }, isCategory: false),
            new(new[] { "seajelly", "seajellies" },                 new[] { "(O)SeaJelly" },      new[] { "seajelly" },   isCategory: false)
        };

        /// <summary>True if the text is one of the dynamic words above (category or not): the search understands it as ONE thing.</summary>
        private static bool IsDynamicWord(string phrase)
        {
            return FindDynamicCategory(Norm(phrase ?? "")) != null;
        }

        private static DynamicCategory? FindDynamicCategory(string normQuery)
        {
            if (normQuery.Length == 0)
                return null;

            foreach (DynamicCategory category in DynamicCategories)
            {
                if (Array.IndexOf(category.Words, normQuery) >= 0)
                    return category;
            }
            return null;
        }

        private static readonly char[] QuerySeparators = { ' ', '\t', '_', '-' };

        // lower-case and drop spaces, underscores and hyphens: "Star Fruit" -> "starfruit"
        // ===================== ITEM NAMES WITH BRACKETS OF THEIR OWN =====================
        // Brackets in the search text are normally only legal as the " (category)" label. A few items have brackets in their real name
        // ("Crop Tank Top (F)" / "(M)"): such a name, typed whole (case, spaces, hyphens ignored), is a normal search word, brackets and all.
        // The list is read from the game's own items (every kind a chest can hold), so items of other mods are covered too.

        private static Dictionary<string, string>? bracketItemNames;   // Norm(name) -> name as the game shows it

        /// <summary>Every item name of the game that has a round bracket (half-width or full-width), sorted.</summary>
        internal static List<string> GetBracketItemNames()
        {
            if (bracketItemNames == null)
            {
                var names = new Dictionary<string, string>();
                foreach (var type in ItemRegistry.ItemTypes)
                {
                    if (!CatalogItemTypes.Contains(type.Identifier))
                        continue;
                    foreach (string id in type.GetAllIds())
                    {
                        try
                        {
                            string? name = type.GetData(id)?.DisplayName?.Trim();
                            if (!string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { '(', ')', '\uFF08', '\uFF09' }) >= 0 && !name.StartsWith("[", StringComparison.Ordinal))
                                names.TryAdd(Norm(name), name);
                        }
                        catch (Exception)
                        {
                            // an item without readable data is left out
                        }
                    }
                }
                bracketItemNames = names;
            }

            return bracketItemNames.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>True if the text is the whole name of an item that has brackets in its name ("crop tank top (f)").</summary>
        internal static bool IsBracketItemName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (bracketItemNames == null)
                GetBracketItemNames();
            return bracketItemNames!.ContainsKey(Norm(text.Trim()));
        }

        // ===================== FLAVOURED ITEMS (icons of built names) =====================
        // Jelly, Juice, Pickles, Wine, Dried Fruit, Dried Mushrooms, Smoked Fish, Roe, Aged Roe and Targeted Bait take the colour of what they are
        // made of. A name the suggestion list builds ("Starfruit Wine") is drawn with the icon of the item the game itself would make from that
        // ingredient (the game's own CreateFlavoredItem), so it looks like in the inventory: a yellow bottle for Starfruit Wine, a blue one for
        // Ancient Fruit Wine. Honey keeps one icon for every flower, like in the game.

        /// <summary>The kind of flavoured item of each base product (the name of the game's Object.PreserveType value).</summary>
        private static readonly Dictionary<string, string> PreserveTypeOfProduct = new(StringComparer.OrdinalIgnoreCase)
        {
            ["(O)348"] = "Wine",
            ["(O)344"] = "Jelly",
            ["(O)342"] = "Pickle",
            ["(O)350"] = "Juice",
            ["(O)812"] = "Roe",
            ["(O)447"] = "AgedRoe",
            ["(O)340"] = "Honey",
            ["(O)SpecificBait"] = "Bait",
            ["(O)DriedFruit"] = "DriedFruit",
            ["(O)DriedMushrooms"] = "DriedMushroom",
            ["(O)SmokedFish"] = "SmokedFish"
        };

        /// <summary>The item the game makes from the ingredient as this product ("(O)348" + Starfruit = Starfruit Wine), or null if it cannot.</summary>
        internal static Item? CreateFlavoredItem(string productId, string ingredientId)
        {
            try
            {
                if (!PreserveTypeOfProduct.TryGetValue(productId, out string? typeName)
                    || !Enum.TryParse(typeName, out StardewValley.Object.PreserveType preserveType))
                    return null;
                if (ItemRegistry.Create(ingredientId) is not StardewValley.Object ingredient)
                    return null;
                return ItemRegistry.GetObjectTypeDefinition().CreateFlavoredItem(preserveType, ingredient);
            }
            catch (Exception)
            {
                return null;   // the plain product's icon is drawn instead
            }
        }

        // ===================== ITEM LABEL: "Egg (item)" =====================
        // A few category words are also the name of a real item: "Egg" (the white and the brown one), "Milk", "Honey", "Bait". Typed alone the word
        // means the category (every egg). The drop-down offers the item itself as "Egg (item)", and that label searches ONLY the items of exactly
        // that name (both Eggs). Case and spaces around the brackets do not matter; half-width and full-width brackets both work.

        private static readonly Regex ItemLabelPattern = new(
            @"\s*[(\uFF08]\s*item\s*[)\uFF09]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True if the text ends with the " (item)" label; <paramref name="name"/> is the text before it ("Egg (item)" -> "Egg").</summary>
        internal static bool TryStripItemLabel(string? text, out string name)
        {
            name = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;
            Match match = ItemLabelPattern.Match(text);
            if (!match.Success)
                return false;
            name = text.Substring(0, match.Index).Trim();
            return name.Length > 0;
        }

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

        // P4: the name contains the query (case, spaces, hyphens ignored), WORD-ALIGNED (see SuggestionIndex, WORD-ALIGNED MATCHING): "sshirt" is not
        // in "Flames Shirt". normQuery: the spelling to look for (a plural / singular spelling of the query is fine); query: what was typed.
        private static bool NameContains(Item item, string query, string normQuery)
        {
            List<string> words = SuggestionIndex.VariantWordKeys(query, normQuery);
            return SuggestionIndex.NameMatchesAligned(item.DisplayName, words)
                || SuggestionIndex.NameMatchesAligned(item.Name, words);
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

        /// <summary>
        /// The spellings the SEARCH tries for a typed word, in this order of priority:
        ///   1. the word as typed (an exact name wins, see BuildSingleMatcher P3),
        ///   2. names that CONTAIN the word as typed (P4),
        ///   3. the singular made by cutting a plural ending off the word ("apples" -> "apple"), but ONLY when the typed word itself matches nothing at all
        ///      in the whole game (no name contains it, it is no category) AND the shorter spelling is exactly a name of the game or whole words of one.
        /// So "weds" never becomes "wed" (a part of "Wedding Ring"): it finds nothing and is offered a correction (Weeds) instead, and "glasses" stays
        /// "glasses" instead of also dragging in everything that contains "glass". The other direction (a singular typed word also tries its plural,
        /// "cranberry" -> "cranberries") only ever ADDS matches and is not restricted.
        /// The first entry is always the word as typed. <paramref name="rawText"/> is the text as typed (spaces and case do not matter).
        /// </summary>
        private List<string> GetSearchVariants(string rawText)
        {
            string normText = Norm(StripCategorySuffix(rawText ?? ""));
            List<string> all = PluralVariants(normText);
            if (all.Count <= 1)
                return all;

            var kept = new List<string> { all[0] };
            bool? typedWordExists = null;   // worked out at most once, and only if a shorter spelling is there at all

            foreach (string variant in all.Skip(1))
            {
                if (variant.Length >= normText.Length)
                {
                    kept.Add(variant);   // singular -> plural: only adds matches
                    continue;
                }

                try
                {
                    suggestionIndex ??= new SuggestionIndex();
                    suggestionIndex.EnsureBuilt();

                    // "found as typed" means inside one word of a name ("glasses" in "Broken Glasses"); "apples" inside "Apple Sapling" only exists
                    // because the spaces are ignored, so it does not stop "apple" (an exact name) from being tried
                    typedWordExists ??= suggestionIndex.AnyNameContainsInOneWord(StripCategorySuffix(rawText ?? "")) || IsCategoryWord(normText) || MatchingCategoryWords(normText).Count > 0;
                    if (typedWordExists == true)
                        continue;   // the typed word is found as it is: the shorter spelling never gets a say

                    if (suggestionIndex.HasWholeNameOrWord(variant))
                        kept.Add(variant);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Could not check the singular spelling of a word: {ex.Message}", LogLevel.Trace);
                    kept.Add(variant);   // when in doubt keep the old behavior
                }
            }

            return kept;
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

        /// <summary>
        /// The separate words of a normalized query: split at the commas if there are any, otherwise at the spaces (see SplitBySpaces). A word
        /// that means the same as an earlier one ("egg fruit egg", "fruit fruits", "trash junk") is left out: only its first place counts.
        /// </summary>
        private List<string> ResolveAllTerms(List<Item> allItems, string queryText)
        {
            if (!HasCommas(queryText) && TryStripItemLabel(queryText, out _))
                return new List<string> { queryText.Trim() };   // "Egg (item)" is one word, its label included

            // an alias or another spelling of a name ("dried grape" -> Raisins, "cranberry jelly" -> Cranberries Jelly): ONE word, the game's name
            if (!HasCommas(queryText) && TryResolveName(queryText, out _, out _))
                return new List<string> { ResolveSearchName(queryText) };

            List<string> coarse = HasCommas(queryText)
                ? GetCommaTerms(queryText).Select(ResolveSearchName).ToList()   // "dried grape, fish": each part may be an alias
                : SplitBySpaces(allItems, queryText);

            // then every term may still be cut at a hyphen / underscore between two letters ("fruit-fish"), unless it is one known name ("star-fruit")
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (string term in coarse)
            {
                foreach (string piece in SplitSoft(allItems, term))
                {
                    string shown = WithCategoryAlias(piece);   // "clothes" is searched and shown as "clothing"
                    if (seen.Add(TermIdentity(shown)))
                        result.Add(shown);
                }
            }

            return result.Count > 0 ? result : coarse.Select(WithCategoryAlias).ToList();
        }

        /// <summary>
        /// The words of a title in the order they are named, with the category label only where it belongs: the categories first (in the order
        /// typed) followed by ONE " (category)" / " (categories)", then the item names without a label. Only categories:
        /// 'fruit', 'fish' (categories) (the label is the suffix). No category: no label. Mixed: 'egg', 'fish' (categories), 'Strawberry'.
        /// A part of a word ("win") is not a category and gets no label.
        /// </summary>
        private static (List<string> Ordered, int LabelAfter, string MidLabel, string EndLabel) LabelCategories(IReadOnlyList<string> terms)
        {
            var categories = terms.Where(t => IsCategoryWord(StripCategorySuffix(t))).ToList();
            var others = terms.Where(t => !IsCategoryWord(StripCategorySuffix(t))).ToList();
            string label = categories.Count == 1 ? " (category)" : categories.Count > 1 ? " (categories)" : "";
            var ordered = categories.Concat(others).ToList();
            return others.Count == 0
                ? (ordered, -1, "", label)
                : (ordered, categories.Count - 1, label, "");
        }

        /// <summary>What a search word stands for: the category it names (so "fruit" / "fruits", "trash" / "junk" are one), else its spelling.</summary>
        internal static string TermIdentity(string term)
        {
            string plain = StripCategorySuffix(term ?? "");
            if (IsCategoryWord(plain))
            {
                string? key = CategoryKey(plain);
                if (key != null)
                    return "category:" + key;
            }
            return Norm(plain);
        }

        // ===================== AT MOST THREE AT A TIME =====================
        // One search takes at most three things, categories and item names alike: "fish fruit egg wine" searches fish, fruit, egg; "wine" is left
        // out. The search box says so while typing (the big white note above it), and the result panel once more in its last line. A word repeated
        // ("egg fruit egg") counts once.

        /// <summary>How many words (categories or items) one search takes.</summary>
        internal const int MaxSearchTerms = 3;

        /// <summary>The words of the last search that were left out (the fourth one on), for the last line of the result panel. Empty = none.</summary>
        private List<string> lastIgnoredTerms = new();

        // ===================== LOST WHEN THE PLAYER DIED (Marlon) =====================
        // Items lost in the mines on the last death can be bought back from Marlon at the Adventurer's Guild (Farmer.itemsLostLastDeath). They lie in
        // no container, so they are not a place: when the search matches some of them, the panel (and the console) end with one grey line saying so.
        private string lostOnDeathNote = "";

        /// <summary>The note line for the items of the last death the search matches, or "" (see LOST WHEN THE PLAYER DIED).</summary>
        private static string BuildLostOnDeathNote(Func<Item, bool> matches)
        {
            Farmer? player = Game1.player;
            if (player == null)
                return "";
            List<string> names = player.itemsLostLastDeath
                .Where(i => i != null && matches(i))
                .Select(i => string.IsNullOrWhiteSpace(GetShownItemName(i)) ? i.DisplayName : GetShownItemName(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
                return "";
            string list = string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", ..." : "");
            return $"Lost when you died: {list}. Buy {(names.Count == 1 ? "it" : "them")} back from Marlon at the Adventurer's Guild.";
        }

        /// <summary>
        /// The words of a text that a search would leave out (the fourth one on), in the order typed, split exactly like the search splits it
        /// (item names of several words stay one: "ancient fruit fish egg wine" leaves out "wine" only). Empty when the mod is not running.
        /// </summary>
        internal static List<string> GetIgnoredSearchTerms(string text)
        {
            text = (text ?? "").Trim();
            ModEntry? mod = Instance;
            if (mod == null || text.Length == 0)
                return new List<string>();

            // fewer than four pieces cannot be too many: no work while typing a short search
            int separators = text.Count(ch => ch == ' ' || ch == ',' || ch == '\uFF0C' || ch == '\u3001');
            if (separators < MaxSearchTerms)
                return new List<string>();

            return ResolveBoxTerms(text).Skip(MaxSearchTerms).ToList();
        }

        /// <summary>
        /// Every word of the search box text, split exactly like the search splits it (repeats left out, no limit): "ancient fruit, fish, egg" ->
        /// Ancient Fruit | fish | egg. The game's whole item list stands in for the chests. Empty when the mod is not running.
        /// </summary>
        internal static List<string> ResolveBoxTerms(string text)
        {
            text = (text ?? "").Trim();
            ModEntry? mod = Instance;
            if (mod == null || text.Length == 0)
                return new List<string>();
            try
            {
                return mod.ResolveAllTerms(mod.GetGameItemCatalog(), NormalizeQueryText(text));
            }
            catch (Exception ex)
            {
                mod.Monitor.LogOnce($"Could not split the words of the search box: {ex.Message}", LogLevel.Trace);
                return new List<string>();
            }
        }

        /// <summary>
        /// True if the word is complete: a category word, a whole item name of the game, or a whole item name with the " (item)" label. A half
        /// typed word ("eg", "ancient fr") is not.
        /// </summary>
        internal static bool IsCompleteTerm(string term)
        {
            ModEntry? mod = Instance;
            if (mod == null || string.IsNullOrWhiteSpace(term))
                return false;
            if (IsCategoryWord(StripCategorySuffix(term)))
                return true;
            Func<string, bool> isKnownName = mod.GetKnownNameCheck();
            return TryStripItemLabel(term, out string labeled) ? isKnownName(labeled) : isKnownName(term.Trim());
        }

        /// <summary>"clothes" -> "clothing" (the hidden second spelling, see SingleWordCategories); any other text as it is.</summary>
        internal static string WithCategoryAlias(string term)
        {
            return Norm(StripCategorySuffix(term ?? "")) == "clothes" ? "clothing" : term ?? "";
        }

        /// <summary>
        /// The words of a text as the category check sees them: at the commas if there are any, otherwise at the spaces, where a run of words that is
        /// ONE category word stays together ("artisan goods fish" -> artisan goods | fish). Used by the search box (the drop-down for several
        /// categories, the note about a fourth one); the search itself splits with the item names too (ResolveAllTerms).
        /// </summary>
        internal static List<string> SplitKeepingCategories(string text)
        {
            text = (text ?? "").Trim();
            if (HasCommas(text))
                return GetCommaTerms(text).Select(StripCategorySuffix).Where(t => t.Length > 0).ToList();

            string[] words = text.Split(SpaceSeparators, StringSplitOptions.RemoveEmptyEntries).Where(w => !IsOnlySoftSeparators(w)).ToArray();
            var pieces = new List<string>();
            int index = 0;
            while (index < words.Length)
            {
                int take = 1;
                for (int length = words.Length - index; length >= 2; length--)
                {
                    if (IsCategoryWord(string.Join(" ", words, index, length)))
                    {
                        take = length;
                        break;
                    }
                }
                pieces.Add(string.Join(" ", words, index, take));
                index += take;
            }
            return pieces;
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
                || IsDynamicWord(phrase)
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

            // every word a category ("fruit wine", "fish fruit egg", "artisan goods fish"): that many categories, never one phrase. Only a whole
            // item name made of category words would stay one (the item); "fruit wine" is not one ("Starfruit Wine" only CONTAINS it)
            if (!isKnownName(text) && !IsCategoryWord(text))
            {
                List<string> categoryPieces = SplitKeepingCategories(text);
                if (categoryPieces.Count >= 2 && categoryPieces.All(piece => IsCategoryWord(piece)))
                    return categoryPieces;
            }

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
        private string BuildQuotedTitle(string prefix, IReadOnlyList<string> names, bool shorten, string suffix, IReadOnlyList<string>? invalidNames = null,
            IReadOnlyList<string>? noItemCategories = null, IReadOnlyList<string>? noItemNames = null, int labelAfter = -1, string midLabel = "")
        {
            var title = new System.Text.StringBuilder(prefix);
            var ranges = new List<(int Start, int Length)>();
            var rangeTerms = new List<string>();   // the full word of every range, in the same order (for the hover tips)

            // labelAt / label: after the name with this index, the label ("'egg', 'fish' (categories), 'Strawberry'": only the categories carry it)
            void AppendNames(IReadOnlyList<string> list, bool cutLongNames, string nameSuffix = "", int labelAt = -1, string label = "")
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
                    rangeTerms.Add(list[i]);
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

                    title.Append(nameSuffix);   // e.g. " (category)" after 'vegetable' in the "No items found in ..." part
                    if (i == labelAt)
                        title.Append(label);
                }
            }

            AppendNames(names, shorten, "", labelAfter, midLabel);
            title.Append(suffix);

            notifyInvalidStart = -1;
            notifyInvalidLength = 0;
            notifyTailStart = -1;

            // The groups after the main sentence, on their own line(s), in this order (a group is left out when it has no words):
            //   (No items found in your chests: 'vegetable' (category), 'Wood')   the word EXISTS in the game (a category, an item, or part of a
            //                                                                     name), only no chest holds it: normal text color, normal hover tips
            //   (Invalid: 'sdfsd')                                                nothing in the whole game matches it: dark red, no hover tip
            // Only the Invalid group is red (notifyInvalidStart / notifyInvalidLength); every quoted word in both groups is bold.
            bool hasInvalid = invalidNames != null && invalidNames.Count > 0;
            bool hasNoCategory = noItemCategories != null && noItemCategories.Count > 0;
            bool hasNoName = noItemNames != null && noItemNames.Count > 0;
            if (hasInvalid || hasNoCategory || hasNoName)
            {
                title.Append(' ');
                notifyTailStart = title.Length;

                if (hasNoCategory || hasNoName)
                {
                    title.Append("(No items found in your chests: ");
                    if (hasNoCategory)
                        AppendNames(noItemCategories!, true, " (category)");
                    if (hasNoCategory && hasNoName)
                        title.Append(", ");
                    if (hasNoName)
                        AppendNames(noItemNames!, true);
                    title.Append(')');
                }

                if (hasInvalid)
                {
                    if (hasNoCategory || hasNoName)
                        title.Append(' ');
                    int redStart = title.Length;
                    title.Append("(Invalid: ");
                    AppendNames(invalidNames!, true);
                    title.Append(')');
                    notifyInvalidStart = redStart;
                    notifyInvalidLength = title.Length - redStart;
                }
            }

            notifyEmphasis = ranges;
            notifyEmphasisTerms = rangeTerms;
            notifyEmphasisTips = new List<string?>(new string?[ranges.Count]);   // filled by ScanChests (BuildEmphasisTips) right after the title is built
            return title.ToString();
        }

        /// <summary>
        /// True: a half-typed category word ("artisa", "frui") finds the items of that category, as the hover tip says. False: a part of a word only
        /// finds items whose name contains it (the behavior before 1.6.0 final).
        /// </summary>
        private static readonly bool PartialCategorySearch = true;

        // One search term: the N / P1 / P2 / P3 / P4 rules above (this is the whole search of the versions before multi-term search).
        private Func<Item, bool> BuildSingleMatcher(List<Item> allItems, string rawQuery)
        {
            // "fruit wine": the products made from that kind of ingredient (see MADE FROM)
            if (TryParseMadeFrom(StripCategorySuffix(rawQuery), out int[] madeFromCategories, out DynamicCategory? madeProduct) && madeProduct != null)
                return i => madeProduct.Matches(i) && IngredientCategoryOf(i) is int ingredientCategory && madeFromCategories.Contains(ingredientCategory);

            // "Egg (item)": only the items of exactly that name (see ITEM LABEL), never the category
            if (TryStripItemLabel(rawQuery, out string itemOnly))
            {
                string exactName = Norm(itemOnly);
                return i => NameEquals(i, exactName);
            }

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

            // P1b: dynamic category ("wine", "jelly", "aged roe" ...): the plain item and every flavored variant
            DynamicCategory? dynamicCategory = FindDynamicCategory(normQuery);
            if (dynamicCategory != null)
                return dynamicCategory.Matches;

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

            // singular / plural spellings of the query ("cranberry" <-> "cranberries"); the first one is the query as typed. A spelling made by cutting
            // a plural "s" off ("weds" -> "wed") is only used when the typed word finds nothing in the game and the shorter word is a real name (GetSearchVariants)
            List<string> variants = GetSearchVariants(query);

            // P3: exact item name -> only that item (the typed spelling is tried first, then its singular / plural).
            // Except a WORD FAMILY of clothing / furniture ("Shirt", "Pants", "Bed"): the plain item and every clothing / furniture whose name has
            // that word ("Bridal Shirt", "Double Bed"), see IsWordFamilyName
            foreach (string variant in variants)
            {
                string exact = variant;
                if (allItems.Any(i => NameEquals(i, exact)))
                {
                    if (IsWordFamilyName(exact))
                        return i => IsInWordFamily(i, exact);
                    return i => NameEquals(i, exact);
                }
            }

            // P4: contains (fallback); the singular / plural spellings only add matches, they never remove any.
            // A word that is only the START (or any part) of a category's name is a half-typed category, NOT a typo: "artisa" means Artisan Goods,
            // "frui" means Fruit and Dried Fruit. So besides the items whose NAME contains the text (Catfish, Sunfish, Fish Stew for "fis"), the items of
            // every category whose name contains the text are found too, exactly what the hover tip announces ("Fish (category)" first, see
            // FindMatchedEntries). Spelling distance plays no part here: nothing is ever refused or called Invalid because it is "close to" something.
            Func<Item, bool> byName = i => NameContains(i, query, normQuery)
                || variants.Skip(1).Any(v => NameContains(i, query, v));

            if (!PartialCategorySearch)
                return byName;

            List<string> partialCategories = MatchingCategoryWords(query);   // empty for fewer than 3 letters, a number, or a text that already IS a category
            if (partialCategories.Count == 0)
                return byName;

            Func<Item, bool>[] categoryMatchers = partialCategories.Select(word => BuildSingleMatcher(allItems, word)).ToArray();
            return i => byName(i) || categoryMatchers.Any(matcher => matcher(i));
        }

        /// <summary>
        /// The category words (the auto-complete's spelling, see SuggestionIndex.CategoryWords) whose name literally CONTAINS a part of a word
        /// ("fis" -> fish, fishing jelly, fishing tackle, smoked fish; "artisa" -> artisan goods; "frui" -> fruit, dried fruit). One word per category
        /// (synonyms like "junk" / "trash" count once). Empty for fewer than 3 letters, for a number, and for a word that already IS a category.
        /// Used for the first lines of the "Showing results for:" tip, to tell an empty result from an Invalid word, and by the search itself (BuildSingleMatcher P4):
        /// a half-typed category word finds the items of those categories. Never for spelling: a word that is part of a category name is not a typo.
        /// </summary>
        internal static List<string> MatchingCategoryWords(string rawText)
        {
            var result = new List<string>();
            string text = StripCategorySuffix(rawText ?? "").Trim();
            string key = Norm(text);
            if (key.Length < 3 || key.All(char.IsDigit) || IsCategoryWord(text))
                return result;

            var seenCategories = new HashSet<string>();
            foreach (string word in SuggestionIndex.CategoryWords)
            {
                if (!IsCategoryWord(word))
                    continue;

                if (!Norm(word).Contains(key, StringComparison.Ordinal))
                    continue;

                if (seenCategories.Add(CategoryKey(word) ?? word))
                    result.Add(word);
            }

            return result;
        }

        /// <summary>
        /// True if something in the game matches a word: a category (also a part of one, see MatchingCategoryWords) or the name of anything the game
        /// has, whole or a part of it (the auto-complete's word list: every item, and the names the game builds on the fly, like "Peach Wine").
        /// False = the word is Invalid: nothing in the whole world is called like that.
        /// </summary>
        private bool ExistsInGame(string term)
        {
            if (TryStripItemLabel(term, out string labeledItem))
                term = labeledItem;   // "Egg (item)": the item Egg
            string text = StripCategorySuffix(term ?? "").Trim();
            if (text.Length == 0)
                return false;
            if (IsCategoryWord(text) || MatchingCategoryWords(text).Count > 0)
                return true;

            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();
                return GetSearchVariants(text).Any(variant => suggestionIndex.AnyNameContains(variant, text));
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check whether a word exists in the game: {ex.Message}", LogLevel.Trace);
                return true;   // when in doubt, do not call the player's word invalid
            }
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
            if (FindDynamicCategory(normQuery)?.IsCategory == true)
                return true;
            string[] tokens = query.ToLowerInvariant().Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var (_, words) in MultiWordCategories)
            {
                if ((tokens.Length > 0 && tokens.All(words.Contains)) || words.Contains(normQuery))
                    return true;
            }

            return TryParseMadeFrom(query, out _, out _);   // "fruit wine": the wines made from fruit (see MADE FROM)
        }

        // ===================== MADE FROM: "fruit wine", "vegetable juice", "fish roe" =====================
        // An ingredient category followed by a product the game makes from it is ONE search word: the products made from that kind of ingredient.
        // "fruit wine" = every wine made from a fruit (in the game that is every wine), "vegetable pickles" = the pickles made from vegetables (not
        // the ones from forage), "fish roe" = every roe. It counts as a category (labelled "(category)"). Any other pair of categories ("fish fruit")
        // stays two separate categories.

        /// <summary>For each product (its base item id): the categories of the ingredients the game makes it from.</summary>
        private static readonly Dictionary<string, int[]> ProductIngredientCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["(O)348"] = new[] { -79 },                 // Wine: fruit (keg)
            ["(O)344"] = new[] { -79 },                 // Jelly: fruit (preserves jar)
            ["(O)350"] = new[] { -75, -81 },            // Juice: vegetables, forage (keg)
            ["(O)342"] = new[] { -75, -81 },            // Pickles: vegetables, forage and mushrooms (preserves jar)
            ["(O)340"] = new[] { -80 },                 // Honey: flowers
            ["(O)812"] = new[] { -4 },                  // Roe: fish (fish pond)
            ["(O)447"] = new[] { -4 },                  // Aged Roe: (the roe of) fish
            ["(O)SmokedFish"] = new[] { -4 },           // Smoked Fish: fish
            ["(O)DriedFruit"] = new[] { -79 },          // Dried Fruit: fruit (dehydrator)
            ["(O)DriedMushrooms"] = new[] { -81 },      // Dried Mushrooms: mushrooms (forage category)
            ["(O)SpecificBait"] = new[] { -4 }          // Targeted Bait: fish
        };

        /// <summary>
        /// True if the text is "ingredient category + product" with a real link ("fruit wine"): the ingredient categories it keeps and the product.
        /// </summary>
        private static bool TryParseMadeFrom(string text, out int[] ingredientCategories, out DynamicCategory? product)
        {
            ingredientCategories = Array.Empty<int>();
            product = null;
            string[] words = (text ?? "").Trim().ToLowerInvariant().Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int split = 1; split < words.Length; split++)
            {
                string ingredientWord = string.Join(" ", words, 0, split);
                if (!SingleWordCategories.TryGetValue(ingredientWord, out int[]? ids))
                    continue;
                DynamicCategory? made = FindDynamicCategory(Norm(string.Join("", words, split, words.Length - split)));
                if (made == null || !made.IsCategory)
                    continue;

                int[] allowed = made.QualifiedIds
                    .SelectMany(id => ProductIngredientCategories.TryGetValue(id, out int[]? madeOf) ? madeOf : Array.Empty<int>())
                    .Intersect(ids)
                    .ToArray();
                if (allowed.Length == 0)
                    continue;

                ingredientCategories = allowed;
                product = made;
                return true;
            }
            return false;
        }

        /// <summary>
        /// For the drop-down (SuggestionIndex, CATEGORY EXPANSION): when the word is "ingredient category + product" ("fruit wine"), the base ids of
        /// the product ("(O)348"), the ingredient categories it keeps (fruit) and whether those are ALL the ingredients the product can be made of
        /// (wine: only fruit, so every wine is a fruit wine; juice: vegetables and forage, so "vegetable juice" is only a part). The same rule the
        /// search uses (TryParseMadeFrom), so the drop-down lists exactly what the search finds.
        /// </summary>
        internal static bool TryGetMadeFrom(string word, out string[] productIds, out int[] ingredientCategories, out bool coversEveryIngredient)
        {
            productIds = Array.Empty<string>();
            ingredientCategories = Array.Empty<int>();
            coversEveryIngredient = false;
            if (!TryParseMadeFrom(StripCategorySuffix((word ?? "").Trim()), out int[] allowed, out DynamicCategory? product) || product == null)
                return false;

            productIds = product.QualifiedIds;
            ingredientCategories = allowed;
            coversEveryIngredient = product.QualifiedIds
                .SelectMany(id => ProductIngredientCategories.TryGetValue(id, out int[]? madeOf) ? madeOf : Array.Empty<int>())
                .All(allowed.Contains);
            return true;
        }

        /// <summary>The category of what a flavoured item is made of (Starfruit Wine -> fruit), or null.</summary>
        private static int? IngredientCategoryOf(Item item)
        {
            if (item is not StardewValley.Object obj)
                return null;
            string? ingredient = obj.preservedParentSheetIndex.Value;
            if (string.IsNullOrEmpty(ingredient))
                return null;
            return ItemRegistry.GetData(ingredient.StartsWith("(", StringComparison.Ordinal) ? ingredient : "(O)" + ingredient)?.Category;
        }

        /// <summary>
        /// True if the text is exactly the name of an item the game has (singular / plural spellings included: "cranberries" -> "cranberry").
        /// Only used to word the title of a result (a real item that no chest holds, versus a typo); it does not take part in searching.
        /// </summary>
        private static bool IsKnownItemName(Func<string, bool> isKnownName, string term)
        {
            return PluralVariants(Norm(term)).Any(isKnownName);
        }

        /// <summary>
        /// A product the game never makes ("corn jelly", "carp wine", "fruit juice", see SuggestionIndex, IMPOSSIBLE PRODUCTS): what the search box
        /// shows instead (the two lines). Null when the text is fine or the mod is not running. Quality words must be gone already.
        /// </summary>
        internal static SuggestionIndex.ImpossibleProduct? GetImpossibleProduct(string text)
        {
            SuggestionIndex? index = Instance?.suggestionIndex;
            if (index == null || HasCommas(text ?? ""))
                return null;
            return index.GetImpossibleProduct(StripCategorySuffix((text ?? "").Trim()));
        }

        /// <summary>
        /// An alias ("dried grape" -> Raisins) or another singular / plural spelling of a name ("cranberry jelly" -> Cranberries Jelly), see
        /// SuggestionIndex, ALIASES AND OTHER SPELLINGS: the game's name, and the note of an alias (null for a spelling). False for anything else.
        /// </summary>
        internal static bool TryResolveName(string text, out string name, out string? note)
        {
            name = "";
            note = null;
            SuggestionIndex? index = Instance?.suggestionIndex;
            if (index == null || HasCommas(text ?? ""))
                return false;
            return index.TryResolveName(StripCategorySuffix((text ?? "").Trim()), out name, out note);
        }

        /// <summary>
        /// The notes of the aliases the last search resolved (game name, normalized -> "Dried grapes are called Raisins in the game."): the tip of
        /// that word in the result title starts with it. Cleared when a new search starts.
        /// </summary>
        private readonly Dictionary<string, string> searchAliasNotes = new(StringComparer.Ordinal);

        /// <summary>A search word with its alias / other spelling resolved to the game's name (the note of an alias is remembered for the title).</summary>
        private string ResolveSearchName(string term)
        {
            if (!TryResolveName(term, out string name, out string? note))
                return term;
            if (note != null)
                searchAliasNotes[Norm(name)] = note;
            return name;
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

        // ===================== ONE ROW PER QUALITY (a search for ONE item) =====================
        // After a search (Enter) for exactly ONE item that can have stars ("apple wine", not a category, not two words), the item lists show one
        // row per quality instead of one row for all of them: plain, silver, gold, iridium (low to high), each icon with its star, like the game's
        // own inventory. With a star filter there is just the one row of that star. Every other search (a category, several words, an item that
        // never has quality) keeps one row per item name. The same rows everywhere: the place list of the result panel, the hover tip of a
        // highlighted chest and the "Found" note of an opened chest. The drop-down and "Did you mean" are not touched (they are not about stars).
        // A row's key is the item name plus QualityRowSeparator plus the quality; RowName / RowQuality take it apart again.
        private bool singleItemQualityRows = false;
        private const char QualityRowSeparator = '\u001F';

        /// <summary>The key of an item's row in the item lists: its name, or with one row per quality, its name and quality.</summary>
        private string RowKey(Item item)
        {
            string name = GetShownItemName(item);
            return singleItemQualityRows ? name + QualityRowSeparator + item.Quality.ToString(CultureInfo.InvariantCulture) : name;
        }

        /// <summary>The item name of a row key ("Apple Wine" of "Apple Wine" + quality, also of a row inside a carried container).</summary>
        private static string RowName(string key)
        {
            int nest = key.IndexOf(NestedRowSeparator);
            if (nest >= 0)
                key = key.Substring(nest + 1);
            int cut = key.IndexOf(QualityRowSeparator);
            return cut < 0 ? key : key.Substring(0, cut);
        }

        // ===================== CONTAINERS INSIDE CONTAINERS (one level) =====================
        // A container can itself lie in the backpack or in a chest: a picked-up dresser keeps its clothes, a mini-fridge or chest item may hold things.
        // What such a container holds is searched too, as part of the backpack / the chest it lies in, ONE level deep only (a chest in a chest in the
        // backpack is not opened). Its rows in the item lists are marked with the container's name: "Mini-Fridge > Apple x3" under "Backpack total",
        // so they are never mixed up with the items lying there directly. A Junimo Chest item is not opened (its items are the shared Junimo
        // inventory, already found through every placed Junimo Chest), nor is a fish tank (its fish are no storage).
        private const char NestedRowSeparator = '\u001E';

        /// <summary>The items, each with the name of the carried container it lies in (null = directly there), one level deep.</summary>
        private static IEnumerable<(Item Item, string? Inside, Item? Container)> WithNested(IEnumerable<Item?>? items)
        {
            if (items == null)
                yield break;
            foreach (Item? item in items)
            {
                if (item == null)
                    continue;

                // what the player wears (see WORN ITEMS): only its items count, the stand-in itself is no item of the backpack
                bool worn = IsEquippedProxy(item);
                if (!worn)
                    yield return (item, null, null);

                IEnumerable<Item?>? inner = null;
                if (worn)
                    inner = ((Chest)item).Items;
                else if (item is Chest carried && carried.SpecialChestType != Chest.SpecialChestTypes.JunimoChest)
                    inner = carried.Items;
                else if (item is StorageFurniture furniture && item is not FishTankFurniture)
                    inner = furniture.heldItems;
                if (inner == null)
                    continue;

                string inside = worn ? EquippedLabel : string.IsNullOrWhiteSpace(item.DisplayName) ? "Container" : item.DisplayName;
                foreach (Item? innerItem in inner)
                {
                    if (innerItem != null)
                        yield return (innerItem, inside, item);
                }
            }
        }

        /// <summary>The items and, one level deep, the items of the containers among them (see CONTAINERS INSIDE CONTAINERS).</summary>
        private static IEnumerable<Item> ItemsWithNested(IEnumerable<Item?>? items)
        {
            return WithNested(items).Select(pair => pair.Item);
        }

        // per place key: whether something of the search lies there directly, and the carried containers the rest lies in (see PlaceNestNote)
        private readonly Dictionary<string, (bool Direct, List<Item> Containers)> placeNesting = new();

        /// <summary>
        /// The "in ..." part of a place's bracket note: written only when EVERYTHING the search found at that place lies inside carried containers
        /// (a dresser full of clothes in the backpack, in a chest, in a fridge ...), so the player knows to look inside them: "Backpack (in Birch
        /// Dresser)", "Farmhouse (fridge, in Birch Dresser)". Several containers: "in 2 dressers" / "in 2 chests" / "in 3 containers". Empty when
        /// something lies there directly (the hover list marks the rest with the container's name anyway).
        /// </summary>
        private string PlaceNestNote(string key)
        {
            if (!placeNesting.TryGetValue(key, out var nesting) || nesting.Direct || nesting.Containers.Count == 0)
                return "";

            // what the player wears is no container: "Backpack (equipped)", or with carried containers too "Backpack (equipped, in Birch Dresser)"
            bool equipped = nesting.Containers.Any(IsEquippedProxy);
            List<Item> containers = nesting.Containers.Where(c => !IsEquippedProxy(c)).ToList();
            string carried;
            if (containers.Count == 0)
                carried = "";
            else if (containers.Count == 1)
                carried = "in " + (string.IsNullOrWhiteSpace(containers[0].DisplayName) ? "a container" : containers[0].DisplayName);
            else
            {
                string plural = containers.All(c => c is StorageFurniture) ? "dressers"
                    : containers.All(c => c is Chest chest && chest.fridge.Value) ? "mini-fridges"
                    : containers.All(c => c is Chest) ? "chests"
                    : "containers";
                carried = $"in {containers.Count} {plural}";
            }

            if (!equipped)
                return carried;
            return carried.Length == 0 ? "equipped" : "equipped, " + carried;
        }

        /// <summary>The whole bracket note of a place: the kind of its containers (PlaceNote) and, if everything lies inside carried containers, where.</summary>
        /// <param name="separator">", " in the place list; "; " for a Ginger Island area, whose areas are themselves separated by ", ".</param>
        private string PlaceNoteWithNesting(string key, string separator = ", ")
        {
            return string.Join(separator, new[] { PlaceNote(key), PlaceNestNote(key) }.Where(part => part.Length > 0));
        }

        /// <summary>The row key of an item that lies inside a carried container: the container's name in front ("Mini-Fridge" + Apple).</summary>
        private string RowKey(Item item, string? inside)
        {
            return inside == null ? RowKey(item) : inside + NestedRowSeparator + RowKey(item);
        }

        /// <summary>True for the row of an item inside a carried container.</summary>
        private static bool IsNestedRow(string key) => key.IndexOf(NestedRowSeparator) >= 0;

        /// <summary>
        /// What stands in front of the icon of an item line: "Mini-Fridge > " for an item inside a carried container, "" for every other item. The
        /// icon and the name follow it ("Mini-Fridge > [icon] Apple x3"), so the icon stands next to the item it shows.
        /// </summary>
        private static string RowPrefix(string key, SpriteFont font)
        {
            int nest = key.IndexOf(NestedRowSeparator);
            if (nest < 0)
                return "";
            string sign = font.Characters.Contains('\u203A') ? " \u203A " : " > ";   // the game's font may not have the small angle
            return key.Substring(0, nest) + sign;
        }

        /// <summary>The quality of a row key (0 for a row without one).</summary>
        private static int RowQuality(string key)
        {
            int cut = key.IndexOf(QualityRowSeparator);
            return cut >= 0 && int.TryParse(key.Substring(cut + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality) ? quality : 0;
        }

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

        // ===================== "DID YOU MEAN" WORDS OF A FAILED SEARCH =====================
        // A search that finds NOTHING at all gets one more row under its title: "Did you mean: Starfruit, Strawberry" (the real item names closest to
        // the misspelled word, SuggestionIndex.FindSimilar). Each name in it is a link: clicking it opens the search box with that name already typed in
        // (and the star filter of the search it comes from) and searches at once, so the result and the history work exactly like a search typed by
        // hand. With several words ("satrfruit, fish") the clicked name replaces only the misspelled word.
        // A search that FOUND something never gets this row, even when a typed word is a near miss ("starfrui" is simply found inside "Starfruit").
        private const int MaxSpellingSuggestions = 3;

        /// <summary>The widest the "Did you mean" row of the result panel may get (text only, without the panel's frame). A little more than the line under the search box has (784).</summary>
        private const int ResultSuggestionRowMaxWidth = 840;
        private const string DidYouMeanLabel = "Did you mean: ";
        private const long SuggestionFreshMs = 200;   // the words count as "on screen" if they were drawn this recently (real clock), like the keep box
        private static readonly Color SuggestionColor = new(32, 84, 160);
        private static readonly Color SuggestionHoverColor = new(20, 125, 225);

        /// <summary>The words shown under the title of a failed search: the name to show, and the whole query that is searched when it is clicked.</summary>
        private List<(string Label, string Query)> resultSuggestions = new();

        /// <summary>The hover tip of each "Did you mean" word (same order as <see cref="resultSuggestions"/>), worked out once per search with GetTermTip.</summary>
        private List<string?> resultSuggestionTips = new();

        /// <summary>The text in front of the words ("Did you mean: ").</summary>
        private string resultHintLead = DidYouMeanLabel;

        /// <summary>Where each word was drawn in the last HUD frame (screen pixels, a little larger than the text) and what it searches. Used by the click handler.</summary>
        private readonly List<(Rectangle Bounds, string Query)> suggestionHitBoxes = new();
        private long suggestionDrawnMs = -1000;
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

        // ===================== CLOSE BUTTON OF THE RESULT PANEL (the game's red X, outside the top right corner) =====================
        // The game's own close button (the red X its menus use, same sprite, same 12px * 4 = 48px size), OUTSIDE the panel: just right of its
        // right edge, level with its top edge. A click closes the panel at once (no fade). It only closes the PANEL: the chest highlights keep
        // running on their own timer, and a checked "Keep highlights" box stays, exactly as when the panel times out by itself.
        // If the X would run into the clock / gold block (narrow windows), the panel moves left just far enough to make room for it.
        // The "Keep highlights" box and the gold stars keep clear of it. Hovering it pauses the countdown like the panel itself, and it grows a
        // little, like the X of the search box.
        private static readonly Rectangle ResultCloseSource = new(337, 494, 12, 12);
        private const int ResultCloseSize = 48;
        private const float ResultCloseHoverScale = 4.4f;                 // zoom while hovered (4 = normal)
        private const int ResultCloseOutsideGap = 4;                      // panel's right edge -> X
        private const int ResultCloseTopInset = 4;                        // panel's top edge -> X
        private const long ResultCloseFreshMs = 200;                      // the X counts as "on screen" if it was drawn this recently (real clock)

        private Rectangle resultCloseBounds;                              // where the X was drawn in the last HUD frame (UI pixels)
        private bool resultCloseIsPeek;                                   // that X belongs to a peeked panel (see PEEK): a click only hides the peek

        // PEEK (see where the result panel is drawn): the panel shown again by hovering the locked "Keep highlights" box, and until when it stays without the mouse
        private bool peekActive;
        private long peekUntilMs;
        private const long PeekGraceMs = 2000;
        private long resultCloseDrawnMs = -1000000;

        // A left click on the red X of the result panel closes the panel. The click is swallowed, so it does not also swing a tool.
        // The mouse is read in UI coordinates, like the "Did you mean" words (see OnSuggestionClicked).
        // With a controller, the A button does the same while the cursor is on the X (and is swallowed, so it does not also open a chest behind it).
        private void OnResultCloseClicked(object? sender, ButtonPressedEventArgs e)
        {
            if (IsHudClickTaken(e.Button))
                return;   // the "Copy" bubble used this click
            if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA)
                return;
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;

            long now = clock.ElapsedMilliseconds;
            if (now - resultCloseDrawnMs > ResultCloseFreshMs || (now >= notifyUntilMs && !resultCloseIsPeek))
                return;   // the panel (and its X) is not on screen right now
            if (!resultCloseBounds.Contains(Game1.getMousePosition(true)))
                return;

            Helper.Input.Suppress(e.Button);
            Game1.playSound("bigDeSelect");   // the sound of the game's own close buttons
            if (resultCloseIsPeek)
            {
                // the X of a peeked panel: only the peek goes away (the highlights and the checkbox stay); hovering the box brings it back
                peekActive = false;
                resultCloseDrawnMs = -1000000;
                return;
            }
            CloseResultPanel();
        }

        // ===================== BACKPACK AS A PLACE =====================
        // The player's backpack is searched with the chests and is a place like any other:
        //   - the place list names it first ("Backpack, Barn x2, Farm"): its distance is -1, nearer than every chest; it is one place, counted once;
        //   - the player flashes like a chest (head to feet), in strips of the words' colors when several words are searched;
        //   - it ends like a chest: opening the inventory page of the game menu is "opening" the backpack (OnMenuChanged): not locked, every highlight
        //     goes; locked ("Keep highlights" checked), only the player's flash goes, and opening a found chest only removes that chest's highlight;
        //     the timeout, a new search, a new day and unchecking "Keep highlights" end it like they end the chests' highlights;
        //   - the arrow: the chest arrow shows the way to the nearest chest (the backpack is never the target of that arrow); on top of that, whenever
        //     the backpack holds something of the search, a second arrow stands above the player's head and says "Backpack" (also while chests are
        //     highlighted); the two keep out of each other's way;
        //   - hovering the flashing player in the world lists what the backpack holds of the search, like hovering a highlighted chest does
        //     ("Backpack total: 3", one line per item with its icon and count);
        //   - only the backpack holds it: the title reads "Found your backpack containing 'egg'" with the small backpack in front, and the search is a
        //     success (history, sounds, Keep box). A search that finds nothing anywhere fails as before (the backpack was searched with the chests).

        private const string BackpackPlaceKey = "Backpack";

        /// <summary>Only the backpack holds the search ("Found your backpack containing ..."): a small backpack in front of the title.</summary>
        private bool resultBackpackIcon;

        // ===================== WORN ITEMS (part of the backpack) =====================
        // What the player wears (hat, shirt, pants, boots, both rings, trinkets) is searched as part of the backpack place, like a dresser lying in
        // the backpack: its rows are marked "Equipped > Iridium Band", and when everything found on the player is worn the place reads
        // "Backpack (equipped)". The worn items are mirrored into one stand-in Chest that only exists for this (nothing is moved or cloned).
        // Only the backpack has it.
        private const string EquippedLabel = "Equipped";
        private static Chest? equippedProxy;

        /// <summary>True for the stand-in of the worn items.</summary>
        private static bool IsEquippedProxy(Item? item) => item != null && equippedProxy != null && ReferenceEquals(item, equippedProxy);

        /// <summary>The stand-in of the worn items, filled with what the player wears right now.</summary>
        private static Chest GetEquippedProxy(Farmer player)
        {
            equippedProxy ??= new Chest(false);
            equippedProxy.Items.Clear();
            var worn = new List<Item?> { player.hat.Value, player.shirtItem.Value, player.pantsItem.Value, player.boots.Value, player.leftRing.Value, player.rightRing.Value };
            worn.AddRange(player.trinketItems);
            foreach (Item? item in worn)
            {
                if (item != null)
                    equippedProxy.Items.Add(item);
            }
            return equippedProxy;
        }

        /// <summary>Everything the player carries (toolbar included), without the empty slots.</summary>
        private List<Item> GetBackpackItems()
        {
            try
            {
                Farmer? player = Game1.player;
                if (player != null)
                {
                    List<Item> items = player.Items.Where(i => i != null).Select(i => i!).ToList();
                    Chest worn = GetEquippedProxy(player);
                    if (worn.Items.Count > 0)
                        items.Add(worn);   // what the player wears, as one more "container" of the backpack (see WORN ITEMS)
                    return items;
                }
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not look into the backpack: {ex.Message}", LogLevel.Trace);
            }
            return new List<Item>();
        }

        // the small backpack in front of the "Found your backpack containing ..." title: 11 x 11 game pixels of 3 screen pixels, like the warning sign
        // ('#' outline, 'o' leather, 'g' buckle, '.' nothing)
        private static readonly string[] BackpackArt =
        {
            "....###....",
            "...#...#...",
            ".#########.",
            ".#ooooooo#.",
            ".#ooooooo#.",
            ".####g####.",
            ".#oo#g#oo#.",
            ".#oo###oo#.",
            ".#ooooooo#.",
            ".#ooooooo#.",
            ".#########."
        };

        private static void DrawBackpackBadge(SpriteBatch b, int x, int y, float alpha)
        {
            int inset = (WarningBadgeSize - BackpackArt.Length * WarningSignPixel) / 2;
            x += inset;
            y += inset;
            Color outline = new(72, 40, 18);
            Color leather = new(178, 110, 52);
            Color buckle = new(240, 196, 64);
            for (int row = 0; row < BackpackArt.Length; row++)
            {
                for (int col = 0; col < BackpackArt[row].Length; col++)
                {
                    Color color;
                    switch (BackpackArt[row][col])
                    {
                        case '#': color = outline; break;
                        case 'o': color = leather; break;
                        case 'g': color = buckle; break;
                        default: continue;
                    }
                    b.Draw(Game1.staminaRect, new Rectangle(x + col * WarningSignPixel, y + row * WarningSignPixel, WarningSignPixel, WarningSignPixel), color * alpha);
                }
            }
        }

        /// <summary>Removes the result panel at once. The chest highlights and a checked "Keep highlights" box are not touched.</summary>
        private void CloseResultPanel()
        {
            notifyUntilMs = clock.ElapsedMilliseconds;
            suggestionHitBoxes.Clear();
            suggestionDrawnMs = -1000;
            resultCloseDrawnMs = -1000000;
            sparkleStartMs = -1000000;
        }

        /// <summary>The red X, drawn around its center so the grown X (hover) stays on the same spot.</summary>
        private static void DrawResultCloseButton(SpriteBatch b, Rectangle bounds, bool hovered, float alpha)
        {
            b.Draw(
                Game1.mouseCursors,
                new Vector2(bounds.Center.X, bounds.Center.Y),
                ResultCloseSource,
                Color.White * alpha,
                0f,
                new Vector2(ResultCloseSource.Width / 2f, ResultCloseSource.Height / 2f),
                hovered ? ResultCloseHoverScale : 4f,
                SpriteEffects.None,
                1f);
        }

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
        /// The outcome, the search words that really found something in a chest (with the chosen star), and ALL the words the query was split into
        /// (found or not). The search menu remembers the words that could be searched in the history, also the ones that found nothing (a typo
        /// like "starfruu" is remembered; a word that cannot be searched at all, like "&&&" or "df", is not).
        /// </returns>
        private SearchReport ScanChests(string rawQuery, int quality = AnyQuality)
        {
            searchAliasNotes.Clear();   // the notes of THIS search's aliases (see ResolveSearchName)
            string itemName = NormalizeQueryText(rawQuery);   // "fruit (category)" -> "fruit"; "fruit,fish" -> "fruit, fish" (the separate terms are worked out below)
            if (itemName.Length == 0)
            {
                IsKeepHighlightsLocked = false;
                highlightedChests.Clear();
                highlightBackpack = false;
                backpackWordSlots = Array.Empty<int>();
                highlightMatcher = null;
                singleItemQualityRows = false;
                chestTipSamples.Clear();
                placeItems.Clear();
                placeSamples.Clear();
                placeKindCounts.Clear();
                placeSectionItems.Clear();
                placeNesting.Clear();
                placeEntries = new List<PlaceEntry>();
                lostOnDeathNote = "";
                return new SearchReport(SearchOutcome.Done);
            }

            var locations = GetAllLocations().ToList();

            // collect every chest and fridge once (GetAllContainers never gives the same one twice). An EMPTY one can never match anything, so it is
            // left out here: it costs nothing in the many loops below (late in a game a farm has hundreds of chests).
            var allChests = new List<(GameLocation location, Vector2 position, Chest chest, string label)>();
            foreach (var container in GetAllContainers(locations))
            {
                if (container.chest.Items == null || !container.chest.Items.Any(i => i != null))
                    continue;   // empty: skipped
                allChests.Add(container);
            }

            var allItems = allChests
                .SelectMany(c => ItemsWithNested(c.chest.Items))   // with what carried containers inside them hold (see CONTAINERS INSIDE CONTAINERS)
                .ToList();

            // the backpack is a place too (see BACKPACK AS A PLACE): its items are part of the pool, so a word that only the backpack holds is found
            List<Item> backpackItems = GetBackpackItems();
            allItems.AddRange(ItemsWithNested(backpackItems));

            // one term = the search as it always was; several terms (commas or spaces) = an item counts if it matches ANY of them.
            // At most three (see AT MOST THREE AT A TIME): the rest is named in the last line of the result panel
            List<string> allTerms = ResolveAllTerms(allItems, itemName);
            List<string> terms = allTerms.Take(MaxSearchTerms).ToList();
            lastIgnoredTerms = allTerms.Skip(MaxSearchTerms).ToList();

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
            var invalidTerms = new List<string>();          // gibberish: not a category word and not the name of an item the game has -> (Invalid: ...)
            var noItemCategoryTerms = new List<string>();   // a real category word ("vegetable"), but no chest holds anything of it -> (No items found in '...' (category))
            var noItemNameTerms = new List<string>();       // a real item name ("Wood") or part of one, but no chest holds it -> (No items found in your chests: '...')
            Func<string, bool>? knownName = null;           // built only when a word found nothing
            for (int t = 0; t < terms.Count; t++)
            {
                Func<Item, bool> byName = termMatchers[t];
                if (!allItems.Any(byName))
                {
                    // only the LABEL of the word is decided here; what matches (the matchers above) is untouched
                    string term = terms[t];
                    bool searchable = terms.Count == 1 || SearchMenu.IsTermSearchable(term);
                    // Invalid = NOTHING in the whole game matches it; a word that exists in the game (a category, an item name, or part of one)
                    // but is in no chest is an empty result instead, never Invalid
                    if (IsCategoryWord(term))
                        noItemCategoryTerms.Add(term);
                    else if (searchable && (IsKnownItemName(knownName ??= GetKnownNameCheck(), term) || ExistsInGame(term)))
                        noItemNameTerms.Add(term);
                    else
                        invalidTerms.Add(term);
                }
                else if (allItems.Any(i => byName(i) && qualityOk(i)))
                    validTerms.Add(terms[t]);
            }

            edgeArrowObstacles.Clear();       // a new result: the arrow avoids the new panel (filled again in the next frame)
            IsKeepHighlightsLocked = false;   // a new result: its "Keep highlights" box starts unchecked (and is shown again)
            keepBoxDismissed = false;
            EndKeepFade();                    // nothing of the old box (fade, saved chests) is left
            resultQuality = quality;   // the panel's quality icon (every search shows one, "any quality" included)
            resultFailed = false;
            resultBackpackIcon = false;
            resultSuggestions = new List<(string Label, string Query)>();   // only a failed search gets "Did you mean" words (set below)
            resultSuggestionTips = new List<string?>();
            resultHintLead = DidYouMeanLabel;
            resultHasNoQualityItemsOnly = false;
            highlightedChests.Clear();
            highlightBackpack = false;
            backpackWordSlots = Array.Empty<int>();
            highlightMatcher = matches;   // the hover tip of a highlighted chest lists exactly what this search matched (star filter included)

            // several words that found something: every one gets its color, in the order typed (see SEVERAL WORDS, SEVERAL COLORS)
            highlightWordCount = validTerms.Count >= 2 ? Math.Min(validTerms.Count, MaxSearchTerms) : 0;
            termWordSlot = new int[terms.Count];
            for (int t = 0; t < terms.Count; t++)
            {
                int termSlot = highlightWordCount > 0 ? validTerms.IndexOf(terms[t]) : -1;
                termWordSlot[t] = termSlot < highlightWordCount ? termSlot : -1;
            }
            chestWordSlots.Clear();
            itemWordSlots = new Dictionary<string, int>();
            highlightLabelWords = new List<string>();

            // the word an item belongs to (its color): an item word (not a category) that finds it comes first, so with "fruit, mango" Mango is
            // Mango's color and every other fruit is the fruit color; else the first word that finds it. -1 = no color.
            bool[] termIsItemWord = terms.Select(term => !IsCategoryWord(StripCategorySuffix(term))).ToArray();
            int OwnerSlot(Item item)
            {
                int firstFound = -1;
                for (int wordIdx = 0; wordIdx < terms.Count; wordIdx++)
                {
                    if (termWordSlot[wordIdx] < 0 || !termMatchers[wordIdx](item))
                        continue;
                    if (termIsItemWord[wordIdx])
                        return termWordSlot[wordIdx];
                    if (firstFound < 0)
                        firstFound = termWordSlot[wordIdx];
                }
                return firstFound;
            }
            titleEmphasisSlots = Array.Empty<int>();
            coloredTitle = "";
            chestTipSamples.Clear();
            placeItems.Clear();
            placeSamples.Clear();
            placeKindCounts.Clear();
            placeSectionItems.Clear();
            placeNesting.Clear();
            placeEntries = new List<PlaceEntry>();

            bool anyQualityCapableMatch = false;   // at least one matched item can carry a star (Corn, Fish, ...)
            var mainCounts = new Dictionary<string, int>();
            var islandCounts = new Dictionary<string, int>();
            int found = 0;

            // how far every place is from the player, for the order of the places in the panel (nearest first, see GetPlaceSortKey): the nearest
            // matching chest of a place decides
            var mainDistances = new Dictionary<string, float>();
            var islandDistances = new Dictionary<string, float>();
            GameLocation? playerLocation = Game1.currentLocation;
            Vector2 playerTile = Game1.player?.Tile ?? Vector2.Zero;
            int playerMapWeight = GetMapWeight(playerLocation);
            Dictionary<GameLocation, (float Cost, Vector2? Arrival, Point FirstExit)>? reach = null;
            try
            {
                exitGraph = null;   // a new search reads the ways between the locations afresh (a building may have been built or moved)
                if (playerLocation != null)
                    reach = ComputeReach(GetExitGraph(), playerLocation, playerTile);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not work out the walking distances: {ex.Message}", LogLevel.Trace);   // the places keep the rough map order
            }

            // one row per quality (see ONE ROW PER QUALITY): one word, not a category, and everything it found is ONE item that can have stars
            singleItemQualityRows = false;
            try
            {
                if (terms.Count == 1 && !IsCategoryWord(StripCategorySuffix(terms[0])))
                {
                    List<Item> matched = allItems.Where(i => matches(i)).ToList();
                    singleItemQualityRows = matched.Count > 0
                        && matched.Select(GetShownItemName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                        && matched.Any(CanHaveQuality);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check for one row per quality: {ex.Message}", LogLevel.Trace);
                singleItemQualityRows = false;
            }

            // every chest is looked at once and counted once: a chest that matches several words of "fruit, fish" (or several of its items match)
            // is still ONE found chest, ONE highlight and one set of item totals, because the words are combined into ONE matcher ("matches")
            foreach (var (location, position, chest, label) in allChests)
            {
                bool chestHasItem = ItemsWithNested(chest.Items).Any(i => matches(i));
                if (!chestHasItem) continue;

                highlightedChests.Add((location, position, chest));
                if (highlightWordCount > 0)
                {
                    // the words of the items this chest holds (with the chosen star; an item counts for its own word, see OwnerSlot), in the
                    // order typed: the strips of its flash
                    chestWordSlots[chest] = StripOrder(ItemsWithNested(chest.Items)
                        .Where(i => matches(i))
                        .Select(OwnerSlot)
                        .Where(slot => slot >= 0));
                }
                if (!anyQualityCapableMatch && ItemsWithNested(chest.Items).Any(i => matches(i) && CanHaveQuality(i)))
                    anyQualityCapableMatch = true;
                string? islandArea = GetIslandArea(location.Name);
                string placeKey = islandArea != null ? IslandKeyPrefix + islandArea : label;
                AddMatchingItems(placeKey, chest, matches);
                RecordContainerKind(placeKey, chest, matches);
                float distance = GetPlaceSortKey(location, position, playerLocation, playerTile, playerMapWeight, reach);
                if (islandArea != null)
                {
                    islandCounts[islandArea] = islandCounts.GetValueOrDefault(islandArea) + 1;
                    islandDistances[islandArea] = islandDistances.TryGetValue(islandArea, out float islandNearest) ? Math.Min(islandNearest, distance) : distance;
                }
                else
                {
                    mainCounts[label] = mainCounts.GetValueOrDefault(label) + 1;
                    mainDistances[label] = mainDistances.TryGetValue(label, out float mainNearest) ? Math.Min(mainNearest, distance) : distance;
                }
                found++;
            }

            // the backpack, a place of its own (see BACKPACK AS A PLACE): it is nearer than everything (distance -1: first in the place list),
            // it counts once, and the player flashes (in the colors of the words the backpack holds)
            List<Item> backpackMatches = ItemsWithNested(backpackItems).Where(i => matches(i)).ToList();
            bool backpackFound = backpackMatches.Count > 0;
            if (backpackFound)
            {
                highlightBackpack = true;
                if (highlightWordCount > 0)
                {
                    backpackWordSlots = StripOrder(backpackMatches
                        .Select(OwnerSlot)
                        .Where(slot => slot >= 0));
                }
                if (!anyQualityCapableMatch && backpackMatches.Any(CanHaveQuality))
                    anyQualityCapableMatch = true;
                AddMatchingItems(BackpackPlaceKey, backpackItems, matches);
                mainCounts[BackpackPlaceKey] = 1;
                mainDistances[BackpackPlaceKey] = -1f;
            }

            // what the search matches among the items Marlon can sell back (lost on the last death): one note line at the end of the panel
            lostOnDeathNote = BuildLostOnDeathNote(matches);

            // the search only refers to items without quality (found: every matched chest item has none; not found: every game item the words
            // match has none): the star filter does not apply, and the panel says so (see resultHasNoQualityItemsOnly)
            resultHasNoQualityItemsOnly = (found > 0 || backpackFound)
                ? !anyQualityCapableMatch
                : SearchRefersOnlyToNoQualityItems(terms);

            if (found == 0 && !backpackFound)
            {
                // say what was searched for, so a typo ("satrfruit") is easy to spot; a very long word is cut short so the title stays readable.
                // Two different failures, never mixed up:
                //   the words exist in the game, only no chest holds them:  No items found in your chests for 'Wood', 'vegetable' (categories).
                //                                                           (+ "(Invalid: ...)" in red for the words that match nothing at all)
                //   nothing in the whole game matches any word:             Invalid: 'sdfsd', 'qwe'   (all in dark red, no hover tips)
                var emptyTerms = terms.Where(t => noItemCategoryTerms.Contains(t) || noItemNameTerms.Contains(t)).ToList();
                if (emptyTerms.Count == 0)
                {
                    notifyTitle = BuildQuotedTitle("Invalid: ", invalidTerms.Count > 0 ? invalidTerms : terms, shorten: true, "");
                    notifyInvalidStart = 0;                    // the whole title is the invalid part: dark red
                    notifyInvalidLength = notifyTitle.Length;
                }
                else
                {
                    var (emptyOrdered, emptyLabelAfter, emptyMidLabel, emptyEndLabel) = LabelCategories(emptyTerms);
                    notifyTitle = BuildQuotedTitle("No items found in your chests for ", emptyOrdered, shorten: true, emptyEndLabel + ".", invalidTerms,
                        labelAfter: emptyLabelAfter, midLabel: emptyMidLabel);
                }
                notifyBody = "";
                resultFailed = true;         // the warning icon at the left end of the title row
                resultSuggestions = BuildSpellingSuggestions(terms, invalidTerms, itemName);   // "Did you mean: ..." under the title
                resultSuggestionTips = resultSuggestions.Select(s => GetTermTip(s.Label)).ToList();
                PlaySearchFeedback(false);   // nothing found: one short "dead end" sound
            }
            else
            {
                // Something was found, so the search is a success, however many of the words found nothing. The title lists the words that found
                // something, each in its own quotes and bold; the words that found nothing (or could not be searched) follow in dark red:
                // Found 15 chests containing 'fruit' (Invalid: 'ere', 'df')
                // only the backpack holds it: "Found your backpack containing 'egg'" with the small backpack in front of the title
                string foundPrefix = found > 0
                    ? $"Found {found} {ContainerWord(highlightedChests.Select(h => h.chest).ToList())} containing "   // "fridge(s)" if only fridges were found, else "chest(s)"
                    : "Found your backpack containing ";
                resultBackpackIcon = found == 0;
                // a single category search ("fish", "fruit", "artisan goods" ...) gets the same " (category)" label as the auto-complete, after the
                // closing quote: 'fish' (category); a real item name gets none. With several words the title lists them, each in its own quotes and
                // bold, and if at least one of them is a category the title gets ONE " (categories)" (a label after every word would make
                // the title too long): Found 15 chests containing 'fruit', 'fish' (categories)
                // only the categories carry the label: 'egg', 'fish' (categories), 'Strawberry' (see LabelCategories)
                var (orderedTerms, labelAfter, midLabel, endLabel) = LabelCategories(validTerms);
                notifyTitle = BuildQuotedTitle(foundPrefix, orderedTerms, shorten: false, endLabel, invalidTerms, noItemCategoryTerms, noItemNameTerms,
                    labelAfter, midLabel);
                if (highlightWordCount > 0)
                {
                    // the words of the main sentence get their colors; the bracketed groups after it (found nothing, invalid) never do
                    titleEmphasisSlots = new int[notifyEmphasis.Count];
                    for (int wordIndex = 0; wordIndex < notifyEmphasis.Count; wordIndex++)
                    {
                        bool inMainSentence = notifyTailStart < 0 || notifyEmphasis[wordIndex].Start < notifyTailStart;
                        int wordSlot = inMainSentence && wordIndex < notifyEmphasisTerms.Count ? validTerms.IndexOf(notifyEmphasisTerms[wordIndex]) : -1;
                        titleEmphasisSlots[wordIndex] = wordSlot < highlightWordCount ? wordSlot : -1;
                    }
                    coloredTitle = notifyTitle;
                }
                notifyBody = BuildPlaceSummary(mainCounts, islandCounts, mainDistances, islandDistances, key => PlaceNoteWithNesting(key), area => PlaceNoteWithNesting(IslandKeyPrefix + area, "; "));
                placeItemOrder = BuildPlaceItemOrder(terms, termMatchers, allItems);
                if (highlightWordCount > 0)
                {
                    foreach (Dictionary<string, Item> placeSampleSet in placeSamples.Values)
                    {
                        foreach (var (rowKey, sample) in placeSampleSet)
                        {
                            string foundName = RowName(rowKey);
                            int owner = itemWordSlots.ContainsKey(foundName) ? -1 : OwnerSlot(sample);
                            if (owner >= 0)
                                itemWordSlots[foundName] = owner;
                        }
                    }
                }
                highlightLabel = string.Join(", ", validTerms.Take(3).Select(t => "'" + StripCategorySuffix(t) + "'")) + (validTerms.Count > 3 ? ", ..." : "");
                highlightLabelWords = highlightWordCount > 0 ? validTerms.Take(highlightWordCount).Select(t => "'" + StripCategorySuffix(t) + "'").ToList() : new List<string>();
                placeEntries = ParsePlaceEntries(notifyBody, name => placeItems.ContainsKey(name));
                PlaySearchFeedback(true);    // found: coin + pick-up sounds and a few gold stars beside the panel
            }

            // hover tips of the words in the title (display only; the title itself is not touched)
            BuildEmphasisTips(allItems, terms, termMatchers, qualityOk, invalidTerms, noItemCategoryTerms);

            notifyUntilMs = clock.ElapsedMilliseconds + 8000;
            highlightUntilMs = ((found > 0 || backpackFound) && Config.HighlightTimeoutSeconds > 0)
                ? clock.ElapsedMilliseconds + Config.HighlightTimeoutSeconds * 1000L
                : -1;
            if (!consoleSearchRunning)   // the console command writes its own, longer list (see CONSOLE SEARCH)
                Monitor.Log(notifyTitle + (notifyBody.Length > 0 ? " | " + notifyBody : ""), LogLevel.Info);

            // history: the search menu remembers every word that could be searched (terms), whether it found something or not
            return new SearchReport(SearchOutcome.Done, (found > 0 || backpackFound) ? validTerms : new List<string>(), terms);
        }

        // ===================== GAME ITEM CATALOG (only used to tell whether a search is about items without quality) =====================
        // One sample item of everything the game has that can lie in a chest, built the first time a search finds nothing (rebuilt if the language
        // or the item data changes). Lets "No item 'Wood' found" still know that Wood has no quality, although no chest holds any.
        private List<Item>? gameItemCatalog;
        private object? catalogBuiltFromObjectData;
        private LocalizedContentManager.LanguageCode catalogBuiltLanguage;

        // every kind of item a chest can hold (the same kinds as the suggestion word list, SuggestionIndex.IncludedItemTypes): clothing, mannequins,
        // wallpaper and flooring included, so "has this item a star?" and the "Showing results for:" tips know them too
        private static readonly HashSet<string> CatalogItemTypes = new() { "(O)", "(BC)", "(F)", "(H)", "(W)", "(T)", "(B)", "(TR)", "(S)", "(P)", "(M)", "(WP)", "(FL)" };

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
        /// <summary>
        /// For the star button of the search menu, while the player is still typing: does this search text only name items that never have
        /// quality ("bridal shirt", "wood")? The same rule as the result panel's "This item has no quality." for a search that found nothing:
        /// decided by the game's own item list, with the quality words taken out and at most three words, like the search itself.
        /// Returns null when the star does apply (or nothing is known yet); otherwise the item's name when the text names exactly one item
        /// ("Bridal Shirt"), or "" when it names several (a category, two words ...).
        /// </summary>
        internal string? TextRefersOnlyToNoQualityItems(string text)
        {
            try
            {
                string query = NormalizeQueryText(SearchMenu.ExtractQuality(text ?? "", out _, out _, out _));
                if (query.Length == 0)
                    return null;

                List<Item> catalog = GetGameItemCatalog();
                if (catalog.Count == 0)
                    return null;

                List<string> terms = ResolveAllTerms(catalog, query).Take(MaxSearchTerms).ToList();
                if (terms.Count == 0 || (terms.Count == 1 && !SearchMenu.IsTermSearchable(terms[0])))
                    return null;   // not a word that can be searched (yet): no extra line

                var matchers = new Func<Item, bool>[terms.Count];
                for (int t = 0; t < terms.Count; t++)
                {
                    bool searchable = terms.Count == 1 || SearchMenu.IsTermSearchable(terms[t]);
                    matchers[t] = searchable ? BuildSingleMatcher(catalog, terms[t]) : (_ => false);
                }
                Func<Item, bool> combined = CombineMatchers(matchers);

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item item in catalog)
                {
                    if (!combined(item))
                        continue;
                    if (CanHaveQuality(item))
                        return null;   // something it names can have a star: the star applies
                    string name = GetShownItemName(item);
                    names.Add(string.IsNullOrWhiteSpace(name) ? item.DisplayName : name);
                }

                if (names.Count == 0)
                    return null;
                return names.Count == 1 ? names.First() : "";
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not check the search text for quality: {ex.Message}", LogLevel.Trace);
                return null;
            }
        }

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
            AddMatchingItems(placeKey, chest.Items, matches);
        }

        private void AddMatchingItems(string placeKey, IEnumerable<Item?> items, Func<Item, bool> matches)
        {
            if (!placeItems.TryGetValue(placeKey, out Dictionary<string, int>? totals))
                placeItems[placeKey] = totals = new Dictionary<string, int>();
            if (!placeSamples.TryGetValue(placeKey, out Dictionary<string, Item>? samples))
                placeSamples[placeKey] = samples = new Dictionary<string, Item>();

            if (!placeNesting.TryGetValue(placeKey, out var nesting))
                placeNesting[placeKey] = nesting = (false, new List<Item>());

            foreach (var (item, inside, container) in WithNested(items))
            {
                if (!matches(item)) continue;
                if (container == null)
                    nesting.Direct = true;
                else if (!nesting.Containers.Any(c => ReferenceEquals(c, container)))
                    nesting.Containers.Add(container);
                placeNesting[placeKey] = nesting;
                string name = RowKey(item, inside);   // the item name, or name + quality (see ONE ROW PER QUALITY); inside a carried container marked so
                totals[name] = totals.GetValueOrDefault(name) + item.Stack;
                if (!samples.ContainsKey(name))
                    samples[name] = MakeIconSample(item, singleItemQualityRows);
            }
        }

        /// <summary>
        /// For every item name found this search: the index of the first search word that found it and its place in that word's list (the list of
        /// its tip, which is also the drop-down's order). See placeItemOrder.
        /// </summary>
        private Dictionary<string, (int Term, int Position)> BuildPlaceItemOrder(List<string> terms, Func<Item, bool>[] termMatchers, List<Item> pool)
        {
            var order = new Dictionary<string, (int Term, int Position)>();
            try
            {
                var samples = new Dictionary<string, Item>();
                foreach (Dictionary<string, Item> placeSampleSet in placeSamples.Values)
                {
                    foreach (var (name, sample) in placeSampleSet)
                        samples.TryAdd(name, sample);
                }

                var positions = new List<Dictionary<string, int>>();
                for (int t = 0; t < terms.Count; t++)
                {
                    var positionOf = new Dictionary<string, int>();
                    List<string> listed = FindMatchedEntries(terms[t], termMatchers[t], pool, out _, out _);
                    for (int i = 0; i < listed.Count; i++)
                        positionOf.TryAdd(Norm(listed[i]), i);
                    positions.Add(positionOf);
                }

                foreach (var (rowKey, sample) in samples)
                {
                    string name = RowName(rowKey);   // quality rows share their item's place
                    for (int t = 0; t < terms.Count; t++)
                    {
                        if (!termMatchers[t](sample))
                            continue;
                        order[name] = (t, positions[t].TryGetValue(Norm(name), out int position) ? position : int.MaxValue);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not order the items of the places: {ex.Message}", LogLevel.Trace);
            }
            return order;
        }

        /// <summary>The sort key of an item line of a place list (see placeItemOrder); an item not known there comes last.</summary>
        private (int Term, int Position) PlaceItemOrderOf(string name)
        {
            return placeItemOrder.TryGetValue(RowName(name), out var key) ? key : (int.MaxValue, int.MaxValue);   // a quality row: by its item
        }

        // Counts one found container by its kind for its place, and adds its matching items to the place's section of that kind.
        private void RecordContainerKind(string placeKey, Chest chest, Func<Item, bool> matches)
        {
            ContainerKind kind = KindOf(chest);
            if (!placeKindCounts.TryGetValue(placeKey, out Dictionary<ContainerKind, int>? kinds))
                placeKindCounts[placeKey] = kinds = new Dictionary<ContainerKind, int>();
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

            if (!placeSectionItems.TryGetValue(placeKey, out Dictionary<string, Dictionary<string, int>>? sections))
                placeSectionItems[placeKey] = sections = new Dictionary<string, Dictionary<string, int>>();
            if (!sections.TryGetValue(kind.SectionKey, out Dictionary<string, int>? totals))
                sections[kind.SectionKey] = totals = new Dictionary<string, int>();

            foreach (var (item, inside, _) in WithNested(chest.Items))
            {
                if (!matches(item)) continue;
                string name = RowKey(item, inside);
                totals[name] = totals.GetValueOrDefault(name) + item.Stack;
            }
        }

        /// <summary>The kind counts of a place key; the bare island prefix adds up every Ginger Island area.</summary>
        private Dictionary<ContainerKind, int> GetPlaceKinds(string key)
        {
            var result = new Dictionary<ContainerKind, int>();
            foreach (var pair in placeKindCounts)
            {
                bool belongs = key == IslandKeyPrefix ? pair.Key.StartsWith(IslandKeyPrefix, StringComparison.Ordinal) : pair.Key == key;
                if (!belongs) continue;
                foreach (var kind in pair.Value)
                    result[kind.Key] = result.GetValueOrDefault(kind.Key) + kind.Value;
            }
            return result;
        }

        /// <summary>
        /// The bracket note of a place in the place list. It is there so a player does not walk past a container they would not think of: so it is
        /// written only when EVERY matching container of the place is one special kind (or one family: fridge + mini-fridge). As soon as a chest
        /// there holds the item too, the player goes in for the chest and sees every highlight anyway: no note. Any other mix: no note either.
        /// "fridge", "mini-fridge", "mini-fridges", "fridges" (fridge + mini-fridge), "Auto-Grabbers", "Dresser" ...
        /// </summary>
        private string PlaceNote(string key)
        {
            Dictionary<ContainerKind, int> kinds = GetPlaceKinds(key);
            if (kinds.Count == 0 || kinds.Keys.Any(k => !k.IsSpecial))
                return "";
            ContainerKind naming = NamingKind(kinds.Keys);
            if (!naming.IsSpecial)
                return "";
            return kinds.Values.Sum() == 1 ? naming.Singular : naming.Plural;
        }

        /// <summary>
        /// The sections of a place's item list when the place mixes kinds of containers (null when it has only one kind: then the list stays as it
        /// always was). Chests first ("Chest" / "Chests", every size together), then the special kinds in their order ("Fridge", "Auto-Grabbers" ...).
        /// The bare island prefix adds up every Ginger Island area.
        /// </summary>
        private List<(string Title, Dictionary<string, int> Items)>? GetPlaceSections(string key)
        {
            // section key -> (order, container count, kind for the name) and the items
            var counts = new Dictionary<string, (int Order, int Count, ContainerKind Kind)>();
            foreach (var kind in GetPlaceKinds(key))
            {
                string sectionKey = kind.Key.SectionKey;
                ContainerKind named = kind.Key.IsSpecial ? kind.Key : ChestKind;
                counts[sectionKey] = counts.TryGetValue(sectionKey, out var known)
                    ? (known.Order, known.Count + kind.Value, known.Kind)
                    : (named.Order, kind.Value, named);
            }
            if (counts.Count < 2)
                return null;

            var items = new Dictionary<string, Dictionary<string, int>>();
            foreach (var pair in placeSectionItems)
            {
                bool belongs = key == IslandKeyPrefix ? pair.Key.StartsWith(IslandKeyPrefix, StringComparison.Ordinal) : pair.Key == key;
                if (!belongs) continue;
                foreach (var section in pair.Value)
                {
                    if (!items.TryGetValue(section.Key, out Dictionary<string, int>? totals))
                        items[section.Key] = totals = new Dictionary<string, int>();
                    foreach (var item in section.Value)
                        totals[item.Key] = totals.GetValueOrDefault(item.Key) + item.Value;
                }
            }

            static string Capitalized(string word) => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;

            return counts
                .OrderBy(c => c.Value.Order)
                .ThenBy(c => c.Value.Kind.Singular)
                .Where(c => items.ContainsKey(c.Key))
                .Select(c => (Capitalized(c.Value.Count == 1 ? c.Value.Kind.Singular : c.Value.Kind.Plural), items[c.Key]))
                .ToList();
        }

        /// <summary>Zoom of the quality star on a 32 px item icon of the lists: 8 px sprite * 2 = 16 px (half the icon, a little larger than the game's own ratio so it reads).</summary>
        private const float IconStarScale = 2f;

        /// <summary>Sort position of a quality row: plain (0), silver (1), gold (2), iridium (4) in that order; rows without a quality first.</summary>
        private static int QualityRowOrder(string key) => RowQuality(key);

        // A copy of an item that is only used to draw its icon: one piece, and no star (the line of the list adds up every quality of the item).
        // With one row per quality (keepQuality) the copy keeps its star: the row is about that quality, and the list draws the star on the icon.
        private static Item MakeIconSample(Item item, bool keepQuality = false)
        {
            try
            {
                Item one = item.getOne();
                one.Quality = keepQuality ? item.Quality : 0;
                return one;
            }
            catch (Exception)
            {
                return item;   // the item itself still draws its icon
            }
        }

        // Icon samples for a place key; the bare island prefix adds up every Ginger Island area (same rule as GetPlaceItems).
        private Dictionary<string, Item>? GetPlaceSamples(string key)
        {
            if (key == IslandKeyPrefix)
            {
                var total = new Dictionary<string, Item>();
                foreach (var pair in placeSamples)
                {
                    if (!pair.Key.StartsWith(IslandKeyPrefix, StringComparison.Ordinal)) continue;
                    foreach (var item in pair.Value)
                        total.TryAdd(item.Key, item.Value);
                }
                return total.Count > 0 ? total : null;
            }

            return placeSamples.TryGetValue(key, out Dictionary<string, Item>? samples) ? samples : null;
        }

        /// <summary>
        /// Draws the real icon of an item exactly centered in a square of <paramref name="size"/> pixels (x, y = its top left corner).
        /// </summary>
        /// <remarks>
        /// The game's Item.drawInMenu is NOT used for this: it is only placed right at its normal 64 px size. At other sizes every item kind
        /// lands somewhere else (an object is drawn at "location + 32 * scale" with an origin that is scaled as well, a tool / hat / weapon /
        /// furniture at "location + 32"), so at 32 px the icons were 8 to 16 px too far right and down, and not even by the same amount.
        /// Instead the sprite is taken straight from the game's item data (the same texture and source rectangle drawInMenu uses) and fitted into
        /// the square: whole-number zoom when that fills it well (crisp pixels, 16 px sprite -> exactly 2x), otherwise the largest zoom that fits.
        /// Colored items (Wine, Jelly, Juice, Pickles, Roe ... made from a specific ingredient) are drawn like the game draws them: the plain
        /// sprite plus its color layer (the next sprite), tinted with the item's color. Smoked fish (the game draws the fish itself, darkened and
        /// with animated smoke) is drawn as its fish's sprite with a smoky brown tint.
        /// </remarks>
        internal static void DrawItemIcon(SpriteBatch b, Item item, int x, int y, int size, float alpha = 1f)
        {
            try
            {
                // every Weeds variant (by name, or by its object id) is drawn with the sprite of object 313, the classic Weeds
                string iconId = IsWeedsItem(item)
                    ? "(O)" + SuggestionIndex.ClassicWeedsObjectId
                    : SuggestionIndex.CanonicalItemId(item.QualifiedItemId);
                ParsedItemData data = ItemRegistry.GetDataOrErrorItem(iconId);
                Color plainTint = Color.White;
                bool smokedFish = false;

                // smoked fish: the sprite of the fish it was made from, darkened like the game's smoked fish
                if (item is StardewValley.Object obj && obj.preserve.Value == StardewValley.Object.PreserveType.SmokedFish
                    && !string.IsNullOrEmpty(obj.preservedParentSheetIndex.Value))
                {
                    ParsedItemData fish = ItemRegistry.GetDataOrErrorItem("(O)" + obj.preservedParentSheetIndex.Value);
                    if (!fish.IsErrorItem)
                    {
                        data = fish;
                        plainTint = SmokedFishTint;
                        smokedFish = true;
                    }
                }

                if (data.IsErrorItem)
                    return;

                Texture2D texture = data.GetTexture();
                Rectangle source = data.GetSourceRect();
                if (texture == null || source.Width <= 0 || source.Height <= 0)
                    return;

                float scale = Math.Min(size / (float)source.Width, size / (float)source.Height);
                float whole = MathF.Floor(scale);
                if (whole >= 1f && whole >= scale * 0.75f)
                    scale = whole;   // crisp: every sprite pixel is the same number of screen pixels

                int w = (int)Math.Round(source.Width * scale);
                int h = (int)Math.Round(source.Height * scale);
                var dest = new Rectangle(x + (size - w) / 2, y + (size - h) / 2, w, h);

                // clothing is drawn like the game draws it: a shirt is its plain sprite plus its dye layer (the same spot in the right half of the
                // shirt sheet, 128 px further) tinted with the shirt's color; a dyeable shirt has almost nothing in the plain sprite, so without the
                // layer its icon was empty. Pants are their sprite tinted with their color.
                if (item is Clothing clothing)
                {
                    Color dye = clothing.clothesColor.Value;
                    if (clothing.clothesType.Value == Clothing.ClothesType.SHIRT)
                    {
                        b.Draw(texture, dest, source, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth);
                        var dyeSource = new Rectangle(source.X + 128, source.Y, source.Width, source.Height);
                        if (dyeSource.Right <= texture.Width)
                            b.Draw(texture, dest, dyeSource, dye * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth + 0.0001f);
                    }
                    else
                    {
                        b.Draw(texture, dest, source, dye * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth);
                    }
                    return;
                }

                if (item is ColoredObject colored && !smokedFish)
                {
                    Color tint = colored.color.Value;
                    if (colored.colorSameIndexAsParentSheetIndex.Value)
                    {
                        b.Draw(texture, dest, source, tint * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth);
                    }
                    else
                    {
                        b.Draw(texture, dest, source, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth);
                        b.Draw(texture, dest, data.GetSourceRect(1), tint * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth + 0.0001f);
                    }
                    return;
                }

                b.Draw(texture, dest, source, plainTint * alpha, 0f, Vector2.Zero, SpriteEffects.None, IconLayerDepth);
            }
            catch (Exception)
            {
                // an icon that cannot be drawn is simply left out
            }
        }

        /// <summary>The smoky brown a smoked fish icon is tinted with.</summary>
        private static readonly Color SmokedFishTint = new(150, 110, 85);

        /// <summary>Layer depth of the item icons (the same 0.9 the game's drawInMenu was called with before).</summary>
        private const float IconLayerDepth = 0.9f;

        /// <summary>
        /// Screen y at which one line of text in <paramref name="font"/> is drawn so that it sits in the vertical middle of a row
        /// (<paramref name="rowTop"/>, <paramref name="rowHeight"/>), like an icon centered in the same row. Whole pixels, so the pixel font stays crisp.
        /// <see cref="TextVisualNudgeY"/> moves it a little down: the font's line box has more empty room below the letters (for g, p, y) than above.
        /// </summary>
        internal static float CenteredTextY(SpriteFont font, int rowTop, int rowHeight)
        {
            return rowTop + (int)Math.Round((rowHeight - font.LineSpacing) / 2f) + TextVisualNudgeY;
        }

        /// <summary>See <see cref="CenteredTextY"/>. 0 = the line box exactly centered; a larger number moves the text down.</summary>
        internal const int TextVisualNudgeY = 1;

        /// <summary>Shortens a text with "..." so it is never wider than <paramref name="maxWidth"/> pixels in the given font.</summary>
        internal static string FitText(string text, int maxWidth, SpriteFont font)
        {
            if (maxWidth <= 0 || font.MeasureString(text).X <= maxWidth)
                return text;

            string cut = text;
            while (cut.Length > 1 && font.MeasureString(cut.TrimEnd() + "...").X > maxWidth)
                cut = cut.Substring(0, cut.Length - 1);
            return cut.TrimEnd() + "...";
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
        // "Farm, Cindersap Forest x3, Ginger Island (Docks, Farm x2)" -> Farm | Cindersap Forest x3 | Ginger Island | Docks | Farm x2
        // isPlace: true for a real place name. A part like "Farmhouse x2 (fridges)" ends with the bracket note of PlaceNote: it is only taken off
        // when what is left is a real place, so a place whose own name ends with brackets keeps them.
        private static List<PlaceEntry> ParsePlaceEntries(string body, Func<string, bool>? isPlace = null)
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
                        // "Farmhouse x2: fridge" is ONE word to hover (the note belongs to its area): the area key is what stands before ": "
                        string area = body.Substring(k, end - k);
                        int noteAt = area.IndexOf(": ", StringComparison.Ordinal);
                        string code = StripCount(noteAt >= 0 ? area.Substring(0, noteAt) : area);
                        entries.Add(new PlaceEntry(IslandKeyPrefix + code, "Ginger Island (" + code + ")", k, end - k));
                        k = end + 2;
                    }
                }
                else if (part.Length > 0)
                {
                    string name = StripCount(part);
                    int bracket = part.LastIndexOf(" (", StringComparison.Ordinal);
                    if (isPlace != null && !isPlace(name) && bracket > 0 && part.EndsWith(")", StringComparison.Ordinal))
                    {
                        string withoutNote = StripCount(part.Substring(0, bracket));
                        if (isPlace(withoutNote))
                            name = withoutNote;   // "Farmhouse x2 (fridges)" -> "Farmhouse"
                    }
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

        // ===================== HOVER TIPS: ONE SET OF RULES FOR EVERY PLACE =====================
        // Every hover tip about a searched word (the quoted words of the result title, the search history, the auto-complete rows, the
        // "Did you mean" words) comes from ONE function, GetTermTip / BuildTermTip, so the same word always gets the same tip:
        //   an item the game has ('Starfruit', 'Peach Wine')      -> "Category: Fruit" (the game's own category name; Peach Wine -> Artisan Goods;
        //                                                            Spook Fish Roe -> Fish Roe). NEVER "Showing results for": that is only for a category word or a part of a name
        //   a category word ('fruit', 'artisan goods', 'wine')    -> "Showing all items under the Fruit (category)" (no item list)
        //       ... in the "No items found in" part of the title  -> "No items under the Vegetable (category) in your chests"
        //   part of a name ('starf') that matches ONE item        -> "Showing results for Starfruit" (no brackets)
        //   part of a name that matches SEVERAL things ('fis')    -> "Showing results for:", then the matching CATEGORIES first ("Fish (category)"),
        //                                                            then every item of the whole game that matches ("Catfish", "Sunfish" ...),
        //                                                            at most 12 lines, then "... and more" (a tip never scrolls)
        //   an item id ('130')                                    -> "Fish (ID: 130)" (the category of that item)
        //   a category id ('-79')                                 -> "Fruit (category, ID: -79)"
        //   a word that nothing in the whole game matches (Invalid) -> in the result title: no tip (the word is dark red); in the search history
        //                                                            (GetTermTip): "Invalid: 'frish'", the wording of the result title
        //   a word that exists in the game but is in no chest (an empty result) -> the normal tip of that word (category / item / part of a name)
        // "(category)" is only ever written after a word that the search really treats as a category.
        // The result title's tips are worked out once per search (BuildEmphasisTips), the others once per word while a menu is open (cached there).

        /// <summary>Kept for the drop-down's own fallback (only used if no tip function is passed to the search menu).</summary>
        internal const string CategoryWordTip = "Showing all items under this category";

        /// <summary>At most this many lines (categories + items) in a "Showing results for:" tip; if there are more, the last line is "... and more".</summary>
        private const int MaxTipNames = 12;

        private static readonly Dictionary<string, string?> categoryNameTipCache = new();

        /// <summary>"Category: Fish Roe", or null (no tip) when the item has no category name at all.</summary>
        private static string? CategoryTipOfItem(string? categoryName)
        {
            // last line of defence: a bogus label ("Weeds", "Seaweed" ...) is never printed, whatever reaches here
            if (string.IsNullOrWhiteSpace(categoryName) || !IsLegalCategoryLabel(categoryName))
                return null;
            return "Category: " + categoryName.Trim();
        }

        // ===================== HARD BLOCK OF BOGUS CATEGORY LABELS =====================
        // The game has no category called "Weeds" / "Seaweed" (those are ITEM names). A "Category: ..." line is only shown when
        //   1. the item's category id is one of the game's real categories (never litter, -999), AND
        //   2. the label is not on the ban list and not clutter-like (Weeds, Twig, Stone), AND
        //   3. it agrees with what the game calls that category id for the other items of that id (majority vote, GetConsensusCategoryName).
        // If a check fails, NO category line is shown at all: nothing is invented.
        private static readonly HashSet<string> BannedCategoryLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "Weeds", "Weed", "Seaweed", "Twig", "Twigs", "Stone", "Litter"
        };

        /// <summary>True for every weeds variant ("Weeds", "Green Rain Weeds"), by display name or internal name. "Seaweed" is not one.</summary>
        internal static bool IsWeedsItem(Item item)
        {
            return SuggestionIndex.IsWeedsName(item.DisplayName) || SuggestionIndex.IsWeedsName(item.Name);
        }

        /// <summary>The item name as the result panel and the tips show it: every weeds variant is merged into "Weeds".</summary>
        internal static string GetShownItemName(Item item)
        {
            return IsWeedsItem(item) ? "Weeds" : item.DisplayName;
        }

        // Sea creatures that must always be labeled Fish in the tips, whatever the game data says (internal names, spaces removed, lower case).
        private static readonly HashSet<string> ForcedFishNames = new() { "seaweed", "seajelly", "seaurchin" };

        private static bool IsLegalCategoryLabel(string? label)
        {
            string text = (label ?? "").Trim();
            if (text.Length == 0 || BannedCategoryLabels.Contains(text) || SuggestionIndex.IsClutterName(text))
                return false;
            return text.Any(char.IsLetter);
        }

        private static readonly Dictionary<string, string?> consensusCategoryNames = new();

        /// <summary>
        /// What the game calls a category id: a majority vote over up to 25 carryable objects with that id, so one odd item can never rename a category.
        /// Null if no object of that id has a usable name (tools, weapons ... have no object entries; the caller then uses the item's own name).
        /// </summary>
        private static string? GetConsensusCategoryName(int categoryId)
        {
            string cacheKey = categoryId + "|" + LocalizedContentManager.CurrentLanguageCode;
            if (consensusCategoryNames.TryGetValue(cacheKey, out string? cached))
                return cached;

            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int sampled = 0;
            try
            {
                foreach (var pair in Game1.objectData)
                {
                    if (pair.Value == null || pair.Value.Category != categoryId || string.Equals(pair.Value.Type, "Quest", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? name;
                    try
                    {
                        name = ItemRegistry.Create("(O)" + pair.Key)?.getCategoryName()?.Trim();
                    }
                    catch (Exception)
                    {
                        name = null;
                    }

                    if (name == null || !IsLegalCategoryLabel(name))
                        continue;

                    votes[name] = votes.GetValueOrDefault(name) + 1;
                    if (++sampled >= 25)
                        break;
                }
            }
            catch (Exception)
            {
                votes.Clear();
            }

            string? winner = votes.Count == 0 ? null : votes.OrderByDescending(v => v.Value).First().Key;
            consensusCategoryNames[cacheKey] = winner;
            return winner;
        }

        /// <summary>
        /// The category name for an item the game gives no name for. Roe is filed under "sell at fish shop" (id -23), which the game does not name,
        /// so it, its aged version and the other products the game builds from a fish or fruit get the name a player knows them by. Any other item
        /// without a name takes the name of its category id from the search's own table; null if even that has none.
        /// </summary>
        private static string? GetFallbackCategoryName(string? qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return null;

            switch (qualifiedId)
            {
                case "(O)812": return "Fish Roe";
                case "(O)447": return "Aged Roe";
                case "(O)SmokedFish": return "Smoked Fish";
                case "(O)DriedFruit": return "Dried Fruit";
                case "(O)DriedMushrooms": return "Dried Mushrooms";
                case "(O)SpecificBait": return "Targeted Bait";
            }

            try
            {
                var data = ItemRegistry.GetData(qualifiedId);
                if (data == null || data.IsErrorItem)
                    return null;
                string? byId = SearchMenu.TryGetIdName(data.Category.ToString(CultureInfo.InvariantCulture));
                return byId != null ? CategoryDisplayName(byId) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The ids of every category the search knows (single-word, multi-word and the id table). Those are the game's native categories.</summary>
        private static HashSet<int>? knownCategoryIdsCache;

        private static HashSet<int> KnownCategoryIds
        {
            get
            {
                if (knownCategoryIdsCache != null)
                    return knownCategoryIdsCache;

                var ids = new HashSet<int>(SearchMenu.CategoryNamesById.Keys);
                foreach (int[] list in SingleWordCategories.Values)
                    ids.UnionWith(list);
                foreach (var (list, _) in MultiWordCategories)
                    ids.UnionWith(list);
                return knownCategoryIdsCache = ids;
            }
        }

        /// <summary>
        /// The game's own category name of an item ("Artisan Goods" for "(O)348"), in the game's language, or null if the item is unknown or has none.
        /// THE one place that names a category for every tip (drop-down, "Did you mean", history, result title). The name always follows the item's real
        /// category id from the game's data: it is the game's text only for an id that is one of the game's native categories (the ones the search
        /// itself knows), and nothing is made up. A name that is really the NAME OF AN ITEM (Weeds, Twig, Stone) is never a category: the game's only
        /// category for those is Litter, and it belongs to category id -999 alone.
        /// </summary>
        internal static string? GetCategoryNameForItemId(string? qualifiedId)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return null;

            string cacheKey = qualifiedId + "|" + LocalizedContentManager.CurrentLanguageCode;
            if (categoryNameTipCache.TryGetValue(cacheKey, out string? cached))
                return cached;

            string? name = null;
            try
            {
                ParsedItemData? data = ItemRegistry.GetData(qualifiedId);

                // hard rule: Seaweed, Sea Jelly and Sea Urchin always read "Category: Fish"
                if (data != null && !data.IsErrorItem && ForcedFishNames.Contains(Norm(data.InternalName ?? "")))
                {
                    categoryNameTipCache[cacheKey] = GetConsensusCategoryName(-4) ?? CategoryDisplayName("fish");
                    return categoryNameTipCache[cacheKey];
                }

                // only a real game category id (never litter, -999) may produce a name at all
                if (data != null && !data.IsErrorItem && data.Category != -999 && KnownCategoryIds.Contains(data.Category))
                {
                    string? own = ItemRegistry.Create(qualifiedId).getCategoryName()?.Trim();
                    string? consensus = GetConsensusCategoryName(data.Category);

                    // the game's agreed name for the id wins (so one item reporting a bogus label like "Weeds" is corrected, e.g. to "Fish");
                    // with no agreed name (tools, weapons ...) the item's own name is used, but only if it is a legal label
                    string? chosen = consensus ?? own;
                    if (IsLegalCategoryLabel(chosen))
                        name = chosen!.Trim();

                    // shirts and pants: the game calls them "Clothes", the search's category is "clothing": every tip says "Clothing", like the
                    // category the player types ("clothes" is only a hidden second spelling of it, see SingleWordCategories)
                    if (data.Category == -100)
                        name = CategoryDisplayName("clothing");

                    // eggs and milk: the game names both (and wool, feathers ...) "Animal Product", but the search has a category word of their own.
                    // The tip names that word first, so it matches what can be typed, and keeps the game's name in brackets: "Egg (Animal Product)"
                    if (data.Category == -5 || data.Category == -6)
                    {
                        string searchWord = CategoryDisplayName(data.Category == -5 ? "egg" : "milk");
                        name = string.IsNullOrEmpty(name) ? searchWord : $"{searchWord} ({name})";
                    }
                }
            }
            catch (Exception)
            {
                name = null;   // no name rather than a crash
            }

            categoryNameTipCache[cacheKey] = name;
            return name;
        }

        /// <summary>The qualified id of the item (or of the base product of a built name like "Peach Wine") whose name is exactly the text, or null.</summary>
        private string? FindExactItemId(string term)
        {
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();

                foreach (string variant in PluralVariants(Norm(term)))   // "cranberries" finds "Cranberries" and the other way round
                {
                    string? holdableId = suggestionIndex.GetHoldableItemId(variant);   // a name resolves to the item a player can really own first
                    if (holdableId != null)
                        return holdableId;

                    IReadOnlyList<Suggestion> best = suggestionIndex.Search(variant, 1);   // the name that equals the text always comes first
                    if (best.Count > 0 && best[0].QualifiedId != null && SuggestionIndex.MakeKey(best[0].Text) == SuggestionIndex.MakeKey(variant))
                        return best[0].QualifiedId;
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not look up the item for a title tip: {ex.Message}", LogLevel.Trace);
            }

            return null;
        }

        /// <summary>
        /// The hover tip of one word, for the search history, the auto-complete rows and the "Did you mean" words (the search menu asks for it while the
        /// mouse is over a word). It runs the SAME code as the result title (BuildTermTip), fed the same way ScanChests feeds it: the items of every chest,
        /// and the matcher the real search uses for that word (BuildSingleMatcher). So "frui", "starfrui" or "fis" get exactly the list the title of
        /// the result page shows, and a word nothing in the game matches ("frish") gets the Invalid line instead of nothing.
        /// (The star filter of a history entry is not applied: the list names what the word means, not what a star would leave.)
        /// </summary>
        internal string? GetTermTip(string word)
        {
            try
            {
                string text = StripCategorySuffix(word ?? "").Trim();

                List<Item> allItems = new();
                Func<Item, bool>? matchesTerm = null;
                try
                {
                    allItems = GetChestItemsForTips();
                    if (text.Length > 0 && SearchMenu.IsTermSearchable(text))
                        matchesTerm = BuildSingleMatcher(allItems, text);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Could not read the chests for a history tip: {ex.Message}", LogLevel.Trace);
                    matchesTerm = null;   // the list is then built from the game's items alone
                }

                string? tip = BuildTermTip(word ?? "", invalid: false, noItemsInCategory: false, matchesTerm: matchesTerm, pool: matchesTerm != null ? allItems : null);
                return tip ?? BuildInvalidTip(text);
            }
            catch (Exception)
            {
                return null;   // no tip rather than a crash
            }
        }

        /// <summary>Every item in every chest and fridge, collected exactly like ScanChests does (every container once).</summary>
        private List<Item> GetChestItemsForTips()
        {
            var items = new List<Item>();
            foreach (var (_, _, chest, _) in GetAllContainers())
            {
                if (chest.Items != null)
                    items.AddRange(ItemsWithNested(chest.Items));
            }
            return items;
        }

        /// <summary>
        /// "Invalid: 'frish'" for a word the result page would list as Invalid (same test as ScanChests: not a category, not the name of an item the game
        /// has, not a part of any name), else null. Numbers (ids) and empty text never get it.
        /// </summary>
        private string? BuildInvalidTip(string text)
        {
            if (text.Length == 0 || int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) || IsCategoryWord(text))
                return null;

            bool exists = SearchMenu.IsTermSearchable(text) && (IsKnownItemName(GetKnownNameCheck(), text) || ExistsInGame(text));
            if (exists)
                return null;

            string shown = text.Length > 30 ? text.Substring(0, 30) + "..." : text;
            return $"Invalid: '{shown}'";
        }

        /// <summary>The hover tip of one searched word (see the block comment above).</summary>
        /// <param name="invalid">The search already knows the word matches nothing (the "(Invalid: ...)" words).</param>
        /// <param name="noItemsInCategory">A category word in the "No items found in your chests" part of the title (it gets the normal category tip).</param>
        /// <param name="matchesTerm">What the word matched in the search (with the star filter), with <paramref name="pool"/> the chest items it ran over. Null = work it out here.</param>
        private string? BuildTermTip(string term, bool invalid, bool noItemsInCategory, Func<Item, bool>? matchesTerm, List<Item>? pool)
        {
            if (invalid)
                return null;   // Invalid (nothing in the game matches it): no tip at all

            // "Egg (item)": NOT the category tip. It says what the label does, then the item's own category, like every item tip
            if (TryStripItemLabel(term, out string labeledItem))
            {
                string? itemId = FindExactItemId(labeledItem);
                string? itemCategory = itemId != null ? GetCategoryNameForItemId(itemId) : null;
                string kinds = Norm(labeledItem) == "egg" ? " (white and brown)" : "";   // the game has two items called "Egg"
                string what = $"Only the item \"{labeledItem}\"{kinds}, not the whole {CategoryDisplayName(labeledItem)} category";
                return string.IsNullOrEmpty(itemCategory) ? what : what + "\nCategory: " + itemCategory;
            }

            string text = StripCategorySuffix(term ?? "").Trim();   // also "268 (Starfruit)" -> "268"
            if (text.Length == 0)
                return null;

            // an id: positive = one object ("Fish (ID: 130)"), negative = a category ("Fruit (category, ID: -79)")
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            {
                string id = number.ToString(CultureInfo.InvariantCulture);
                if (number < 0)
                {
                    string? categoryName = SearchMenu.TryGetIdName(id);
                    return categoryName != null ? $"{CategoryDisplayName(categoryName)} (category, ID: {id})" : null;
                }

                string qualifiedId = "(O)" + id;
                var data = ItemRegistry.GetData(qualifiedId);
                if (data == null || data.IsErrorItem)
                    return null;

                string label = GetCategoryNameForItemId(qualifiedId) ?? data.DisplayName;
                return $"{label} (ID: {id})";
            }

            // a word the search treats as a whole category
            if (IsCategoryWord(text))
            {
                // the same tip whether or not a chest holds something of it (an empty result still shows what the category is)
                return $"Showing all items under the {CategoryDisplayName(text)} (category)";
            }

            // a word family ("shirt", "bed", see WORD FAMILIES): the search finds the whole family, so the tip lists it, the plain item first
            if (IsWordFamilyName(Norm(text)))
            {
                HashSet<string> family = GetWordFamilyNames(Norm(text));
                List<string> found = FindMatchedEntries(text, matchesTerm, pool, out _, out _)
                    .Where(name => family.Contains(Norm(name)))
                    .OrderBy(name => Norm(name) == Norm(text) ? 0 : 1)   // the plain item first, the rest in the usual order
                    .ToList();
                if (found.Count > 1)
                {
                    var familyTip = new System.Text.StringBuilder("Showing results for:");
                    foreach (string entry in found.Take(MaxTipNames))
                        familyTip.Append('\n').Append(entry);
                    if (found.Count > MaxTipNames)
                        familyTip.Append('\n').Append("... and more");
                    return familyTip.ToString();
                }
            }

            // a real item name, or a name the game builds on the fly ("Peach Wine", "Spook Fish Roe"): ALWAYS "Category: ...", never "Showing results for".
            // ("Showing results for" belongs to a category word or to a part of a name, below.)
            string? exactId = FindExactItemId(text);
            if (exactId != null)
                return CategoryTipOfItem(GetCategoryNameForItemId(exactId) ?? GetFallbackCategoryName(exactId));

            // an item that lies in a chest but is not in the word list (another mod's item): its own category
            Item? chestItem = pool?.FirstOrDefault(i => i != null && Norm(i.DisplayName ?? "") == Norm(text));
            if (chestItem != null)
            {
                string? ownCategory = GetCategoryNameForItemId(chestItem.QualifiedItemId);   // the same rule as every other tip
                return CategoryTipOfItem(!string.IsNullOrEmpty(ownCategory) ? ownCategory : GetFallbackCategoryName(chestItem.QualifiedItemId));
            }

            // part of a name: the categories whose name contains it (first, labeled "(category)"), then every item the game has that contains it
            List<string> entries = FindMatchedEntries(text, matchesTerm, pool, out int categoryCount, out string? onlyCategory);
            if (entries.Count == 0)
                return null;   // nothing in the game matches it (Invalid): no tip
            if (entries.Count == 1)
            {
                if (onlyCategory != null)
                    return $"Showing all items under the {onlyCategory} (category)";

                // the text IS the one name (an item the lookups above could not place): a concrete item has no "Showing results", so no tip
                if (Norm(entries[0]) == Norm(text))
                    return null;

                return $"Showing results for {entries[0]}";   // a part of a name that fits exactly one item ("starf")
            }

            // a fixed-size list, never a scrolling one: what does not fit is cut, with "... and more" as the last line
            var tip = new System.Text.StringBuilder("Showing results for:");
            foreach (string entry in entries.Take(MaxTipNames))
                tip.Append('\n').Append(entry);
            if (entries.Count > MaxTipNames)
                tip.Append('\n').Append("... and more");
            return tip.ToString();
        }

        /// <summary>
        /// The lines of a "Showing results for:" tip for a part of a name ("fis", "artisa"):
        ///   1. the categories whose name contains the text (see MatchingCategoryWords; the search finds the items of exactly these categories too,
        ///      see BuildSingleMatcher P4), labeled: "Fish (category)", "Fishing Tackle (category)" ... ALWAYS first,
        ///      the category whose own name starts with the text before one where only a later word does;
        ///   2. every item of the WHOLE GAME whose name contains the text (not only what is in the chests), plus whatever the search found by name in
        ///      the chests (so flavored items like "Catfish Roe" and items of other mods are never missing). Items with the same root stay together
        ///      ("Starfruit", "Starfruit Wine", "Starfruit Jelly" ...), the groups are ordered by how much a player uses the root item, and furniture /
        ///      clothing / decoration / dishes come last but are never left out (see SuggestionIndex.GetTipNames: it lists every name the drop-down
        ///      offers for the same text; the junk blacklist and the demotion only exist in "Did you mean").
        /// A name that is already listed as a category is not listed again as an item.
        /// </summary>
        /// <param name="categoryCount">How many of the lines are categories.</param>
        /// <param name="onlyCategory">The category name when the ONLY line is one category, else null.</param>
        private List<string> FindMatchedEntries(string text, Func<Item, bool>? matchesTerm, List<Item>? pool, out int categoryCount, out string? onlyCategory)
        {
            categoryCount = 0;
            onlyCategory = null;
            var lines = new List<string>();
            if (!SearchMenu.IsTermSearchable(text))
                return lines;

            string key = Norm(text);
            var listed = new HashSet<string>();   // Norm of every name already listed

            // how well a name fits: 0 = starts with the text, 1 = a later word starts with it, 2 = contains it somewhere
            int Rank(string name)
            {
                if (Norm(name).StartsWith(key, StringComparison.Ordinal))
                    return 0;
                foreach (string word in SplitNameWords(name))
                {
                    if (Norm(word).StartsWith(key, StringComparison.Ordinal))
                        return 1;
                }
                return 2;
            }

            // 1. categories, always first
            var categories = MatchingCategoryWords(text)
                .Select(CategoryDisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Rank)
                .ThenBy(name => name.Length)
                .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (string category in categories)
            {
                if (listed.Add(Norm(category)))
                    lines.Add(category + " (category)");
            }
            categoryCount = lines.Count;

            // 2. items whose NAME contains the text: the whole game (and the names the game builds on the fly, "Starfruit Wine"), the items in the
            //    chests, and what the search matched by name in the chests. The candidate pool, the junk filter (furniture, clothing, decoration and
            //    dishes only when the text is nearly the whole name) and the order (items of one root together, the most used root first) all live in
            //    SuggestionIndex.GetTipNames.
            try
            {
                suggestionIndex ??= new SuggestionIndex();
                suggestionIndex.EnsureBuilt();

                IEnumerable<(string Name, string? QualifiedId)>? extra = null;
                if (matchesTerm != null && pool != null)
                    extra = pool.Where(item => item != null && matchesTerm(item) && NameMatchesPart(item, text))
                                .Select(item => (item.DisplayName, (string?)item.QualifiedItemId));

                // a whole category word ("wine", "fruit"): first its items in exactly the drop-down's order (the category's favourites, a built name
                // like its ingredient: the wines in the order of the fruit), so the tip and the drop-down always list them alike
                string plainWord = StripCategorySuffix(text);
                if (IsCategoryWord(plainWord))
                {
                    foreach (Suggestion row in suggestionIndex.Search(plainWord, int.MaxValue))
                    {
                        if (row.IsCategory || TryStripItemLabel(row.Text, out _) || string.IsNullOrWhiteSpace(row.Text) || row.Text.StartsWith("[", StringComparison.Ordinal))
                            continue;
                        if (listed.Add(Norm(row.Text)))
                            lines.Add(row.Text);
                    }
                }

                foreach (string name in suggestionIndex.GetTipNames(GetSearchVariants(text), extra, text))
                {
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("[", StringComparison.Ordinal))
                        continue;
                    if (listed.Add(Norm(name)))
                        lines.Add(name);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not list the matching items for a tip: {ex.Message}", LogLevel.Trace);
            }

            if (lines.Count == 1 && categoryCount == 1)
                onlyCategory = categories[0];
            return lines;
        }

        /// <summary>True if the item's name (shown or internal) contains the text word-aligned (see NameContains), also in its singular / plural spelling.</summary>
        private bool NameMatchesPart(Item item, string text)
        {
            foreach (string variant in GetSearchVariants(text))
            {
                if (NameContains(item, text, variant))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// What a category word is called in the tips: "fruit" -> "Fruit", "artisan" -> "Artisan Goods", "gems" -> "Gem", "trash" -> "Trash". The word is
        /// looked up in the auto-complete's category words (the spelling a player types), first by itself, then by the category it stands for.
        /// </summary>
        internal static string CategoryDisplayName(string word)
        {
            string norm = Norm(word ?? "");
            string? key = CategoryKey(word ?? "");
            string? best = null;

            foreach (string candidate in SuggestionIndex.CategoryWords)
            {
                if (Norm(candidate) == norm)
                {
                    best = candidate;
                    break;
                }
                if (best == null && key != null && CategoryKey(candidate) == key)
                    best = candidate;
            }

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase((best ?? (word ?? "").Trim()).ToLowerInvariant());
        }

        /// <summary>
        /// The game's category ids of a category word ("fish" -> -4, "artisan goods" -> -26 and -27), from the same tables the search uses. Empty for a
        /// word that is no category or one of the flavored categories ("wine", "roe": those are found by item id, not by category id).
        /// </summary>
        internal static HashSet<int> GetStaticCategoryIds(string word)
        {
            var result = new HashSet<int>();
            string query = (word ?? "").Trim().ToLowerInvariant();
            if (query.Length == 0)
                return result;

            if (SingleWordCategories.TryGetValue(query, out int[]? single))
            {
                foreach (int id in single)
                    result.Add(id);
                return result;
            }

            if (FindDynamicCategory(Norm(query)) != null)
                return result;

            string normQuery = Norm(query);
            string[] tokens = query.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var (ids, words) in MultiWordCategories)
            {
                if ((tokens.Length > 0 && tokens.All(words.Contains)) || words.Contains(normQuery))
                    foreach (int id in ids)
                        result.Add(id);
            }
            return result;
        }

        /// <summary>Which category a word stands for (same tables as the search, see BuildSingleMatcher), as a comparable key; null if it is no category.</summary>
        /// <summary>Every single-word category spelling the search understands (used to inject categories missing from the auto-complete word list, e.g. "litter").</summary>
        internal static IEnumerable<string> GetSingleWordCategoryNames() => SingleWordCategories.Keys;

        internal static string? CategoryKey(string word)
        {
            string query = (word ?? "").Trim().ToLowerInvariant();
            if (query.Length == 0)
                return null;

            if (TryParseMadeFrom(query, out int[] madeFrom, out DynamicCategory? madeProduct) && madeProduct != null)
                return "made:" + string.Join(",", madeFrom.OrderBy(i => i)) + ":" + Array.IndexOf(DynamicCategories, madeProduct);

            if (SingleWordCategories.TryGetValue(query, out int[]? ids1))
                return "ids:" + string.Join(",", ids1.OrderBy(i => i));

            DynamicCategory? dynamic = FindDynamicCategory(Norm(query));
            if (dynamic != null)
                return "dyn:" + Array.IndexOf(DynamicCategories, dynamic);

            string normQuery = Norm(query);
            string[] tokens = query.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
            var ids2 = new SortedSet<int>();
            foreach (var (ids, words) in MultiWordCategories)
            {
                if ((tokens.Length > 0 && tokens.All(words.Contains)) || words.Contains(normQuery))
                    foreach (int id in ids)
                        ids2.Add(id);
            }

            return ids2.Count > 0 ? "ids:" + string.Join(",", ids2) : null;
        }

        /// <summary>Fills notifyEmphasisTips: one tip (or null) for every quoted word of the title just built, in the same order as notifyEmphasis.</summary>
        private void BuildEmphasisTips(List<Item> allItems, List<string> terms, Func<Item, bool>[] termMatchers, Func<Item, bool> qualityOk, List<string> invalidTerms, List<string> noItemCategoryTerms)
        {
            var tips = new List<string?>(notifyEmphasisTerms.Count);
            try
            {
                foreach (string term in notifyEmphasisTerms)
                {
                    int t = terms.FindIndex(x => string.Equals(x, term, StringComparison.Ordinal));
                    Func<Item, bool>? byName = t >= 0 ? termMatchers[t] : null;
                    Func<Item, bool>? matchesTerm = null;
                    if (byName != null)
                        matchesTerm = i => byName(i) && qualityOk(i);
                    string? termTip = BuildTermTip(term, invalidTerms.Contains(term), noItemCategoryTerms.Contains(term), matchesTerm, matchesTerm != null ? allItems : null);
                    if (searchAliasNotes.TryGetValue(Norm(term), out string? aliasNote))   // searched by an alias: first say that it is the same thing
                        termTip = termTip == null ? aliasNote : aliasNote + "\n" + termTip;
                    string? ownColorNote = t >= 0 && matchesTerm != null ? OwnColorNote(t, terms, termMatchers, matchesTerm, allItems) : null;
                    if (ownColorNote != null)   // "fruit, mango": Mango is fruit too, but keeps its own color (see OwnerSlot in ScanChests)
                        termTip = termTip == null ? ownColorNote : termTip + "\n" + ownColorNote;
                    tips.Add(termTip);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not build the title tips: {ex.Message}", LogLevel.Trace);
                tips.Clear();
            }

            while (tips.Count < notifyEmphasis.Count)
                tips.Add(null);
            notifyEmphasisTips = tips;
        }

        /// <summary>
        /// The last line of the tip of an item word in a search with several words, when a category word of the same search also finds its items
        /// ("fruit, mango"): It belongs to 'fruit', which you also searched, so it is marked in its own color. Null otherwise.
        /// </summary>
        private string? OwnColorNote(int t, List<string> terms, Func<Item, bool>[] termMatchers, Func<Item, bool> matchesTerm, List<Item> allItems)
        {
            if (highlightWordCount <= 0 || t >= termWordSlot.Length || termWordSlot[t] < 0 || IsCategoryWord(StripCategorySuffix(terms[t])))
                return null;

            var categories = new List<string>();
            for (int c = 0; c < terms.Count && c < termWordSlot.Length; c++)
            {
                if (c == t || termWordSlot[c] < 0 || !IsCategoryWord(StripCategorySuffix(terms[c])))
                    continue;
                Func<Item, bool> categoryMatcher = termMatchers[c];
                if (allItems.Any(i => matchesTerm(i) && categoryMatcher(i)))
                    categories.Add("'" + StripCategorySuffix(terms[c]) + "'");
            }
            if (categories.Count == 0)
                return null;
            // the line above already names its category; this only says why its color is not the category's
            string categoryNames = categories.Count == 1 ? categories[0] : string.Join(", ", categories.Take(categories.Count - 1)) + " and " + categories[^1];
            return $"It belongs to {categoryNames}, which you also searched, so it is marked in its own color.";
        }

        /// <summary>
        /// Finds the quoted title word under the mouse that has a tip. Returns its index in notifyEmphasis and the on-screen rectangle of each line piece
        /// of it (a word with spaces can be split over two lines by the wrapping). Placed exactly like DrawTitleEmphasis places its bold copies.
        /// </summary>
        private bool TryGetHoveredTitleWord(string wrappedTitle, Vector2 titlePos, float scale, Point mouse, out int emphasisIndex, out List<Rectangle> rects)
        {
            emphasisIndex = -1;
            rects = new List<Rectangle>();
            if (notifyEmphasis.Count == 0 || notifyEmphasisTips.Count != notifyEmphasis.Count || !TryMapTitleLetters(wrappedTitle, out int[] lineOf, out int[] colOf))
                return false;

            string[] lines = wrappedTitle.Replace("\r", "").Split('\n');
            SpriteFont font = Game1.dialogueFont;
            float lineHeight = font.LineSpacing * scale;

            for (int e = 0; e < notifyEmphasis.Count; e++)
            {
                if (notifyEmphasisTips[e] == null)
                    continue;

                (int emphasisStart, int emphasisLength) = notifyEmphasis[e];
                int first = emphasisStart;
                int last = emphasisStart + emphasisLength - 1;
                if (emphasisLength <= 0 || first < 0 || last >= notifyTitle.Length)
                    continue;

                var pieces = new List<Rectangle>();
                int idx = first;
                while (idx <= last)
                {
                    if (char.IsWhiteSpace(notifyTitle[idx]))
                    {
                        idx++;
                        continue;
                    }

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
                            float x = titlePos.X + font.MeasureString(text.Substring(0, colStart)).X * scale;
                            float w = font.MeasureString(text.Substring(colStart, colEnd - colStart)).X * scale;
                            pieces.Add(new Rectangle((int)x, (int)(titlePos.Y + runLine * lineHeight), (int)Math.Ceiling(w), (int)Math.Ceiling(lineHeight)));
                        }
                    }

                    idx = runEnd + 1;
                }

                foreach (Rectangle piece in pieces)
                {
                    Rectangle hit = piece;
                    hit.Inflate(3, 0);
                    if (hit.Contains(mouse))
                    {
                        emphasisIndex = e;
                        rects = pieces;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Draws the hover tip of a title word with the game's own hover box. It follows the mouse (the game's default spot: right of and below the cursor),
        /// but never covers the "Keep highlights" box: if the default spot would, the tip moves to the left of the cursor, then above it, then below the box.
        /// </summary>
        private static void DrawTitleWordTip(SpriteBatch b, string tip, Rectangle? avoid)
        {
            Vector2 size = Game1.smallFont.MeasureString(tip);
            int tipWidth = (int)Math.Ceiling(size.X) + 48;    // text + the box frame (a little more than the game uses, to stay on the safe side)
            int tipHeight = (int)Math.Ceiling(size.Y) + 40;
            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            int mouseX = Game1.getOldMouseX();
            int mouseY = Game1.getOldMouseY();

            Point Clamp(int px, int py) => new(
                Math.Clamp(px, 8, Math.Max(8, screenWidth - tipWidth - 8)),
                Math.Clamp(py, 8, Math.Max(8, screenHeight - tipHeight - 8)));

            Rectangle? keep = null;
            if (avoid is Rectangle keepRect)
            {
                keepRect.Inflate(8, 8);
                keep = keepRect;
            }

            Point[] candidates =
            {
                Clamp(mouseX + 32, mouseY + 32),                       // the game's own spot
                Clamp(mouseX - 32 - tipWidth, mouseY + 32),            // left of the cursor
                Clamp(mouseX + 32, mouseY - 32 - tipHeight),           // above the cursor
                Clamp(mouseX - 32 - tipWidth, mouseY - 32 - tipHeight) // above left
            };

            Point spot = candidates[0];
            if (keep != null)
            {
                bool placed = false;
                foreach (Point candidate in candidates)
                {
                    if (!new Rectangle(candidate.X, candidate.Y, tipWidth, tipHeight).Intersects(keep.Value))
                    {
                        spot = candidate;
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                    spot = Clamp(candidates[0].X, keep.Value.Bottom + 8);   // nothing beside the cursor is free: just under the box
            }

            IClickableMenu.drawHoverText(
                b,
                tip,
                Game1.smallFont,
                xOffset: spot.X - (mouseX + 32),
                yOffset: spot.Y - (mouseY + 32));
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

        /// <summary>
        /// The title as drawn: wrapped to the given width. A title with a dark-red part ("(Invalid: ...)", "(No items found in ...)") puts that part on its
        /// own line(s) under the main sentence instead of squeezing it in behind it; the main sentence and the red part are each wrapped on their own.
        /// Only line breaks are added, so TryMapTitleLetters still finds every letter of <see cref="notifyTitle"/>.
        /// </summary>
        private string WrapTitle(int width)
        {
            // no red part: the title is wrapped exactly as before
            if (notifyTailStart <= 0 || notifyTailStart >= notifyTitle.Length)
                return NormalizeBreaks(Game1.parseText(notifyTitle, Game1.dialogueFont, width));

            string head = notifyTitle.Substring(0, notifyTailStart).TrimEnd();
            string tail = notifyTitle.Substring(notifyTailStart).TrimStart();
            if (head.Length == 0 || tail.Length == 0)
                return NormalizeBreaks(Game1.parseText(notifyTitle, Game1.dialogueFont, width));

            // the main sentence and the red part are wrapped separately and joined with ONE forced line break, so the red part always starts
            // on its own line (both parts start at the left edge of the title, so everything lines up)
            string wrappedHead = NormalizeBreaks(Game1.parseText(head, Game1.dialogueFont, width));
            string wrappedTail = NormalizeBreaks(Game1.parseText(tail, Game1.dialogueFont, width));
            return wrappedHead + "\n" + wrappedTail;
        }

        /// <summary>Uses "\n" as the only line break (Game1.parseText may use "\r\n") and removes blank lines and trailing spaces at the ends and before breaks.</summary>
        private static string NormalizeBreaks(string wrapped)
        {
            string[] lines = wrapped.Replace("\r", "").Split('\n');
            var kept = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = line.TrimEnd();
                if (trimmed.Length > 0)
                    kept.Add(trimmed);
            }
            return string.Join("\n", kept);
        }

        /// <summary>The color of the "(Invalid: ...)" part of the result title: the words of the search that found nothing.</summary>
        private static readonly Color InvalidTitleColor = Color.DarkRed;

        private bool IsInInvalidPart(int titleIndex)
        {
            return notifyInvalidLength > 0 && titleIndex >= notifyInvalidStart && titleIndex < notifyInvalidStart + notifyInvalidLength;
        }

        /// <summary>True while the title has words in the colors of a search with several words (see SEVERAL WORDS, SEVERAL COLORS).</summary>
        private bool HasColoredTitleWords()
        {
            return titleEmphasisSlots.Length > 0 && titleEmphasisSlots.Length == notifyEmphasis.Count && notifyTitle == coloredTitle
                && titleEmphasisSlots.Any(slot => slot >= 0);
        }

        // Several words: a mark in the flash color of each word behind the word in the title, like a highlighter pen (the text itself keeps its
        // normal color). Drawn BEFORE the title text. One mark per line the word lies on (a word with spaces can be split over two lines).
        private void DrawTitleMarks(SpriteBatch b, string wrappedTitle, Vector2 titlePos, float scale, float alpha)
        {
            if (!HasColoredTitleWords() || !TryMapTitleLetters(wrappedTitle, out int[] lineOf, out int[] colOf))
                return;

            string[] lines = wrappedTitle.Replace("\r", "").Split('\n');
            SpriteFont font = Game1.dialogueFont;
            float lineHeight = font.LineSpacing * scale;

            for (int e = 0; e < notifyEmphasis.Count; e++)
            {
                int slot = titleEmphasisSlots[e];
                (int first, int length) = notifyEmphasis[e];
                if (slot < 0 || length <= 0 || first < 0 || first + length > notifyTitle.Length)
                    continue;

                // per line: the first and the last column of the word's letters there
                var spans = new Dictionary<int, (int From, int To)>();
                for (int idx = first; idx < first + length; idx++)
                {
                    if (char.IsWhiteSpace(notifyTitle[idx]))
                        continue;
                    int line = lineOf[idx];
                    int col = colOf[idx];
                    spans[line] = spans.TryGetValue(line, out var span) ? (Math.Min(span.From, col), Math.Max(span.To, col)) : (col, col);
                }

                Color mark = WordFlashColor(slot) * (WordMarkAlpha * alpha);
                foreach (var (line, (from, to)) in spans)
                {
                    if (line < 0 || line >= lines.Length)
                        continue;
                    string text = lines[line];
                    int colEnd = Math.Min(to + 1, text.Length);
                    if (from < 0 || from >= colEnd)
                        continue;
                    float x = titlePos.X + font.MeasureString(text.Substring(0, from)).X * scale;
                    float w = font.MeasureString(text.Substring(from, colEnd - from)).X * scale;
                    float y = titlePos.Y + line * lineHeight;
                    b.Draw(Game1.staminaRect, new Rectangle((int)x - 3, (int)(y + lineHeight * 0.12f), (int)Math.Ceiling(w) + 6, (int)(lineHeight * 0.8f)), mark);
                }
            }
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
        // Header row: "Farm total: 3" (the place and the sum of all matching stacks there); then one line per item: "Starfruit ×1", "Apple ×2".
        // above: the panel is at the bottom of the screen, so the list opens ABOVE it: topY is then the list's BOTTOM edge instead of its top edge.
        // sections: a place that mixes kinds of containers lists its items per kind, each under a small heading ("Chest", "Fridge", "Auto-Grabbers");
        // the same item may then appear in two sections, each with its own count. Null = one plain list, as always.
        private void DrawPlaceTooltip(SpriteBatch b, string title, Dictionary<string, int> items, Dictionary<string, Item>? samples, int anchorX /* left edge of the text */, int topY, Rectangle? avoid = null, bool above = false,
            List<(string Title, Dictionary<string, int> Items)>? sections = null)
        {
            const float titleScale = 0.7f;
            const int padX = 28;
            const int padY = 22;

            // The font's measured height already contains its own top/bottom spacing, so these gaps are small on purpose.
            const int gapTitleToDivider = 0;    // title text -> divider (was 8 + font spacing: looked too airy)
            const int gapDividerToList = 2;     // divider -> first item line (was 8)
            const int dividerHeight = 2;

            // every item line starts with the real icon of the item (32 px), then, after a clear gap, the name and the count.
            // Icon and text are both centered on the row (see DrawItemIcon / CenteredTextY), and the row leaves air above and below the icon.
            const int iconSize = 32;
            const int iconGap = 14;
            const int iconSlot = iconSize + iconGap;
            const int rowAir = 12;   // free room above + below the icon, together

            int screenWidth = Game1.uiViewport.Width;
            int screenHeight = Game1.uiViewport.Height;
            int fontLineHeight = Game1.smallFont.LineSpacing;
            int lineHeight = Math.Max(fontLineHeight + 8, iconSize + rowAir);   // one row of the list

            // the widest an item line (icon + name + count) may be: a very long name is cut short with "..." (the count always stays visible)
            int maxLineWidth = Math.Max(240, Math.Min(screenWidth - 16 - padX * 2 - 24, 560));

            // the header: the place and the total of ALL matching items there (not just the lines that fit on screen)
            string header = $"{title} total: {items.Values.Sum()}";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(header) * titleScale;
            int headerHeight = (int)titleSize.Y + gapTitleToDivider + dividerHeight + gapDividerToList;

            // "×" if the game's font has it, otherwise a plain "x" (a character the font does not have cannot be drawn)
            string times = Game1.smallFont.Characters.Contains('\u00D7') ? "\u00D7" : "x";

            // in the order of the search words, and inside one word in its tip / drop-down order (see placeItemOrder); then the most numerous first.
            // If the list is too tall for the screen the rest is summarised as "+N more"
            var ordered = items
                .OrderBy(p => PlaceItemOrderOf(p.Key).Term)
                .ThenBy(p => PlaceItemOrderOf(p.Key).Position)
                .ThenBy(p => IsNestedRow(p.Key))       // the item lying there directly first, then the same item inside a carried container
                .ThenBy(p => QualityRowOrder(p.Key))   // one row per quality: plain, silver, gold, iridium
                .ThenByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            int availableHeight = (above ? topY : screenHeight - topY) - 16 - padY * 2 - headerHeight;
            int maxLines = Math.Max(3, availableHeight / lineHeight);

            // an item inside a carried container: "Birch Dresser > " comes first, then the item's icon and name, so the icon stands next to the item it
            // shows (see CONTAINERS INSIDE CONTAINERS). Prefix is "" for every other line.
            (string Text, Item? Icon, Color? Mark, string Prefix) MakeLine(KeyValuePair<string, int> pair)
            {
                string countText = $" {times}{pair.Value}";
                string prefix = RowPrefix(pair.Key, Game1.smallFont);
                int prefixWidth = prefix.Length > 0 ? (int)Math.Ceiling(Game1.smallFont.MeasureString(prefix).X) : 0;
                int nameRoom = maxLineWidth - prefixWidth - iconSlot - (int)Math.Ceiling(Game1.smallFont.MeasureString(countText).X);
                Item? icon = null;
                if (samples != null)
                    samples.TryGetValue(pair.Key, out icon);
                // several words: a mark in the flash color of the first word that found the item (see SEVERAL WORDS, SEVERAL COLORS)
                return (FitText(RowName(pair.Key), nameRoom, Game1.smallFont) + countText, icon, ItemLineMark(pair.Key), prefix);   // a quality row shows just the item name (its star is on the icon)
            }

            var lines = new List<(string Text, Item? Icon, Color? Mark, string Prefix)>();
            var headings = new HashSet<int>();   // indexes of the lines that are section headings
            if (sections != null && sections.Count > 1)
            {
                // per section: its heading, then its items (most numerous first); if everything does not fit, the rest is "+N more" (items only)
                var all = new List<(string Text, Item? Icon, Color? Mark, string Prefix, bool Heading)>();
                foreach (var (sectionTitle, sectionItems) in sections)
                {
                    all.Add((sectionTitle, null, null, "", true));
                    foreach (var pair in sectionItems.OrderBy(p => PlaceItemOrderOf(p.Key).Term).ThenBy(p => PlaceItemOrderOf(p.Key).Position)
                                 .ThenBy(p => IsNestedRow(p.Key)).ThenBy(p => QualityRowOrder(p.Key)).ThenByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var line = MakeLine(pair);
                        all.Add((line.Text, line.Icon, line.Mark, line.Prefix, false));
                    }
                }

                int shown = all.Count <= maxLines ? all.Count : maxLines - 1;
                for (int i = 0; i < shown; i++)
                {
                    if (all[i].Heading)
                        headings.Add(lines.Count);
                    lines.Add((all[i].Text, all[i].Icon, all[i].Mark, all[i].Prefix));
                }
                if (shown < all.Count)
                    lines.Add(($"+{all.Skip(shown).Count(l => !l.Heading)} more", null, null, ""));
            }
            else if (ordered.Count <= maxLines)
            {
                lines.AddRange(ordered.Select(MakeLine));
            }
            else
            {
                lines.AddRange(ordered.Take(maxLines - 1).Select(MakeLine));
                lines.Add(($"+{ordered.Count - (maxLines - 1)} more", null, null, ""));
            }

            // the header row must fit the title AND the total; the item lines (icon slot + text) may be wider still
            int headerRowWidth = (int)Math.Ceiling(titleSize.X);
            int PrefixWidth(string prefix) => prefix.Length > 0 ? (int)Math.Ceiling(Game1.smallFont.MeasureString(prefix).X) : 0;
            int textWidth = Math.Max(headerRowWidth, (int)lines.Max(l => PrefixWidth(l.Prefix) + Game1.smallFont.MeasureString(l.Text).X) + iconSlot);
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

            placeTooltipBounds = new Rectangle(boxX, boxY, boxWidth, boxHeight);   // the edge arrow hides only if it would lie on this list
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White);

            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            Utility.drawTextWithShadow(b, header, Game1.dialogueFont, textPos, Game1.textColor, titleScale);

            int dividerY = (int)(textPos.Y + titleSize.Y) + gapTitleToDivider;
            b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, dividerHeight), Game1.textColor * 0.35f);

            int lineX = (int)textPos.X;
            int lineY = dividerY + dividerHeight + gapDividerToList;
            for (int index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (headings.Contains(index))
                {
                    // a section heading: at the left edge (no icon slot), a little lighter than the items, so the items stay the main thing
                    Utility.drawTextWithShadow(b, line.Text, Game1.smallFont, new Vector2(lineX, CenteredTextY(Game1.smallFont, lineY, lineHeight)), Game1.textColor * 0.7f);
                    lineY += lineHeight;
                    continue;
                }

                // an item inside a carried container: "Birch Dresser > " first, the item's icon and name after it
                int itemX = lineX;
                if (line.Prefix.Length > 0)
                {
                    Utility.drawTextWithShadow(b, line.Prefix, Game1.smallFont, new Vector2(lineX, CenteredTextY(Game1.smallFont, lineY, lineHeight)), Game1.textColor);
                    itemX += PrefixWidth(line.Prefix);
                }

                if (line.Icon != null)
                {
                    int iconY = lineY + (lineHeight - iconSize) / 2;
                    DrawItemIcon(b, line.Icon, itemX, iconY, iconSize);

                    // one row per quality: the star in the icon's lower left corner, like the game's inventory (only these samples keep a star)
                    if (SearchMenu.HasStarSprite(line.Icon.Quality))
                        SearchMenu.DrawQualityStar(b, new Vector2(itemX - 2, iconY + iconSize - 8 * IconStarScale + 2), line.Icon.Quality, IconStarScale);
                }

                var textAt = new Vector2(itemX + iconSlot, CenteredTextY(Game1.smallFont, lineY, lineHeight));
                if (line.Mark is Color mark)
                {
                    // the highlighter mark behind the item line, in the flash color of its word (the text keeps its normal color)
                    Vector2 textSize = Game1.smallFont.MeasureString(line.Text);
                    b.Draw(Game1.staminaRect, new Rectangle((int)textAt.X - 4, (int)(textAt.Y + textSize.Y * 0.1f), (int)Math.Ceiling(textSize.X) + 8, (int)(textSize.Y * 0.8f)), mark);
                }
                Utility.drawTextWithShadow(b, line.Text, Game1.smallFont, textAt, Game1.textColor);
                lineY += lineHeight;
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
        private const string KeepBoxNothingTip = "Nothing left to keep.";   // unchecked, and the highlights are already gone (expired, or the found chest was opened)

        /// <summary>Unchecked and nothing is highlighted any more: checking the box would keep nothing, so it does not react (see KeepBoxNothingTip).</summary>
        private bool KeepBoxHasNothingToKeep => !keepBoxDismissed && !IsKeepHighlightsLocked && !AnyHighlight;

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
                keepSavedBackpack = highlightBackpack;
                keepFadeUntilMs = clock.ElapsedMilliseconds + KeepBoxFadeMs;
            }
            else
            {
                keepSavedChests = null;
                keepSavedBackpack = false;
                keepFadeUntilMs = 0;
            }

            ClearHighlights();
        }

        // Ends a running fade on purpose: the box is GONE and can no longer be revived.
        private void EndKeepFade()
        {
            keepFadeUntilMs = 0;
            keepSavedChests = null;
            keepSavedBackpack = false;
        }

        // Brings a fading box back: locked again, and the chests that were highlighted at the moment of unchecking light up again.
        private void ReviveKeepLock()
        {
            highlightedChests = keepSavedChests != null
                ? new List<(GameLocation location, Vector2 position, Chest chest)>(keepSavedChests)
                : new List<(GameLocation location, Vector2 position, Chest chest)>();
            highlightBackpack = keepSavedBackpack;
            keepSavedBackpack = false;
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
        /// <summary>Where the game's clock / gold block starts on the left (minus a small gap): the real position when the game has it, never further right than the reserved width.</summary>
        private static int GetHudLeft()
        {
            int hudLeft = Game1.uiViewport.Width - KeepBoxHudReserve;
            if (Game1.dayTimeMoneyBox != null)
                hudLeft = Math.Min(hudLeft, Game1.dayTimeMoneyBox.xPositionOnScreen);
            return hudLeft - KeepBoxHudGap;
        }

        // avoid: the red X of the panel (outside its top right corner). A box on the panel's right side that would touch it moves right of the X.
        private void LayoutKeepBox(int panelX, int panelY, int panelWidth, int panelHeight, bool panelAtBottom, Rectangle avoid)
        {
            int labelWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(KeepBoxLabel).X);
            int width = KeepBoxPadX * 2 + CheckboxSize + KeepBoxLabelGap + labelWidth;
            int height = Math.Max(CheckboxSize, Game1.smallFont.LineSpacing) + KeepBoxPadY * 2;

            int hudLeft = GetHudLeft();

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

            // the red X sits right of the panel's top right corner: a box beside the panel that would touch it goes right of the X instead
            if (avoid.Width > 0)
            {
                Rectangle avoidArea = avoid;
                avoidArea.Inflate(KeepBoxPanelGap, KeepBoxPanelGap);
                if (new Rectangle(rightX, sideY, width, height).Intersects(avoidArea))
                    rightX = avoid.Right + KeepBoxPanelGap;
            }

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
            if (keepBoxBounds.Contains(mouse) && !KeepBoxHasNothingToKeep)   // the whole box is clickable: hovering any part of it lights the checkbox (not when there is nothing to keep)
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
                IClickableMenu.drawHoverText(b, KeepBoxHasNothingToKeep ? KeepBoxNothingTip : KeepBoxTip, Game1.smallFont, xOffset: keepBoxBounds.X - (Game1.getOldMouseX() + 32));
        }

        // A left click anywhere on the box (the checkbox or the words "Keep highlights") flips it, and the click is swallowed, so it does not also
        // swing a tool or interact with whatever lies under the box. With a controller, the A button does the same while the cursor is on the box
        // (and is swallowed too, so it does not also open a chest or talk to someone behind the box).
        // The mouse is read in UI coordinates (getMousePosition(true)): the box is drawn by the HUD, which uses the UI scale. The plain
        // getMousePosition() uses the world zoom in this event, so whenever Zoom Level and UI Scale differed the click missed the box (fixed in 1.6.1).
        private void OnKeepBoxClicked(object? sender, ButtonPressedEventArgs e)
        {
            if (IsHudClickTaken(e.Button))
                return;   // the "Copy" bubble used this click
            if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA)
                return;
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;
            long now = clock.ElapsedMilliseconds;
            if (now - keepBoxDrawnMs > KeepBoxFreshMs)
                return;   // the box is not on screen right now
            if (keepBoxDismissed && !IsKeepBoxFading(now))
                return;   // GONE: nothing to click
            if (!keepBoxBounds.Contains(Game1.getMousePosition(true)))
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
                if (!AnyHighlight)
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
        private void DrawSearchSparkles(SpriteBatch b, Rectangle panel, bool panelAtBottom, float panelAlpha, Rectangle? keepBox, Rectangle closeButton)
        {
            Rectangle closeArea = closeButton;
            closeArea.Inflate(6, 6);   // a star never touches the red X either

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
                    || box.Intersects(panel) || box.Intersects(clockBlock) || box.Intersects(toolbarStrip) || box.Intersects(closeArea)
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
            hudBlockRects.Clear();   // refilled below while the panel / the keep box are on screen (OnRenderedHudChestTip reads it)
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

            // the "Did you mean" row may use more room than the title (it is the same row as under the search box, whose dialog is wider): the panel
            // grows to fit it, never past the screen, so three long names fit on one line
            int suggestionMaxWidth = Math.Max(maxTextWidth, Math.Min(ResultSuggestionRowMaxWidth, screenWidth - 160 - padX * 2));

            // the quality icon at the right end of the title row: the title gets a little less room, so it never runs under the icon
            int iconReserve = QualityBoxSize + QualityIconGap - (padX - QualityIconRightInset);

            // a failed search: the warning badge sits at the left end of the title row, so the title starts a little further right
            bool showFailIcon = resultFailed;
            bool showBackpackIcon = resultBackpackIcon && !resultFailed;   // found only in the backpack: a backpack instead of the warning sign
            int leftReserve = showFailIcon || showBackpackIcon ? WarningBadgeSize + WarningBadgeGap - (padX - WarningBadgeLeftInset) : 0;

            string title = WrapTitle((int)((maxTextWidth - iconReserve - leftReserve) / titleScale));
            string body = notifyBody.Length > 0 ? Game1.parseText(notifyBody, Game1.smallFont, maxTextWidth) : "";

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            Vector2 bodySize = body.Length > 0 ? Game1.smallFont.MeasureString(body) : Vector2.Zero;

            // a failed search with spelling suggestions: one more row under the title, "Did you mean: Starfruit, Strawberry"
            List<(string Label, string Query, int Width, string? Tip)> suggestionChips = LayoutSuggestionChips(suggestionMaxWidth, out int suggestionRowWidth);

            // the last line, when the search left words out (see AT MOST THREE AT A TIME): grey, part of the panel, so it never covers anything
            string limitNote = lastIgnoredTerms.Count > 0
                ? Game1.parseText($"Only {MaxSearchTerms} at a time: {string.Join(", ", lastIgnoredTerms.Select(t => "\"" + t + "\""))} "
                    + (lastIgnoredTerms.Count == 1 ? "was" : "were") + " not searched.", Game1.smallFont, maxTextWidth)
                : "";
            // the items of the last death Marlon can sell back (see LOST WHEN THE PLAYER DIED): one more grey line, in the same spot
            if (lostOnDeathNote.Length > 0)
            {
                string deathNote = Game1.parseText(lostOnDeathNote, Game1.smallFont, maxTextWidth);
                limitNote = limitNote.Length > 0 ? limitNote + "\n" + deathNote : deathNote;
            }
            Vector2 limitNoteSize = limitNote.Length > 0 ? Game1.smallFont.MeasureString(limitNote) : Vector2.Zero;

            int textWidth = (int)Math.Max(Math.Max(Math.Max(titleSize.X + iconReserve + leftReserve, bodySize.X), suggestionRowWidth), limitNoteSize.X);
            int boxWidth = textWidth + padX * 2;
            int boxHeight = (int)titleSize.Y + padY * 2;
            if (body.Length > 0)
                boxHeight += gap * 2 + 2 + (int)bodySize.Y;
            int suggestionBlockHeight = suggestionChips.Count > 0 ? gap * 2 + 2 + Game1.smallFont.LineSpacing : 0;   // divider + row; a found search has this above its place list
            boxHeight += suggestionBlockHeight;
            int limitNoteHeight = limitNote.Length > 0 ? gap + (int)limitNoteSize.Y : 0;           // the "Only 3 at a time" line, the very last one
            boxHeight += limitNoteHeight;

            int boxX = (screenWidth - boxWidth) / 2;

            // The game moves its toolbar between the bottom and the top of the screen depending on where the player stands. The panel normally
            // sits at the top; while the toolbar is at the top it moves to the bottom instead, so it never covers the toolbar. The "Keep highlights"
            // box and the item list of a hovered place are laid out relative to the panel, so they follow it.
            bool panelAtBottom = IsToolbarAtTop();
            int boxY = panelAtBottom ? Math.Max(20, Game1.uiViewport.Height - boxHeight - 20) : 20;

            // the red X just outside the panel's top right corner. It must stay clear of the clock / gold block (panel at the top) and on screen:
            // if it does not fit, the whole panel moves left by just as much
            int closeLimit = panelAtBottom ? screenWidth - KeepBoxScreenMargin : GetHudLeft();
            int closeOverflow = boxX + boxWidth + ResultCloseOutsideGap + ResultCloseSize - closeLimit;
            if (closeOverflow > 0)
                boxX = Math.Max(KeepBoxScreenMargin, boxX - closeOverflow);
            var closeRect = new Rectangle(boxX + boxWidth + ResultCloseOutsideGap, boxY + ResultCloseTopInset, ResultCloseSize, ResultCloseSize);

            // the "Keep highlights" box beside the panel, only when something was found (placed here because hovering it pauses the countdown too).
            // It lives as long as the panel, or, while locked, until the lock is released; a menu hides it like the rest of the HUD.
            bool showKeepBox = body.Length > 0
                && (!keepBoxDismissed || keepBoxFading)
                && (panelVisible || IsKeepHighlightsLocked || keepBoxFading)
                && IsKeepBoxHudVisible();
            if (showKeepBox)
                LayoutKeepBox(boxX, boxY, boxWidth, boxHeight, panelAtBottom, panelVisible || IsKeepHighlightsLocked ? closeRect : Rectangle.Empty);   // locked: the X of a peeked panel comes back, the box stays clear of it

            // what the hover tip of a highlighted chest must keep out of: the panel, its red X and the "Keep highlights" box
            if (panelVisible)
            {
                hudBlockRects.Add(new Rectangle(boxX, boxY, boxWidth, boxHeight));
                hudBlockRects.Add(closeRect);
            }
            if (showKeepBox)
                hudBlockRects.Add(keepBoxBounds);

            // hover pause: while the mouse is over the panel the countdown does not run (the real clock keeps going,
            // so the end time is pushed back by the same amount) and the panel stays fully visible
            // (the same goes for the mouse being over the "Keep highlights" box)
            Point mouse = Game1.getMousePosition();
            bool overClose = panelVisible && closeRect.Contains(mouse);
            bool overPanel = panelVisible && (new Rectangle(boxX, boxY, boxWidth, boxHeight).Contains(mouse) || overClose);   // the X belongs to the panel
            bool overKeepBox = showKeepBox && !keepBoxDismissed && keepBoxBounds.Contains(mouse);

            // PEEK: the panel has faded but "Keep highlights" is checked: the moment the mouse is on the box (this very frame, no delay) the panel is
            // back, as it was (title, star, places), fully opaque. When the mouse leaves the box it stays PeekGraceMs longer, so the mouse can move
            // onto it: while the mouse is on the peeked panel (or back on the box) it stays, and its place words show their item lists like on the real
            // panel. PeekGraceMs after the mouse is on neither, it is gone (no fade). Its red X hides it at once (the highlights and the checkbox stay).
            // Nothing else is touched: no countdown (the panel's end time and the highlights' end time stay as they are), not the checkbox. Apart from
            // that it works exactly like the real panel: the hover tips of its words and its star, and the right-click "Copy" bubble.
            var panelRect = new Rectangle(boxX, boxY, boxWidth, boxHeight);
            bool peeking = false;
            if (!panelVisible && IsKeepHighlightsLocked && showKeepBox)
            {
                bool overPeeked = peekActive && (panelRect.Contains(mouse) || closeRect.Contains(mouse) || hudCopyBubble.IsOpen);   // an open "Copy" bubble holds it too
                if (overKeepBox || overPeeked)
                {
                    peekActive = true;
                    peekUntilMs = now + PeekGraceMs;
                }
                else if (peekActive && now >= peekUntilMs)
                {
                    peekActive = false;
                }
                peeking = peekActive;
            }
            else
            {
                peekActive = false;
            }

            bool panelShown = panelVisible || peeking;
            bool overPeekPanel = peeking && panelRect.Contains(mouse);
            bool overPeekClose = peeking && closeRect.Contains(mouse);
            if (peeking)
            {
                alpha = 1f;
                hudBlockRects.Add(panelRect);   // the hover tip of a chest keeps out of the peeked panel too
                hudBlockRects.Add(closeRect);
            }

            // a FADING box: hovering it (clicking is not needed) freezes its own countdown and shows it fully opaque again, like the result
            // panel; moving the mouse away lets the remaining countdown and the fade carry on
            bool overFadingKeepBox = keepBoxFading && showKeepBox && keepBoxBounds.Contains(mouse);
            if (overFadingKeepBox)
                keepFadeUntilMs += frameDelta;
            if (hudCopyBubble.IsOpen && !panelVisible && !peeking)
                hudCopyBubble.Close();   // the panel is gone (closed with its X): so is the bubble of its words
            if (panelVisible && (overPanel || overKeepBox || hudCopyBubble.IsOpen))   // a panel that is already gone is never revived by hovering the locked box; an open "Copy" bubble holds it like the mouse does
            {
                notifyUntilMs = Math.Max(notifyUntilMs + frameDelta, now + HoverMinRemainingMs);
                alpha = 1f;

                // the chest highlights stand still with the panel: their end time is pushed back by the same frame time, so their remaining time
                // is exactly what it was when the mouse arrived, and it runs on from there when the mouse leaves. (Locked highlights never
                // expire, so there is nothing to freeze then; "never expire" (-1) stays as it is.)
                highlightHoverFrameMs = now;
                if (!IsKeepHighlightsLocked && AnyHighlight && highlightUntilMs >= 0)
                    highlightUntilMs += frameDelta;
            }

            (string Title, Dictionary<string, int> Items, Dictionary<string, Item>? Samples, int AnchorX, List<(string Title, Dictionary<string, int> Items)>? Sections)? pendingTooltip = null;   // drawn after the keep box, see below
            Rectangle qualityIconBounds = Rectangle.Empty;   // where the quality icon is on screen (for its hover tip)
            string? pendingTitleTip = null;                  // hover tip of the title word under the mouse (drawn last, see below)

            if (panelShown)
            {
                // the game's standard menu box
                IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), boxX, boxY, boxWidth, boxHeight, Color.White * alpha);

                Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
                // the whole title in the normal text color (its "(Invalid: ...)" part, if it has one, in dark red), then the emphasized parts (the quoted
                // item names of "Found ... containing 'name'", or the searched words of "No item 'name' found") drawn bold on top of it in the very same color
                Vector2 titlePos = new Vector2(textPos.X + leftReserve, textPos.Y);
                DrawTitleMarks(b, title, titlePos, titleScale, alpha);   // several words: the highlighter marks behind the words
                DrawTitleText(b, title, titlePos, titleScale, Game1.textColor * alpha, InvalidTitleColor * alpha);
                DrawTitleEmphasis(b, title, titlePos, titleScale, Game1.textColor * alpha, InvalidTitleColor * alpha);

                // hovering a quoted word of the title: only the hover tip (drawn last, see below). No underline: the words are display only, not links.
                // The word under the mouse is also kept for a right click ("Copy" bubble, see OnHudCopyInput).
                hoveredTitleTerm = null;
                // a peeked panel (see PEEK) works exactly like the real one: hover tips and the right-click "Copy" bubble too
                if ((overPanel || overPeekPanel) && !overKeepBox && !overClose && !overPeekClose && TryGetHoveredTitleWord(title, titlePos, titleScale, mouse, out int hoveredWord, out _))
                {
                    if (!hudCopyBubble.IsOpen)
                        pendingTitleTip = notifyEmphasisTips[hoveredWord];
                    if (hoveredWord < notifyEmphasisTerms.Count)
                    {
                        hoveredTitleTerm = StripCategorySuffix(notifyEmphasisTerms[hoveredWord]);
                        hoveredTitleTermMs = now;
                    }
                }

                // the red X outside the panel's top right corner (closes the panel, see OnResultCloseClicked); on a peeked panel it hides the peek
                if (panelShown)
                {
                    resultCloseBounds = closeRect;
                    DrawResultCloseButton(b, resultCloseBounds, overClose || overPeekClose, alpha);
                    resultCloseDrawnMs = now;
                    resultCloseIsPeek = peeking;
                }

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

                // found only in the backpack ("Found your backpack containing ..."): a small backpack in the same slot, display only
                if (showBackpackIcon)
                {
                    float firstLineHeight = Game1.dialogueFont.LineSpacing * titleScale;
                    DrawBackpackBadge(
                        b,
                        boxX + WarningBadgeLeftInset,
                        Math.Max(boxY + 22, (int)Math.Round(textPos.Y + (firstLineHeight - WarningBadgeSize) / 2f)),
                        alpha);
                }

                // the "Only 3 at a time" line at the very bottom (grey: a note, not an error)
                if (limitNote.Length > 0)
                {
                    var notePos = new Vector2(boxX + padX, boxY + boxHeight - padY - limitNoteSize.Y + 4);
                    Utility.drawTextWithShadow(b, limitNote, Game1.smallFont, notePos, Game1.textColor * 0.6f * alpha);
                }

                // the "Did you mean" row: a divider like the one above the place list, then the label and the clickable words (underlined, blue; brighter
                // while the mouse is over one). Where each word is on screen is kept for the click handler (OnSuggestionClicked).
                suggestionHitBoxes.Clear();
                if (suggestionChips.Count > 0)
                {
                    int suggestDividerY = (int)(textPos.Y + titleSize.Y) + gap;
                    b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, suggestDividerY, textWidth, 2), Game1.textColor * 0.35f * alpha);

                    SpriteFont rowFont = Game1.smallFont;
                    int rowHeight = rowFont.LineSpacing;
                    float rowY = suggestDividerY + 2 + gap;
                    float rowX = textPos.X;

                    Utility.drawTextWithShadow(b, resultHintLead, rowFont, new Vector2(rowX, rowY), Game1.textColor * alpha);
                    rowX += rowFont.MeasureString(resultHintLead).X;
                    float commaWidth = rowFont.MeasureString(", ").X;

                    for (int c = 0; c < suggestionChips.Count; c++)
                    {
                        var chip = suggestionChips[c];
                        var wordBounds = new Rectangle((int)rowX, (int)rowY, chip.Width, rowHeight);
                        Rectangle hitBounds = wordBounds;
                        hitBounds.Inflate(4, 2);
                        bool hovered = overPanel && hitBounds.Contains(mouse);

                        Color wordColor = hovered ? SuggestionHoverColor : SuggestionColor;
                        Utility.drawTextWithShadow(b, chip.Label, rowFont, new Vector2(rowX, rowY), wordColor * alpha);
                        b.Draw(Game1.staminaRect, new Rectangle(wordBounds.X, wordBounds.Bottom - 4, wordBounds.Width, hovered ? 3 : 2), wordColor * (hovered ? 1f : 0.7f) * alpha);

                        suggestionHitBoxes.Add((hitBounds, chip.Query));
                        if (hovered && chip.Tip != null)
                            pendingTitleTip = chip.Tip;   // same tip rules as everywhere (drawn last, see below)
                        rowX += chip.Width;

                        if (c < suggestionChips.Count - 1)
                        {
                            Utility.drawTextWithShadow(b, ", ", rowFont, new Vector2(rowX, rowY), Game1.textColor * alpha);
                            rowX += commaWidth;
                        }
                    }

                    suggestionDrawnMs = now;
                }

                if (body.Length > 0)
                {
                    int dividerY = (int)(textPos.Y + titleSize.Y) + gap + suggestionBlockHeight;
                    b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, 2), Game1.textColor * 0.35f * alpha);

                    Vector2 bodyPos = new Vector2(textPos.X, dividerY + 2 + gap);
                    Utility.drawTextWithShadow(b, body, Game1.smallFont, bodyPos, Game1.textColor * alpha);

                    // hovering a place word: underline it and show what was found there
                    if ((overPanel || overPeekPanel) && TryGetHoveredPlace(body, bodyPos, mouse, out PlaceEntry hovered, out List<Rectangle> pieces))
                    {
                        Dictionary<string, int>? items = GetPlaceItems(hovered.Key);
                        if (items != null && pieces.Count > 0)
                        {
                            foreach (Rectangle piece in pieces)
                                b.Draw(Game1.staminaRect, new Rectangle(piece.X, piece.Bottom - 4, piece.Width, 2), Game1.textColor * 0.8f);

                            pendingTooltip = (hovered.Title, items, GetPlaceSamples(hovered.Key), pieces[0].X, GetPlaceSections(hovered.Key));
                        }
                    }
                }
            }

            // the gold stars of a successful search, beside the panel (under the keep box and the item list, which are drawn after them)
            if (panelVisible)
                DrawSearchSparkles(b, new Rectangle(boxX, boxY, boxWidth, boxHeight), panelAtBottom, alpha, showKeepBox ? keepBoxBounds : (Rectangle?)null, closeRect);

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
                DrawPlaceTooltip(b, tip.Title, tip.Items, tip.Samples, tip.AnchorX, panelAtBottom ? boxY - 8 : boxY + boxHeight + 8, keepAvoid, panelAtBottom, tip.Sections);
                placeTooltipShownTick = Game1.ticks;   // the edge arrow stays off this list while it is up (see DrawEdgeArrow)
            }

            // hovering the quality icon: the game's own hover tip names the filter (drawn last, so it lies on top of everything else).
            // The mouse over the icon is also over the panel, so the countdown is paused like anywhere else on the panel.
            if (panelShown && qualityIconBounds.Contains(mouse))
                IClickableMenu.drawHoverText(b, resultHasNoQualityItemsOnly ? NoQualityItemTip : GetQualityTip(resultQuality), Game1.smallFont);
            else if (pendingTitleTip != null && !qualityIconBounds.Contains(mouse))
                DrawTitleWordTip(b, pendingTitleTip, showKeepBox ? keepBoxBounds : (Rectangle?)null);   // follows the mouse, never covers the "Keep highlights" box

            // the "Copy" bubble of a title word and its "Copied!" note, on top of everything
            if (hudCopyBubble.IsOpen || hudCopyBubble.NoteVisible)
            {
                hudCopyBubble.Draw(b, mouse.X, mouse.Y);
                hudCopyBubbleDrawnMs = now;
            }
        }

        // ===================== RIGHT CLICK ON A TITLE WORD OF THE RESULT PANEL: "COPY" BUBBLE =====================
        // Right-clicking a quoted word of the panel's title ('apple', 'fish' ...) opens the same small "Copy" bubble as in the search box (CopyBubble):
        // nothing is copied until "Copy" is left-clicked (then: clipboard, a soft click, "Copied!"). A left click anywhere else closes it and does
        // nothing else (it does not swing a tool, click a word or the X). While it is open the panel does not fade. The right click itself is
        // swallowed, so it does not also open a chest or use an item.

        private readonly CopyBubble hudCopyBubble = new();
        private string? hoveredTitleTerm;           // the title word under the mouse in the last HUD frame (null = none)
        private long hoveredTitleTermMs = -1000;
        private long hudCopyBubbleDrawnMs = -1000;
        private const long HudCopyFreshMs = 200;   // the word / the bubble count as "on screen" if they were drawn this recently (real clock)

        // set when this handler used a mouse click, so the other click handlers of the same press leave it alone
        private int hudClickTakenTick = -1;
        private SButton hudClickTakenButton;

        /// <summary>True if the "Copy" bubble already used this very click (the other HUD click handlers then ignore it).</summary>
        private bool IsHudClickTaken(SButton button) => hudClickTakenTick == Game1.ticks && hudClickTakenButton == button;

        private void TakeHudClick(SButton button)
        {
            Helper.Input.Suppress(button);
            hudClickTakenTick = Game1.ticks;
            hudClickTakenButton = button;
        }

        private void OnHudCopyInput(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.MouseLeft && e.Button != SButton.MouseRight)
                return;

            long now = clock.ElapsedMilliseconds;
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
            {
                hudCopyBubble.Close();
                return;
            }

            Point mouse = Game1.getMousePosition(true);

            if (hudCopyBubble.IsOpen)
            {
                if (now - hudCopyBubbleDrawnMs > HudCopyFreshMs)
                {
                    hudCopyBubble.Close();   // not on screen any more (a menu covered the HUD): just forget it
                }
                else
                {
                    // a left click: "Copy" copies, anywhere else closes; a right click closes it (and may open a new one on another word, below).
                    // A left click on the result panel, its red X or the "Keep highlights" box (hudBlockRects) closes the bubble AND still does its
                    // own job in the same click (the X closes, the box is checked ...): no second click needed. Only a click out in the world is
                    // used up, so closing the bubble never swings a tool.
                    if (e.Button == SButton.MouseLeft)
                    {
                        if (!hudCopyBubble.IsOverButton(mouse.X, mouse.Y) && hudBlockRects.Any(r => r.Contains(mouse)))
                        {
                            hudCopyBubble.Close();
                            return;   // not taken: the handlers below (OnKeepBoxClicked, OnResultCloseClicked ...) get this click
                        }
                        hudCopyBubble.HandleLeftClick(mouse.X, mouse.Y);
                        TakeHudClick(e.Button);
                        return;
                    }
                    hudCopyBubble.Close();
                }
            }

            if (e.Button == SButton.MouseRight && (now < notifyUntilMs || peekActive) && hoveredTitleTerm != null && now - hoveredTitleTermMs <= HudCopyFreshMs)
            {
                hudCopyBubble.Open(hoveredTitleTerm, mouse.X, mouse.Y);
                Game1.playSound("smallSelect");
                TakeHudClick(e.Button);
            }
        }

        // ===================== HOVER TIP OF A HIGHLIGHTED CHEST (in the world) =====================
        // (The flashing player, the backpack, has the same tip: see the end of this method's search for a hovered container.)
        // While the mouse is over a chest that is highlighted right now, the same item list as a hovered place word pops up (DrawPlaceTooltip), but only
        // for THIS chest: "Chest total: 3", then one line per matching item with its real icon and count. It is display only (no click, no pick-up).
        // The list is worked out live from the chest with the matcher of the last search, so it is always what the chest holds at this moment; a chest
        // that no longer holds anything of it shows nothing. No tip while the highlight is not drawn (0% opacity), while a menu is open, or while the
        // mouse is over the result panel / its X / the "Keep highlights" box (those have their own tips).
        // The mouse is read twice: in WORLD pixels (getMousePosition(false) + the viewport) to find the tile, and in UI pixels to place the tip, because
        // the HUD is drawn with the UI scale and the world with the zoom, and the two scales can differ.
        private void OnRenderedHudChestTip(object? sender, RenderedHudEventArgs e)
        {
            try
            {
                Func<Item, bool>? matcher = highlightMatcher;
                if ((highlightedChests.Count == 0 && !highlightBackpack) || matcher == null)
                    return;
                // chests: the highlight is not drawn at 0%, so there is nothing to hover. The player (the backpack) is different: its tip stays as long
                // as the backpack is highlighted, also when its flash is switched off in the config.
                bool chestTipsOn = highlightedChests.Count > 0 && Config.HighlightOpacityPercent > 0;
                if (!chestTipsOn && !highlightBackpack)
                    return;
                if (!Context.IsWorldReady || Game1.activeClickableMenu != null || Game1.eventUp || Game1.currentLocation == null)
                    return;

                Point uiMouse = Game1.getMousePosition(true);
                foreach (Rectangle blocked in hudBlockRects)
                {
                    if (blocked.Contains(uiMouse))
                        return;
                }

                Point worldMouse = Game1.getMousePosition(false);
                int tileX = (int)Math.Floor((worldMouse.X + Game1.viewport.X) / 64f);
                int tileY = (int)Math.Floor((worldMouse.Y + Game1.viewport.Y) / 64f);

                Chest? hoveredChest = null;
                Vector2 hoveredPosition = Vector2.Zero;
                var mouseTile = new Point(tileX, tileY);
                foreach (var (location, position, chest) in highlightedChests)
                {
                    if (!chestTipsOn)
                        break;

                    // any tile of the container counts: both halves of a fridge, every tile of a building or a dresser
                    if (location == Game1.currentLocation && FootprintOf(chest, position).Contains(mouseTile))
                    {
                        hoveredChest = chest;
                        hoveredPosition = position;
                        break;
                    }
                }

                if (hoveredChest == null)
                {
                    // the flashing player (the backpack): the same list as for a chest, from the backpack's items right now
                    Farmer? farmer = Game1.player;
                    if (!highlightBackpack || farmer == null)
                        return;
                    var playerBox = new Rectangle((int)farmer.Position.X - 8, (int)farmer.Position.Y - 104, 80, 160);   // the same box that flashes
                    if (!playerBox.Contains(worldMouse.X + Game1.viewport.X, worldMouse.Y + Game1.viewport.Y))
                        return;

                    var backpackTotals = new Dictionary<string, int>();
                    var backpackSamples = new Dictionary<string, Item>();
                    foreach (var (item, inside, _) in WithNested(GetBackpackItems()))
                    {
                        if (!matcher(item))
                            continue;

                        string name = RowKey(item, inside);
                        backpackTotals[name] = backpackTotals.GetValueOrDefault(name) + item.Stack;
                        if (!chestTipSamples.TryGetValue(name, out Item? sample))
                            chestTipSamples[name] = sample = MakeIconSample(item, singleItemQualityRows);
                        backpackSamples[name] = sample;
                    }

                    if (backpackTotals.Count == 0)
                        return;   // the backpack holds nothing of the search any more: no tip

                    bool aboveBackpack = uiMouse.Y > Game1.uiViewport.Height * 0.6f;
                    DrawPlaceTooltip(e.SpriteBatch, BackpackPlaceKey, backpackTotals, backpackSamples, uiMouse.X + 32 + 28, aboveBackpack ? uiMouse.Y - 16 : uiMouse.Y + 32, null, aboveBackpack);
                    return;
                }

                // what THIS container holds of the search: item name -> total stack (all qualities added up), like the place list
                var totals = new Dictionary<string, int>();
                var samples = new Dictionary<string, Item>();
                foreach (var (item, inside, _) in WithNested(CurrentItems(hoveredChest)))
                {
                    if (!matcher(item))
                        continue;

                    string name = RowKey(item, inside);   // the item name, or name + quality (see ONE ROW PER QUALITY); inside a carried container marked so
                    totals[name] = totals.GetValueOrDefault(name) + item.Stack;
                    if (!chestTipSamples.TryGetValue(name, out Item? sample))
                        chestTipSamples[name] = sample = MakeIconSample(item, singleItemQualityRows);
                    samples[name] = sample;
                }

                if (totals.Count == 0)
                    return;

                // the tip's title: the container's own full name ("Chest", "Big Chest", "Fridge", "Mini-Fridge", "Auto-Grabber", "Mill", "Retro Dresser" ...)
                string title = InfoOf(hoveredChest, hoveredPosition).FullName;

                // right of and below the cursor (the game's own spot); in the lower part of the screen it opens above the cursor instead
                bool above = uiMouse.Y > Game1.uiViewport.Height * 0.6f;
                DrawPlaceTooltip(e.SpriteBatch, title, totals, samples, uiMouse.X + 32 + 28, above ? uiMouse.Y - 16 : uiMouse.Y + 32, null, above);
            }
            catch (Exception ex)
            {
                Monitor.LogOnce($"Could not draw the chest hover tip: {ex.Message}", LogLevel.Trace);   // no tip rather than a crash
            }
        }

        // The flash speed of the highlights (radians per second of the sine wave): normal, and faster during the last FastFlashLastMs (5 seconds)
        // before they expire on their own, so it is easy to see that they are about to disappear.
        private const double NormalFlashSpeed = 4;
        private const double FastFlashSpeed = 15;
        private const long FastFlashLastMs = 5000;
        private double? fastFlashPhaseOffset;   // keeps the wave continuous when the speed changes (no jump of the brightness); null = normal speed

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (!AnyHighlight) return;
            if (Config.HighlightOpacityPercent <= 0) return;   // 0% = do not draw the highlight at all

            // the flash pulses between 40% and 100% of the chosen opacity
            float peak = Config.HighlightOpacityPercent / 100f;
            // the last 5 seconds before the highlights expire (real clock, see highlightUntilMs): the flash gets much faster. Locked highlights
            // ("Keep highlights") and highlights without a timeout never expire, so they always flash at the normal speed.
            double seconds = Game1.currentGameTime.TotalGameTime.TotalSeconds;
            bool expiringSoon = !IsKeepHighlightsLocked && highlightUntilMs >= 0 && highlightUntilMs - clock.ElapsedMilliseconds <= FastFlashLastMs;
            double phase;
            if (expiringSoon)
            {
                fastFlashPhaseOffset ??= seconds * (NormalFlashSpeed - FastFlashSpeed);   // continues exactly where the slow wave was
                phase = seconds * FastFlashSpeed + fastFlashPhaseOffset.Value;
            }
            else
            {
                fastFlashPhaseOffset = null;
                phase = seconds * NormalFlashSpeed;
            }
            float pulse = (float)(Math.Sin(phase) + 1) / 2f;
            float alpha = peak * (0.4f + pulse * 0.6f);
            Color baseColor = WordFlashColor(0);   // the main color
            bool perWord = highlightWordCount > 0;

            foreach (var (location, position, chest) in highlightedChests)
            {
                if (location != Game1.currentLocation) continue;

                // the flash covers the container's whole footprint: one tile for a chest, both halves of a fridge, every tile of a building / dresser
                Rectangle area = FootprintPixels(chest, position);
                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(area.X, area.Y));
                var flash = new Rectangle((int)screenPos.X - 4, (int)screenPos.Y - 4, area.Width + 8, area.Height + 8);

                // several words: one upright strip per word the chest holds, left to right in the order typed (see SEVERAL WORDS, SEVERAL COLORS)
                if (perWord && chestWordSlots.TryGetValue(chest, out int[]? slots) && slots.Length > 0)
                {
                    for (int k = 0; k < slots.Length; k++)
                    {
                        int left = flash.X + flash.Width * k / slots.Length;
                        int right = flash.X + flash.Width * (k + 1) / slots.Length;
                        e.SpriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(left, flash.Y, right - left, flash.Height), WordFlashColor(slots[k]) * alpha);
                    }
                    continue;
                }

                e.SpriteBatch.Draw(Game1.fadeToBlackRect, flash, baseColor * alpha);
            }

            // the backpack: the player flashes like a chest (head to feet), in strips when several words are searched
            if (highlightBackpack && Config.FlashPlayer && Game1.player != null)
            {
                Vector2 playerTopLeft = Game1.GlobalToLocal(Game1.viewport, Game1.player.Position + new Vector2(-8f, -104f));
                var playerFlash = new Rectangle((int)playerTopLeft.X, (int)playerTopLeft.Y, 80, 160);

                if (perWord && backpackWordSlots.Length > 0)
                {
                    for (int k = 0; k < backpackWordSlots.Length; k++)
                    {
                        int left = playerFlash.X + playerFlash.Width * k / backpackWordSlots.Length;
                        int right = playerFlash.X + playerFlash.Width * (k + 1) / backpackWordSlots.Length;
                        e.SpriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(left, playerFlash.Y, right - left, playerFlash.Height), WordFlashColor(backpackWordSlots[k]) * alpha);
                    }
                }
                else
                {
                    e.SpriteBatch.Draw(Game1.fadeToBlackRect, playerFlash, baseColor * alpha);
                }
            }
        }
    }
}