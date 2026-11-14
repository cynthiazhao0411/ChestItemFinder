using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ChestItemFinder
{
    /// <summary>What happened when the search menu asked for a search.</summary>
    public enum SearchOutcome
    {
        /// <summary>The search ran (found or not found); the menu should close.</summary>
        Done,

        /// <summary>The item exists, but not in the chosen star quality; the menu must stay open.</summary>
        NoMatchingQuality
    }

    /// <summary>
    /// The answer of a search: what happened, which search words really found something in a chest (with the chosen star), and all the words
    /// the query was split into. The search history remembers every word that could be searched, found or not (see SearchMenu.Confirm).
    /// </summary>
    public sealed class SearchReport
    {
        public SearchOutcome Outcome { get; }

        /// <summary>The words (separate search terms) that found at least one item. Empty when nothing was found.</summary>
        public IReadOnlyList<string> FoundTerms { get; }

        /// <summary>Every word the query was split into by the search (the words that found something and the ones that found nothing). Empty when the search did not run.</summary>
        public IReadOnlyList<string> AllTerms { get; }

        public SearchReport(SearchOutcome outcome, IReadOnlyList<string>? foundTerms = null, IReadOnlyList<string>? allTerms = null)
        {
            Outcome = outcome;
            FoundTerms = foundTerms ?? Array.Empty<string>();
            AllTerms = allTerms ?? Array.Empty<string>();
        }
    }

    public class SearchMenu : IClickableMenu
    {
        /// <summary>Minimum characters required for a text search (pure-number IDs are exempt).</summary>
        private const int MinQueryLength = 3;

        /// <summary>
        /// The red hints for a query that is only spaces / tabs, for a query that is too short, for a query made only of symbols,
        /// for a query with stray round brackets, and for a query with characters that cannot belong to an item name
        /// (all five are shown in the hint row under the text box). A query with several words ("fruit, fish") is judged word by word, but a bad
        /// word never refuses the search while at least one other word can be searched: the bad words are shown as "(Invalid: ...)" in the title
        /// of the result panel instead (see ModEntry.ScanChests). Only when NO word can be searched, the query gets one of two hints instead:
        /// invalid symbols, or too short.
        /// </summary>
        private const string TooShortHintText = "Please enter at least 3 characters";
        private const string BlankHintText = "Spaces or tabs are not allowed as a search.";
        private const string SymbolsOnlyHintText = "Please don't enter only symbols";
        private const string InvalidBracketsHintText = "Invalid brackets. Use (category) for categories.";
        private const string InvalidCharactersHintText = "Invalid characters. Please use letters and numbers.";

        /// <summary>The two hints of a query with SEVERAL words ("fruit, &amp;&amp;&amp;", "fruit, fi") when none of its words can be searched. As long as at least one word can be searched, these are not shown: the bad words only appear as "(Invalid: ...)" in the result title.</summary>
        private const string MultiInvalidSymbolsHintText = "One of the search terms contains invalid symbols.";
        private const string MultiTooShortHintText = "One of the search terms is too short.";

        /// <summary>The hints of a query whose quality words cannot be used: only a quality and no item ("gold star"), or two different qualities.</summary>
        private const string QualityOnlyHintText = "Add an item to the quality, e.g. 'gold star apple'.";
        private const string QualityConflictHintText = "Please use only one quality, e.g. 'gold star apple'.";

        /// <summary>The hint for the impossible query "vegetable jelly" (jelly is made from fruit; vegetables become pickles). The search is not run.</summary>
        private const string VegetableJellyHintText = ModEntry.VegetableJellyHint;

        /// <summary>
        /// Punctuation a search may contain besides letters, digits, spaces and (in any script) letters with accents:
        /// straight and curly apostrophe ("Farmer's Lunch"), hyphen (negative category ids like "-26", and "Auto-Grabber"),
        /// underscore (the search ignores it like the hyphen), period ("L. Goat Milk") and comma (it separates several search words).
        /// The separators of several search words (, ; / \ &amp; + and their full-width forms, see ModEntry.QueryCommas) are cut out of the text
        /// before this check, so they never reach it; a query made of nothing but such separators counts as "only symbols".
        /// Everything else (* $ @ # % ^ = ~ ... and emoji) is refused. Round brackets are not in this list on purpose:
        /// the one legal use, the " (category)" label, is cut off before this check, and any other bracket is refused earlier by its own hint.
        /// To allow one more symbol, add it to this string.
        /// </summary>
        private const string AllowedPunctuation = "'\u2019-_.,";

        /// <summary>
        /// Round brackets, half-width and full-width. After the one legal " (category)" label has been cut off (ModEntry.StripCategorySuffix),
        /// none of these may be left in the query.
        /// </summary>
        private static readonly char[] BracketChars = { '(', ')', '\uFF08', '\uFF09' };

        /// <summary>The game's own cue played when a search is refused by one of the checks above (only spaces / tabs, only symbols, too short, invalid brackets, or invalid characters).</summary>
        private const string RefusedSound = "cancel";

        /// <summary>Menu height. Taller than before to make room for the (possibly two-line) hint row, plus some air below the key hint line.</summary>
        private const int MenuHeight = 330;

        /// <summary>
        /// The star filter values, in click order. -1 = any quality (no filtering); 1 = silver, 2 = gold, 4 = iridium
        /// (Stardew's Quality values); 0 = no quality (only items without a star: normal quality, or items that never have stars).
        /// </summary>
        private static readonly int[] QualityValues = { -1, 1, 2, 4, 0 };
        private static readonly string[] QualityLabels = { "Any quality", "Silver star", "Gold star", "Iridium star", "No quality" };

        /// <summary>
        /// The star filter the player picked last, remembered for the rest of this game session so the next search box opens with it
        /// (a position in <see cref="QualityValues"/>, not a quality value: 0 = "Any quality"). Static and in memory only: it is not saved
        /// to config.json, so after restarting the game the filter is back to "Any quality".
        /// </summary>
        private static int lastQualityIndex = 0;

        // quality star sprites in Game1.mouseCursors (the same ones the game draws on item icons)
        private static readonly Rectangle SilverStarSource = new(338, 400, 8, 8);
        private static readonly Rectangle GoldStarSource = new(346, 400, 8, 8);
        private static readonly Rectangle IridiumStarSource = new(346, 392, 8, 8);

        /// <summary>
        /// A small button in the game's own menu style: the same wooden menu frame as every menu and as the result panel (Game1.menuTexture, the
        /// standard 60 x 60 box), dark brown text (the game's text color) in the middle. While hovered, the inside gets a warm light glow and the
        /// text a soft shadow lift, like the game's hovered menu buttons. Used by the "Check Backpack" button of the result panel.
        /// </summary>
        internal static void DrawMenuButton(SpriteBatch b, Rectangle bounds, string label, bool hovered, float alpha = 1f)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Color.White * alpha, 1f, false);

            if (hovered)
            {
                // a warm glow inside the frame (the frame art is about 12 px thick at this size)
                b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 12, bounds.Y + 12, bounds.Width - 24, bounds.Height - 24), new Color(255, 236, 170) * 0.45f * alpha);
            }

            SpriteFont font = Game1.smallFont;
            Vector2 size = font.MeasureString(label);
            var textPos = new Vector2(
                MathF.Round(bounds.X + (bounds.Width - size.X) / 2f),
                MathF.Round(bounds.Y + (bounds.Height - size.Y) / 2f + 2f - (hovered ? 1f : 0f)));
            Utility.drawTextWithShadow(b, label, font, textPos, Game1.textColor * alpha);
        }

        /// <summary>True for the star qualities that have a star sprite (1 silver, 2 gold, 4 iridium).</summary>
        internal static bool HasStarSprite(int quality) => quality == 1 || quality == 2 || quality == 4;

        /// <summary>
        /// Draws the game's own quality star (the one on item icons) with its top left corner at <paramref name="position"/>: 8 x 8 game pixels times
        /// <paramref name="scale"/>. Nothing is drawn for a quality without a star (any / no star). Used by the "Found" note of an opened chest.
        /// </summary>
        internal static void DrawQualityStar(SpriteBatch b, Vector2 position, int quality, float scale, float alpha = 1f)
        {
            if (!HasStarSprite(quality))
                return;

            Rectangle source = quality switch
            {
                1 => SilverStarSource,
                2 => GoldStarSource,
                _ => IridiumStarSource
            };

            b.Draw(Game1.mouseCursors, position, source, Color.White * alpha, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
        }

        /// <summary>Zoom of the star inside the button: 8px sprite * 3 = 24px star in the 48px button, so the star leaves some room around it.</summary>
        private const int StarScale = 3;

        /// <summary>Line width of the outline star: one game pixel (= StarScale screen pixels), the same weight as the game's own pixel art.</summary>
        private const int OutlineThickness = StarScale;

        private static Texture2D? outlineStarTexture;

        // --- search history ---

        /// <summary>How many recent searches are remembered.</summary>
        public const int MaxHistory = 5;

        /// <summary>
        /// The fixed width of the whole search dialog (was 640). Wider on purpose so the "Did you mean: A, B, C" line under the text box has room for
        /// three long names without cutting any of them (there is no content-based resizing: the width is always this, see <see cref="DialogWidth"/>).
        /// </summary>
        private const int MenuWidth = 880;

        /// <summary>The width really used: <see cref="MenuWidth"/>, but never wider than the screen (small screens / big UI scale) and never narrower than the old 640.</summary>
        private static int DialogWidth => Math.Min(MenuWidth, Math.Max(640, Game1.uiViewport.Width - 32));

        // History block layout (offsets from the top of the menu). It is only added when there is a history,
        // so the menu looks exactly as before when the history is empty:
        //   header row:  "Recent searches" on the left, the red [Clear History] button on the right
        //   word rows:   the history words
        //   divider, then the "= Enter to search / = Esc to cancel" line
        private const int HistoryHeaderY = 204;   // was 196: moved down 8px to leave room for the tip line above the history block
        private const int HistoryHeaderHeight = 40;
        private const int HistoryRowsTop = 256;   // was 248 (same 8px; the menu and the Enter / Esc line follow automatically)
        private const int HistoryRowHeight = 40;
        private const int HistoryFooterGap = 8;

        /// <summary>Horizontal gap between two history words.</summary>
        private const int HistoryGap = 28;

        /// <summary>A very long history word is shortened with "..." for display (the full word is still what gets filled in).</summary>
        private const int HistoryMaxWordWidth = 250;

        private const string ClearHistoryLabel = "Clear History";

        // The small red x after every history word (removes just that word). It is a 5x5 pixel cross drawn with plain
        // rectangles, so it stays crisp and always has the same size, whatever the word looks like.
        private const int RemovePixel = 3;                          // screen pixels per cross pixel
        private const int RemoveMarkSize = 5 * RemovePixel;         // 15px wide and tall
        private const int HistoryRemoveGap = 8;                     // between the word and its x
        private const int HistoryRemovePad = 5;                     // extra clickable room around the x, left and right

        // The tiny pin between a history word and its x (pins the word: it stays at the front and is never pushed out). Same 3 px pixel art as the x.
        private const int PinMarkSize = 5 * RemovePixel;            // 15px wide and tall, like the x
        private const int HistoryPinGap = 6;                        // between the pin and the x

        // '#' = head of the pin, 'n' = needle, '.' = nothing (5 x 5, drawn with plain rectangles)
        private static readonly string[] PinArt =
        {
            ".###.",
            ".###.",
            "#####",
            "..n..",
            "..n.."
        };

        // --- search suggestions (auto-complete drop-down) ---

        /// <summary>
        /// How many suggestions the search asks for at most (the whole list the drop-down can scroll through).
        /// ModEntry passes this number to the suggestion index as its limit, so raising it here is all it takes to allow longer lists.
        /// </summary>
        public const int MaxSuggestions = 100;

        /// <summary>
        /// How many rows the drop-down may hold when the player has typed a WHOLE category word ("artisan goods", "fish", "furniture"): the category
        /// row, then EVERY item of that category (see SuggestionIndex, CATEGORY EXPANSION), then the other matches. Far more than fit on screen, so the
        /// existing scrollbar (drag, click, mouse wheel, arrow keys) is used to go through them. Normal typing keeps the <see cref="MaxSuggestions"/> limit.
        /// </summary>
        public const int MaxCategorySuggestions = 3000;

        /// <summary>How many suggestion rows the drop-down shows at once. A longer list gets a scrollbar; a list this short or shorter looks as before.</summary>
        public const int MaxVisibleSuggestions = 6;

        /// <summary>Minimum row height of the drop-down (it grows if the font is taller). Roomy enough that the icon and the text both sit centered with air above and below.</summary>
        private const int MinSuggestionRowHeight = 48;

        /// <summary>The real item icon in front of every drop-down row: its size, the gap behind it, and the room it takes from the row's text (category rows have no icon but keep the room, so all names line up).</summary>
        private const int SuggestionIconSize = 32;
        private const int SuggestionIconGap = 14;

        /// <summary>Space between the left edge of a drop-down row and its icon.</summary>
        private const int SuggestionRowInset = 10;
        private const int SuggestionIconSlot = SuggestionIconSize + SuggestionIconGap;

        /// <summary>Space between the frame of the drop-down box and its rows (the box art has a 20px border).</summary>
        private const int SuggestionPadX = 20;
        private const int SuggestionPadY = 20;

        /// <summary>Gap between the bottom of the text box and the top of the drop-down.</summary>
        private const int SuggestionGap = 4;

        // --- scrollbar of the drop-down (only shown when there are more suggestions than rows) ---
        // Same sprites the game's own menus use: a track drawn with the slider-track box and a 6x10 pixel knob, zoom 4.

        /// <summary>Width of the scrollbar track (6px sprite * zoom 4).</summary>
        private const int ScrollbarWidth = 24;

        /// <summary>Space between the rows and the scrollbar.</summary>
        private const int ScrollbarGap = 6;

        /// <summary>Height of the knob (10px sprite * zoom 4); smaller only if the track itself is shorter.</summary>
        private const int ScrollbarThumbHeight = 40;

        /// <summary>Extra clickable room left and right of the track.</summary>
        private const int ScrollbarHitPad = 3;

        private static readonly Rectangle ScrollbarTrackSource = new(403, 383, 6, 6);
        private static readonly Rectangle ScrollbarThumbSource = new(435, 463, 6, 10);

        // --- magnifier button (inside the search box, at its right end, left of the star button) ---

        /// <summary>Screen pixels per icon pixel (the red x of the history words uses 3 as well).</summary>
        private const int MagnifierPixel = 3;

        /// <summary>The icon is drawn on a 9 x 9 grid, so it is 27 x 27 screen pixels.</summary>
        private const int MagnifierSize = 9 * MagnifierPixel;

        /// <summary>Extra clickable / hover room around the icon on every side.</summary>
        private const int MagnifierHitPad = 3;

        /// <summary>Gap between the clickable area and the right edge of the text box (the area lies completely inside the box).</summary>
        private const int MagnifierRightInset = 8;

        /// <summary>How far the icon sinks while the mouse button is held, and how far its soft shadow is moved (screen pixels).</summary>
        private const int MagnifierPressShift = 2;
        private const int MagnifierShadowOffset = 2;

        // '#' = outline, 'g' = glass, 'w' = shine on the glass, 'h' = handle, '.' = nothing
        private static readonly string[] MagnifierArt =
        {
            ".####....",
            "#wggg#...",
            "#wggg#...",
            "#gggg#...",
            "#gggg#...",
            ".####h...",
            "......hh.",
            ".......hh",
            "........h"
        };

        // --- clear button (inside the search box, directly left of the magnifier): a small red x that empties the text ---

        /// <summary>Screen pixels per cross pixel (the same 3 as the magnifier and the red x of the history words).</summary>
        private const int ClearTextPixel = 3;

        /// <summary>The cross is drawn on a 7 x 7 grid, so it is 21 x 21 screen pixels: a little smaller than the magnifier (27 x 27).</summary>
        private const int ClearTextGrid = 7;
        private const int ClearTextSize = ClearTextGrid * ClearTextPixel;

        /// <summary>Extra clickable / hover room around the cross on every side (the whole area lies inside the text box).</summary>
        private const int ClearTextHitPad = 4;

        /// <summary>Gap between the clickable area of the cross and the clickable area of the magnifier.</summary>
        private const int ClearTextGap = 4;

        /// <summary>How far the soft shadow of the cross is moved (screen pixels), the same as the magnifier's.</summary>
        private const int ClearTextShadowOffset = 2;

        private readonly SearchTextBox textBox;
        private readonly Func<string, int, SearchReport> onConfirm;
        private readonly string? leakedChar;
        private readonly Rectangle starButton;
        private readonly List<SearchHistoryEntry> history;
        private readonly Action onHistoryChanged;
        private readonly int extraHeight;
        private int ticks = 0;

        // A search that starts by itself: the result panel's "Did you mean" words open the box with the word already typed in and the star of the
        // search it belongs to, and the search is confirmed in the first update (see update). Null / false = the box opens empty, as always.
        private readonly string? autoSearchText;
        private bool autoSearchPending;

        private bool showTooShortHint = false;   // also used for the "only spaces / tabs", "only symbols", "invalid brackets" and "invalid characters" warnings: this flag means "show the red validation hint"
        private string validationHintText = TooShortHintText;   // which text that red hint shows
        private bool showNoQualityHint = false;
        private int qualityIndex = 0;

        /// <summary>
        /// The star filter written INTO the search text ("gold star apple" -> 2), worked out whenever the text changes (see RefreshSuggestions); null = the
        /// text has no quality words and the star button decides. While it is set, it wins over the star button (the button shows it, too).
        /// </summary>
        private int? typedQuality;
        private string lastText = "";

        // auto-complete state: the current list, the row picked with the arrow keys (-1 = none; an index into the WHOLE list, not into the
        // visible rows), and a text for which the list stays closed (a history word that was just filled in, or a search that was refused)
        private readonly Func<string, IReadOnlyList<Suggestion>>? getSuggestions;
        private IReadOnlyList<Suggestion> suggestions = Array.Empty<Suggestion>();
        private int activeSuggestion = -1;
        private string? suppressedText;

        // Multi-term search: the drop-down completes only the word being typed (the one after the last comma, see RefreshSuggestions).
        // This is the text that stays in front of it when a suggestion is picked: "fruit, " for "fruit, fi". Empty = the whole text is completed.
        private string suggestionPrefix = "";
        private static readonly char[] SpaceChars = { ' ', '\t' };
        private bool magnifierPressStarted;   // the left button went down on the magnifier and is (possibly) still held

        // Instant correction ("Did you mean: Fish, Catfish" under the text box, before Enter): filled by RefreshSuggestions only when the word being typed
        // gets no auto-complete row AND nothing in the game matches it. Kept apart from the drop-down (suggestions) and from the search itself.
        private readonly Func<string, IReadOnlyList<string>>? getSpelling;
        private readonly List<string> spellingWords = new();   // the names as FindSimilar writes them ("Fish (category)"); the row shows them without the label
        private string spellingPrefix = "";                    // what stays in front of the corrected word when one is picked ("fruit, " for "fruit, frish")

        // hover tips of the history words (asked from ModEntry once per word and remembered while this menu is open)
        private readonly Func<string, string?>? getWordTip;
        private readonly Dictionary<string, string?> wordTipCache = new();

        // hover tip of the drop-down: set while the list is drawn (DrawSuggestions), drawn last (DrawSuggestionTip) so it lies on top
        private string? suggestionTipText;
        private int suggestionTipRowTop;   // screen y of the top of the row the tip belongs to
        private static readonly Dictionary<string, string?> categoryTipCache = new();

        // Left / Right / Delete held down: the key repeats like in any text field (the game reports a key press only once)
        private Keys? heldEditKey;
        private double heldEditKeySinceMs;
        private double heldEditKeyLastMs;
        private const double EditKeyRepeatDelayMs = 420;
        private const double EditKeyRepeatEveryMs = 38;

        // scrolling of the drop-down: index of the first visible suggestion, and the state of a scrollbar drag
        private int suggestionScroll = 0;
        private bool draggingScrollbar = false;
        private int scrollbarGrabOffset = 0;   // where inside the knob the mouse grabbed it (pixels from the knob's top), so the knob does not jump

        /// <param name="onConfirm">Called with the search text and the star filter (-1 = any, 0 = no star) when the player presses Enter. Return a report with <see cref="SearchOutcome.NoMatchingQuality"/> to keep the menu open; its AllTerms are the words the search worked out, and the history remembers the ones that could be searched (found or not).</param>
        /// <param name="history">The recent searches (word + the star filter used), newest first. The menu edits this list directly.</param>
        /// <param name="onHistoryChanged">Called after the history list was changed, so the caller can save it.</param>
        /// <param name="leakedChar">The character of the key that opened this menu, if it types a character. It is removed if it leaks into the text box.</param>
        /// <param name="getSuggestions">Returns the auto-complete suggestions for the text typed so far. Null = no drop-down at all.</param>
        /// <param name="autoSearchText">Optional: a text that is put into the search box and searched at once (no Enter needed), like a player typing it and pressing Enter. Used when a "Did you mean" word of the result panel is clicked. Null = the box opens empty.</param>
        /// <param name="autoSearchQuality">The star filter of that automatic search (-1 = any, 1 = silver, 2 = gold, 4 = iridium, 0 = no star). Only used together with <paramref name="autoSearchText"/>.</param>
        /// <param name="getSpelling">Returns the names spelled like a typed word that nothing in the game matches (ModEntry.GetInstantSpelling), for the "Did you mean" line under the text box while the player is still typing. Null = no such line.</param>
        /// <param name="getWordTip">Returns the hover tip of one word of the search history (null = no tip), see ModEntry.GetTermTip (the same tip rules everywhere: history, drop-down, result panel). Null = no tips in the history.</param>
        public SearchMenu(Func<string, int, SearchReport> onConfirm, List<SearchHistoryEntry> history, Action onHistoryChanged, string? leakedChar = null, Func<string, IReadOnlyList<Suggestion>>? getSuggestions = null,
            string? autoSearchText = null, int autoSearchQuality = -1, Func<string, string?>? getWordTip = null, Func<string, IReadOnlyList<string>>? getSpelling = null)
            : base(
                Game1.uiViewport.Width / 2 - DialogWidth / 2,
                Game1.uiViewport.Height / 2 - (MenuHeight + ExtraHeightFor(history)) / 2,
                DialogWidth,
                MenuHeight + ExtraHeightFor(history),
                false)
        {
            this.onConfirm = onConfirm;
            this.leakedChar = leakedChar;
            this.history = history;
            this.onHistoryChanged = onHistoryChanged;
            this.extraHeight = ExtraHeightFor(history);
            this.getSuggestions = getSuggestions;
            this.getWordTip = getWordTip;
            this.getSpelling = getSpelling;
            this.qualityIndex = Math.Clamp(lastQualityIndex, 0, QualityValues.Length - 1);   // reopen with the star the player used last

            // the star button sits at the right end of the search row; the text box is shortened to make room
            const int starSize = 48;
            const int starGap = 16;

            textBox = new SearchTextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                Game1.smallFont,
                Game1.textColor)
            {
                X = xPositionOnScreen + 48,
                Y = yPositionOnScreen + 104,
                Width = width - 96 - starSize - starGap,
                Height = 48,
                Text = ""
            };

            starButton = new Rectangle(xPositionOnScreen + width - 48 - starSize, yPositionOnScreen + 104, starSize, starSize);

            // the text may only use the room left of the paste button (the paste / clear / magnifier buttons sit inside the box at its right end)
            textBox.TextAreaWidth = PasteBounds.X - PasteTextGap - (textBox.X + 16);

            textBox.OnEnterPressed += sender => Confirm();

            // connect the text box to the game's keyboard input system
            Game1.keyboardDispatcher.Subscriber = textBox;
            textBox.Selected = true;

            // an automatic search: the word is typed in, the star is the one of the search it comes from, and the search itself runs in the first update
            if (!string.IsNullOrWhiteSpace(autoSearchText))
            {
                this.autoSearchText = autoSearchText.Trim();
                int starIndex = Array.IndexOf(QualityValues, autoSearchQuality);
                if (starIndex >= 0)
                    qualityIndex = starIndex;

                textBox.Text = this.autoSearchText;
                lastText = textBox.Text;          // so update does not take the typed-in text for an edit
                SuppressSuggestions();            // and the drop-down stays closed
                autoSearchPending = true;
            }
        }

        // ===================== QUALITY WORDS IN THE SEARCH TEXT ("gold star apple") =====================
        // A quality can be typed together with the item: "gold star apple", "apple gold star", "iridium quality wine", "star silver fish", "no star corn".
        // The quality words are taken out BEFORE anything else looks at the text (the checks, the search, the drop-down, "Did you mean", the history),
        // so "gold star apple" is ONE search for Apple with the gold filter, never the three words "gold", "star", "apple" ("gold" would find Gold Bar).
        // A quality needs its "star" / "stars" / "quality" word: a plain "gold" or "iridium" stays an item word (Gold Bar, Iridium Ore). The match works
        // on whole words only ("gold starfruit" is NOT a quality phrase). A typed quality wins over the star button.

        private static readonly Regex QualityPhraseRegex = new(
            @"(?<![\p{L}\p{N}])(?:(?<q>silver|gold|golden|iridium|no|normal|base)[\s_\-]*(?:stars?|quality)|(?:stars?|quality)[\s_\-]*(?<q>silver|gold|golden|iridium))(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Takes the quality words out of a search text. Returns the text without them (exactly the text itself when there are none).
        /// <paramref name="quality"/>: the game's quality value of the typed quality (1 silver, 2 gold, 4 iridium, 0 no star), null = none typed.
        /// <paramref name="conflict"/>: two different qualities were typed ("gold star silver star apple"). <paramref name="phrase"/>: the first
        /// quality phrase as typed ("gold star"), "" if none; it is kept in front of a picked suggestion so the quality is not lost.
        /// </summary>
        internal static string ExtractQuality(string text, out int? quality, out bool conflict, out string phrase)
        {
            quality = null;
            conflict = false;
            phrase = "";
            text ??= "";

            MatchCollection matches = QualityPhraseRegex.Matches(text);
            if (matches.Count == 0)
                return text;

            foreach (Match match in matches)
            {
                int value = match.Groups["q"].Value.ToLowerInvariant() switch
                {
                    "silver" => 1,
                    "gold" or "golden" => 2,
                    "iridium" => 4,
                    _ => 0   // no / normal / base
                };

                if (quality == null)
                {
                    quality = value;
                    phrase = Regex.Replace(match.Value.Trim(), @"\s+", " ");
                }
                else if (quality != value)
                {
                    conflict = true;
                }
            }

            // the text without the phrases: the words around them stay, the separators of several search words stay meaningful
            string rest = QualityPhraseRegex.Replace(text, " ");
            string[] parts = rest.Split(ModEntry.QueryCommas);
            if (parts.Length > 1)
            {
                // "apple, gold star, fish" -> "apple, fish" (a part that only held the quality is dropped)
                rest = string.Join(", ", parts.Select(part => Regex.Replace(part, @"\s+", " ").Trim()).Where(part => part.Length > 0));
            }
            else
            {
                rest = Regex.Replace(rest, @"\s+", " ").Trim();
            }

            return rest;
        }

        /// <summary>The star filter really used: the quality typed into the text, or else the star button.</summary>
        private int EffectiveQuality => typedQuality ?? QualityValues[qualityIndex];

        /// <summary>
        /// Whether the query may be searched. Empty is never allowed; 3+ characters is allowed;
        /// shorter queries are allowed only if they are a pure number (item ID, or negative category ID).
        /// Uses the same number rule as BuildSingleMatcher in ModEntry.
        /// </summary>
        private static bool IsQueryValid(string query)
        {
            if (query.Length == 0)
                return false;
            if (query.Length >= MinQueryLength)
                return true;
            return int.TryParse(query, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>
        /// Whether ONE word of a query with several words can be searched at all: it is long enough (or a number), it is not only symbols and it has
        /// no character that cannot belong to an item name. A word that fails this is never searched; the search reports it as "(Invalid: ...)"
        /// in the result title (ModEntry.ScanChests) instead of refusing the whole query.
        /// </summary>
        internal static bool IsTermSearchable(string term)
        {
            return IsQueryValid(term) && !IsSymbolsOnly(term) && !HasInvalidCharacters(term);
        }

        /// <summary>
        /// True if the query is nothing but symbols (like "&amp;@#!", "---" or "((("): it has no letter and no digit at all, but is not empty.
        /// Round brackets and the minus sign count as symbols here too; they only matter together with a letter or a digit
        /// ("fruit (category)", "-26", "-2"), and such a query has one, so it passes.
        /// </summary>
        private static bool IsSymbolsOnly(string query)
        {
            bool hasCharacter = false;
            foreach (char ch in query)
            {
                if (char.IsLetterOrDigit(ch))
                    return false;
                if (!char.IsWhiteSpace(ch))
                    hasCharacter = true;
            }
            return hasCharacter;   // an empty query is not "only symbols": it gets the "too short" hint
        }

        /// <summary>
        /// True if the query still contains a round bracket, half-width or full-width. Call it with the query AFTER the legal
        /// " (category)" label was cut off: the only legal use of brackets is that label, so any bracket left over
        /// ("(ca", "cat)", "fruit (cate", "fruit (categry)", "(category) fruit (something)") means the query is invalid.
        /// </summary>
        private static bool HasStrayBrackets(string plainQuery)
        {
            return plainQuery.IndexOfAny(BracketChars) >= 0;
        }

        /// <summary>
        /// True if the query contains a character that cannot belong to an item name: anything except letters, digits, white space,
        /// accent / vowel-sign marks and the symbols of <see cref="AllowedPunctuation"/> ("*$f", "ab@cd", "fruit#", emoji ...).
        /// Call it with the query AFTER the " (category)" label was cut off. Letters of every language count as letters, so
        /// translated item names (Chinese, Korean, Russian, accents like "Pi�a") are fine.
        /// </summary>
        private static bool HasInvalidCharacters(string plainQuery)
        {
            foreach (char ch in plainQuery)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || AllowedPunctuation.IndexOf(ch) >= 0)
                    continue;

                // accents and vowel signs that are stored as a separate character (many scripts need them inside a word)
                UnicodeCategory category = char.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark)
                    continue;

                return true;
            }
            return false;
        }

        /// <summary>
        /// Used for a query of several words: true if this one word cannot be part of a search, because it has no letter or digit at all ("&amp;&amp;&amp;", "-")
        /// or contains a character that cannot belong to an item name ("fr&amp;", "ab@cd"). Brackets are handled before this by their own hint.
        /// </summary>
        private static bool IsInvalidSymbolWord(string word)
        {
            return IsSymbolsOnly(word) || HasInvalidCharacters(word);
        }

        /// <summary>How much taller the menu must be to hold the history block (0 when there is no history).</summary>
        private static int ExtraHeightFor(List<SearchHistoryEntry> history)
        {
            int rows = CountHistoryRows(history);
            if (rows == 0)
                return 0;
            return HistoryRowsTop + rows * HistoryRowHeight + HistoryFooterGap - 256;
        }

        /// <summary>
        /// Puts a finished search at the front of the history, as ONE entry made of ALL the words the player typed
        /// (<paramref name="typedTerms"/>, joined as "a, b", including words that found nothing: "fish, ere" stays "fish, ere") together with the
        /// star filter it was searched with. The history holds plain words only (no brackets, no numbers): a word that is an item id or a
        /// category id ("268", "-4") is stored as its name ("Starfruit", "fish"), see <see cref="TryGetIdName"/>; an id without a name stays
        /// the number. Then the old entries are cleaned:
        ///   - "prefix swallowing": an old word that is the start of (or equal to) a new word is removed, so the newest, most complete
        ///     spelling is the only one left ("frui" is swallowed by "fruit"). This works word by word: an old entry "frui, fish" loses only "frui",
        ///     and an old entry with no words left is removed completely. The star of the old entry does not matter.
        ///     Two kinds of old words are only swallowed by an EXACT repeat, never by a longer word: category words ("fish" must not be eaten by
        ///     "Fishing Rod") and numbers ("26" must not be eaten by "268"), because those are finished searches, not half-typed words.
        ///   - at most <see cref="MaxHistory"/> entries.
        /// </summary>
        private void RecordHistory(IReadOnlyList<string> typedTerms, int quality)
        {
            var newTerms = new List<string>();   // as stored, e.g. "Starfruit"
            var newKeys = new List<string>();    // what old words are compared with (the name of an id, so "starf" is still swallowed by "Starfruit")
            var newIds = new List<string>();     // the plain numbers among the new words ("268", "-26")
            foreach (string raw in typedTerms)
            {
                string term = (raw ?? "").Trim();
                bool isNumber = int.TryParse(term, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
                string? idName = TryGetIdName(term);

                string text = idName ?? term;   // plain word: the name of an id, or the word itself (the number if no name is known)
                string key = isNumber && idName == null ? "#" + term : SuggestionIndex.MakeKey(idName ?? term);   // "-26" and "26" are different searches, though the key drops the hyphen
                if (key.Length == 0 || newKeys.Contains(key))
                    continue;

                newTerms.Add(text);
                newKeys.Add(key);
                if (isNumber)
                    newIds.Add(term);
            }

            if (newTerms.Count == 0)
                return;

            // the same search is already in the history (the same words, in any order): ONE entry, never a duplicate. Only its star is updated; a pinned
            // entry stays pinned and stays where it is, an unpinned one moves to the front of the unpinned entries.
            var newKeySet = new HashSet<string>(newKeys, StringComparer.Ordinal);
            int same = history.FindIndex(h => SameSearch(h.Word, newKeySet));
            if (same >= 0)
            {
                SearchHistoryEntry existing = history[same];
                existing.Quality = quality;
                if (!existing.Pinned)
                {
                    history.RemoveAt(same);
                    history.Insert(FirstUnpinnedIndex(), existing);
                }

                onHistoryChanged();
                return;
            }

            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Pinned)
                    continue;   // a pinned entry is never swallowed by a newer, longer word: it stays exactly as the player pinned it

                // the words of the old entry as they are stored (an id keeps its "(name)" label)
                List<string> oldTerms = history[i].Word
                    .Split(ModEntry.QueryCommas, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .ToList();
                if (oldTerms.Count == 0)
                    continue;

                List<string> kept = oldTerms.Where(old => !IsSwallowed(old, newIds, newKeys)).ToList();
                if (kept.Count == oldTerms.Count)
                    continue;   // nothing of this entry was swallowed: it stays exactly as it is

                if (kept.Count == 0)
                    history.RemoveAt(i);
                else
                    history[i].Word = string.Join(", ", kept);
            }

            // the new search goes in front of the unpinned entries (the pinned ones always stay at the very front); only unpinned entries count
            // towards MaxHistory, the oldest unpinned ones fall out
            history.Insert(FirstUnpinnedIndex(), new SearchHistoryEntry { Word = string.Join(", ", newTerms), Quality = quality });
            TrimUnpinned(history);

            onHistoryChanged();
        }

        /// <summary>Where the unpinned entries start (the pinned ones are always at the front).</summary>
        private int FirstUnpinnedIndex()
        {
            int index = 0;
            while (index < history.Count && history[index].Pinned)
                index++;
            return index;
        }

        /// <summary>Keeps at most <see cref="MaxHistory"/> UNPINNED entries (the oldest go); pinned entries are never removed here.</summary>
        internal static void TrimUnpinned(List<SearchHistoryEntry> history)
        {
            int unpinned = 0;
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Pinned)
                    continue;
                unpinned++;
                if (unpinned > MaxHistory)
                {
                    history.RemoveAt(i);
                    i--;
                }
            }
        }

        /// <summary>Puts the pinned entries in front (in their order), then the unpinned ones (in their order).</summary>
        internal static void SortPinnedFirst(List<SearchHistoryEntry> history)
        {
            List<SearchHistoryEntry> ordered = history.Where(h => h.Pinned).Concat(history.Where(h => !h.Pinned)).ToList();
            history.Clear();
            history.AddRange(ordered);
        }

        /// <summary>True if a stored history word holds exactly the given search words (keys, any order): "fruit, fish" = "fish, fruit".</summary>
        private static bool SameSearch(string storedWord, HashSet<string> newKeys)
        {
            var oldKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in (storedWord ?? "").Split(ModEntry.QueryCommas, StringSplitOptions.RemoveEmptyEntries))
            {
                string term = ModEntry.StripCategorySuffix(part.Trim());
                if (term.Length == 0)
                    continue;
                bool isNumber = int.TryParse(term, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
                string? idName = TryGetIdName(term);
                oldKeys.Add(isNumber && idName == null ? "#" + term : SuggestionIndex.MakeKey(idName ?? term));
            }

            return oldKeys.Count > 0 && oldKeys.SetEquals(newKeys);
        }

        /// <summary>
        /// Category ids and the word the search understands as that category (every word here is checked with ModEntry.IsCategoryWord before it is used,
        /// so a stored name always searches a category again). Some game words cover a sibling id as well ("artisan goods" = -26 and -27, "mineral" = -2 and -12).
        /// </summary>
        internal static readonly Dictionary<int, string> CategoryNamesById = new()
        {
            [-2] = "gem",
            [-4] = "fish",
            [-5] = "egg",
            [-6] = "milk",
            [-7] = "cooking",
            [-8] = "crafting",
            [-9] = "big craftable",
            [-12] = "mineral",
            [-14] = "meat",
            [-15] = "metal resources",
            [-16] = "building resources",
            [-19] = "fertilizer",
            [-20] = "junk",
            [-21] = "bait",
            [-22] = "fishing tackle",
            [-24] = "furniture",
            [-25] = "ingredient",
            [-26] = "artisan goods",
            [-27] = "syrup",
            [-28] = "monster loot",
            [-29] = "equipment",
            [-74] = "seed",
            [-75] = "vegetable",
            [-79] = "fruit",
            [-80] = "flower",
            [-81] = "forage",
            [-95] = "hat",
            [-96] = "ring",
            [-97] = "boots",
            [-98] = "weapon",
            [-99] = "tool",
            [-100] = "clothing",
            [-101] = "trinket",
            [-102] = "book",
            [-103] = "skill book"
        };

        /// <summary>
        /// The real name of an id search word, or null. A pure number is an id for the search: negative = category id ("-26" -> "artisan goods"),
        /// otherwise the id of an object ("268" -> "Starfruit", the game's display name). Null for anything that is not a number, for an id without a
        /// usable name, and for a name that could not be read back as a label (it contains brackets or a search separator). Used for the history
        /// and for the title of the result panel (ModEntry).
        /// </summary>
        internal static string? TryGetIdName(string term)
        {
            string trimmed = (term ?? "").Trim();
            if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
                return null;

            try
            {
                string? name = null;

                if (number < 0)
                {
                    if (CategoryNamesById.TryGetValue(number, out string? category) && ModEntry.IsCategoryWord(category))
                        name = category;
                }
                else
                {
                    name = ItemRegistry.GetData("(O)" + number.ToString(CultureInfo.InvariantCulture))?.DisplayName?.Trim();
                }

                if (string.IsNullOrWhiteSpace(name)
                    || name.StartsWith("[", StringComparison.Ordinal)
                    || name.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                    || !name.Any(char.IsLetterOrDigit))
                    return null;

                // the label "(name)" must survive a round trip through the search box
                if (name.IndexOfAny(ModEntry.QueryCommas) >= 0 || name.IndexOfAny(BracketChars) >= 0)
                    return null;

                return name;
            }
            catch (Exception)
            {
                return null;   // no name available: the number stays as it is
            }
        }

        /// <summary>True if a word of an old history entry is replaced by one of the new words (see <see cref="RecordHistory"/>).</summary>
        private static bool IsSwallowed(string oldTerm, List<string> newIds, List<string> newKeys)
        {
            string trimmed = oldTerm.Trim();

            // an id word ("268", "-26", or "268 (Starfruit)" with its label) is only replaced by the very same number
            string plain = ModEntry.StripCategorySuffix(trimmed);   // also cuts the "(name)" label of an id
            if (int.TryParse(plain, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                return newIds.Any(id => string.Equals(id, plain, StringComparison.Ordinal));

            string oldKey = SuggestionIndex.MakeKey(trimmed);
            if (oldKey.Length == 0)
                return false;

            bool exactOnly = ModEntry.IsCategoryWord(trimmed);
            foreach (string newKey in newKeys)
            {
                if (newKey == oldKey)
                    return true;
                if (!exactOnly && newKey.StartsWith(oldKey, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private void Confirm()
        {
            // a suggestion picked with the arrow keys wins over the typed text
            if (SuggestionsVisible && activeSuggestion >= 0 && activeSuggestion < suggestions.Count)
                textBox.Text = ApplySuggestion(activeSuggestion);

            // quality words ("gold star apple") are taken out first: the rest is the item search, the typed quality wins over the star button
            string searchText = ExtractQuality(textBox.Text, out int? qualityInText, out bool qualityConflict, out _);
            typedQuality = qualityInText;
            int searchQuality = EffectiveQuality;
            string query = searchText.Trim();

            // the player typed something, but all of it was white space (spaces, tabs): that is not a typo that is "too short", so it gets
            // its own hint. A completely empty box (nothing typed) still gets the "too short" hint, and "  fruit  " is trimmed to "fruit" and searched normally.
            bool blankOnly = textBox.Text.Length > 0 && query.Length == 0 && qualityInText == null;

            // "fruit (category)" is the same search as "fruit": the history keeps the plain word, and the length rule looks at the plain word.
            // Words separated by commas ("fruit, fish", half-width or full-width comma) are separate search terms; the history keeps them as
            // "fruit, fish". Without a comma the whole text is one term for the length rule (spaces inside it are not split there: whether
            // "fruit fish" is two words is decided by the search itself, see ModEntry.SplitBySpaces).
            bool hasCommas = ModEntry.HasCommas(query);
            string plainQuery = ModEntry.NormalizeQueryText(query);
            List<string> terms = hasCommas ? ModEntry.GetCommaTerms(query) : new List<string> { plainQuery };
            string checkedText = string.Join(" ", terms);   // all terms together, for the checks that only look for a bad character

            // every word of the query, split at the commas AND at the spaces (the legal " (category)" label is already gone)
            List<string> words = checkedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Refusals, all without touching the history (a refused query is never remembered). They block the search, keep the menu open, play
            // the game's "cancel" cue and show one red hint.
            //
            // ONE word (or nothing), as always, in this order: only spaces / tabs, only symbols, too short, invalid brackets, invalid characters.
            //   "fruit&&&" is one word with a bad character: refused as a whole ("Invalid characters ...").
            //
            // SEVERAL words ("fruit, &&&", "fruit &&&", "fruit, fi", "fruit ere"): a bad word no longer refuses the query. Stray brackets still refuse
            // it (they are a mistake in the syntax, not a word). Otherwise the query is refused only when NOT ONE word can be searched at all:
            //   - with commas: no comma-separated term passes IsTermSearchable (too short, only symbols, bad character);
            //   - without commas: the text as a whole is too short, or every space-separated word is only symbols / has a bad character
            //     (item names contain short words ("Pale Ale"), so the length rule looks at the whole text; the search itself splits it, see
            //     ModEntry.SplitBySpaces).
            // Every word that cannot be searched, and every word that finds nothing, is then listed as "(Invalid: ...)" in the title of the result
            // panel (ModEntry.ScanChests), and the search counts as a success as soon as one word finds something.
            string? refusal = null;
            if (qualityConflict)
            {
                refusal = QualityConflictHintText;
            }
            else if (qualityInText != null && query.Length == 0)
            {
                refusal = QualityOnlyHintText;   // "gold star" alone: a quality of what?
            }
            else if (blankOnly)
            {
                refusal = BlankHintText;
            }
            else if (words.Count >= 2)
            {
                bool severalTerms = hasCommas && terms.Count >= 2;

                if (HasStrayBrackets(checkedText))
                {
                    refusal = InvalidBracketsHintText;
                }
                else
                {
                    bool nothingSearchable = hasCommas
                        ? !terms.Any(IsTermSearchable)
                        : (!IsQueryValid(plainQuery) || words.All(IsInvalidSymbolWord));

                    if (nothingSearchable)
                    {
                        refusal = words.Any(IsInvalidSymbolWord) ? MultiInvalidSymbolsHintText
                            : severalTerms ? MultiTooShortHintText
                            : TooShortHintText;
                    }
                }
            }
            else
            {
                // (a query that is only commas, like ",", has no letter or digit, so it counts as "only symbols")
                bool symbolsOnly = IsSymbolsOnly(ModEntry.StripCategorySuffix(query)) || terms.Any(IsSymbolsOnly);
                bool tooShort = terms.Count == 0 || terms.Any(term => !IsQueryValid(term));
                bool strayBrackets = HasStrayBrackets(checkedText);
                bool invalidCharacters = HasInvalidCharacters(checkedText);

                refusal = symbolsOnly ? SymbolsOnlyHintText
                    : tooShort ? TooShortHintText
                    : strayBrackets ? InvalidBracketsHintText
                    : invalidCharacters ? InvalidCharactersHintText
                    : null;
            }

            // "vegetable jelly" can never exist in the game: refuse it with an explanation instead of searching the two words separately
            if (refusal == null && !hasCommas && ModEntry.IsVegetableJellyQuery(plainQuery))
                refusal = VegetableJellyHintText;

            if (refusal != null)
            {
                Game1.playSound(RefusedSound);
                validationHintText = refusal;
                showTooShortHint = true;
                showNoQualityHint = false;
                lastText = textBox.Text;
                SuppressSuggestions();
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            // the query passed the checks: search. Only afterwards is the history updated: every search that really ran is remembered, whether it found
            // something or not (a typo like "starfruu", a pasted text, a mix like "fruit, xyz"). A refused query never is.
            SearchReport report = onConfirm(query, searchQuality);

            if (report.Outcome == SearchOutcome.NoMatchingQuality)
            {
                // the item exists but not in this quality: stay open so the player can change the star and search again
                showNoQualityHint = true;
                showTooShortHint = false;
                lastText = textBox.Text;
                SuppressSuggestions();
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            // The words as the search itself split them ("fruit fish" at the space, "268 -4" into two ids). In a query of several words, a word that could
            // not be searched at all (too short, only symbols, a bad character) was never searched, so it stays out of the history; a word that was
            // searched but found nothing ("xyz") stays in, so "fruit, xyz" is remembered as it was typed.
            List<string> historyTerms;
            if (report.AllTerms.Count > 0)
            {
                historyTerms = report.AllTerms.Count == 1 ? report.AllTerms.ToList() : report.AllTerms.Where(IsTermSearchable).ToList();
            }
            else
            {
                historyTerms = terms;
                if (!hasCommas)
                {
                    string[] tokens = plainQuery.Split(SpaceChars, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 && tokens.All(t => int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                        historyTerms = tokens.ToList();
                }
            }

            if (historyTerms.Count > 0)
                RecordHistory(historyTerms, searchQuality);   // the history keeps the plain words with the quality ("apple" + gold): clicking it sets the star button

            exitThisMenu();
        }

        public override void update(GameTime time)
        {
            base.update(time);

            // the automatic search of a clicked "Did you mean" word: exactly what pressing Enter does (same checks, same history, closes the box
            // when the search is done; a refused search leaves the box open with its red hint, like a typed one)
            if (autoSearchPending)
            {
                autoSearchPending = false;
                Confirm();
                return;
            }

            // the key that opened this menu can leak into the box; remove it
            if (ticks < 3)
            {
                ticks++;
                if (leakedChar != null
                    && textBox.Text.Length == 1
                    && textBox.Text.Equals(leakedChar, StringComparison.OrdinalIgnoreCase))
                {
                    textBox.Text = "";
                }
            }

            // hide the hints again as soon as the player edits the text
            if (textBox.Text != lastText)
            {
                // a text may hold line breaks, control characters or characters the game's font cannot draw, or be longer than 120 characters:
                // clean it (see CleanTextBoxText)
                string cleaned = CleanTextBoxText(textBox.Text);
                if (cleaned != textBox.Text)
                    textBox.Text = cleaned;

                lastText = textBox.Text;
                showTooShortHint = false;
                showNoQualityHint = false;
                RefreshSuggestions();
            }

            // selecting text with the mouse: the selection follows the mouse while the left button is held
            if (textBox.IsMouseSelecting)
            {
                if (Game1.input.GetMouseState().LeftButton == ButtonState.Pressed)
                    textBox.MouseDrag(Game1.getMouseX());
                else
                    textBox.MouseUp();
            }

            // Left / Right / Delete held: repeat after a short delay
            if (heldEditKey is Keys held)
            {
                double nowMs = time.TotalGameTime.TotalMilliseconds;
                if (!Game1.input.GetKeyboardState().IsKeyDown(held))
                    heldEditKey = null;
                else if (nowMs - heldEditKeySinceMs >= EditKeyRepeatDelayMs && nowMs - heldEditKeyLastMs >= EditKeyRepeatEveryMs)
                {
                    heldEditKeyLastMs = nowMs;
                    HandleEditKey(held);
                }
            }

            // dragging the scrollbar knob: follow the mouse while the left button is held, stop as soon as it is released
            // (polled here as well as in releaseLeftClick, so a release outside the window can never leave the knob stuck to the mouse)
            if (draggingScrollbar)
            {
                if (Game1.input.GetMouseState().LeftButton != ButtonState.Pressed || !SuggestionsVisible || !NeedsScrollbar)
                    draggingScrollbar = false;
                else
                    SetScrollFromMouse(Game1.getMouseY());
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            // do not call base: it would close the menu when typing letters like "E"
            if (key == Keys.Escape)
            {
                if (copyBubble.IsOpen)
                {
                    copyBubble.Close();   // Esc first closes the "Copy" bubble, a second Esc the search box
                    return;
                }
                exitThisMenu();
                return;
            }

            // Ctrl shortcuts (Cmd on Mac). They are handled here and only here: the text box itself ignores every key typed while Ctrl is held and
            // switches off the game's own paste (see SearchTextBox), so one Ctrl+V pastes exactly once.
            if (SearchTextBox.IsShortcutModifierDown() && HandleShortcut(key))
                return;

            // caret keys: Left / Right / Home / End (with Shift = select), Delete. Left / Right / Delete repeat while held (see update).
            if (HandleEditKey(key))
            {
                if (key == Keys.Left || key == Keys.Right || key == Keys.Delete)
                {
                    heldEditKey = key;
                    heldEditKeySinceMs = heldEditKeyLastMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
                }
                return;
            }

            if (!SuggestionsVisible)
                return;

            int total = suggestions.Count;

            switch (key)
            {
                case Keys.Down:
                    // none -> first -> ... -> last -> none (back to the typed text, like Google); the list scrolls along to keep the pick visible
                    activeSuggestion = activeSuggestion + 1 >= total ? -1 : activeSuggestion + 1;
                    EnsureActiveSuggestionVisible();
                    Game1.playSound("smallSelect");
                    break;

                case Keys.Up:
                    activeSuggestion = activeSuggestion < 0 ? total - 1 : activeSuggestion - 1;
                    EnsureActiveSuggestionVisible();
                    Game1.playSound("smallSelect");
                    break;

                case Keys.Tab:
                    // complete the text with the picked suggestion (or the top one) without searching yet
                    textBox.Text = ApplySuggestion(activeSuggestion >= 0 ? activeSuggestion : 0);
                    activeSuggestion = -1;
                    Game1.playSound("smallSelect");
                    break;
            }
        }

        /// <summary>
        /// Ctrl+V pastes at the caret (in place of the selection), Ctrl+A selects all, Ctrl+X cuts the selection, Ctrl+C copies it (Cmd on Mac).
        /// Returns true if the key was one of these.
        /// </summary>
        private bool HandleShortcut(Keys key)
        {
            switch (key)
            {
                case Keys.V:
                    PasteFromClipboard(playFeedback: false);
                    return true;

                case Keys.A:
                    textBox.SelectAll();
                    return true;

                case Keys.X:
                    if (textBox.HasSelection && WriteClipboardText(textBox.SelectedText))
                    {
                        textBox.DeleteSelection();   // update() notices the change: hints hidden, drop-down refreshed
                        Game1.playSound("tinyWhip");
                    }
                    return true;

                case Keys.C:
                    if (textBox.HasSelection)
                        WriteClipboardText(textBox.SelectedText);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Left / Right / Home / End / Delete for the text box. Shift extends the selection. Word jumps: Ctrl (Windows / Linux) or Option (Mac);
        /// on Mac, Cmd + Left / Right jumps to the start / end. Returns true if the key was one of these.
        /// </summary>
        private bool HandleEditKey(Keys key)
        {
            if (key != Keys.Left && key != Keys.Right && key != Keys.Home && key != Keys.End && key != Keys.Delete)
                return false;

            KeyboardState keyboard = Game1.input.GetKeyboardState();
            bool shift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
            bool alt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
            bool word = SearchTextBox.IsMac ? alt : SearchTextBox.IsCtrlDown(keyboard);
            bool line = SearchTextBox.IsMac && SearchTextBox.IsCommandDown(keyboard);

            return textBox.HandleNavigationKey(key, shift, word, line);
        }

        /// <summary>The part of the text box where the text is (a click there places the caret): from its left edge up to the paste button.</summary>
        private Rectangle TextClickArea => new(textBox.X, textBox.Y, PasteBounds.X - textBox.X, textBox.Height);

        // ===================== RIGHT CLICK: THE "COPY" BUBBLE =====================
        // A right click on a word opens a small bubble with one entry, "Copy" (the game's menu frame, see CopyBubble). Nothing is copied yet: only a
        // left click on "Copy" puts the word on the clipboard, with a soft click and a short "Copied!". A click anywhere else (or Esc) just closes it.
        // What a right click copies:
        //   the text field          the selected part, or the whole text when nothing is selected; ONLY here a separate "Paste" button
        //                           appears right beside "Copy" (same look, same size): it pastes the clipboard at the caret, like Ctrl+V
        //   a history word          the whole entry as it is stored ("fruit, fish")
        //   a drop-down row         the name of the row ("Apple"; a category row without its "(category)" label: "fish")
        private readonly CopyBubble copyBubble = new();

        /// <summary>The stored history entry whose word is under the mouse (not its pin or its x), or null.</summary>
        private string? HistoryWordAt(int x, int y)
        {
            foreach (var chip in LayoutHistory())
            {
                if (chip.Bounds.Contains(x, y))
                    return chip.Word;
            }
            return null;
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            string? word = null;

            if (SuggestionsVisible && GetSuggestionBounds().Contains(x, y))
            {
                int row = GetSuggestionRowAt(x, y);
                int index = row >= 0 ? suggestionScroll + row : -1;
                if (index >= 0 && index < suggestions.Count)
                    word = ModEntry.StripCategorySuffix(suggestions[index].Text);
            }
            else if (HistoryVisible && HistoryWordAt(x, y) is string historyWord)
            {
                word = historyWord;
            }
            else if (new Rectangle(textBox.X, textBox.Y, textBox.Width, textBox.Height).Contains(x, y))
            {
                // the text field: "Copy" (the selection, or the whole text) as everywhere, and right beside it a separate "Paste" button, which
                // only the text field has (it pastes at the caret exactly like Ctrl+V and the paste button: PasteFromClipboard)
                string typed = textBox.HasSelection ? textBox.SelectedText : textBox.Text;
                copyBubble.Open(string.IsNullOrWhiteSpace(typed) ? null : typed.Trim(), x, y, paste: () => PasteFromClipboard(playFeedback: true));
                Game1.playSound("smallSelect");
                return;
            }

            if (!string.IsNullOrWhiteSpace(word))
            {
                copyBubble.Open(word.Trim(), x, y);
                Game1.playSound("smallSelect");
                return;
            }

            copyBubble.Close();   // a right click on nothing: only closes an open bubble
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // an open "Copy" bubble takes the click: on "Copy" it copies, anywhere else it only closes (the click does nothing else)
            if (copyBubble.IsOpen)
            {
                copyBubble.HandleLeftClick(x, y);
                return;
            }

            // a click inside the drop-down: on the scrollbar = grab / jump; on a row = search for that item; on the frame = swallowed
            if (SuggestionsVisible && GetSuggestionBounds().Contains(x, y))
            {
                if (NeedsScrollbar)
                {
                    Rectangle runner = GetScrollbarRunner();
                    Rectangle grabArea = runner;
                    grabArea.Inflate(ScrollbarHitPad, 0);

                    if (grabArea.Contains(x, y))
                    {
                        Rectangle thumb = GetScrollbarThumb();
                        if (thumb.Contains(x, y))
                        {
                            scrollbarGrabOffset = y - thumb.Y;   // grabbed the knob: it keeps its position under the mouse
                        }
                        else
                        {
                            scrollbarGrabOffset = thumb.Height / 2;   // clicked the empty track: the knob jumps there (centered on the mouse), then follows
                            if (SetScrollFromMouse(y))
                                Game1.playSound("shiny4");
                        }

                        draggingScrollbar = true;
                        return;
                    }
                }

                int row = GetSuggestionRowAt(x, y);
                if (row >= 0)
                {
                    Game1.playSound("smallSelect");
                    textBox.Text = ApplySuggestion(suggestionScroll + row);
                    activeSuggestion = -1;
                    Confirm();   // may close the menu, so nothing else may touch the text box afterwards
                }
                return;
            }

            // a word of the "Did you mean" line under the text box: only fills the word into the box (Enter still has to be pressed to search)
            if (TryApplySpellingClick(x, y))
                return;

            // the red X in the top right corner: same as pressing Esc (the drop-down, if it covers the X, has already swallowed the click above)
            if (CloseButtonVisible && CloseButtonBounds.Contains(x, y))
            {
                exitThisMenu();
                return;
            }

            // the paste button inside the search box: puts the clipboard text into the box. It never searches: Enter / the magnifier still has to be used.
            if (PasteBounds.Contains(x, y))
            {
                PasteFromClipboard(playFeedback: true);
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            // the red x inside the search box (always there): with text it empties the box and keeps the focus there, so the player can type on;
            // with an empty box nothing happens (it is drawn faded, and its hover tip says there is nothing to clear).
            // update() notices the changed text: it hides the red hints, refreshes the drop-down (nothing to suggest) and brings the history back.
            if (ClearTextBounds.Contains(x, y))
            {
                if (textBox.Text.Length > 0)
                {
                    textBox.Text = "";
                    Game1.playSound("smallSelect");
                }
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            // the magnifier inside the search box: same as pressing Enter. Everywhere else in the box a click still just focuses the box
            // (the magnifier area lies inside the box but does not overlap the star button or any other clickable part).
            if (MagnifierBounds.Contains(x, y))
            {
                magnifierPressStarted = true;
                Confirm();   // exactly what the Enter key does; may close the menu, so nothing else may touch the text box afterwards
                return;
            }

            // a click on the text: places the caret there (Shift + click extends the selection, a drag selects);
            // a double click selects the word, a triple click selects the whole text; a fourth click starts a new series
            if (TextClickArea.Contains(x, y))
            {
                // the text box counts fast clicks itself (single / double / triple click), see SearchTextBox.Click
                KeyboardState keyboard = Game1.input.GetKeyboardState();
                bool shift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
                textBox.Click(x, y, shift);
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            // the two underlined words of the key hint line work like links: "Enter" = pressing Enter, "Esc" = pressing Esc
            if (FooterVisible)
            {
                if (IsOverFooterWord(FooterEnterBounds, x, y))
                {
                    Confirm();   // exactly what the Enter key does; may close the menu
                    return;
                }

                if (IsOverFooterWord(FooterEscBounds, x, y))
                {
                    exitThisMenu();   // exactly what the Esc key does
                    return;
                }
            }

            if (HistoryVisible)
            {
                if (GetClearHistoryBounds().Contains(x, y))
                {
                    history.RemoveAll(h => !h.Pinned);   // pinned words are kept on purpose (unpin them first, or use their x)
                    onHistoryChanged();
                    Game1.playSound("trashcan");
                }
                else
                {
                    foreach (var chip in LayoutHistory())
                    {
                        // the small pin: pin / unpin this word (a pinned word moves to the front and is never pushed out by newer searches)
                        if (chip.Pin.Contains(x, y))
                        {
                            SearchHistoryEntry? entry = history.Find(h => string.Equals(h.Word, chip.Word, StringComparison.OrdinalIgnoreCase));
                            if (entry != null)
                            {
                                entry.Pinned = !entry.Pinned;
                                if (entry.Pinned)
                                {
                                    // a newly pinned word goes behind the words pinned before it
                                    history.Remove(entry);
                                    history.Insert(FirstUnpinnedIndex(), entry);
                                }
                                else
                                {
                                    // unpinned: it becomes the newest normal entry (nothing is removed now; the limit applies again with the next search)
                                    history.Remove(entry);
                                    history.Insert(FirstUnpinnedIndex(), entry);
                                }
                                onHistoryChanged();
                                Game1.playSound(entry.Pinned ? "drumkit6" : "smallSelect");
                            }
                            break;
                        }

                        // the small red x: remove just this word (the list is laid out again on the next frame)
                        if (chip.Remove.Contains(x, y))
                        {
                            history.RemoveAll(h => string.Equals(h.Word, chip.Word, StringComparison.OrdinalIgnoreCase));
                            onHistoryChanged();
                            Game1.playSound("trashcan");
                            break;
                        }

                        if (!chip.Bounds.Contains(x, y))
                            continue;

                        // fill the word into the search box, and put the star button back to what it was when this word was searched
                        // (an entry without a star, e.g. from an older save, or an unknown value: any quality)
                        textBox.Text = chip.Word;
                        SearchHistoryEntry? picked = history.Find(h => string.Equals(h.Word, chip.Word, StringComparison.OrdinalIgnoreCase));
                        qualityIndex = picked == null ? 0 : Math.Max(Array.IndexOf(QualityValues, picked.Quality), 0);
                        lastQualityIndex = qualityIndex;   // same as clicking the star button: the next search box opens with it
                        SuppressSuggestions();   // the player picked a finished search: no drop-down on top of it
                        showTooShortHint = false;
                        showNoQualityHint = false;
                        Game1.playSound("smallSelect");
                        break;
                    }
                }
            }

            if (starButton.Contains(x, y))
            {
                // outline star (any) -> silver -> gold -> iridium -> empty frame (no quality) -> outline star ...
                qualityIndex = (qualityIndex + 1) % QualityValues.Length;
                lastQualityIndex = qualityIndex;   // remember it for the next time the search box opens
                showNoQualityHint = false;
                Game1.playSound("smallSelect");
            }

            textBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = textBox;
        }

        public override void releaseLeftClick(int x, int y)
        {
            draggingScrollbar = false;
            textBox.MouseUp();
            base.releaseLeftClick(x, y);
        }

        /// <summary>Mouse wheel: scrolls the drop-down one row per notch, but only while the mouse is over it and it has a scrollbar.</summary>
        public override void receiveScrollWheelAction(int direction)
        {
            if (direction == 0 || !SuggestionsVisible || !NeedsScrollbar
                || !GetSuggestionBounds().Contains(Game1.getMouseX(), Game1.getMouseY()))
            {
                base.receiveScrollWheelAction(direction);
                return;
            }

            // wheel up (positive) = towards the top of the list
            if (SetScroll(suggestionScroll + (direction > 0 ? -1 : 1)))
                Game1.playSound("shiny4");
        }

        // ===================== SEARCH SUGGESTIONS (auto-complete drop-down) =====================
        // A parchment box that drops down from the text box while the player types. It is drawn on top of everything
        // below the text box (like Google's list) and is refreshed whenever the text changes, never while drawing.
        //   mouse:  hovering highlights a row, a click searches for that item
        //   keys:   Down / Up move the highlight, Enter searches the highlighted item, Tab fills it into the box
        //   scroll: with more suggestions than rows (MaxVisibleSuggestions) a scrollbar appears at the right edge of the list;
        //           it can be dragged, clicked, or the mouse wheel can be used while the mouse is over the list.
        //           Down / Up keep the highlighted row in view by scrolling along; scrolling the list away from the highlighted
        //           row drops the keyboard highlight (Enter then searches the typed text again), so Enter never picks an unseen row.

        /// <summary>
        /// The drop-down is shown while there is something to show and no hint line needs the space. It depends only on the current list (rebuilt
        /// whenever the text changes, see RefreshSuggestions), never on the mouse or the caret, so moving either never hides it.
        /// </summary>
        private bool SuggestionsVisible =>
            ticks >= 3   // not during the first frames, while the key that opened the menu may still be in the box
            && suggestions.Count > 0
            && !showTooShortHint
            && !showNoQualityHint
            && VisibleSuggestionRows > 0;

        private int SuggestionRowHeight => Math.Max(MinSuggestionRowHeight, Game1.smallFont.LineSpacing + 16);

        /// <summary>How many rows are shown at once: at most <see cref="MaxVisibleSuggestions"/>, fewer on a very small screen or for a short list.</summary>
        private int VisibleSuggestionRows
        {
            get
            {
                int top = textBox.Y + textBox.Height + SuggestionGap;
                int room = Game1.uiViewport.Height - top - 12 - SuggestionPadY * 2;
                return Math.Max(0, Math.Min(Math.Min(suggestions.Count, MaxVisibleSuggestions), room / SuggestionRowHeight));
            }
        }

        /// <summary>True when the list is longer than the rows shown, so the scrollbar is needed (and the rows are a little narrower to make room for it).</summary>
        private bool NeedsScrollbar => VisibleSuggestionRows > 0 && suggestions.Count > VisibleSuggestionRows;

        /// <summary>The largest value of <see cref="suggestionScroll"/>: the first row of the last full page.</summary>
        private int MaxSuggestionScroll => Math.Max(0, suggestions.Count - VisibleSuggestionRows);

        private Rectangle GetSuggestionBounds()
        {
            return new Rectangle(
                textBox.X,
                textBox.Y + textBox.Height + SuggestionGap,
                textBox.Width,
                VisibleSuggestionRows * SuggestionRowHeight + SuggestionPadY * 2);
        }

        /// <summary>How much of the inside width the scrollbar takes away from the rows (0 without a scrollbar).</summary>
        private int ScrollbarReserve => NeedsScrollbar ? ScrollbarWidth + ScrollbarGap : 0;

        /// <summary>The visible row (0 = first row on screen) under a screen position, or -1. The scrollbar column does not count as a row.</summary>
        private int GetSuggestionRowAt(int x, int y)
        {
            Rectangle box = GetSuggestionBounds();
            int firstRowTop = box.Y + SuggestionPadY;

            if (x < box.X + SuggestionPadX || x >= box.Right - SuggestionPadX - ScrollbarReserve || y < firstRowTop)
                return -1;

            int row = (y - firstRowTop) / SuggestionRowHeight;
            return row < VisibleSuggestionRows ? row : -1;
        }

        /// <summary>The track of the scrollbar: right end of the rows area, as tall as the visible rows.</summary>
        private Rectangle GetScrollbarRunner()
        {
            Rectangle box = GetSuggestionBounds();
            return new Rectangle(
                box.Right - SuggestionPadX - ScrollbarWidth,
                box.Y + SuggestionPadY,
                ScrollbarWidth,
                VisibleSuggestionRows * SuggestionRowHeight);
        }

        /// <summary>The knob of the scrollbar: its position along the track follows <see cref="suggestionScroll"/>.</summary>
        private Rectangle GetScrollbarThumb()
        {
            Rectangle runner = GetScrollbarRunner();
            int thumbHeight = Math.Min(ScrollbarThumbHeight, runner.Height);
            int travel = runner.Height - thumbHeight;
            int max = MaxSuggestionScroll;
            int offset = max > 0 ? (int)Math.Round(travel * (double)suggestionScroll / max) : 0;
            return new Rectangle(runner.X, runner.Y + offset, runner.Width, thumbHeight);
        }

        /// <summary>Moves the knob so that it follows the mouse (using <see cref="scrollbarGrabOffset"/>). Returns true if the list scrolled.</summary>
        private bool SetScrollFromMouse(int mouseY)
        {
            Rectangle runner = GetScrollbarRunner();
            int thumbHeight = Math.Min(ScrollbarThumbHeight, runner.Height);
            int travel = runner.Height - thumbHeight;
            if (travel <= 0)
                return false;

            double ratio = (mouseY - scrollbarGrabOffset - runner.Y) / (double)travel;
            ratio = Math.Clamp(ratio, 0.0, 1.0);
            return SetScroll((int)Math.Round(ratio * MaxSuggestionScroll));
        }

        /// <summary>Sets the first visible suggestion (kept within range). Returns true if it changed.</summary>
        private bool SetScroll(int value)
        {
            int clamped = Math.Clamp(value, 0, MaxSuggestionScroll);
            if (clamped == suggestionScroll)
                return false;

            suggestionScroll = clamped;

            // scrolling the highlighted row out of view drops the keyboard highlight, so Enter never searches a row the player cannot see
            if (activeSuggestion >= 0 && (activeSuggestion < suggestionScroll || activeSuggestion >= suggestionScroll + VisibleSuggestionRows))
                activeSuggestion = -1;

            return true;
        }

        /// <summary>Scrolls just far enough that the row picked with the arrow keys is on screen.</summary>
        private void EnsureActiveSuggestionVisible()
        {
            if (activeSuggestion < 0)
                return;

            int rows = VisibleSuggestionRows;
            if (activeSuggestion < suggestionScroll)
                suggestionScroll = activeSuggestion;
            else if (activeSuggestion >= suggestionScroll + rows)
                suggestionScroll = activeSuggestion - rows + 1;

            suggestionScroll = Math.Clamp(suggestionScroll, 0, MaxSuggestionScroll);
        }

        /// <summary>
        /// Closes the DROP-DOWN and keeps it closed until the text is edited. The spelling check is NOT part of that: the "Did you mean" line is
        /// worked out again for the current text right here, so a word that was filled in by a click (history, auto-suggest, "Did you mean")
        /// is checked exactly like a typed one.
        /// </summary>
        private void SuppressSuggestions()
        {
            suppressedText = textBox.Text;
            RefreshSuggestions();   // the drop-down stays empty (suppressedText), the spelling line is re-validated
        }

        /// <summary>
        /// The text box content after picking a suggestion: the words typed before the one being completed stay, the suggestion replaces the rest
        /// ("fruit, fi" + "Fish" = "fruit, Fish"). With a single word the whole text is replaced, as before.
        /// </summary>
        private string ApplySuggestion(int index)
        {
            return suggestionPrefix + suggestions[index].Text;
        }

        /// <summary>
        /// Validates the current text: the drop-down suggestions AND the "Did you mean" line. Called whenever the text changes, whatever changed it
        /// (typing, a click on a history word, a click on a drop-down row or a "Did you mean" word, paste, clear), and from SuppressSuggestions.
        /// A closed drop-down (<see cref="suppressedText"/>) only hides the rows; the spelling check always runs.
        /// Only the word being typed is completed:
        ///   with a comma in the text, the word after the LAST comma ("fruit, fi" completes "fi"; right after the comma nothing is typed yet, so no list);
        ///   without a comma, the whole text is completed first (so "star fr" still finds "Starfruit" and "ancient fr" "Ancient Fruit"); only when that finds
        ///   nothing, the last space-separated word is completed on its own ("fruit fi" completes "fi").
        /// The words in front of it are remembered in <see cref="suggestionPrefix"/>, so picking a suggestion never overwrites them.
        /// </summary>
        private void RefreshSuggestions()
        {
            activeSuggestion = -1;
            suggestionScroll = 0;          // a new list always starts at its top
            draggingScrollbar = false;
            suggestionPrefix = "";
            spellingWords.Clear();         // the instant correction is worked out again below, only for a word without any suggestion
            spellingPrefix = "";

            string text = textBox.Text;
            if (suppressedText != null && text != suppressedText)
                suppressedText = null;

            // quality words ("gold star aplle") are not part of the item: the drop-down and "Did you mean" only look at the rest ("aplle"), and a
            // picked word keeps the quality in front of it ("gold star Apple")
            text = ExtractQuality(text, out typedQuality, out _, out string qualityPhrase);
            string qualityLead = qualityPhrase.Length > 0 ? qualityPhrase + " " : "";

            string query = text.Trim();
            if (getSuggestions == null || query.Length == 0)
            {
                suggestions = Array.Empty<Suggestion>();
                return;
            }

            string prefix = "";
            string term = query;
            IReadOnlyList<Suggestion> found = Array.Empty<Suggestion>();

            int comma = text.LastIndexOfAny(ModEntry.QueryCommas);   // half-width or full-width
            if (comma >= 0)
            {
                prefix = text.Substring(0, comma + 1).TrimStart() + " ";
                term = text.Substring(comma + 1).Trim();
                if (term.Length > 0)
                    found = getSuggestions(term);
            }
            else
            {
                found = getSuggestions(query);

                int space = query.LastIndexOfAny(SpaceChars);
                if (found.Count == 0 && space >= 0)
                {
                    prefix = query.Substring(0, space + 1);
                    term = query.Substring(space + 1);
                    found = getSuggestions(term);
                }
            }

            // NOTE: a list whose only row equals the typed text ("apple wine" -> "Apple Wine") is KEPT. An older version removed it as "noise", which
            // made the drop-down vanish the moment the last letter of a name was typed (it looked like the list disappeared at random). As long as
            // anything matches, the drop-down stays open; it only closes when nothing matches, or when it was closed on purpose (suppressedText).

            // a closed drop-down shows no rows, but `found` still decides below whether the word exists (no row = maybe a typo)
            suggestions = suppressedText != null ? Array.Empty<Suggestion>() : found;
            suggestionPrefix = qualityLead + prefix;

            // Instant correction: the word being typed has no auto-complete row. If nothing in the game matches it at all (a typo like "frish"), the
            // closest names are offered in a "Did you mean" line under the box (no drop-down, no red hint). A word that exists, even half typed, gets nothing.
            if (found.Count == 0 && getSpelling != null && term.Length > 0)
            {
                try
                {
                    IReadOnlyList<string> names = Array.Empty<string>();
                    string namesPrefix = prefix;

                    // several words typed without a comma ("iridium sprinkleh"): the whole phrase is corrected first, as one name, so the words that are
                    // already right are part of the match and a click replaces the whole text. Only if that finds nothing, the last word is corrected alone.
                    if (comma < 0 && prefix.Length > 0)
                    {
                        names = getSpelling(query);
                        namesPrefix = "";
                    }

                    if (names.Count == 0)
                    {
                        names = getSpelling(term);
                        namesPrefix = prefix;
                    }

                    if (names.Count > 0)
                    {
                        spellingWords.AddRange(names);
                        spellingPrefix = qualityLead + namesPrefix;
                    }
                }
                catch (Exception)
                {
                    spellingWords.Clear();   // no line rather than a crash
                }
            }
        }

        // ===================== INSTANT CORRECTION LINE ("Did you mean: Fish, Catfish") =====================
        // Sits in the free line between the text box and the key hint line (the spot of the red hints and the quiet tip, which take its place), in the same
        // blue, underlined, link-like words as the "Did you mean" row of the result panel. The words show NO "(category)" label (its hover tip says it).
        // Clicking a word only puts it into the text box (in place of the misspelled word, the words before it stay): it NEVER searches, and it keeps the
        // drop-down closed. The player presses Enter when they are ready.

        private const string SpellingLead = "Did you mean: ";
        private const int SpellingRowOffsetY = 166;   // same line as the red hints and the tip
        private static readonly Color SpellingColor = new(32, 84, 160);
        private static readonly Color SpellingHoverColor = new(20, 125, 225);

        /// <summary>The line is shown while it has words, no hint needs the spot, and the drop-down is not open.</summary>
        private bool SpellingVisible =>
            ticks >= 3
            && spellingWords.Count > 0
            && !showTooShortHint
            && !showNoQualityHint
            && !SuggestionsVisible
            && textBox.Text.Trim().Length > 0;

        /// <summary>Where each word of the line is: the text shown (without the label, shortened if it does not fit), the name behind it, and its screen rectangle.</summary>
        private List<(string Label, string Name, Rectangle Bounds)> LayoutSpelling()
        {
            var chips = new List<(string Label, string Name, Rectangle Bounds)>();
            if (spellingWords.Count == 0)
                return chips;

            SpriteFont font = Game1.smallFont;
            int left = xPositionOnScreen + 48;
            int top = yPositionOnScreen + SpellingRowOffsetY;
            int right = left + width - 96;
            int commaWidth = (int)Math.Ceiling(font.MeasureString(", ").X);
            int cursor = left + (int)Math.Ceiling(font.MeasureString(SpellingLead).X);

            // A word is only ever shown COMPLETE. The words come best first, so a word that does not fit into what is left of the line is simply left
            // out (it is a weaker match than the ones before it): better two whole words than a third one cut to "Mushroom Tre...". A later, shorter
            // word may still take its place. Only a first word that is wider than the whole line (never with the normal dialog width) is shortened.
            foreach (string name in spellingWords)
            {
                string label = ModEntry.StripCategorySuffix(name);
                int separator = chips.Count > 0 ? commaWidth : 0;
                int room = right - cursor - separator;
                int fullWidth = (int)Math.Ceiling(font.MeasureString(label).X);

                if (chips.Count > 0 && fullWidth > room)
                    continue;   // would be cut: left out

                string shown = chips.Count == 0 && fullWidth > room ? FitLabel(label, Math.Max(40, room)) : label;
                int wordWidth = (int)Math.Ceiling(font.MeasureString(shown).X);

                chips.Add((shown, name, new Rectangle(cursor + separator, top, wordWidth, font.LineSpacing)));
                cursor += separator + wordWidth;
            }

            return chips;
        }

        private static Rectangle SpellingHitBox(Rectangle bounds)
        {
            bounds.Inflate(4, 2);
            return bounds;
        }

        private void DrawSpelling(SpriteBatch b)
        {
            SpriteFont font = Game1.smallFont;
            var chips = LayoutSpelling();
            if (chips.Count == 0)
                return;

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            Utility.drawTextWithShadow(b, SpellingLead, font, new Vector2(xPositionOnScreen + 48, yPositionOnScreen + SpellingRowOffsetY), Game1.textColor);

            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                bool hovered = SpellingHitBox(chip.Bounds).Contains(mouseX, mouseY);
                Color color = hovered ? SpellingHoverColor : SpellingColor;

                Utility.drawTextWithShadow(b, chip.Label, font, new Vector2(chip.Bounds.X, chip.Bounds.Y), color);
                b.Draw(Game1.staminaRect, new Rectangle(chip.Bounds.X, chip.Bounds.Bottom - 4, chip.Bounds.Width, hovered ? 3 : 2), color * (hovered ? 1f : 0.7f));

                if (i < chips.Count - 1)
                    Utility.drawTextWithShadow(b, ", ", font, new Vector2(chip.Bounds.Right, chip.Bounds.Y), Game1.textColor);
            }
        }

        /// <summary>The hover tip of the word of the line under the mouse (the same tip rules as everywhere, see ModEntry.GetTermTip), or null.</summary>
        private string? GetSpellingTipAt(int x, int y)
        {
            if (getWordTip == null || !SpellingVisible)
                return null;

            foreach (var chip in LayoutSpelling())
            {
                if (SpellingHitBox(chip.Bounds).Contains(x, y))
                    return GetWordTipCached(chip.Name);
            }

            return null;
        }

        /// <summary>A click on a word of the line: it goes into the text box and nothing else happens (no search, no drop-down). True = the click was on a word.</summary>
        private bool TryApplySpellingClick(int x, int y)
        {
            if (!SpellingVisible)
                return false;

            foreach (var chip in LayoutSpelling())
            {
                if (!SpellingHitBox(chip.Bounds).Contains(x, y))
                    continue;

                textBox.Text = spellingPrefix + ModEntry.StripCategorySuffix(chip.Name);   // the caret goes to the end
                lastText = textBox.Text;      // update() must not take this for an edit: no new drop-down, no new line
                showTooShortHint = false;
                showNoQualityHint = false;
                SuppressSuggestions();        // closes the drop-down for this text; the spelling line is checked again for the new text
                Game1.playSound("smallSelect");
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return true;
            }

            return false;
        }

        /// <summary>Hover tip of a category word ("Roe (category)"): the same text as everywhere else (result title, search history).</summary>
        private const string CategoryTipText = ModEntry.CategoryWordTip;

        /// <summary>Gap between the drop-down and its hover tip.</summary>
        private const int SuggestionTipGap = 12;

        private static readonly Dictionary<string, Item?> suggestionIconCache = new();

        /// <summary>The item whose real icon is drawn in front of a suggestion, or null (category words, unknown names, category ids). Created once per item id.</summary>
        private static Item? GetSuggestionIconItem(Suggestion suggestion)
        {
            if (suggestion.IsCategory)
                return null;

            string? qualifiedId = suggestion.QualifiedId;
            if (qualifiedId == null && suggestion.IdLabel != null
                && int.TryParse(suggestion.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) && number >= 0)
                qualifiedId = "(O)" + number.ToString(CultureInfo.InvariantCulture);

            if (qualifiedId == null)
                return null;

            if (suggestionIconCache.TryGetValue(qualifiedId, out Item? cached))
                return cached;

            Item? item = null;
            try
            {
                if (ItemRegistry.GetData(qualifiedId) != null)
                    item = ItemRegistry.Create(qualifiedId);
            }
            catch (Exception)
            {
                item = null;   // no icon rather than a crash
            }

            suggestionIconCache[qualifiedId] = item;
            return item;
        }

        /// <summary>
        /// The text of the hover tip of one suggestion, or null for none:
        ///   a category word                 -> "Showing all items under this category" (also a negative category id like -79)
        ///   an item, or a name the game builds on the fly ("Peach Wine") -> "Category: " + the game's own category name of the item, or of its base
        ///                                      product for a built name (Peach Wine -> Artisan Goods)
        ///   anything whose item is unknown  -> none
        /// The category name is the one the game shows on item tooltips (so it follows the game language). Results are cached.
        /// </summary>
        private static string? GetSuggestionTip(Suggestion suggestion)
        {
            if (suggestion.IsCategory)
                return CategoryTipText;

            string? qualifiedId = suggestion.QualifiedId;
            if (qualifiedId == null && suggestion.IdLabel != null && int.TryParse(suggestion.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            {
                if (number < 0)
                    return CategoryTipText;   // a category id
                qualifiedId = "(O)" + number.ToString(CultureInfo.InvariantCulture);
            }

            if (qualifiedId == null)
                return null;

            string cacheKey = qualifiedId + "|" + LocalizedContentManager.CurrentLanguageCode;
            if (categoryTipCache.TryGetValue(cacheKey, out string? cached))
                return cached ?? ViaCategoryTip(suggestion);

            string? tip = null;
            try
            {
                if (ItemRegistry.GetData(qualifiedId) != null)
                {
                    string? category = ModEntry.GetCategoryNameForItemId(qualifiedId);   // the one category rule, shared with every other tip
                    if (!string.IsNullOrEmpty(category))
                        tip = "Category: " + category;
                }
            }
            catch (Exception)
            {
                tip = null;   // no tip rather than a crash
            }

            categoryTipCache[cacheKey] = tip;
            return tip ?? ViaCategoryTip(suggestion);
        }

        /// <summary>Hover tip of a "popular item of a category" row when the game gives no category name of its own: the category the row was added for.</summary>
        private static string? ViaCategoryTip(Suggestion suggestion)
        {
            return string.IsNullOrWhiteSpace(suggestion.ViaCategory) ? null : "Category: " + suggestion.ViaCategory;
        }

        /// <summary>
        /// Draws the hover tip of the highlighted suggestion with the game's own hover box, to the RIGHT of the drop-down (level with its row), so it never
        /// covers another row. If there is no room on the right it goes just below the drop-down, and if there is none there either, above it.
        /// The game places a hover box 32 px right of / below the old mouse position plus the offsets, so the offsets are worked out from the wanted
        /// top left corner (the same way the "Keep highlights" tip does it).
        /// </summary>
        private void DrawSuggestionTip(SpriteBatch b)
        {
            if (suggestionTipText == null || !SuggestionsVisible)
                return;

            Rectangle list = GetSuggestionBounds();
            Vector2 size = Game1.smallFont.MeasureString(suggestionTipText);
            int tipWidth = (int)Math.Ceiling(size.X) + 48;    // text + the box frame (a little more than the game uses, to stay on the safe side)
            int tipHeight = (int)Math.Ceiling(size.Y) + 40;

            int x = list.Right + SuggestionTipGap;
            int y = suggestionTipRowTop;

            if (x + tipWidth > Game1.uiViewport.Width - 8)
            {
                // no room on the right: below the list, left edges level
                x = list.X;
                y = list.Bottom + SuggestionTipGap;

                if (y + tipHeight > Game1.uiViewport.Height - 8)
                    y = Math.Max(8, list.Y - SuggestionTipGap - tipHeight);   // no room below either: above the list
            }

            if (y + tipHeight > Game1.uiViewport.Height - 8)
                y = Math.Max(8, Game1.uiViewport.Height - 8 - tipHeight);

            IClickableMenu.drawHoverText(
                b,
                suggestionTipText,
                Game1.smallFont,
                xOffset: x - (Game1.getOldMouseX() + 32),
                yOffset: y - (Game1.getOldMouseY() + 32));
        }

        /// <summary>
        /// The drop-down: the game's own parchment box (same as the star button and the result panel), one row per suggestion.
        /// The part the player already typed is drawn softer, the part that would be added in full color.
        /// </summary>
        private void DrawSuggestions(SpriteBatch b)
        {
            Rectangle box = GetSuggestionBounds();
            int rows = VisibleSuggestionRows;
            int rowHeight = SuggestionRowHeight;
            int reserve = ScrollbarReserve;

            // keep the window valid even if the list or the screen size changed since the last frame
            suggestionScroll = Math.Clamp(suggestionScroll, 0, MaxSuggestionScroll);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), box.X, box.Y, box.Width, box.Height, Color.White);

            // the hovered row wins over the arrow-key row (Enter still uses only the arrow-key row); no row hover while the knob is being dragged
            int hoveredRow = draggingScrollbar ? -1 : GetSuggestionRowAt(Game1.getMouseX(), Game1.getMouseY());
            int highlighted = hoveredRow >= 0 ? suggestionScroll + hoveredRow : activeSuggestion;

            // the hover tip belongs to the row that is highlighted (the mouse wins over the arrow keys, same as the highlight); only a visible row has one
            suggestionTipText = null;
            if (!draggingScrollbar && highlighted >= suggestionScroll && highlighted < suggestionScroll + rows && highlighted < suggestions.Count)
            {
                suggestionTipText = getWordTip != null ? GetWordTipCached(suggestions[highlighted].Text) : GetSuggestionTip(suggestions[highlighted]);
                suggestionTipRowTop = box.Y + SuggestionPadY + (highlighted - suggestionScroll) * rowHeight;
            }

            for (int i = 0; i < rows; i++)
            {
                int index = suggestionScroll + i;
                var row = new Rectangle(box.X + SuggestionPadX, box.Y + SuggestionPadY + i * rowHeight, box.Width - SuggestionPadX * 2 - reserve, rowHeight);

                if (index == highlighted)
                    b.Draw(Game1.staminaRect, row, Game1.textColor * 0.16f);

                Suggestion suggestion = suggestions[index];
                var pos = new Vector2(row.X + SuggestionRowInset + SuggestionIconSlot, ModEntry.CenteredTextY(Game1.smallFont, row.Y, rowHeight));

                // the real icon of the item (a category word has none; its room stays empty so all names line up), centered on the row like the text
                Item? icon = GetSuggestionIconItem(suggestion);
                if (icon != null)
                    ModEntry.DrawItemIcon(b, icon, row.X + SuggestionRowInset, row.Y + (rowHeight - SuggestionIconSize) / 2, SuggestionIconSize);
                const string CategoryTag = " (category)";

                // the tag after the word: " (category)" for a category word, " (Starfruit)" / " (fruit)" (the name of the id) for an id suggestion
                string? tag = suggestion.IsCategory ? CategoryTag : null;

                // A "popular item of the category" row (typing "fish" offers Sturgeon) shows ONLY the item name: no " (Fish)" / " (Litter)" after it.
                // The category it belongs to is shown in the hover tip (see GetSuggestionTip).

                if (suggestion.IdLabel != null)
                {
                    int numberWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(suggestion.Text).X);
                    int bracketsWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(" ()").X);
                    tag = " (" + FitLabel(suggestion.IdLabel, Math.Max(40, row.Width - SuggestionRowInset * 2 - SuggestionIconSlot - numberWidth - bracketsWidth)) + ")";
                }

                float tagWidth = tag != null ? Game1.smallFont.MeasureString(tag).X : 0f;
                string label = FitLabel(suggestion.Text, row.Width - SuggestionRowInset * 2 - SuggestionIconSlot - (int)Math.Ceiling(tagWidth));

                if (label == suggestion.Text && suggestion.MatchedLength > 0 && suggestion.MatchedLength < suggestion.Text.Length)
                {
                    string typed = suggestion.Text.Substring(0, suggestion.MatchedLength);
                    string rest = suggestion.Text.Substring(suggestion.MatchedLength);

                    Utility.drawTextWithShadow(b, typed, Game1.smallFont, pos, Game1.textColor * 0.6f);
                    Utility.drawTextWithShadow(b, rest, Game1.smallFont, new Vector2(pos.X + Game1.smallFont.MeasureString(typed).X, pos.Y), Game1.textColor);
                }
                else
                {
                    Utility.drawTextWithShadow(b, label, Game1.smallFont, pos, Game1.textColor);
                }

                // a category word gets a quiet "(category)" after it, an id gets its name in brackets (a little stronger, it is the point of the row);
                // this is display only: the row still searches / fills in the plain word or number
                if (tag != null)
                    Utility.drawTextWithShadow(b, tag, Game1.smallFont, new Vector2(pos.X + Game1.smallFont.MeasureString(label).X, pos.Y), Game1.textColor * (suggestion.IdLabel != null ? 0.8f : 0.6f));
            }

            // the scrollbar: the game's slider-track box as the track, the game's own 6x10 knob on it
            if (NeedsScrollbar)
            {
                Rectangle runner = GetScrollbarRunner();
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, ScrollbarTrackSource, runner.X, runner.Y, runner.Width, runner.Height, Color.White, 4f, false);

                Rectangle thumb = GetScrollbarThumb();
                b.Draw(Game1.mouseCursors, thumb, ScrollbarThumbSource, Color.White);
            }
        }

        /// <summary>The history block is drawn while the search box is empty (and the menu was opened with a history).</summary>
        private bool HistoryAreaVisible => extraHeight > 0 && textBox.Text.Length == 0;

        /// <summary>The quiet getting-started tip under the text box.</summary>
        private const string TipText = "Tip: Try 'starfruit' or 'fruit (category)'";

        /// <summary>
        /// The tip shows whenever the text box is completely empty, with or without a history (it sits between the text box and the
        /// history block, which has room for it), unless a red hint line is using the same spot. It is display only: no click, no hover.
        /// </summary>
        private bool TipVisible => textBox.Text.Length == 0 && !showTooShortHint && !showNoQualityHint;

        /// <summary>The words and the Clear History button can be clicked only while there is something to pick or clear.</summary>
        private bool HistoryVisible => HistoryAreaVisible && history.Count > 0;

        private int FooterY => yPositionOnScreen + 256 + extraHeight;

        // The key hint line reads "[star] Enter to search    [star] Esc to cancel" ("=" is drawn as a star by the game's font).
        // Every part (star, Enter, to, search, star, Esc, to, cancel) is measured with SpriteFont.MeasureString and placed on its own,
        // see GetFooterLayout, so the spaces between them are exact instead of whatever a space character happens to be.

        /// <summary>Clear space between the outer edge of a word's circle and the text next to it (the star before, "to" after).</summary>
        private const int FooterCircleClearance = 9;

        /// <summary>The clearance only shrinks (down to this) if the whole line would otherwise be wider than the menu.</summary>
        private const int FooterMinCircleClearance = 4;

        /// <summary>Added to the width of a normal space between "to" and "search" / "cancel".</summary>
        private const int FooterWordGapExtra = 3;

        /// <summary>Where each part of the key hint line starts, in pixels from the left edge of the line.</summary>
        private struct FooterLayout
        {
            public float Star1, Enter, To1, Search, Star2, Esc, To2, Cancel;
        }

        private FooterLayout? footerLayout;
        private SpriteFont? footerLayoutFont;

        /// <summary>The key hint line sits a few pixels lower than <see cref="FooterY"/>, so it does not crowd the wooden frame.</summary>
        private const int FooterTextNudge = 3;

        // the hand-drawn circle around a clickable word: extra room left and right of the word, and where its box starts
        // relative to the top of the text line (negative = a little above it, because the circle also wraps the capital letters)
        private const int FooterFramePadX = 8;
        private const int FooterFrameTop = -2;

        // colors of the two links; the word and its circle always share one color (a little deeper and calmer than pure signal colors,
        // but still clean, not muddy). The color never changes with the mouse.
        private static readonly Color FooterGreen = new(6, 130, 34);           // Enter
        private static readonly Color FooterRed = new(202, 32, 30);            // Esc

        // fill inside the circle while the mouse is over a word: a light see-through green / red, so the orange menu background shows through
        private static readonly Color FooterGreenFill = new Color(86, 198, 100) * 0.6f;
        private static readonly Color FooterRedFill = new Color(255, 120, 115) * 0.6f;

        // knockout gap: a thin rim in the box's own color around each word, drawn over the circle, so the pen line stops short of the letters.
        // The dialog box is a vertical gradient (lighter at the top), so the rim color is looked up for the row the words sit on.
        private const int FooterKnockoutRadius = 2;
        private static readonly Color BoxFillTop = new(255, 203, 125);      // measured on the real dialog box; also the fallback
        private static readonly Color BoxFillBottom = new(235, 167, 104);
        private static Color[]? boxFillColumn;
        private static Texture2D? boxFillSource;

        private int FooterTextY => FooterY + FooterTextNudge;

        /// <summary>The key hint line is hidden while the drop-down covers it (and then it cannot be clicked either).</summary>
        private bool FooterVisible => !SuggestionsVisible || GetSuggestionBounds().Bottom <= FooterY - 6;

        /// <summary>The box of the hand-drawn circle around "Enter" (the circle is the ellipse inside this box).</summary>
        private Rectangle FooterEnterBounds => FooterWordBounds("Enter", GetFooterLayout().Enter);

        /// <summary>The box of the hand-drawn circle around "Esc" (the circle is the ellipse inside this box).</summary>
        private Rectangle FooterEscBounds => FooterWordBounds("Esc", GetFooterLayout().Esc);

        /// <summary>
        /// Measures every part of the key hint line with <see cref="SpriteFont.MeasureString(string)"/> and places them one after the other:
        ///   star, gap, [circle around Enter], gap, "to", space, "search", wide gap, star, gap, [circle around Esc], gap, "to", space, "cancel".
        /// The gap on each side of a circled word is the circle's own padding plus <see cref="FooterCircleClearance"/>, so the circle never
        /// touches the star or "to". If the line would not fit into the menu, the clearance shrinks pixel by pixel. All places are whole pixels.
        /// </summary>
        private FooterLayout GetFooterLayout()
        {
            SpriteFont font = Game1.smallFont;
            if (footerLayout.HasValue && ReferenceEquals(footerLayoutFont, font))
                return footerLayout.Value;

            float Measure(string text) => font.MeasureString(text).X;

            float star = Measure("=");
            float enter = Measure("Enter") + 1;   // +1: the bold copy of a word is one pixel wider
            float esc = Measure("Esc") + 1;
            float to = Measure("to");
            float search = Measure("search");
            float cancel = Measure("cancel");
            float wordGap = Measure(" ") + FooterWordGapExtra;
            float groupGap = Measure("    ");
            float available = width - 96;

            var layout = new FooterLayout();
            for (int clearance = FooterCircleClearance; clearance >= FooterMinCircleClearance; clearance--)
            {
                float link = FooterFramePadX + clearance;
                float x = 0f;

                layout.Star1 = x; x += star + link;
                layout.Enter = x; x += enter + link;
                layout.To1 = x; x += to + wordGap;
                layout.Search = x; x += search + groupGap;
                layout.Star2 = x; x += star + link;
                layout.Esc = x; x += esc + link;
                layout.To2 = x; x += to + wordGap;
                layout.Cancel = x; x += cancel;

                if (x <= available)
                    break;
            }

            layout.Star1 = MathF.Round(layout.Star1);
            layout.Enter = MathF.Round(layout.Enter);
            layout.To1 = MathF.Round(layout.To1);
            layout.Search = MathF.Round(layout.Search);
            layout.Star2 = MathF.Round(layout.Star2);
            layout.Esc = MathF.Round(layout.Esc);
            layout.To2 = MathF.Round(layout.To2);
            layout.Cancel = MathF.Round(layout.Cancel);

            footerLayout = layout;
            footerLayoutFont = font;
            return layout;
        }

        /// <summary>The box in which the hand-drawn circle of a word is drawn: the word, plus <see cref="FooterFramePadX"/> on each side.</summary>
        private Rectangle FooterWordBounds(string word, float wordOffsetX)
        {
            float wordWidth = Game1.smallFont.MeasureString(word).X;
            int height = Math.Max(30, (int)(Game1.smallFont.LineSpacing * 0.95f));   // tall enough to wrap the capital letters

            return new Rectangle(
                xPositionOnScreen + 48 + (int)wordOffsetX - FooterFramePadX,
                FooterTextY + FooterFrameTop,
                (int)wordWidth + 1 + FooterFramePadX * 2,   // +1: the bold copy of the word is one pixel wider
                height);
        }

        /// <summary>
        /// The key hint line. The plain parts (the stars, "to search", "to cancel") have the game's usual text shadow.
        /// "Enter" and "Esc" are drawn as links: a bold word circled by a hand-drawn looking pen stroke. "Enter" is green and
        /// "Esc" red, circle and word alike, from the start. The words are in normal weight; while the mouse is over one it turns
        /// bold (and gets the game's text shadow, as before). While the mouse is over it, the whole inside of the circle
        /// is filled with a see-through light highlighter color (bottom layer), the circle is drawn on it, and the word lies directly
        /// on the fill: bold, with no shadow and no knockout.
        /// </summary>
        private void DrawFooter(SpriteBatch b)
        {
            SpriteFont font = Game1.smallFont;
            float left = xPositionOnScreen + 48;
            float top = FooterTextY;

            Rectangle enterCircle = FooterEnterBounds;
            Rectangle escCircle = FooterEscBounds;
            bool overEnter = IsOverFooterWord(enterCircle, Game1.getMouseX(), Game1.getMouseY());
            bool overEsc = IsOverFooterWord(escCircle, Game1.getMouseX(), Game1.getMouseY());

            FooterLayout layout = GetFooterLayout();

            Utility.drawTextWithShadow(b, "=", font, new Vector2(left + layout.Star1, top), Game1.textColor);
            Utility.drawTextWithShadow(b, "to", font, new Vector2(left + layout.To1, top), Game1.textColor);
            Utility.drawTextWithShadow(b, "search", font, new Vector2(left + layout.Search, top), Game1.textColor);
            Utility.drawTextWithShadow(b, "=", font, new Vector2(left + layout.Star2, top), Game1.textColor);
            Utility.drawTextWithShadow(b, "to", font, new Vector2(left + layout.To2, top), Game1.textColor);
            Utility.drawTextWithShadow(b, "cancel", font, new Vector2(left + layout.Cancel, top), Game1.textColor);

            Color enterColor = FooterGreen;
            Color escColor = FooterRed;

            Color gapColor = GetBoxFillAt(FooterTextY + font.LineSpacing / 2);

            // layer order, bottom to top. At rest: circle -> knockout gap -> word. While the mouse is over a word: solid fill -> circle -> knockout -> bold word.
            // The hovered knockout is drawn in the fill's own look (the fill over the box color), so it only cuts the circle line away from the
            // letters and leaves no visible rim between the word and the fill (see enterKnockout / escKnockout below and DrawFooterWord).
            // The fill colors are see-through, so the box color shows through.
            if (overEnter)
                DrawCircleFill(b, enterCircle, FooterGreenFill, 1);
            if (overEsc)
                DrawCircleFill(b, escCircle, FooterRedFill, 4);

            DrawHandDrawnCircle(b, enterCircle, enterColor, 1);
            DrawHandDrawnCircle(b, escCircle, escColor, 4);
            // While hovered, the knockout rim must look exactly like the fill around it (so the word lies tight on the fill, with no halo),
            // but it still cuts the circle line away from the letters. So the rim gets the fill laid over the box color, as one solid color.
            Color enterKnockout = overEnter ? CompositeOver(FooterGreenFill, gapColor) : gapColor;
            Color escKnockout = overEsc ? CompositeOver(FooterRedFill, gapColor) : gapColor;

            DrawFooterWord(b, "Enter", new Vector2(left + layout.Enter, top), overEnter, overEnter, enterColor, enterKnockout);
            DrawFooterWord(b, "Esc", new Vector2(left + layout.Esc, top), overEsc, overEsc, escColor, escKnockout);
        }

        /// <summary>
        /// What a see-through <paramref name="fill"/> (as the SpriteBatch draws it: color already multiplied by its alpha) looks like laid over
        /// an opaque <paramref name="background"/>, as one solid color.
        /// </summary>
        private static Color CompositeOver(Color fill, Color background)
        {
            float keep = 1f - fill.A / 255f;
            return new Color(
                Math.Min(255, (int)MathF.Round(fill.R + background.R * keep)),
                Math.Min(255, (int)MathF.Round(fill.G + background.G * keep)),
                Math.Min(255, (int)MathF.Round(fill.B + background.B * keep)),
                255);
        }

        /// <summary>
        /// A clickable word, always the top layer. Normal weight at rest; bold while the mouse is over it (the game's pixel font has
        /// no bold version, so the word is drawn twice, one pixel apart). Under it, from the bottom up:
        ///   1. knockout gap (not while pressed): the word in the box's own color, moved to every spot within <see cref="FooterKnockoutRadius"/> pixels
        ///      (a square, so the edge is as straight as the pixel font), in the same weight as the word on top. This paints a thin
        ///      rim around the letters over the circle, so the pen line breaks off a hair away from the word.
        /// The word has no shadow until the mouse is over it, then it gets the game's normal text shadow. While the left button is held
        /// down it is bold with no shadow at all, so the letters stay crisp on top of the pressed fill.
        /// </summary>
        private static void DrawFooterWord(SpriteBatch b, string word, Vector2 position, bool hovered, bool pressed, Color color, Color gapColor)
        {
            // knockout gap, also while hovered: gapColor is the box color at rest, and the fill-over-box color while hovered,
            // so the rim cuts the circle line away from the letters but is invisible against the fill
            DrawWordCopies(b, word, position, FooterKnockoutRadius, gapColor, bold: hovered, square: true);

            if (pressed || hovered)
            {
                // bold, no shadow, no glow: only the letters themselves, drawn twice one pixel apart
                b.DrawString(Game1.smallFont, word, position, color);
                b.DrawString(Game1.smallFont, word, position + new Vector2(1f, 0f), color);
            }
            else
            {
                b.DrawString(Game1.smallFont, word, position, color);
            }
        }

        /// <summary>
        /// Draws a word many times, each copy moved by every offset inside a small square (or disc): the result is the word
        /// thickened by <paramref name="radius"/> pixels. With <paramref name="bold"/> every copy is drawn twice, one pixel apart,
        /// so the thickened shape matches a bold word exactly.
        /// </summary>
        private static void DrawWordCopies(SpriteBatch b, string word, Vector2 position, int radius, Color color, bool bold, bool square)
        {
            int limit = radius * radius + 1;   // disc only: +1 rounds the corners

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (!square && dx * dx + dy * dy > limit)
                        continue;

                    Vector2 p = position + new Vector2(dx, dy);
                    b.DrawString(Game1.smallFont, word, p, color);
                    if (bold)
                        b.DrawString(Game1.smallFont, word, p + new Vector2(1f, 0f), color);
                }
            }
        }

        /// <summary>
        /// The fill color of the dialog box at one screen row. Game1.drawDialogueBox stretches a 64x64 piece of the menu tiles over the
        /// inside of the box (28px in from every side), and that piece is a vertical gradient, so the color depends on the row.
        /// </summary>
        private Color GetBoxFillAt(int screenY)
        {
            float t = Math.Clamp((screenY - (yPositionOnScreen + 28)) / (float)Math.Max(1, height - 64), 0f, 1f);

            Color[]? column = GetBoxFillColumn();
            if (column != null)
                return column[Math.Min(column.Length - 1, (int)(t * column.Length))];

            return Color.Lerp(BoxFillTop, BoxFillBottom, t);
        }

        /// <summary>
        /// One pixel column of that gradient, read from the game's own texture (so it follows recolor mods). It is only trusted when its two
        /// ends look like the orange box color; otherwise null, and <see cref="GetBoxFillAt"/> uses the measured gradient instead.
        /// </summary>
        private static Color[]? GetBoxFillColumn()
        {
            Texture2D source = Game1.menuTexture;
            if (ReferenceEquals(boxFillSource, source))
                return boxFillColumn;

            boxFillSource = source;
            boxFillColumn = null;

            try
            {
                var column = new Color[64];
                source.GetData(0, new Rectangle(64 + 32, 128, 1, 64), column, 0, column.Length);

                if (IsCloseColor(column[0], BoxFillTop) && IsCloseColor(column[63], BoxFillBottom))
                    boxFillColumn = column;
            }
            catch (Exception)
            {
                // keep the measured gradient
            }

            return boxFillColumn;
        }

        private static bool IsCloseColor(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) <= 16 && Math.Abs(a.G - b.G) <= 16 && Math.Abs(a.B - b.B) <= 16;
        }

        /// <summary>True if a point is inside the circle drawn around a word. The test is an ellipse a little wider than the drawn line, so the word is easy to hit.</summary>
        private static bool IsOverFooterWord(Rectangle circleBox, int x, int y)
        {
            float dx = (x - circleBox.Center.X) / (circleBox.Width / 2f);
            float dy = (y - circleBox.Center.Y) / (circleBox.Height / 2f);
            return dx * dx + dy * dy <= 1.3f;
        }

        /// <summary>
        /// One point of the hand-drawn circle's center line, for t from 0 (pen touches down) to 1 (pen lifts off), used by
        /// <see cref="DrawHandDrawnCircle"/>. Also returns the angle of the point (the stroke width ripple uses it).
        /// </summary>
        private static Vector2 GetHandDrawnCirclePoint(Rectangle box, float t, int seed, out float angle)
        {
            const float startAngle = -2.5f;                // radians: upper left
            const float sweep = 2f * 3.14159265f * 1.06f;  // a bit more than one full turn

            float centerX = box.X + box.Width / 2f;
            float centerY = box.Y + box.Height / 2f;
            float radiusX = box.Width / 2f - 1.5f;    // the thick middle of the stroke stays inside the box
            float radiusY = box.Height / 2f - 1.5f;

            angle = startAngle + t * sweep;

            float scale = 0.93f + 0.12f * t                              // spiral: starts a bit inside, ends a bit outside
                        + 0.035f * MathF.Sin(angle * 3f + seed)          // slow wobbles of the radius
                        + 0.025f * MathF.Sin(angle * 5f + seed * 2.3f);

            return new Vector2(
                centerX + radiusX * scale * MathF.Cos(angle),
                centerY + radiusY * scale * MathF.Sin(angle));
        }

        /// <summary>Pen width (1 to 3 pixels) of the hand-drawn circle at one point of its stroke. Shared by the stroke and the fill.</summary>
        private static int GetHandDrawnCircleThickness(float angle, float t, int seed)
        {
            float ripple = 0.30f * MathF.Sin(angle * 9f + seed * 1.7f) + 0.20f * MathF.Sin(angle * 17f + seed * 0.6f);
            return Math.Clamp(1 + (int)MathF.Floor(MathF.Sin(3.14159265f * t) * 2.4f + ripple), 1, 3);   // 1 .. 3 pixels
        }

        /// <summary>One cached fill mask per circle size and seed (there are only two circles, so this stays tiny).</summary>
        private static readonly Dictionary<(int Width, int Height, int Seed), Texture2D> circleFillTextures = new();

        /// <summary>Empty room around the circle's box inside a fill texture (the wobbling pen line can lie a little outside the box).</summary>
        private const int CircleFillPad = 4;

        /// <summary>
        /// The pressed/hover fill: the inside of the hand-drawn circle as ONE solid shape, drawn with a single SpriteBatch.Draw. The shape
        /// is built from the same path as <see cref="DrawHandDrawnCircle"/> (see <see cref="GetHandDrawnCirclePoint"/>), so it has exactly
        /// the circle's outline and never lies outside the pen line. Because it is one quad, a see-through color tints evenly: no seams,
        /// no darker stripes. Bottom layer: draw it BEFORE the stroke and the word.
        /// </summary>
        private static void DrawCircleFill(SpriteBatch b, Rectangle box, Color color, int seed)
        {
            Texture2D texture = GetCircleFillTexture(b.GraphicsDevice, box.Width, box.Height, seed);
            b.Draw(texture, new Vector2(box.X - CircleFillPad, box.Y - CircleFillPad), color);
        }

        /// <summary>
        /// Builds (once per size) a white mask of the circle's inside: the center line of the pen over one full turn is the outline, and each
        /// row is filled from half a pixel before the left line to half a pixel after the right line, so the mask ends under the pen line.
        /// </summary>
        private static Texture2D GetCircleFillTexture(GraphicsDevice device, int boxWidth, int boxHeight, int seed)
        {
            var key = (boxWidth, boxHeight, seed);
            if (circleFillTextures.TryGetValue(key, out Texture2D? cached) && !cached.IsDisposed && cached.GraphicsDevice == device)
                return cached;

            int texWidth = boxWidth + CircleFillPad * 2;
            int texHeight = boxHeight + CircleFillPad * 2;
            var localBox = new Rectangle(CircleFillPad, CircleFillPad, boxWidth, boxHeight);

            const int points = 160;
            const float oneTurn = 1f / 1.06f;   // t at which the stroke has gone around exactly once

            var outline = new Vector2[points];
            for (int i = 0; i < points; i++)
                outline[i] = GetHandDrawnCirclePoint(localBox, i / (float)points * oneTurn, seed, out _);

            var data = new Color[texWidth * texHeight];   // all transparent
            var crossings = new List<float>();

            for (int y = 0; y < texHeight; y++)
            {
                float scanY = y + 0.5f;
                crossings.Clear();

                for (int i = 0; i < points; i++)
                {
                    Vector2 p0 = outline[i];
                    Vector2 p1 = outline[(i + 1) % points];
                    if ((p0.Y <= scanY) == (p1.Y <= scanY))
                        continue;

                    crossings.Add(p0.X + (scanY - p0.Y) / (p1.Y - p0.Y) * (p1.X - p0.X));
                }

                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int x0 = Math.Max(0, (int)MathF.Round(crossings[k] - 0.5f));
                    int x1 = Math.Min(texWidth, (int)MathF.Round(crossings[k + 1] + 0.5f));
                    for (int x = x0; x < x1; x++)
                        data[y * texWidth + x] = Color.White;
                }
            }

            var texture = new Texture2D(device, texWidth, texHeight);
            texture.SetData(data);
            circleFillTextures[key] = texture;
            return texture;
        }

        /// <summary>
        /// A circle drawn like a marker pen stroke, out of square pixels, around the ellipse that fits into <paramref name="box"/>:
        ///   - one stroke that starts at the upper left, goes once around clockwise and overshoots its start a little
        ///     (the end lies slightly outside the start, like a spiral, so the ends never meet exactly);
        ///   - the radius wobbles gently, so it is not a perfect ellipse;
        ///   - the stroke is 1 pixel wide where the pen touches down and lifts off and up to 3 pixels in the middle,
        ///     with a tiny ripple on top, so the width wanders a little here and there like a real pen (never beyond 1..3);
        ///   - the last part of the stroke is "dry": every second dot is missing.
        /// The color is chosen by the caller.
        /// <paramref name="seed"/> makes two circles wobble differently. Nothing is random: the circle looks the same every frame.
        /// </summary>
        private static void DrawHandDrawnCircle(SpriteBatch b, Rectangle box, Color color, int seed)
        {
            const int samples = 200;

            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;

                // the last part of the stroke is dry
                if (t > 0.9f && i % 2 == 0)
                    continue;

                Vector2 point = GetHandDrawnCirclePoint(box, t, seed, out float angle);

                int thickness = GetHandDrawnCircleThickness(angle, t, seed);

                float x = point.X;
                float y = point.Y;

                b.Draw(
                    Game1.staminaRect,
                    new Rectangle((int)MathF.Round(x - thickness / 2f), (int)MathF.Round(y - thickness / 2f), thickness, thickness),
                    color);
            }
        }

        /// <summary>Shortens a word with "..." so it is never wider than <see cref="HistoryMaxWordWidth"/>.</summary>
        private static string FitLabel(string word, int maxWidth = HistoryMaxWordWidth)
        {
            if (Game1.smallFont.MeasureString(word).X <= maxWidth)
                return word;

            string cut = word;
            while (cut.Length > 1 && Game1.smallFont.MeasureString(cut + "...").X > maxWidth)
                cut = cut.Substring(0, cut.Length - 1);
            return cut + "...";
        }

        /// <summary>Places the history words left to right, wrapping to a new row when the next word would not fit.</summary>
        private static List<(string Word, string Label, Rectangle Bounds, Rectangle Remove, Rectangle Pin, bool Pinned)> FlowHistory(List<SearchHistoryEntry> history, int left, int right, int top)
        {
            var result = new List<(string, string, Rectangle, Rectangle, Rectangle, bool)>();
            int lineHeight = Game1.smallFont.LineSpacing;

            int x = left;
            int y = top;

            foreach (SearchHistoryEntry entry in history)
            {
                string word = entry.Word;
                string label = FitLabel(word);
                int w = (int)Game1.smallFont.MeasureString(label).X;
                int chipWidth = w + HistoryRemoveGap + PinMarkSize + HistoryPinGap + RemoveMarkSize;   // the word, its pin and its x wrap together

                if (x > left && x + chipWidth > right)
                {
                    x = left;
                    y += HistoryRowHeight;
                }

                // Bounds = the word (click fills it in); Pin = the area around its pin (click pins / unpins it); Remove = the area around its x (click deletes it)
                int pinX = x + w + HistoryRemoveGap;
                int removeX = pinX + PinMarkSize + HistoryPinGap;
                var pin = new Rectangle(pinX - 3, y, PinMarkSize + 3 + HistoryPinGap / 2, lineHeight);
                var remove = new Rectangle(removeX - HistoryPinGap / 2, y, RemoveMarkSize + HistoryPinGap / 2 + HistoryRemovePad, lineHeight);
                result.Add((word, label, new Rectangle(x, y, w, lineHeight), remove, pin, entry.Pinned));
                x += chipWidth + HistoryGap;
            }

            return result;
        }

        private static int CountHistoryRows(List<SearchHistoryEntry> history)
        {
            if (history.Count == 0)
                return 0;

            var chips = FlowHistory(history, 0, DialogWidth - 96, 0);   // must be the same width the dialog is really built with
            return chips[chips.Count - 1].Bounds.Y / HistoryRowHeight + 1;
        }

        private List<(string Word, string Label, Rectangle Bounds, Rectangle Remove, Rectangle Pin, bool Pinned)> LayoutHistory()
        {
            return FlowHistory(history, xPositionOnScreen + 48, xPositionOnScreen + width - 48, yPositionOnScreen + HistoryRowsTop);
        }

        /// <summary>The words of one history entry ("fruit, Starfruit" -> fruit, Starfruit) with where each one starts and how long it is inside the entry text.</summary>
        private static List<(string Term, int Start, int Length)> SplitHistoryTerms(string word)
        {
            var result = new List<(string, int, int)>();
            int position = 0;
            foreach (string part in word.Split(ModEntry.QueryCommas))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                    result.Add((trimmed, position + (part.Length - part.TrimStart().Length), trimmed.Length));
                position += part.Length + 1;   // every separator is one character
            }
            return result;
        }

        private string? GetWordTipCached(string term)
        {
            if (wordTipCache.TryGetValue(term, out string? cached))
                return cached;

            string? tip = null;
            try
            {
                tip = getWordTip?.Invoke(term);
            }
            catch (Exception)
            {
                tip = null;   // no tip rather than a crash
            }

            wordTipCache[term] = tip;
            return tip;
        }

        /// <summary>
        /// The hover tip of the history word under the mouse, or null. A history entry can hold several words ("fruit, Starfruit"): the tip belongs to
        /// the single word the mouse is on (the comma between them has none), while a click still fills in the whole entry.
        /// An existing item -> "Category: Fruit", a category word or a part of a name -> "Showing results for ..." / the item list, a typo -> "Invalid: '...'"
        /// (see ModEntry.GetTermTip: the same tip rules everywhere: history, drop-down, result panel).
        /// </summary>
        private string? GetHistoryTipAt(int x, int y)
        {
            if (getWordTip == null)
                return null;

            foreach (var chip in LayoutHistory())
            {
                if (!chip.Bounds.Contains(x, y))
                    continue;

                foreach (var (term, start, length) in SplitHistoryTerms(chip.Word))
                {
                    float left = chip.Bounds.X + Game1.smallFont.MeasureString(chip.Word.Substring(0, start)).X;
                    float right = chip.Bounds.X + Game1.smallFont.MeasureString(chip.Word.Substring(0, start + length)).X;
                    right = Math.Min(right, chip.Bounds.Right);   // a shortened entry ("...") only has the words that are really shown

                    if (x >= left && x <= right)
                        return GetWordTipCached(term);
                }

                return null;
            }

            return null;
        }

        /// <summary>The Clear History button: right end of the header row, well away from the Enter / Esc line.</summary>
        private Rectangle GetClearHistoryBounds()
        {
            int w = (int)Game1.smallFont.MeasureString(ClearHistoryLabel).X + 40;
            return new Rectangle(xPositionOnScreen + width - 48 - w, yPositionOnScreen + HistoryHeaderY, w, HistoryHeaderHeight);
        }

        private void DrawHistory(SpriteBatch b)
        {
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            int left = xPositionOnScreen + 48;
            int lineHeight = Game1.smallFont.LineSpacing;

            // header row: quiet label on the left, the danger button on the right
            Utility.drawTextWithShadow(
                b,
                "Recent searches",
                Game1.smallFont,
                new Vector2(left, yPositionOnScreen + HistoryHeaderY + (HistoryHeaderHeight - lineHeight) / 2),
                Game1.textColor * 0.7f);

            if (history.Count > 0)
            {
                foreach (var chip in LayoutHistory())
                {
                    Utility.drawTextWithShadow(b, chip.Label, Game1.smallFont, new Vector2(chip.Bounds.X, chip.Bounds.Y), Game1.textColor);

                    // hovered word: underline it, the same way the result panel does
                    if (chip.Bounds.Contains(mouseX, mouseY))
                        b.Draw(Game1.staminaRect, new Rectangle(chip.Bounds.X, chip.Bounds.Bottom - 2, chip.Bounds.Width, 2), Game1.textColor * 0.8f);

                    DrawPinMark(b, chip.Pin, chip.Pinned, chip.Pin.Contains(mouseX, mouseY));
                    DrawRemoveMark(b, chip.Remove, chip.Remove.Contains(mouseX, mouseY));
                }

                DrawClearButton(b, mouseX, mouseY);
            }
            else
            {
                // history was just cleared: the block stays (the menu does not jump), with a quiet note
                Utility.drawTextWithShadow(
                    b,
                    "No search history",
                    Game1.smallFont,
                    new Vector2(left, yPositionOnScreen + HistoryRowsTop),
                    Game1.textColor * 0.6f);
            }

            // thin divider (same look as the divider in the result panel) between the history and the key hints
            b.Draw(Game1.staminaRect, new Rectangle(left, FooterY - 6, width - 96, 2), Game1.textColor * 0.35f);
        }

        /// <summary>The small red x of a history word: a 5x5 pixel cross with the same soft shadow the game puts under its text. Brighter while hovered.</summary>
        private static void DrawRemoveMark(SpriteBatch b, Rectangle hitArea, bool hovered)
        {
            int left = hitArea.X + HistoryRemovePad;
            int top = hitArea.Y + (hitArea.Height - RemoveMarkSize) / 2 + 1;   // centered on the letters, not on the line box

            Color color = hovered ? new Color(255, 70, 60) : new Color(220, 30, 30);
            Color shadow = new Color(221, 148, 84);

            for (int layer = 0; layer < 2; layer++)
            {
                int offset = layer == 0 ? RemovePixel : 0;   // shadow first, then the cross on top
                Color c = layer == 0 ? shadow : color;

                for (int i = 0; i < 5; i++)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(left + offset + i * RemovePixel, top + offset + i * RemovePixel, RemovePixel, RemovePixel), c);
                    b.Draw(Game1.staminaRect, new Rectangle(left + offset + (4 - i) * RemovePixel, top + offset + i * RemovePixel, RemovePixel, RemovePixel), c);
                }
            }
        }

        /// <summary>
        /// The tiny pin of a history word, in the same 3 px pixel style as its red x. Unpinned: a faint outline-colored pin that does not compete with
        /// the word (a little stronger while hovered). Pinned: a red head on a dark needle, like a pin pressed into a board.
        /// </summary>
        private static void DrawPinMark(SpriteBatch b, Rectangle hitArea, bool pinned, bool hovered)
        {
            int left = hitArea.X + 3;
            int top = hitArea.Y + (hitArea.Height - PinMarkSize) / 2 + 1;   // centered on the letters, like the x

            Color head = pinned ? (hovered ? new Color(255, 80, 60) : new Color(210, 40, 35)) : Game1.textColor * (hovered ? 0.75f : 0.35f);
            Color needle = pinned ? new Color(80, 50, 30) : head;
            Color shadow = new Color(221, 148, 84) * (pinned || hovered ? 1f : 0.6f);

            for (int layer = 0; layer < 2; layer++)
            {
                int offset = layer == 0 ? RemovePixel : 0;   // shadow first, then the pin on top
                for (int row = 0; row < PinArt.Length; row++)
                {
                    for (int col = 0; col < PinArt[row].Length; col++)
                    {
                        char ch = PinArt[row][col];
                        if (ch == '.')
                            continue;
                        Color c = layer == 0 ? shadow : (ch == 'n' ? needle : head);
                        b.Draw(Game1.staminaRect, new Rectangle(left + offset + col * RemovePixel, top + offset + row * RemovePixel, RemovePixel, RemovePixel), c);
                    }
                }
            }
        }

        /// <summary>The pin under the mouse: 1 = an unpinned word's pin, 2 = a pinned word's pin, 0 = none.</summary>
        private int HistoryPinUnder(int x, int y)
        {
            foreach (var chip in LayoutHistory())
            {
                if (chip.Pin.Contains(x, y))
                    return chip.Pinned ? 2 : 1;
            }
            return 0;
        }

        private bool IsOverHistoryRemove(int x, int y)
        {
            foreach (var chip in LayoutHistory())
            {
                if (chip.Remove.Contains(x, y))
                    return true;
            }
            return false;
        }

        /// <summary>A real button: the game's own menu box tinted red, with white text. Brighter while hovered.</summary>
        private void DrawClearButton(SpriteBatch b, int mouseX, int mouseY)
        {
            Rectangle bounds = GetClearHistoryBounds();
            bool hovered = bounds.Contains(mouseX, mouseY);

            // the parchment box is light orange, so this tint turns it into a clear red
            Color tint = hovered ? new Color(255, 120, 110) : new Color(225, 75, 75);
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, bounds.Y, bounds.Width, bounds.Height, tint);

            Vector2 textSize = Game1.smallFont.MeasureString(ClearHistoryLabel);
            Utility.drawTextWithShadow(
                b,
                ClearHistoryLabel,
                Game1.smallFont,
                new Vector2(bounds.X + (bounds.Width - textSize.X) / 2f, bounds.Y + (bounds.Height - textSize.Y) / 2f),
                Color.White);
        }

        private void DrawStarButton(SpriteBatch b)
        {
            DrawQualityButton(b, starButton, EffectiveQuality, 1f);   // a quality typed into the text ("gold star apple") is shown here, too
        }

        /// <summary>
        /// Draws a star filter as a framed button: the game's menu box with the star (or outline star, or empty frame) in its middle.
        /// The star button of the search box and the quality icon of the result panel (ModEntry) both use this, so they always look the same.
        /// </summary>
        /// <param name="quality">-1 = any (outline star), 0 = no quality (empty frame), 1 = silver, 2 = gold, 4 = iridium.</param>
        /// <param name="alpha">1 = fully visible (the result panel fades out, and its icon fades with it).</param>
        internal static void DrawQualityButton(SpriteBatch b, Rectangle bounds, int quality, float alpha)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * alpha);

            int starSize = 8 * StarScale;
            var starPos = new Vector2(bounds.X + (bounds.Width - starSize) / 2, bounds.Y + (bounds.Height - starSize) / 2);

            if (quality < 0)
            {
                // "any quality": the same star shape as the real quality stars, drawn as an outline only
                b.Draw(GetOutlineStarTexture(b.GraphicsDevice), starPos, new Color(130, 75, 35) * alpha);
                return;
            }

            if (quality == 0)
            {
                // "no quality": an empty frame, the same size, line width and color as the outline star, with no star in it
                Color frameColor = new Color(130, 75, 35) * alpha;
                int fx = (int)starPos.X;
                int fy = (int)starPos.Y;
                int t = OutlineThickness;

                b.Draw(Game1.staminaRect, new Rectangle(fx, fy, starSize, t), frameColor);                          // top
                b.Draw(Game1.staminaRect, new Rectangle(fx, fy + starSize - t, starSize, t), frameColor);           // bottom
                b.Draw(Game1.staminaRect, new Rectangle(fx, fy + t, t, starSize - 2 * t), frameColor);              // left
                b.Draw(Game1.staminaRect, new Rectangle(fx + starSize - t, fy + t, t, starSize - 2 * t), frameColor); // right
                return;
            }

            Rectangle source = quality switch
            {
                1 => SilverStarSource,
                2 => GoldStarSource,
                _ => IridiumStarSource
            };

            b.Draw(
                Game1.mouseCursors,
                starPos,
                source,
                Color.White * alpha,
                0f,
                Vector2.Zero,
                (float)StarScale,
                SpriteEffects.None,
                1f);
        }

        /// <summary>
        /// Builds (once) a white outline of the game's own silver star: the sprite's shape is read from the game texture,
        /// scaled up by <see cref="StarScale"/>, and only its border pixels are kept.
        /// </summary>
        private static Texture2D GetOutlineStarTexture(GraphicsDevice device)
        {
            if (outlineStarTexture != null && !outlineStarTexture.IsDisposed && outlineStarTexture.GraphicsDevice == device)
                return outlineStarTexture;

            const int spriteSize = 8;
            int n = spriteSize * StarScale;

            var sprite = new Color[spriteSize * spriteSize];
            Game1.mouseCursors.GetData(0, SilverStarSource, sprite, 0, sprite.Length);

            var solid = new bool[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                    solid[y * n + x] = sprite[(y / StarScale) * spriteSize + x / StarScale].A > 0;
            }

            // shrink the shape by the line width; what is left of the original shape is the outline
            bool[] inner = solid;
            for (int i = 0; i < OutlineThickness; i++)
                inner = Erode(inner, n);

            var data = new Color[n * n];
            for (int i = 0; i < data.Length; i++)
                data[i] = solid[i] && !inner[i] ? Color.White : Color.Transparent;

            outlineStarTexture = new Texture2D(device, n, n);
            outlineStarTexture.SetData(data);
            return outlineStarTexture;
        }

        /// <summary>Removes the outermost layer of pixels of a shape (pixels at the texture edge count as outside).</summary>
        private static bool[] Erode(bool[] src, int n)
        {
            var result = new bool[src.Length];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    if (!src[i]) continue;
                    result[i] = x > 0 && y > 0 && x < n - 1 && y < n - 1
                        && src[i - 1] && src[i + 1] && src[i - n] && src[i + n];
                }
            }
            return result;
        }

        // ===================== MAGNIFIER BUTTON (inside the search box, right end) =====================

        /// <summary>The clickable / hoverable area of the magnifier: inside the text box, at its right end, a few pixels from the box's edge.</summary>
        private Rectangle MagnifierBounds
        {
            get
            {
                int size = MagnifierSize + MagnifierHitPad * 2;
                return new Rectangle(
                    textBox.X + textBox.Width - MagnifierRightInset - size,
                    textBox.Y + (textBox.Height - size) / 2,
                    size,
                    size);
            }
        }

        /// <summary>
        /// A small pixel-art magnifying glass in the game's own look (same dark brown as the text, drawn with plain rectangles, so it is always crisp).
        /// Hover: the glass and the handle get brighter and a soft highlight appears behind the icon. Pressed (left button held after a click on it):
        /// the icon sinks a little, its shadow disappears and the highlight gets darker.
        /// </summary>
        private void DrawMagnifierButton(SpriteBatch b)
        {
            Rectangle hit = MagnifierBounds;
            bool hovered = hit.Contains(Game1.getMouseX(), Game1.getMouseY());

            if (Game1.input.GetMouseState().LeftButton != ButtonState.Pressed)
                magnifierPressStarted = false;
            bool pressed = magnifierPressStarted && hovered;

            if (hovered)
                b.Draw(Game1.staminaRect, hit, Game1.textColor * (pressed ? 0.22f : 0.12f));

            int left = hit.X + MagnifierHitPad + (pressed ? MagnifierPressShift : 0);
            int top = hit.Y + MagnifierHitPad + (pressed ? MagnifierPressShift : 0);

            if (!pressed)
                DrawMagnifierPixels(b, left + MagnifierShadowOffset, top + MagnifierShadowOffset, hovered, shadow: true);

            DrawMagnifierPixels(b, left, top, hovered, shadow: false);
        }

        private static void DrawMagnifierPixels(SpriteBatch b, int left, int top, bool bright, bool shadow)
        {
            Color outline = Game1.textColor;
            Color glass = bright ? new Color(232, 248, 255) : new Color(196, 226, 236);
            Color shine = Color.White;
            Color handle = bright ? new Color(178, 108, 56) : new Color(139, 80, 40);

            for (int row = 0; row < MagnifierArt.Length; row++)
            {
                string line = MagnifierArt[row];
                for (int col = 0; col < line.Length; col++)
                {
                    Color color;
                    switch (line[col])
                    {
                        case '#': color = outline; break;
                        case 'g': color = glass; break;
                        case 'w': color = shine; break;
                        case 'h': color = handle; break;
                        default: continue;
                    }

                    if (shadow)
                        color = Color.Black * 0.18f;

                    b.Draw(Game1.staminaRect, new Rectangle(left + col * MagnifierPixel, top + row * MagnifierPixel, MagnifierPixel, MagnifierPixel), color);
                }
            }
        }

        // ===================== CLEAR BUTTON (small red x inside the search box, left of the magnifier) =====================

        /// <summary>The x always stays in the box. With an empty box it is drawn faded and a click on it does nothing (there is nothing to clear).</summary>
        private bool ClearTextHasWork => textBox.Text.Length > 0;

        /// <summary>The clickable / hoverable area of the x: inside the text box, directly left of the magnifier's area, centered like it.</summary>
        private Rectangle ClearTextBounds
        {
            get
            {
                int size = ClearTextSize + ClearTextHitPad * 2;
                return new Rectangle(
                    MagnifierBounds.X - ClearTextGap - size,
                    textBox.Y + (textBox.Height - size) / 2,
                    size,
                    size);
            }
        }

        /// <summary>
        /// A small, fine red cross in the same pixel style as the red x of the history words (one cross pixel = 3 screen pixels, drawn with plain
        /// rectangles, so it is always crisp), with the magnifier's soft shadow. Hover: brighter red and the same soft highlight behind it.
        /// </summary>
        private void DrawClearTextButton(SpriteBatch b)
        {
            Rectangle hit = ClearTextBounds;
            bool active = ClearTextHasWork;
            bool hovered = active && hit.Contains(Game1.getMouseX(), Game1.getMouseY());

            if (hovered)
                b.Draw(Game1.staminaRect, hit, Game1.textColor * 0.12f);

            int left = hit.X + ClearTextHitPad;
            int top = hit.Y + ClearTextHitPad;
            float fade = active ? 1f : 0.35f;   // an empty box: the x is there but faded, it has nothing to do

            DrawClearTextPixels(b, left + ClearTextShadowOffset, top + ClearTextShadowOffset, hovered, shadow: true, fade);
            DrawClearTextPixels(b, left, top, hovered, shadow: false, fade);
        }

        private static void DrawClearTextPixels(SpriteBatch b, int left, int top, bool bright, bool shadow, float fade)
        {
            Color color = (shadow ? Color.Black * 0.18f : (bright ? new Color(255, 70, 60) : new Color(220, 30, 30))) * fade;

            for (int i = 0; i < ClearTextGrid; i++)
            {
                b.Draw(Game1.staminaRect, new Rectangle(left + i * ClearTextPixel, top + i * ClearTextPixel, ClearTextPixel, ClearTextPixel), color);

                // the other diagonal; the middle pixel belongs to both, so it is drawn only once (a see-through shadow would get darker there)
                int mirrored = ClearTextGrid - 1 - i;
                if (mirrored != i)
                    b.Draw(Game1.staminaRect, new Rectangle(left + mirrored * ClearTextPixel, top + i * ClearTextPixel, ClearTextPixel, ClearTextPixel), color);
            }
        }

        // ===================== PASTE BUTTON (inside the search box, left of the clear x) =====================

        /// <summary>
        /// The icon is a real game sprite: the Journal Scrap, a flat sheet of written paper, which reads as "a piece of text to put in" (no scroll),
        /// 16 x 16 game pixels drawn crisp at exactly 2x. If a mod removed it, the next id in the list is used (Lost Book). To use another sprite,
        /// put its qualified item id first.
        /// </summary>
        private static readonly string[] PasteIconItemIds = { "(O)842", "(O)102" };
        private const int PasteIconSize = 32;

        /// <summary>Extra clickable / hover room around the icon on every side.</summary>
        private const int PasteHitPad = 3;

        /// <summary>How far the icon rises while the mouse is over it, and how far its soft shadow lies below / right of it (screen pixels).</summary>
        private const int PasteHoverLift = 2;
        private const int PasteShadowOffset = 2;

        /// <summary>Free space between the end of the visible text and the paste button.</summary>
        private const int PasteTextGap = 6;

        /// <summary>The longest text the box may hold. Typing stops here, and a pasted text is cut at this length.</summary>
        internal const int MaxTextLength = 120;

        /// <summary>The clickable / hoverable area of the paste button: inside the text box, directly left of the clear x's area. It is always there (the clear x only appears with text), so it never jumps.</summary>
        private Rectangle PasteBounds
        {
            get
            {
                int size = PasteIconSize + PasteHitPad * 2;
                return new Rectangle(
                    ClearTextBounds.X - ClearTextGap - size,
                    textBox.Y + (textBox.Height - size) / 2,
                    size,
                    size);
            }
        }

        private static StardewValley.ItemTypeDefinitions.ParsedItemData? GetPasteIconData()
        {
            foreach (string id in PasteIconItemIds)
            {
                var data = ItemRegistry.GetData(id);
                if (data != null && !data.IsErrorItem)
                    return data;
            }
            return null;
        }

        /// <summary>
        /// The paste button: the scroll sprite with the same soft shadow as the magnifier and the clear x. Hover: the soft highlight behind it
        /// (like the other buttons) and the scroll lifts a little off its shadow.
        /// </summary>
        private void DrawPasteButton(SpriteBatch b)
        {
            Rectangle hit = PasteBounds;
            bool hovered = hit.Contains(Game1.getMouseX(), Game1.getMouseY());

            if (hovered)
                b.Draw(Game1.staminaRect, hit, Game1.textColor * 0.12f);

            int lift = hovered ? PasteHoverLift : 0;
            var icon = new Rectangle(hit.X + PasteHitPad, hit.Y + PasteHitPad - lift, PasteIconSize, PasteIconSize);

            var data = GetPasteIconData();
            if (data != null)
            {
                Texture2D texture = data.GetTexture();
                Rectangle source = data.GetSourceRect();
                var shadow = new Rectangle(icon.X + PasteShadowOffset, icon.Y + PasteShadowOffset + lift, icon.Width, icon.Height);
                b.Draw(texture, shadow, source, Color.Black * 0.18f);
                b.Draw(texture, icon, source, Color.White);
            }
            else
            {
                // no sprite available at all: a plain "V" keeps the button usable
                Vector2 size = Game1.smallFont.MeasureString("V");
                Utility.drawTextWithShadow(b, "V", Game1.smallFont, new Vector2(icon.Center.X - size.X / 2f, icon.Center.Y - size.Y / 2f), Game1.textColor);
            }
        }

        /// <summary>
        /// Puts the clipboard text into the box at the caret (in place of the selection, if there is one). The text is cleaned first
        /// (see <see cref="CleanTextBoxText"/>) and only as much as still fits into <see cref="MaxTextLength"/> characters goes in; the rest is cut off.
        /// The search is NOT started: the player has to press Enter, click the magnifier or use the key hint.
        /// This is the ONLY paste there is: the text box ignores the game's own paste (see SearchTextBox), so Ctrl+V and the button paste exactly once.
        /// </summary>
        private void PasteFromClipboard(bool playFeedback)
        {
            string cleaned = CleanTextBoxText(ReadClipboardText()).Trim();
            bool pasted = textBox.InsertText(cleaned);   // false: nothing to paste, or the box is full

            if (playFeedback)
                Game1.playSound(pasted ? "smallSelect" : RefusedSound);
        }

        private static SpriteFont? fontCharsFont;
        private static HashSet<char> fontChars = new();

        /// <summary>
        /// Makes a text safe for the search box: line breaks and tabs become spaces, other control characters are dropped, a character the game's font
        /// cannot draw (it would crash the drawing) becomes "?" (which the search refuses with its usual "Invalid characters" hint), and the text is cut at
        /// <see cref="MaxTextLength"/>. Normal typed text comes out unchanged.
        /// </summary>
        private static string CleanTextBoxText(string text)
        {
            if (text.Length == 0)
                return text;

            SpriteFont font = Game1.smallFont;
            if (!ReferenceEquals(fontCharsFont, font))
            {
                fontChars = new HashSet<char>(font.Characters);
                fontCharsFont = font;
            }

            System.Text.StringBuilder? builder = null;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                char replacement = ch;
                bool drop = false;

                if (ch == '\r' || ch == '\n' || ch == '\t')
                    replacement = ' ';
                else if (char.IsControl(ch))
                    drop = true;
                else if (!fontChars.Contains(ch))
                    replacement = '?';

                if ((drop || replacement != ch) && builder == null)
                    builder = new System.Text.StringBuilder(text, 0, i, text.Length);

                if (builder != null && !drop)
                    builder.Append(replacement);
            }

            string result = builder?.ToString() ?? text;
            return result.Length > MaxTextLength ? result.Substring(0, MaxTextLength) : result;
        }

        /// <summary>The text on the system clipboard ("" if there is none), read with the game's own clipboard helper (the one its text boxes use).</summary>
        private static string ReadClipboardText()
        {
            try
            {
                string text = "";
                return DesktopClipboard.GetText(ref text) ? text ?? "" : "";
            }
            catch (Exception)
            {
                return "";   // no clipboard available
            }
        }

        /// <summary>Puts a text on the system clipboard with the game's own clipboard helper. False if that did not work.</summary>
        private static bool WriteClipboardText(string text)
        {
            try
            {
                DesktopClipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ===================== CLOSE BUTTON (red X, top right) =====================

        /// <summary>The red X the game itself uses as the close button of its menus (same sprite, same 12px * 4 = 48px size).</summary>
        private static readonly Rectangle CloseButtonSource = new(337, 494, 12, 12);

        private const int CloseButtonSize = 48;

        /// <summary>Zoom of the X while the mouse is over it (4 = normal).</summary>
        private const float CloseButtonHoverScale = 4.4f;

        /// <summary>
        /// On the title row, with its right edge in line with the star button below it. The box is exactly the sprite, so only the X itself can be clicked.
        /// </summary>
        private Rectangle CloseButtonBounds => new(
            xPositionOnScreen + width - 48 - CloseButtonSize,
            yPositionOnScreen + 40 + (Game1.dialogueFont.LineSpacing - CloseButtonSize) / 2,
            CloseButtonSize,
            CloseButtonSize);

        /// <summary>The X is hidden (and cannot be clicked) while the auto-complete drop-down covers it.</summary>
        private bool CloseButtonVisible => !SuggestionsVisible || !GetSuggestionBounds().Intersects(CloseButtonBounds);

        private void DrawCloseButton(SpriteBatch b)
        {
            Rectangle bounds = CloseButtonBounds;
            bool hovered = bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
            float scale = hovered ? CloseButtonHoverScale : 4f;

            // drawn around its center, so the grown X stays centered on the same spot
            b.Draw(
                Game1.mouseCursors,
                new Vector2(bounds.Center.X, bounds.Center.Y),
                CloseButtonSource,
                Color.White,
                0f,
                new Vector2(CloseButtonSource.Width / 2f, CloseButtonSource.Height / 2f),
                scale,
                SpriteEffects.None,
                1f);
        }

        public override void draw(SpriteBatch b)
        {
            DrawMenu(b);
            if (autoSearchPending)
                return;

            // the "Copy" bubble and its "Copied!" note lie on top of everything (tips included), the mouse cursor on top of them
            if (copyBubble.IsOpen || copyBubble.NoteVisible)
            {
                copyBubble.Draw(b, Game1.getMouseX(), Game1.getMouseY());
                drawMouse(b);
            }
        }

        private void DrawMenu(SpriteBatch b)
        {
            if (autoSearchPending)
                return;   // the automatic search runs in the next update and closes the box: do not flash it for one frame

            suggestionTipText = null;   // set again by DrawSuggestions if the drop-down is drawn and a row is highlighted

            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            Utility.drawTextWithShadow(
                b,
                "Search chests for an item",
                Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 40),
                Game1.textColor);

            textBox.Draw(b);
            DrawPasteButton(b);
            DrawClearTextButton(b);
            DrawMagnifierButton(b);
            DrawStarButton(b);
            if (CloseButtonVisible)
                DrawCloseButton(b);

            // validation hints (same native pixel font + shadow as the rest of the menu)
            if (showTooShortHint)
            {
                Utility.drawTextWithShadow(
                    b,
                    Game1.parseText(validationHintText, Game1.smallFont, width - 96),   // wraps onto a second line if a hint is too wide (the hint row has room for two)
                    Game1.smallFont,
                    new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 166),
                    Color.DarkRed);
            }
            else if (showNoQualityHint)
            {
                string hint = Game1.parseText(
                    typedQuality != null
                        ? "Item found, but no matching quality. Try another quality word."
                        : "Item found, but no matching quality. Try adjusting the star filter.",
                    Game1.smallFont,
                    width - 96);

                Utility.drawTextWithShadow(
                    b,
                    hint,
                    Game1.smallFont,
                    new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 166),
                    Color.DarkRed);
            }
            else if (SpellingVisible)
            {
                DrawSpelling(b);   // the instant correction line (only while the typed word matches nothing, see RefreshSuggestions)
            }
            else if (TipVisible)
            {
                // quiet tip in the free space between the text box and the key hint line (same spot as the red hints, which take its place)
                Utility.drawTextWithShadow(
                    b,
                    FitLabel(TipText, width - 96),
                    Game1.smallFont,
                    new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 166),
                    Color.DimGray * 0.9f);   // a bit deeper than the first Gray * 0.7: readable on the orange box, still quieter than the real text
            }

            if (HistoryAreaVisible)
                DrawHistory(b);

            // the drop-down floats over everything below the text box; the key hint line is skipped while the drop-down covers it
            bool showSuggestions = SuggestionsVisible;
            if (FooterVisible)
                DrawFooter(b);

            if (showSuggestions)
                DrawSuggestions(b);

            drawMouse(b);

            // the tip of the history word under the mouse (same text as the result page; a typo gets the Invalid line)
            string? historyTip = HistoryVisible ? GetHistoryTipAt(Game1.getMouseX(), Game1.getMouseY()) : null;
            string? spellingTip = GetSpellingTipAt(Game1.getMouseX(), Game1.getMouseY());   // the "(category)" label of a corrected word shows here

            // hovering the star shows which quality is selected
            if (starButton.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, typedQuality is int typed
                    ? QualityLabels[Math.Max(0, Array.IndexOf(QualityValues, typed))] + " (set by the search text)"
                    : QualityLabels[qualityIndex], Game1.smallFont);
            else if (MagnifierBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Search", Game1.smallFont);
            else if (PasteBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Paste from clipboard", Game1.smallFont);
            else if (ClearTextBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, ClearTextHasWork ? "Clear text" : "Nothing to clear", Game1.smallFont);
            else if (HistoryVisible && GetClearHistoryBounds().Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, history.Any(h => h.Pinned) ? "Delete all searches that are not pinned" : "Delete all search history", Game1.smallFont);
            else if (HistoryVisible && HistoryPinUnder(Game1.getMouseX(), Game1.getMouseY()) > 0)
                IClickableMenu.drawHoverText(b, HistoryPinUnder(Game1.getMouseX(), Game1.getMouseY()) == 2 ? "Unpin this search" : "Pin this search", Game1.smallFont);
            else if (HistoryVisible && IsOverHistoryRemove(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Remove from history", Game1.smallFont);
            else if (spellingTip != null)
                IClickableMenu.drawHoverText(b, spellingTip, Game1.smallFont);
            else if (historyTip != null)
                IClickableMenu.drawHoverText(b, historyTip, Game1.smallFont);
            else
                DrawSuggestionTip(b);   // none of the button tips is showing: the tip of the highlighted drop-down row (drawn last, on top of everything)
        }

        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == textBox)
                Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }
    }

    /// <summary>
    /// The small right-click bubble "Copy", and the short "Copied!" note after a copy. Used by the search box (text field, history, drop-down) and by
    /// the result panel on the HUD (its title words), so it looks and works the same everywhere. The text field also gets a separate "Paste" button
    /// right beside it (same look, same size; only there). All coordinates are UI pixels.
    /// </summary>
    internal sealed class CopyBubble
    {
        private const string CopyLabel = "Copy";
        private const string PasteLabel = "Paste";
        private const string CopiedText = "Copied!";
        private const int Height = 52;
        private const int PadX = 26;
        private const int ButtonGap = 8;   // between "Copy" and "Paste"
        private const long NoteMs = 1000;
        private const long NoteFadeMs = 300;

        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        private bool open;
        private string? word;            // what "Copy" copies (null = no "Copy" button: the text field is empty)
        private Action? paste;           // what "Paste" does (null = no "Paste" button: everywhere except the text field)
        private Rectangle copyBounds;
        private Rectangle pasteBounds;
        private Vector2 notePosition;
        private long noteUntilMs;

        /// <summary>True while the bubble is open.</summary>
        public bool IsOpen => open;

        /// <summary>True while the "Copied!" note is still on screen.</summary>
        public bool NoteVisible => clock.ElapsedMilliseconds < noteUntilMs;

        /// <summary>
        /// Opens the bubble just right of / below the mouse, always fully on screen. <paramref name="text"/>: what "Copy" copies (null: no "Copy").
        /// <paramref name="paste"/>: the action of a "Paste" button beside it (null: no "Paste"). Nothing opens if neither is given.
        /// </summary>
        public void Open(string? text, int mouseX, int mouseY, Action? paste = null)
        {
            word = string.IsNullOrEmpty(text) ? null : text;
            this.paste = paste;
            open = word != null || paste != null;
            if (!open)
                return;

            // both buttons have the same size: the width of the longer word
            SpriteFont font = Game1.smallFont;
            int width = (int)Math.Ceiling(Math.Max(font.MeasureString(CopyLabel).X, font.MeasureString(PasteLabel).X)) + PadX * 2;
            int count = (word != null ? 1 : 0) + (paste != null ? 1 : 0);
            int total = width * count + ButtonGap * (count - 1);

            int x = Math.Clamp(mouseX + 8, 4, Math.Max(4, Game1.uiViewport.Width - total - 4));
            int y = Math.Clamp(mouseY + 8, 4, Math.Max(4, Game1.uiViewport.Height - Height - 4));

            copyBounds = Rectangle.Empty;
            pasteBounds = Rectangle.Empty;
            if (word != null)
            {
                copyBounds = new Rectangle(x, y, width, Height);
                x += width + ButtonGap;
            }
            if (paste != null)
                pasteBounds = new Rectangle(x, y, width, Height);
        }

        public void Close()
        {
            open = false;
            word = null;
            paste = null;
        }

        /// <summary>
        /// A left click while the bubble is open: on "Copy" the word goes to the clipboard (the game's own clipboard helper), with a soft click and the
        /// "Copied!" note; on "Paste" the paste action runs (it plays its own sound); anywhere else the bubble only closes. Returns true if the bubble
        /// was open (the click is used up either way).
        /// </summary>
        public bool HandleLeftClick(int x, int y)
        {
            if (!open)
                return false;

            string? copyText = word;
            Action? pasteAction = paste;
            Close();

            if (copyText != null && copyBounds.Contains(x, y))
            {
                bool copied;
                try
                {
                    DesktopClipboard.SetText(copyText);
                    copied = true;
                }
                catch (Exception)
                {
                    copied = false;   // no clipboard available
                }

                if (copied)
                {
                    Game1.playSound("drumkit6");
                    notePosition = new Vector2(copyBounds.X, copyBounds.Y);
                    noteUntilMs = clock.ElapsedMilliseconds + NoteMs;
                }
            }
            else if (pasteAction != null && pasteBounds.Contains(x, y))
            {
                pasteAction();
            }

            return true;
        }

        /// <summary>Draws the open buttons (brighter while the mouse is on one) and the "Copied!" note.</summary>
        public void Draw(SpriteBatch b, int mouseX, int mouseY)
        {
            if (open)
            {
                if (word != null)
                    SearchMenu.DrawMenuButton(b, copyBounds, CopyLabel, copyBounds.Contains(mouseX, mouseY));
                if (paste != null)
                    SearchMenu.DrawMenuButton(b, pasteBounds, PasteLabel, pasteBounds.Contains(mouseX, mouseY));
            }

            long left = noteUntilMs - clock.ElapsedMilliseconds;
            if (left > 0)
            {
                float alpha = left < NoteFadeMs ? left / (float)NoteFadeMs : 1f;
                SpriteFont font = Game1.smallFont;
                Vector2 size = font.MeasureString(CopiedText);
                var position = new Vector2(
                    MathF.Round(Math.Clamp(notePosition.X, 4f, Game1.uiViewport.Width - size.X - 4f)),
                    MathF.Round(Math.Clamp(notePosition.Y + (Height - size.Y) / 2f, 4f, Game1.uiViewport.Height - size.Y - 4f)));
                Utility.drawTextWithShadow(b, CopiedText, font, position, Game1.textColor * alpha);
            }
        }
    }
}