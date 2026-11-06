using System;
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

        /// <summary>Menu height. Taller than before to make room for the (possibly two-line) hint row.</summary>
        private const int MenuHeight = 316;

        /// <summary>The star filter values, in click order. -1 = any quality; 1 = silver, 2 = gold, 4 = iridium (Stardew's Quality values).</summary>
        private static readonly int[] QualityValues = { -1, 1, 2, 4 };
        private static readonly string[] QualityLabels = { "Any quality", "Silver star", "Gold star", "Iridium star" };

        // quality star sprites in Game1.mouseCursors (the same ones the game draws on item icons)
        private static readonly Rectangle SilverStarSource = new(338, 400, 8, 8);
        private static readonly Rectangle GoldStarSource = new(346, 400, 8, 8);
        private static readonly Rectangle IridiumStarSource = new(346, 392, 8, 8);

        /// <summary>Zoom of the star inside the button: 8px sprite * 3 = 24px star in the 48px button, so the star leaves some room around it.</summary>
        private const int StarScale = 3;

        /// <summary>Line width of the outline star: one game pixel (= StarScale screen pixels), the same weight as the game's own pixel art.</summary>
        private const int OutlineThickness = StarScale;

        private static Texture2D? outlineStarTexture;

        private readonly TextBox textBox;
        private readonly Func<string, int, SearchOutcome> onConfirm;
        private readonly string? leakedChar;
        private readonly Rectangle starButton;
        private int ticks = 0;

        private bool showTooShortHint = false;
        private bool showNoQualityHint = false;
        private int qualityIndex = 0;
        private string lastText = "";

        /// <param name="onConfirm">Called with the search text and the star filter (-1 = any) when the player presses Enter. Return <see cref="SearchOutcome.NoMatchingQuality"/> to keep the menu open.</param>
        /// <param name="leakedChar">The character of the key that opened this menu, if it types a character. It is removed if it leaks into the text box.</param>
        public SearchMenu(Func<string, int, SearchOutcome> onConfirm, string? leakedChar = null)
            : base(
                Game1.uiViewport.Width / 2 - 320,
                Game1.uiViewport.Height / 2 - MenuHeight / 2,
                640,
                MenuHeight,
                false)
        {
            this.onConfirm = onConfirm;
            this.leakedChar = leakedChar;

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

        private void Confirm()
        {
            string query = textBox.Text.Trim();

            if (!IsQueryValid(query))
            {
                // block the search, keep the menu open, and show the hint
                showTooShortHint = true;
                showNoQualityHint = false;
                lastText = textBox.Text;
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
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            // do not call base: it would close the menu when typing letters like "E"
            if (key == Keys.Escape)
                exitThisMenu();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (starButton.Contains(x, y))
            {
                // empty star -> silver -> gold -> iridium -> empty star ...
                qualityIndex = (qualityIndex + 1) % QualityValues.Length;
                showNoQualityHint = false;
                Game1.playSound("smallSelect");
            }

            textBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = textBox;
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
            DrawStarButton(b);

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

            Utility.drawTextWithShadow(
                b,
                "Enter = search    Esc = cancel",
                Game1.smallFont,
                new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 256),
                Game1.textColor);

            drawMouse(b);

            // hovering the star shows which quality is selected
            if (starButton.Contains(Game1.getMouseX(), Game1.getMouseY()))
                IClickableMenu.drawHoverText(b, QualityLabels[qualityIndex], Game1.smallFont);
        }

        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == textBox)
                Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }
    }
}