using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ChestItemFinder
{
    /// <summary>
    /// A slider for the Generic Mod Config Menu that looks like the game's own sliders, but also draws
    /// a notch, a tick mark and a label (0%, 20%, ...) at every allowed stop, so the fixed steps are easy to see.
    /// </summary>
    /// <remarks>
    /// GMCM has no built-in slider with ticks, so this is registered through AddComplexOption.
    /// The slider edits its own copy of the value and only writes it to the config when the player saves
    /// (<see cref="Commit"/>), so Cancel really cancels. The player can drag the knob or click anywhere
    /// on the slider area (including the labels) to jump to the nearest stop.
    /// </remarks>
    internal class TickSlider
    {
        /// <summary>Pixel height reserved for the option in the config menu.</summary>
        public const int RowHeight = 84;

        private const int TrackWidth = 400;
        private const int TrackHeight = 24;   // 6px sprite * zoom 4
        private const int KnobWidth = 40;     // 10px sprite * zoom 4

        // the same sprites the game's own option sliders use
        private static readonly Rectangle TrackSource = new(403, 383, 6, 6);
        private static readonly Rectangle KnobSource = new(420, 441, 10, 6);

        private readonly Func<int> readConfig;
        private readonly Action<int> writeConfig;
        private readonly int min;
        private readonly int max;
        private readonly int step;

        private int value;
        private bool dragging;
        private bool wasMouseDown;

        public TickSlider(Func<int> readConfig, Action<int> writeConfig, int min, int max, int step)
        {
            this.readConfig = readConfig;
            this.writeConfig = writeConfig;
            this.min = min;
            this.max = max;
            this.step = step;
            this.value = Snap(readConfig());
        }

        /// <summary>Load the slider position from the config (menu opened, or form reset to defaults).</summary>
        public void Reload()
        {
            value = Snap(readConfig());
            dragging = false;
            wasMouseDown = true;   // require a fresh click before dragging can start
        }

        /// <summary>Write the slider position to the config (called just before GMCM saves).</summary>
        public void Commit()
        {
            writeConfig(value);
        }

        public void CancelDrag()
        {
            dragging = false;
        }

        public void Draw(SpriteBatch b, Vector2 position)
        {
            int x0 = (int)position.X;
            int trackY = (int)position.Y + 4;

            HandleMouse(x0, trackY);

            int stops = (max - min) / step + 1;
            float knobTravel = TrackWidth - KnobWidth;

            // track
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, TrackSource, x0, trackY, TrackWidth, TrackHeight, Color.White, 4f, false);

            for (int i = 0; i < stops; i++)
            {
                int stopValue = min + i * step;
                bool selected = stopValue == value;
                int centerX = x0 + KnobWidth / 2 + (int)Math.Round(knobTravel * i / (stops - 1));

                // faint notch inside the track
                b.Draw(Game1.staminaRect, new Rectangle(centerX - 1, trackY + 6, 2, TrackHeight - 12), Game1.textColor * 0.45f);

                // tick mark below the track (taller and colored for the selected stop)
                int tickTop = trackY + TrackHeight + 2;
                Color tickColor = selected ? Color.DarkRed : Game1.textColor;
                b.Draw(Game1.staminaRect, new Rectangle(centerX - 2, tickTop, 4, selected ? 16 : 10), tickColor);

                // label under the tick
                string label = $"{stopValue}%";
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(centerX - size.X / 2f, tickTop + 18), tickColor);
            }

            // knob (drawn last, on top of the notches)
            float t = (value - min) / (float)(max - min);
            b.Draw(Game1.mouseCursors, new Vector2(x0 + knobTravel * t, trackY), KnobSource, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
        }

        /// <summary>Handle clicking and dragging. GMCM does not forward mouse input to custom options, so poll it here.</summary>
        private void HandleMouse(int x0, int trackY)
        {
            bool down = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            var hitBox = new Rectangle(x0 - 12, trackY - 6, TrackWidth + 24, RowHeight - 8);
            if (down && !wasMouseDown && hitBox.Contains(mouseX, mouseY))
                dragging = true;
            if (!down)
                dragging = false;

            if (dragging)
            {
                int stops = (max - min) / step + 1;
                float t = Math.Clamp((mouseX - (x0 + KnobWidth / 2f)) / (TrackWidth - KnobWidth), 0f, 1f);
                int newValue = min + (int)Math.Round(t * (stops - 1)) * step;
                if (newValue != value)
                {
                    value = newValue;
                    Game1.playSound("drumkit6");
                }
            }

            wasMouseDown = down;
        }

        /// <summary>Round to the nearest allowed stop and keep it within range.</summary>
        private int Snap(int raw)
        {
            int snapped = min + (int)Math.Round((raw - min) / (double)step) * step;
            return Math.Clamp(snapped, min, max);
        }
    }
}