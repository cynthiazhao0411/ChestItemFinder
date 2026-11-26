using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace ChestItemFinder
{
    /// <summary>
    /// A row of color boxes for the Generic Mod Config Menu: one click on a box picks that color. The picked box has a thick dark frame; the
    /// name of the color under the mouse (or, with the mouse elsewhere, of the picked color) is written to the right of the row.
    /// </summary>
    /// <remarks>
    /// Registered through AddComplexOption, with the same mouse polling and edit-a-copy-then-<see cref="Commit"/> pattern as the sliders
    /// (GMCM does not forward mouse input to custom options), so Cancel really cancels.
    /// </remarks>
    internal class ColorSwatchPicker
    {
        /// <summary>Pixel height reserved for the option in the config menu.</summary>
        public const int RowHeight = 48;

        private const int BoxSize = 30;
        private const int BoxGap = 8;
        private const int LabelGap = 16;   // between the last box and the color name

        private readonly Func<string> readConfig;
        private readonly Action<string> writeConfig;
        private readonly IReadOnlyList<(string Name, Color Color)> choices;

        private string picked;
        private bool wasMouseDown;

        /// <summary>Room for the note under the row (see <see cref="SameAsNote"/>).</summary>
        private const int NoteHeight = 30;
        private static readonly Color NoteColor = new(200, 30, 30);

        /// <summary>The color picked on the page right now (saved only with the page).</summary>
        public string Picked => picked;

        /// <summary>A note under the row ("Same as Flash color"), or null for none. Set by the owner, who knows the other rows.</summary>
        public Func<string?>? SameAsNote { get; set; }

        /// <summary>Height of the option: one row, and the room for the note while there is one.</summary>
        public int Height => RowHeight + (SameAsNote?.Invoke() != null ? NoteHeight : 0);

        /// <param name="readConfig">The color name in the config.</param>
        /// <param name="writeConfig">Receives the picked color name when the player saves.</param>
        /// <param name="choices">The boxes, left to right: the name of each color and how it is drawn.</param>
        public ColorSwatchPicker(Func<string> readConfig, Action<string> writeConfig, IReadOnlyList<(string Name, Color Color)> choices)
        {
            this.readConfig = readConfig;
            this.writeConfig = writeConfig;
            this.choices = choices;
            this.picked = readConfig();
        }

        /// <summary>Load the picked color from the config (menu opened, or form reset to defaults).</summary>
        public void Reload()
        {
            picked = readConfig();
            wasMouseDown = true;   // a click that was already down (the one that opened the menu) does not pick a color
        }

        /// <summary>Write the picked color to the config (called just before GMCM saves).</summary>
        public void Commit()
        {
            writeConfig(picked);
        }

        public void Draw(SpriteBatch b, Vector2 drawPosition)
        {
            int x0 = (int)drawPosition.X;
            int boxY = (int)drawPosition.Y + (RowHeight - BoxSize) / 2;
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            bool down = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;

            string? hovered = null;
            for (int i = 0; i < choices.Count; i++)
            {
                var (name, color) = choices[i];
                var box = new Rectangle(x0 + i * (BoxSize + BoxGap), boxY, BoxSize, BoxSize);
                bool isPicked = string.Equals(name, picked, StringComparison.OrdinalIgnoreCase);
                bool isHovered = box.Contains(mouseX, mouseY);
                if (isHovered)
                {
                    hovered = name;
                    if (down && !wasMouseDown && !isPicked)
                    {
                        picked = name;
                        isPicked = true;
                        Game1.playSound("drumkit6");
                    }
                }

                // the picked box: a thick dark frame around it; the hovered one: a thin frame; every box has a thin dark edge
                if (isPicked)
                    b.Draw(Game1.staminaRect, Inflate(box, 5), Game1.textColor);
                else if (isHovered)
                    b.Draw(Game1.staminaRect, Inflate(box, 3), Game1.textColor * 0.6f);
                b.Draw(Game1.staminaRect, Inflate(box, 1), Game1.textColor * 0.8f);
                b.Draw(Game1.staminaRect, box, color);
                if (isPicked)
                    b.Draw(Game1.staminaRect, Inflate(box, -BoxSize / 2 + 3), Contrast(color));   // a small dot in the middle: picked
            }

            wasMouseDown = down;

            // the name of the hovered color, or of the picked one
            string label = hovered ?? picked;
            Vector2 size = Game1.dialogueFont.MeasureString(label);
            float labelX = x0 + choices.Count * (BoxSize + BoxGap) - BoxGap + LabelGap;
            b.DrawString(Game1.dialogueFont, label, new Vector2(labelX, drawPosition.Y + (RowHeight - size.Y) / 2f), hovered != null ? Game1.textColor * 0.75f : Game1.textColor);

            // the note under the boxes ("Same as Flash color")
            string? note = SameAsNote?.Invoke();
            if (note != null)
                b.DrawString(Game1.smallFont, note, new Vector2(x0, drawPosition.Y + RowHeight), NoteColor);
        }

        private static Rectangle Inflate(Rectangle rect, int by)
        {
            rect.Inflate(by, by);
            return rect;
        }

        /// <summary>Black on a light color, white on a dark one (the dot in the middle of the picked box).</summary>
        private static Color Contrast(Color color)
        {
            float light = (color.R * 0.299f + color.G * 0.587f + color.B * 0.114f) / 255f;
            return light > 0.55f ? Color.Black : Color.White;
        }
    }
}