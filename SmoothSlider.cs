using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ChestItemFinder
{
    /// <summary>
    /// A slider for the Generic Mod Config Menu with a few fixed stops (like the highlight timeout) that follows the mouse smoothly:
    /// while dragging, the knob sits exactly where the mouse is (it does not jump from stop to stop), and the text next to it shows the
    /// nearest stop. The position is only rounded to a stop when the mouse button is released and when the value is saved.
    /// No tick marks: it looks like the game's own slider, with the current value written to its right.
    /// </summary>
    /// <remarks>
    /// Registered through AddComplexOption, with the same mouse polling and edit-a-copy-then-<see cref="Commit"/> pattern as <see cref="TickSlider"/>
    /// (GMCM does not forward mouse input to custom options), so Cancel really cancels.
    /// </remarks>
    internal class SmoothSlider
    {
        /// <summary>Pixel height reserved for the option in the config menu.</summary>
        public const int RowHeight = 48;

        private const int TrackWidth = 400;   // same as the opacity slider (TickSlider), so the two tracks are exactly equally long
        private const int TrackHeight = 24;   // 6px sprite * zoom 4
        private const int KnobWidth = 40;     // 10px sprite * zoom 4
        private const int LabelGap = 16;      // between the track and the value text

        // the same sprites the game's own option sliders use
        private static readonly Rectangle TrackSource = new(403, 383, 6, 6);
        private static readonly Rectangle KnobSource = new(420, 441, 10, 6);

        private readonly Func<int> readConfig;
        private readonly Action<int> writeConfig;
        private readonly int stopCount;
        private readonly Func<int, string> formatStop;

        /// <summary>Slider position as a stop number with a fraction (0 = first stop, stopCount - 1 = last stop). Fractions only exist while dragging.</summary>
        private float position;
        private bool dragging;
        private bool wasMouseDown;

        /// <param name="readConfig">Current stop number (0 to stopCount - 1) from the config.</param>
        /// <param name="writeConfig">Receives the stop number when the player saves.</param>
        /// <param name="stopCount">How many fixed stops there are.</param>
        /// <param name="formatStop">The text shown for a stop number.</param>
        public SmoothSlider(Func<int> readConfig, Action<int> writeConfig, int stopCount, Func<int, string> formatStop)
        {
            this.readConfig = readConfig;
            this.writeConfig = writeConfig;
            this.stopCount = Math.Max(2, stopCount);
            this.formatStop = formatStop;
            this.position = ClampStop(readConfig());
        }

        /// <summary>Load the slider position from the config (menu opened, or form reset to defaults).</summary>
        public void Reload()
        {
            position = ClampStop(readConfig());
            dragging = false;
            wasMouseDown = true;   // require a fresh click before dragging can start
        }

        /// <summary>Write the nearest stop to the config (called just before GMCM saves).</summary>
        public void Commit()
        {
            writeConfig(NearestStop());
        }

        public void CancelDrag()
        {
            if (dragging)
                position = NearestStop();
            dragging = false;
        }

        public void Draw(SpriteBatch b, Vector2 drawPosition)
        {
            int x0 = (int)drawPosition.X;
            int trackY = (int)drawPosition.Y + (RowHeight - TrackHeight) / 2;

            HandleMouse(x0, trackY);

            float knobTravel = TrackWidth - KnobWidth;

            // track
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, TrackSource, x0, trackY, TrackWidth, TrackHeight, Color.White, 4f, false);

            // knob
            float t = position / (stopCount - 1);
            b.Draw(Game1.mouseCursors, new Vector2(x0 + knobTravel * t, trackY), KnobSource, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);

            // the nearest stop, written to the right of the track
            string label = formatStop(NearestStop());
            Vector2 size = Game1.dialogueFont.MeasureString(label);
            b.DrawString(Game1.dialogueFont, label, new Vector2(x0 + TrackWidth + LabelGap, drawPosition.Y + (RowHeight - size.Y) / 2f), Game1.textColor);
        }

        /// <summary>Handle clicking and dragging. GMCM does not forward mouse input to custom options, so poll it here.</summary>
        private void HandleMouse(int x0, int trackY)
        {
            bool down = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            var hitBox = new Rectangle(x0 - 12, trackY - 12, TrackWidth + 24, TrackHeight + 24);
            if (down && !wasMouseDown && hitBox.Contains(mouseX, mouseY))
                dragging = true;

            if (!down)
            {
                // button released: settle on the nearest stop
                if (dragging)
                    position = NearestStop();
                dragging = false;
            }

            if (dragging)
            {
                int before = NearestStop();

                float t = Math.Clamp((mouseX - (x0 + KnobWidth / 2f)) / (TrackWidth - KnobWidth), 0f, 1f);
                position = t * (stopCount - 1);   // follows the mouse exactly, no snapping while dragging

                if (NearestStop() != before)
                    Game1.playSound("drumkit6");
            }

            wasMouseDown = down;
        }

        private int NearestStop()
        {
            return (int)Math.Round(ClampStop(position));
        }

        private float ClampStop(float stop)
        {
            return Math.Clamp(stop, 0f, stopCount - 1);
        }
    }
}