using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ChestItemFinder
{
    /// <summary>
    /// The search box's text field: a real single-line editor built on the game's TextBox (so it still plugs into the game's keyboard dispatcher),
    /// with what the game's box does not have:
    ///   - a caret that can stand anywhere in the text: placed with the mouse, moved with Left / Right / Home / End;
    ///   - a selection: Shift + Left / Right / Home / End, mouse drag, Shift + click, double click (one word), triple click or Ctrl+A (everything);
    ///     typing, Backspace, Delete, Ctrl+X and Ctrl+V replace or remove the selection;
    ///   - word jumps: Ctrl + Left / Right (Windows / Linux), Option + Left / Right (Mac); Cmd + Left / Right = Home / End on Mac;
    ///   - Ctrl shortcuts (Cmd on Mac) are never typed in, and the game's own paste is switched off: SearchMenu does every paste itself, exactly once;
    ///   - the text never runs under the buttons at the right end of the box: it scrolls sideways so the caret always stays in view,
    ///     inside <see cref="TextAreaWidth"/>;
    ///   - at most <see cref="SearchMenu.MaxTextLength"/> characters (a paste is cut to what still fits);
    ///   - a right click on the text opens SearchMenu's small "Copy" bubble (the selection, or the whole text).
    /// The text itself still lives in TextBox.Text, so every other part of the mod reads and sets it as before. When it is set from outside
    /// (a history word, a suggestion, the clear button), the caret simply goes to the end.
    /// </summary>
    /// <remarks>
    /// Keys: SearchMenu.receiveKeyPress calls <see cref="HandleNavigationKey"/> (and repeats it while the key is held). Characters, Backspace and Enter
    /// come through the game's keyboard dispatcher (RecieveTextInput / RecieveCommandInput), like in every game text box.
    /// Mouse: SearchMenu calls <see cref="MouseDown"/>, <see cref="MouseDrag"/> and <see cref="MouseUp"/>.
    /// </remarks>
    internal sealed class SearchTextBox : TextBox
    {
        /// <summary>Text position inside the box, the same as the game's own text box.</summary>
        private const int TextInsetX = 16;
        private const int TextInsetY = 8;

        /// <summary>Room kept free right of the text for the caret.</summary>
        private const int CaretRoom = 6;

        /// <summary>The caret stays solid this long after every edit or move, then blinks (half a second on, half off).</summary>
        private const int CaretSolidMs = 500;

        private static readonly Color SelectionColor = new Color(70, 130, 210) * 0.40f;

        private readonly Texture2D background;
        private readonly SpriteFont font;
        private readonly Color textColor;

        /// <summary>Caret position (0 = before the first character, Text.Length = after the last).</summary>
        private int caret;

        /// <summary>The other end of the selection. Equal to <see cref="caret"/> = nothing selected.</summary>
        private int anchor;

        /// <summary>Index of the first character drawn (the text scrolls sideways when it is wider than the text area).</summary>
        private int scroll;

        /// <summary>The text as this box last knew it. A different Text means it was set from outside: the caret goes to the end.</summary>
        private string knownText = "";

        private DateTime caretSolidUntil = DateTime.MinValue;

        private bool mouseSelecting;

        // a series of fast clicks on the text, counted here (like Windows): 1 = place the caret, 2 = select the word, 3 = select everything,
        // a 4th click starts a new series. Timed with a real stopwatch, so nothing in the game (time speed, frame rate, paused game) can disturb it.
        private static readonly System.Diagnostics.Stopwatch clickClock = System.Diagnostics.Stopwatch.StartNew();
        private long lastClickMs = -100000;
        private int lastClickX = -10000;
        private int lastClickY = -10000;
        private int clickCount;

        /// <summary>The next click belongs to the same series if it comes at most this many milliseconds after the last one...</summary>
        internal const int MultiClickMs = 450;

        /// <summary>...and at most this many pixels away from it (the mouse may shake a little between the clicks).</summary>
        private const int MultiClickSlop = 12;

        /// <summary>Width in pixels that the text may use, measured from the left text edge (X + 16). Set by the menu: up to the paste button.</summary>
        public int TextAreaWidth { get; set; }

        public SearchTextBox(Texture2D background, SpriteFont font, Color textColor)
            : base(background, null, font, textColor)
        {
            this.background = background;
            this.font = font;
            this.textColor = textColor;

            limitWidth = false;                       // the length limit is the only limit; the box scrolls, so the text never leaves it
            textLimit = SearchMenu.MaxTextLength;
        }

        // ===================== state =====================

        public bool HasSelection
        {
            get
            {
                Sync();
                return anchor != caret;
            }
        }

        private int SelStart => Math.Min(anchor, caret);
        private int SelEnd => Math.Max(anchor, caret);

        /// <summary>The selected text ("" if nothing is selected).</summary>
        public string SelectedText
        {
            get
            {
                Sync();
                return anchor == caret ? "" : Text.Substring(SelStart, SelEnd - SelStart);
            }
        }

        /// <summary>True while the mouse button that started on the text is still held (a drag selection is going on).</summary>
        public bool IsMouseSelecting => mouseSelecting;

        /// <summary>Picks up a text that was set from outside (TextBox.Text is not virtual, so this is checked before every use).</summary>
        private void Sync()
        {
            string text = Text;
            if (!ReferenceEquals(text, knownText) && text != knownText)
            {
                knownText = text;
                caret = anchor = text.Length;
                EnsureCaretVisible();
            }

            caret = Math.Clamp(caret, 0, text.Length);
            anchor = Math.Clamp(anchor, 0, text.Length);
            scroll = Math.Clamp(scroll, 0, text.Length);
        }

        /// <summary>Sets the text from inside this box, with the caret (and no selection) at <paramref name="newCaret"/>.</summary>
        private void SetTextAndCaret(string newText, int newCaret)
        {
            Text = newText;          // the game's setter drops characters its font cannot draw
            knownText = Text;
            caret = anchor = Math.Clamp(newCaret, 0, Text.Length);
            EnsureCaretVisible();
            KeepCaretSolid();
        }

        private void KeepCaretSolid()
        {
            caretSolidUntil = DateTime.UtcNow.AddMilliseconds(CaretSolidMs);
        }

        // ===================== editing =====================

        public void SelectAll()
        {
            Sync();
            anchor = 0;
            caret = Text.Length;
            EnsureCaretVisible();
        }

        public void ClearSelection()
        {
            Sync();
            anchor = caret;
        }

        /// <summary>
        /// Puts a text at the caret, in place of the selection if there is one. Only as much as still fits into <see cref="SearchMenu.MaxTextLength"/>
        /// characters is put in; the rest is cut off. False if nothing could be put in (empty text, or the box is full).
        /// </summary>
        public bool InsertText(string insert)
        {
            Sync();
            if (string.IsNullOrEmpty(insert))
                return false;

            string text = Text;
            int start = SelStart;
            int end = SelEnd;
            int room = SearchMenu.MaxTextLength - (text.Length - (end - start));
            if (room <= 0)
                return false;

            if (insert.Length > room)
                insert = insert.Substring(0, room);

            SetTextAndCaret(text.Substring(0, start) + insert + text.Substring(end), start + insert.Length);
            return true;
        }

        /// <summary>Removes the selection. False if nothing was selected.</summary>
        public bool DeleteSelection()
        {
            Sync();
            if (anchor == caret)
                return false;

            int start = SelStart;
            SetTextAndCaret(Text.Remove(start, SelEnd - start), start);
            return true;
        }

        /// <summary>Backspace: the selection, or the character (with <paramref name="word"/>: the word) before the caret.</summary>
        private void DeleteBackward(bool word)
        {
            if (DeleteSelection() || caret == 0)
                return;

            int from = word ? PreviousWordStart(caret) : caret - 1;
            SetTextAndCaret(Text.Remove(from, caret - from), from);
        }

        /// <summary>Delete: the selection, or the character (with <paramref name="word"/>: the word) after the caret.</summary>
        private void DeleteForward(bool word)
        {
            if (DeleteSelection() || caret >= Text.Length)
                return;

            int to = word ? NextWordEnd(caret) : caret + 1;
            SetTextAndCaret(Text.Remove(caret, to - caret), caret);
        }

        private void MoveCaretTo(int position, bool extend)
        {
            Sync();
            caret = Math.Clamp(position, 0, Text.Length);
            if (!extend)
                anchor = caret;
            EnsureCaretVisible();
            KeepCaretSolid();
        }

        /// <summary>
        /// Left / Right / Home / End / Delete. <paramref name="shift"/> extends the selection, <paramref name="word"/> moves (or deletes) by whole words,
        /// <paramref name="line"/> (Cmd on Mac) jumps to the start / end like Home / End. Returns true if the key was one of these.
        /// </summary>
        public bool HandleNavigationKey(Keys key, bool shift, bool word, bool line)
        {
            Sync();

            switch (key)
            {
                case Keys.Left:
                    if (line)
                        MoveCaretTo(0, shift);
                    else if (anchor != caret && !shift && !word)
                        MoveCaretTo(SelStart, false);   // a selection collapses to its left end, like every text field
                    else
                        MoveCaretTo(word ? PreviousWordStart(caret) : caret - 1, shift);
                    return true;

                case Keys.Right:
                    if (line)
                        MoveCaretTo(Text.Length, shift);
                    else if (anchor != caret && !shift && !word)
                        MoveCaretTo(SelEnd, false);
                    else
                        MoveCaretTo(word ? NextWordEnd(caret) : caret + 1, shift);
                    return true;

                case Keys.Home:
                    MoveCaretTo(0, shift);
                    return true;

                case Keys.End:
                    MoveCaretTo(Text.Length, shift);
                    return true;

                case Keys.Delete:
                    DeleteForward(word);
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '\'' || ch == '\u2019';   // straight and curly apostrophe ("Farmer's", "Farmer’s")

        private int PreviousWordStart(int from)
        {
            string text = Text;
            int i = Math.Clamp(from, 0, text.Length);
            while (i > 0 && !IsWordChar(text[i - 1]))
                i--;
            while (i > 0 && IsWordChar(text[i - 1]))
                i--;
            return i;
        }

        private int NextWordEnd(int from)
        {
            string text = Text;
            int i = Math.Clamp(from, 0, text.Length);
            while (i < text.Length && !IsWordChar(text[i]))
                i++;
            while (i < text.Length && IsWordChar(text[i]))
                i++;
            return i;
        }

        // ===================== mouse =====================

        /// <summary>The caret position nearest to a screen x (the visible part of the text is measured; left of it = the first visible character).</summary>
        private int IndexAtX(int screenX)
        {
            Sync();
            string text = Text;
            float offset = screenX - (X + TextInsetX);
            if (offset <= 0)
                return scroll;

            float previous = 0f;
            for (int i = scroll + 1; i <= text.Length; i++)
            {
                float width = font.MeasureString(text.Substring(scroll, i - scroll)).X;
                if (width >= offset)
                    return offset - previous < width - offset ? i - 1 : i;   // the nearer edge of the character under the mouse
                previous = width;
            }

            return text.Length;
        }

        /// <summary>
        /// A left click on the text (the menu calls this for every click there). Fast clicks in a row are counted like Windows does:
        /// 1st = place the caret (with <paramref name="shift"/>: extend the selection to there; a drag then selects), 2nd = select the word under the
        /// mouse, 3rd = select the whole text; the 4th starts over.
        /// </summary>
        public void Click(int screenX, int screenY, bool shift)
        {
            long now = clickClock.ElapsedMilliseconds;
            bool sameSeries = now - lastClickMs <= MultiClickMs
                && Math.Abs(screenX - lastClickX) <= MultiClickSlop
                && Math.Abs(screenY - lastClickY) <= MultiClickSlop;

            clickCount = sameSeries && clickCount < 3 ? clickCount + 1 : 1;
            lastClickMs = now;
            lastClickX = screenX;
            lastClickY = screenY;

            MouseDown(screenX, shift && clickCount == 1, clickCount);
        }

        /// <summary>One click of a series: 1 = place the caret (Shift: extend the selection), 2 = select the word, 3 or more = select everything.</summary>
        private void MouseDown(int screenX, bool shift, int clickCount)
        {
            Sync();

            if (clickCount >= 3)
            {
                SelectAll();
                mouseSelecting = false;
                KeepCaretSolid();
                return;
            }

            int index = IndexAtX(screenX);

            if (clickCount == 2)
            {
                string text = Text;
                int start = index, end = index;
                if (index < text.Length && IsWordChar(text[index]) || index > 0 && IsWordChar(text[index - 1]))
                {
                    while (start > 0 && IsWordChar(text[start - 1]))
                        start--;
                    while (end < text.Length && IsWordChar(text[end]))
                        end++;
                }

                anchor = start;
                caret = end;
                mouseSelecting = false;
                EnsureCaretVisible();
                KeepCaretSolid();
                return;
            }

            caret = index;
            if (!shift)
                anchor = index;
            mouseSelecting = true;
            EnsureCaretVisible();
            KeepCaretSolid();
        }

        /// <summary>The mouse moved with the button held: the selection follows it (past the edges, the text scrolls along).</summary>
        public void MouseDrag(int screenX)
        {
            if (!mouseSelecting)
                return;

            Sync();
            int left = X + TextInsetX;
            int right = left + Math.Max(0, TextAreaWidth - CaretRoom);

            if (screenX < left)
                caret = Math.Max(0, scroll - 1);
            else if (screenX > right)
                caret = Math.Min(Text.Length, VisibleEnd() + 1);
            else
                caret = IndexAtX(screenX);

            EnsureCaretVisible();
            KeepCaretSolid();
        }

        public void MouseUp()
        {
            mouseSelecting = false;
        }

        // ===================== keyboard input from the game's dispatcher =====================

        /// <summary>True while Ctrl (Cmd on Mac) is held without Alt: the key press is a shortcut, never text (Ctrl + Alt is AltGr on some keyboards).</summary>
        internal static bool IsShortcutModifierDown()
        {
            KeyboardState keyboard = Game1.input.GetKeyboardState();
            bool alt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
            return IsCtrlDown(keyboard) && !alt || IsMac && IsCommandDown(keyboard);
        }

        internal static bool IsMac => OperatingSystem.IsMacOS();

        internal static bool IsCtrlDown(KeyboardState keyboard) => keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);

        /// <summary>The Command key of a Mac keyboard (reported as the Windows key).</summary>
        internal static bool IsCommandDown(KeyboardState keyboard) => keyboard.IsKeyDown(Keys.LeftWindows) || keyboard.IsKeyDown(Keys.RightWindows);

        public override void RecieveTextInput(char inputChar)
        {
            if (!Selected || IsShortcutModifierDown() || char.IsControl(inputChar))
                return;   // Ctrl / Cmd + a letter is a shortcut (the menu handles it), never a typed character

            InsertText(inputChar.ToString());
        }

        public override void RecieveTextInput(string text)
        {
            // the game's own paste arrives here as one string while Ctrl is held: it is dropped, the menu pastes by itself (exactly once)
            if (!Selected || IsShortcutModifierDown() || string.IsNullOrEmpty(text))
                return;

            InsertText(text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
        }

        public override void RecieveCommandInput(char command)
        {
            switch (command)
            {
                // Ctrl+A / Ctrl+C / Ctrl+V / Ctrl+X can arrive as control characters (the game's paste uses \u0016): handled by the menu only
                case '\u0001':
                case '\u0003':
                case '\u0016':
                case '\u0018':
                    return;

                case '\b':
                    if (Selected)
                    {
                        KeyboardState keyboard = Game1.input.GetKeyboardState();
                        bool word = IsMac
                            ? keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt)
                            : IsCtrlDown(keyboard);
                        DeleteBackward(word);
                    }
                    return;
            }

            base.RecieveCommandInput(command);   // Enter (search) and Tab, as in the game's box
        }

        // ===================== drawing =====================

        private int TextRoom => Math.Max(0, TextAreaWidth - CaretRoom);

        private float WidthBetween(int from, int to)
        {
            return to <= from ? 0f : font.MeasureString(Text.Substring(from, to - from)).X;
        }

        /// <summary>One past the last character that fits into the text area when drawing starts at <see cref="scroll"/>.</summary>
        private int VisibleEnd()
        {
            string text = Text;
            int room = TextRoom;
            int end = scroll;
            while (end < text.Length && WidthBetween(scroll, end + 1) <= room)
                end++;
            return end;
        }

        /// <summary>Scrolls just far enough that the caret is in view, and back to the start as soon as the whole text fits.</summary>
        private void EnsureCaretVisible()
        {
            string text = Text;
            int room = TextRoom;
            caret = Math.Clamp(caret, 0, text.Length);
            scroll = Math.Clamp(scroll, 0, text.Length);

            if (caret < scroll)
                scroll = caret;
            while (scroll < caret && WidthBetween(scroll, caret) > room)
                scroll++;

            // no empty room on the right while there is hidden text on the left (e.g. after deleting at the end)
            while (scroll > 0 && WidthBetween(scroll - 1, text.Length) <= room)
                scroll--;
        }

        /// <summary>Draws the box like the game does (same texture, same text position, same caret), plus the selection and sideways scrolling.</summary>
        public override void Draw(SpriteBatch spriteBatch, bool drawShadow = true)
        {
            Sync();

            // background: left cap, stretched middle, right cap (the game's own text box art)
            int srcHeight = Math.Min(Height, background.Height);
            spriteBatch.Draw(background, new Rectangle(X, Y, 16, Height), new Rectangle(0, 0, 16, srcHeight), Color.White);
            spriteBatch.Draw(background, new Rectangle(X + 16, Y, Width - 32, Height), new Rectangle(16, 0, 4, srcHeight), Color.White);
            spriteBatch.Draw(background, new Rectangle(X + Width - 16, Y, 16, Height), new Rectangle(background.Width - 16, 0, 16, srcHeight), Color.White);

            string text = Text;
            int end = VisibleEnd();
            string shown = text.Substring(scroll, end - scroll);
            var textPos = new Vector2(X + TextInsetX, Y + TextInsetY);

            // selection (only its visible part)
            if (anchor != caret)
            {
                int from = Math.Clamp(SelStart, scroll, end);
                int to = Math.Clamp(SelEnd, scroll, end);
                if (to > from)
                {
                    int x1 = (int)(textPos.X + WidthBetween(scroll, from));
                    int x2 = (int)Math.Ceiling(textPos.X + WidthBetween(scroll, to));
                    spriteBatch.Draw(Game1.staminaRect, new Rectangle(x1 - 1, (int)textPos.Y, x2 - x1 + 2, 32), SelectionColor);
                }
            }

            if (shown.Length > 0)
            {
                if (drawShadow)
                    Utility.drawTextWithShadow(spriteBatch, shown, font, textPos, textColor);
                else
                    spriteBatch.DrawString(font, shown, textPos, textColor);
            }

            // the caret: solid right after typing / moving, then blinking
            DateTime now = DateTime.UtcNow;
            bool caretVisible = now < caretSolidUntil || now.Millisecond % 1000 >= 500;
            if (Selected && caretVisible && caret >= scroll && caret <= end)
            {
                int caretX = (int)(textPos.X + WidthBetween(scroll, caret));
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(caretX, (int)textPos.Y, 3, 32), textColor);
            }
        }
    }
}