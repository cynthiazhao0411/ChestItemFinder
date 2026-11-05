using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ChestItemFinder
{
    public class SearchMenu : IClickableMenu
    {
        private readonly TextBox textBox;
        private readonly Action<string> onConfirm;
        private readonly string? leakedChar;
        private int ticks = 0;

        /// <param name="onConfirm">Called with the search text when the player presses Enter.</param>
        /// <param name="leakedChar">The character of the key that opened this menu, if it types a character. It is removed if it leaks into the text box.</param>
        public SearchMenu(Action<string> onConfirm, string? leakedChar = null)
            : base(
                Game1.uiViewport.Width / 2 - 320,
                Game1.uiViewport.Height / 2 - 120,
                640,
                240,
                false)
        {
            this.onConfirm = onConfirm;
            this.leakedChar = leakedChar;

            textBox = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null,
                Game1.smallFont,
                Game1.textColor)
            {
                X = xPositionOnScreen + 48,
                Y = yPositionOnScreen + 104,
                Width = width - 96,
                Height = 48,
                Text = ""
            };

            textBox.OnEnterPressed += sender => Confirm();

            // connect the text box to the game's keyboard input system
            Game1.keyboardDispatcher.Subscriber = textBox;
            textBox.Selected = true;
        }

        private void Confirm()
        {
            string query = textBox.Text.Trim();
            exitThisMenu();
            if (query.Length > 0)
                onConfirm(query);
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
        }

        public override void receiveKeyPress(Keys key)
        {
            // do not call base: it would close the menu when typing letters like "E"
            if (key == Keys.Escape)
                exitThisMenu();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            textBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = textBox;
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

            Utility.drawTextWithShadow(
                b,
                "Enter = search    Esc = cancel",
                Game1.smallFont,
                new Vector2(xPositionOnScreen + 48, yPositionOnScreen + 170),
                Game1.textColor);

            drawMouse(b);
        }

        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == textBox)
                Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }
    }
}