using System;
using System.Collections.Generic;
using System.Globalization;
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

    public class SearchMenu : IClickableMenu
    {
        /// <summary>Minimum characters required for a text search (pure-number IDs are exempt).</summary>
        private const int MinQueryLength = 3;

        /// <summary>Menu height. Taller than before to make room for the (possibly two-line) hint row, plus some air below the key hint line.</summary>
        private const int MenuHeight = 330;

        /// <summary>
        /// The star filter values, in click order. -1 = any quality (no filtering); 1 = silver, 2 = gold, 4 = iridium
        /// (Stardew's Quality values); 0 = no quality (only items without a star: normal quality, or items that never have stars).
        /// </summary>
        private static readonly int[] QualityValues = { -1, 1, 2, 4, 0 };
        private static readonly string[] QualityLabels = { "Any quality", "Silver star", "Gold star", "Iridium star", "No quality" };

        // quality star sprites in Game1.mouseCursors (the same ones the game draws on item icons)
        private static readonly Rectangle SilverStarSource = new(338, 400, 8, 8);
        private static readonly Rectangle GoldStarSource = new(346, 400, 8, 8);
        private static readonly Rectangle IridiumStarSource = new(346, 392, 8, 8);

        /// <summary>Zoom of the star inside the button: 8px sprite * 3 = 24px star in the 48px button, so the star leaves some room around it.</summary>
        private const int StarScale = 3;

        /// <summary>Line width of the outline star: one game pixel (= StarScale screen pixels), the same weight as the game's own pixel art.</summary>
        private const int OutlineThickness = StarScale;

        private static Texture2D? outlineStarTexture;

        // --- search history ---

        /// <summary>How many recent searches are remembered.</summary>
        public const int MaxHistory = 5;

        private const int MenuWidth = 640;

        // History block layout (offsets from the top of the menu). It is only added when there is a history,
        // so the menu looks exactly as before when the history is empty:
        //   header row:  "Recent searches" on the left, the red [Clear History] button on the right
        //   word rows:   the history words
        //   divider, then the "= Enter to search / = Esc to cancel" line
        private const int HistoryHeaderY = 196;
        private const int HistoryHeaderHeight = 40;
        private const int HistoryRowsTop = 248;
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

        // --- search suggestions (auto-complete drop-down) ---

        /// <summary>How many suggestions the drop-down shows at most.</summary>
        public const int MaxSuggestions = 6;

        /// <summary>Minimum row height of the drop-down (it grows if the font is taller).</summary>
        private const int MinSuggestionRowHeight = 36;

        /// <summary>Space between the frame of the drop-down box and its rows (the box art has a 20px border).</summary>
        private const int SuggestionPadX = 20;
        private const int SuggestionPadY = 20;

        /// <summary>Gap between the bottom of the text box and the top of the drop-down.</summary>
        private const int SuggestionGap = 4;

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

        private readonly TextBox textBox;
        private readonly Func<string, int, SearchOutcome> onConfirm;
        private readonly string? leakedChar;
        private readonly Rectangle starButton;
        private readonly List<string> history;
        private readonly Action onHistoryChanged;
        private readonly int extraHeight;
        private int ticks = 0;

        private bool showTooShortHint = false;
        private bool showNoQualityHint = false;
        private int qualityIndex = 0;
        private string lastText = "";

        // auto-complete state: the current list, the row picked with the arrow keys (-1 = none), and a text for which
        // the list stays closed (a history word that was just filled in, or a search that was refused)
        private readonly Func<string, IReadOnlyList<Suggestion>>? getSuggestions;
        private IReadOnlyList<Suggestion> suggestions = Array.Empty<Suggestion>();
        private int activeSuggestion = -1;
        private string? suppressedText;
        private bool magnifierPressStarted;   // the left button went down on the magnifier and is (possibly) still held

        /// <param name="onConfirm">Called with the search text and the star filter (-1 = any, 0 = no star) when the player presses Enter. Return <see cref="SearchOutcome.NoMatchingQuality"/> to keep the menu open.</param>
        /// <param name="history">The recent searches, newest first. The menu edits this list directly.</param>
        /// <param name="onHistoryChanged">Called after the history list was changed, so the caller can save it.</param>
        /// <param name="leakedChar">The character of the key that opened this menu, if it types a character. It is removed if it leaks into the text box.</param>
        /// <param name="getSuggestions">Returns the auto-complete suggestions for the text typed so far. Null = no drop-down at all.</param>
        public SearchMenu(Func<string, int, SearchOutcome> onConfirm, List<string> history, Action onHistoryChanged, string? leakedChar = null, Func<string, IReadOnlyList<Suggestion>>? getSuggestions = null)
            : base(
                Game1.uiViewport.Width / 2 - 320,
                Game1.uiViewport.Height / 2 - (MenuHeight + ExtraHeightFor(history)) / 2,
                640,
                MenuHeight + ExtraHeightFor(history),
                false)
        {
            this.onConfirm = onConfirm;
            this.leakedChar = leakedChar;
            this.history = history;
            this.onHistoryChanged = onHistoryChanged;
            this.extraHeight = ExtraHeightFor(history);
            this.getSuggestions = getSuggestions;

            // the star button sits at the right end of the search row; the text box is shortened to make room
            const int starSize = 48;
            const int starGap = 16;

            textBox = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null,
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

            textBox.OnEnterPressed += sender => Confirm();

            // connect the text box to the game's keyboard input system
            Game1.keyboardDispatcher.Subscriber = textBox;
            textBox.Selected = true;
        }

        /// <summary>
        /// Whether the query may be searched. Empty is never allowed; 3+ characters is allowed;
        /// shorter queries are allowed only if they are a pure number (item ID, or negative category ID).
        /// Uses the same number rule as BuildMatcher in ModEntry.
        /// </summary>
        private static bool IsQueryValid(string query)
        {
            if (query.Length == 0)
                return false;
            if (query.Length >= MinQueryLength)
                return true;
            return int.TryParse(query, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>How much taller the menu must be to hold the history block (0 when there is no history).</summary>
        private static int ExtraHeightFor(List<string> history)
        {
            int rows = CountHistoryRows(history);
            if (rows == 0)
                return 0;
            return HistoryRowsTop + rows * HistoryRowHeight + HistoryFooterGap - 256;
        }

        /// <summary>Puts a search word at the front of the history (no duplicates, case-insensitive, at most <see cref="MaxHistory"/>).</summary>
        private void RecordHistory(string query)
        {
            if (query.Length == 0)
                return;

            history.RemoveAll(h => string.Equals(h, query, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, query);
            if (history.Count > MaxHistory)
                history.RemoveRange(MaxHistory, history.Count - MaxHistory);

            onHistoryChanged();
        }

        private void Confirm()
        {
            // a suggestion picked with the arrow keys wins over the typed text
            if (SuggestionsVisible && activeSuggestion >= 0 && activeSuggestion < suggestions.Count)
                textBox.Text = suggestions[activeSuggestion].Text;

            string query = textBox.Text.Trim();

            // every Enter is remembered: found or not, valid or not
            RecordHistory(query);

            if (!IsQueryValid(query))
            {
                // block the search, keep the menu open, and show the hint
                showTooShortHint = true;
                showNoQualityHint = false;
                lastText = textBox.Text;
                SuppressSuggestions();
                textBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = textBox;
                return;
            }

            SearchOutcome outcome = onConfirm(query, QualityValues[qualityIndex]);

            if (outcome == SearchOutcome.NoMatchingQuality)
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

            exitThisMenu();
        }

        public override void update(GameTime time)
        {
            base.update(time);

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
                lastText = textBox.Text;
                showTooShortHint = false;
                showNoQualityHint = false;
                RefreshSuggestions();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            // do not call base: it would close the menu when typing letters like "E"
            if (key == Keys.Escape)
            {
                exitThisMenu();
                return;
            }

            if (!SuggestionsVisible)
                return;

            int rows = VisibleSuggestionRows;

            switch (key)
            {
                case Keys.Down:
                    // none -> first -> ... -> last -> none (back to the typed text, like Google)
                    activeSuggestion = activeSuggestion + 1 >= rows ? -1 : activeSuggestion + 1;
                    Game1.playSound("smallSelect");
                    break;

                case Keys.Up:
                    activeSuggestion = activeSuggestion < 0 ? rows - 1 : activeSuggestion - 1;
                    Game1.playSound("smallSelect");
                    break;

                case Keys.Tab:
                    // complete the text with the picked suggestion (or the top one) without searching yet
                    textBox.Text = suggestions[activeSuggestion >= 0 ? activeSuggestion : 0].Text;
                    activeSuggestion = -1;
                    Game1.playSound("smallSelect");
                    break;
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // a click inside the drop-down: on a row = search for that item; on the frame = swallowed
            if (SuggestionsVisible && GetSuggestionBounds().Contains(x, y))
            {
                int row = GetSuggestionRowAt(x, y);
                if (row >= 0)
                {
                    Game1.playSound("smallSelect");
                    textBox.Text = suggestions[row].Text;
                    activeSuggestion = -1;
                    Confirm();   // may close the menu, so nothing else may touch the text box afterwards
                }
                return;
            }

            // the red X in the top right corner: same as pressing Esc (the drop-down, if it covers the X, has already swallowed the click above)
            if (CloseButtonVisible && CloseButtonBounds.Contains(x, y))
            {
                exitThisMenu();
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
                    history.Clear();
                    onHistoryChanged();
                    Game1.playSound("trashcan");
                }
                else
                {
                    foreach (var chip in LayoutHistory())
                    {
                        // the small red x: remove just this word (the list is laid out again on the next frame)
                        if (chip.Remove.Contains(x, y))
                        {
                            history.RemoveAll(h => string.Equals(h, chip.Word, StringComparison.OrdinalIgnoreCase));
                            onHistoryChanged();
                            Game1.playSound("trashcan");
                            break;
                        }

                        if (!chip.Bounds.Contains(x, y))
                            continue;

                        // fill the word into the search box
                        textBox.Text = chip.Word;
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
                showNoQualityHint = false;
                Game1.playSound("smallSelect");
            }

            textBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = textBox;
        }

        // ===================== SEARCH SUGGESTIONS (auto-complete drop-down) =====================
        // A parchment box that drops down from the text box while the player types. It is drawn on top of everything
        // below the text box (like Google's list) and is refreshed whenever the text changes, never while drawing.
        //   mouse:  hovering highlights a row, a click searches for that item
        //   keys:   Down / Up move the highlight, Enter searches the highlighted item, Tab fills it into the box

        /// <summary>The drop-down is shown while there is something to show and no hint line needs the space.</summary>
        private bool SuggestionsVisible =>
            ticks >= 3   // not during the first frames, while the key that opened the menu may still be in the box
            && suggestions.Count > 0
            && !showTooShortHint
            && !showNoQualityHint
            && VisibleSuggestionRows > 0;

        private int SuggestionRowHeight => Math.Max(MinSuggestionRowHeight, Game1.smallFont.LineSpacing + 8);

        /// <summary>How many rows fit: at most the whole list, fewer on a very small screen.</summary>
        private int VisibleSuggestionRows
        {
            get
            {
                int top = textBox.Y + textBox.Height + SuggestionGap;
                int room = Game1.uiViewport.Height - top - 12 - SuggestionPadY * 2;
                return Math.Max(0, Math.Min(suggestions.Count, room / SuggestionRowHeight));
            }
        }

        private Rectangle GetSuggestionBounds()
        {
            return new Rectangle(
                textBox.X,
                textBox.Y + textBox.Height + SuggestionGap,
                textBox.Width,
                VisibleSuggestionRows * SuggestionRowHeight + SuggestionPadY * 2);
        }

        /// <summary>The row under a screen position, or -1.</summary>
        private int GetSuggestionRowAt(int x, int y)
        {
            Rectangle box = GetSuggestionBounds();
            int firstRowTop = box.Y + SuggestionPadY;

            if (x < box.X + SuggestionPadX || x >= box.Right - SuggestionPadX || y < firstRowTop)
                return -1;

            int row = (y - firstRowTop) / SuggestionRowHeight;
            return row < VisibleSuggestionRows ? row : -1;
        }

        /// <summary>Closes the list and keeps it closed until the text is edited.</summary>
        private void SuppressSuggestions()
        {
            suppressedText = textBox.Text;
            suggestions = Array.Empty<Suggestion>();
            activeSuggestion = -1;
        }

        /// <summary>Asks for the suggestions of the current text. Called when the text changes.</summary>
        private void RefreshSuggestions()
        {
            activeSuggestion = -1;

            string text = textBox.Text;
            if (suppressedText != null && text != suppressedText)
                suppressedText = null;

            string query = text.Trim();
            if (getSuggestions == null || suppressedText != null || query.Length == 0)
            {
                suggestions = Array.Empty<Suggestion>();
                return;
            }

            IReadOnlyList<Suggestion> found = getSuggestions(query);

            // a list that only repeats what was typed ("Cranberries" -> "Cranberries") is just noise
            if (found.Count == 1 && !found[0].IsCategory && string.Equals(found[0].Text, query, StringComparison.OrdinalIgnoreCase))
                found = Array.Empty<Suggestion>();

            suggestions = found;
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
            int lineHeight = Game1.smallFont.LineSpacing;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), box.X, box.Y, box.Width, box.Height, Color.White);

            // the hovered row wins over the arrow-key row (Enter still uses only the arrow-key row)
            int hovered = GetSuggestionRowAt(Game1.getMouseX(), Game1.getMouseY());
            int highlighted = hovered >= 0 ? hovered : activeSuggestion;

            for (int i = 0; i < rows; i++)
            {
                var row = new Rectangle(box.X + SuggestionPadX, box.Y + SuggestionPadY + i * rowHeight, box.Width - SuggestionPadX * 2, rowHeight);

                if (i == highlighted)
                    b.Draw(Game1.staminaRect, row, Game1.textColor * 0.16f);

                Suggestion suggestion = suggestions[i];
                var pos = new Vector2(row.X + 10, row.Y + (rowHeight - lineHeight) / 2f);
                const string CategoryTag = " (category)";
                float tagWidth = suggestion.IsCategory ? Game1.smallFont.MeasureString(CategoryTag).X : 0f;
                string label = FitLabel(suggestion.Text, row.Width - 20 - (int)Math.Ceiling(tagWidth));

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

                // a category word gets a quiet "(category)" after it; this is display only, the row still searches the plain word
                if (suggestion.IsCategory)
                    Utility.drawTextWithShadow(b, CategoryTag, Game1.smallFont, new Vector2(pos.X + Game1.smallFont.MeasureString(label).X, pos.Y), Game1.textColor * 0.6f);
            }
        }

        /// <summary>The history block is drawn while the search box is empty (and the menu was opened with a history).</summary>
        private bool HistoryAreaVisible => extraHeight > 0 && textBox.Text.Length == 0;

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
        private static List<(string Word, string Label, Rectangle Bounds, Rectangle Remove)> FlowHistory(List<string> history, int left, int right, int top)
        {
            var result = new List<(string, string, Rectangle, Rectangle)>();
            int lineHeight = Game1.smallFont.LineSpacing;

            int x = left;
            int y = top;

            foreach (string word in history)
            {
                string label = FitLabel(word);
                int w = (int)Game1.smallFont.MeasureString(label).X;
                int chipWidth = w + HistoryRemoveGap + RemoveMarkSize;   // the word and its x wrap together

                if (x > left && x + chipWidth > right)
                {
                    x = left;
                    y += HistoryRowHeight;
                }

                // Bounds = the word (click fills it in); Remove = the area around its x (click deletes it)
                var remove = new Rectangle(x + w + HistoryRemoveGap - HistoryRemovePad, y, RemoveMarkSize + HistoryRemovePad * 2, lineHeight);
                result.Add((word, label, new Rectangle(x, y, w, lineHeight), remove));
                x += chipWidth + HistoryGap;
            }

            return result;
        }

        private static int CountHistoryRows(List<string> history)
        {
            if (history.Count == 0)
                return 0;

            var chips = FlowHistory(history, 0, MenuWidth - 96, 0);
            return chips[chips.Count - 1].Bounds.Y / HistoryRowHeight + 1;
        }

        private List<(string Word, string Label, Rectangle Bounds, Rectangle Remove)> LayoutHistory()
        {
            return FlowHistory(history, xPositionOnScreen + 48, xPositionOnScreen + width - 48, yPositionOnScreen + HistoryRowsTop);
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
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), starButton.X, starButton.Y, starButton.Width, starButton.Height, Color.White);

            int quality = QualityValues[qualityIndex];
            int starSize = 8 * StarScale;
            var starPos = new Vector2(starButton.X + (starButton.Width - starSize) / 2, starButton.Y + (starButton.Height - starSize) / 2);

            if (quality < 0)
            {
                // "any quality": the same star shape as the real quality stars, drawn as an outline only
                b.Draw(GetOutlineStarTexture(b.GraphicsDevice), starPos, new Color(130, 75, 35));
                return;
            }

            if (quality == 0)
            {
                // "no quality": an empty frame, the same size, line width and color as the outline star, with no star in it
                Color frameColor = new Color(130, 75, 35);
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
                Color.White,
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
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            Utility.drawTextWithShadow(
                b,
                "Search chests for an item",
                Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 40),
                Game1.textColor);

            textBox.Draw(b);
            DrawMagnifierButton(b);
            DrawStarButton(b);
            if (CloseButtonVisible)
                DrawCloseButton(b);

            // validation hints (same native pixel font + shadow as the rest of the menu)
            if (showTooShortHint)
            {
                Utility.drawTextWithShadow(
                    b,
                    "Please enter at least 3 characters",
                    Game1.smallFont,
                    new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 166),
                    Color.DarkRed);
            }
            else if (showNoQualityHint)
            {
                string hint = Game1.parseText(
                    "Item found, but no matching quality. Try adjusting the star filter.",
                    Game1.smallFont,
                    width - 96);

                Utility.drawTextWithShadow(
                    b,
                    hint,
                    Game1.smallFont,
                    new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 166),
                    Color.DarkRed);
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

            // hovering the star shows which quality is selected
            if (starButton.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, QualityLabels[qualityIndex], Game1.smallFont);
            else if (MagnifierBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Search", Game1.smallFont);
            else if (HistoryVisible && GetClearHistoryBounds().Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Delete all search history", Game1.smallFont);
            else if (HistoryVisible && IsOverHistoryRemove(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, "Remove from history", Game1.smallFont);
        }

        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == textBox)
                Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }
    }
}