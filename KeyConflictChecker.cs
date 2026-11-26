using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace ChestItemFinder
{
    /// <summary>
    /// The red "same key as ..." lines under the Search key option of the Generic Mod Config Menu. Nothing is shown when there is no conflict.
    /// </summary>
    /// <remarks>
    /// Rules (Cindy, 2026-11-25):
    /// - Another mod's key conflicts when it shares any button with the search key that is not a lone modifier (Shift / Ctrl / Alt): T,
    ///   Alt + T and Ctrl + Shift + T all conflict with LeftShift + T, and so do 5 and LeftShift + 5. A modifier only counts on its own: a
    ///   search key that is just LeftShift conflicts with every key that uses LeftShift.
    /// - The game's own controls are single keys, and the search key is suppressed for the game when pressed (see OnButtonPressed), so the
    ///   game never sees LeftShift + T: Chat on T still works. A game control only conflicts when a search key is exactly that one key
    ///   (search key T vs Chat T, search key LeftShift vs Run).
    /// Other mods' keys come from their config.json on disk: every setting whose name contains Key / Button / Keybind / Hotkey and whose
    /// text value reads as keys. A mod that keeps its key in code only cannot be seen. Files that cannot be read are skipped.
    /// The mods are read again each time the config page opens; a changed search key is checked again at once (GMCM OnFieldChanged).
    /// When the game starts, the same check writes a warning to the SMAPI console (see CheckNow), for players without GMCM.
    /// </remarks>
    internal class KeyConflictChecker
    {
        private const int WrapWidth = 520;   // fits the value column of the GMCM page
        private const int MaxShown = 4;      // more conflicts are summed up in one last line
        private static readonly Color TextColor = new(200, 30, 30);

        private static readonly HashSet<SButton> Modifiers = new()
        {
            SButton.LeftShift, SButton.RightShift, SButton.LeftControl, SButton.RightControl, SButton.LeftAlt, SButton.RightAlt
        };

        private static readonly string[] KeyWords = { "key", "button", "keybind", "hotkey" };

        private readonly IModHelper helper;
        private readonly string ownModId;

        // the keys found when the page was opened: game controls (single buttons) and other mods' keybinds
        private List<(string Name, SButton Button)> gameKeys = new();
        private List<(string Mod, string Setting, KeybindList Keys)> modKeys = new();

        private List<string> lines = new();      // the wrapped red text for the search key as currently set on the page

        public KeyConflictChecker(IModHelper helper, string ownModId)
        {
            this.helper = helper;
            this.ownModId = ownModId;
        }

        /// <summary>Height of the option: no room at all without a conflict.</summary>
        public int Height => lines.Count == 0 ? 0 : lines.Count * LineHeight + 8;

        private static int LineHeight => (int)Game1.smallFont.MeasureString("Ay").Y;

        /// <summary>The config page was opened: read every key again and check the saved search key.</summary>
        public void Reload(KeybindList current)
        {
            gameKeys = ReadGameKeys();
            modKeys = ReadModKeys();
            SetKey(current);
        }

        /// <summary>
        /// Every conflict of the search key as one sentence each ("Same key as Chat in the game controls (T)."), reading every key afresh. Used when
        /// the game starts, so a player without GMCM (who never sees the red lines) still gets a warning in the SMAPI console.
        /// </summary>
        public List<string> CheckNow(KeybindList key)
        {
            gameKeys = ReadGameKeys();
            modKeys = ReadModKeys();
            return FindConflicts(key);
        }

        /// <summary>The search key changed on the page (or was reset): check it again.</summary>
        public void SetKey(KeybindList key)
        {
            lines = BuildLines(FindConflicts(key));
        }

        public void Draw(SpriteBatch b, Vector2 position)
        {
            float y = position.Y + 4;
            foreach (string line in lines)
            {
                b.DrawString(Game1.smallFont, line, new Vector2(position.X, y), TextColor);
                y += LineHeight;
            }
        }

        // ---- checking ----

        private List<string> FindConflicts(KeybindList search)
        {
            var found = new List<string>();
            var combos = search.Keybinds.Where(k => k.IsBound).Select(k => k.Buttons.Where(x => x != SButton.None).ToArray()).Where(k => k.Length > 0).ToList();
            if (combos.Count == 0) return found;

            foreach (var (name, button) in gameKeys)
            {
                if (combos.Any(c => c.Length == 1 && c[0] == button))
                    found.Add($"Same key as {name} in the game controls ({ButtonName(button)}).");
            }

            foreach (var (mod, setting, keys) in modKeys)
            {
                var hit = keys.Keybinds
                    .Where(k => k.IsBound)
                    .Select(k => k.Buttons.Where(x => x != SButton.None).ToArray())
                    .FirstOrDefault(other => combos.Any(c => Clashes(c, other)));
                if (hit != null)
                    found.Add($"Same key as {mod}: {setting} ({string.Join(" + ", hit.Select(ButtonName))}).");
            }

            return found.Distinct().ToList();
        }

        /// <summary>Whether two key combinations share a button that counts (see remarks).</summary>
        private static bool Clashes(SButton[] search, SButton[] other)
        {
            var searchMain = search.Where(x => !Modifiers.Contains(x)).ToList();
            if (searchMain.Count == 0)
                return search.Any(other.Contains);   // the search key is only modifiers: any shared button
            return searchMain.Any(other.Contains);
        }

        private static List<string> BuildLines(List<string> conflicts)
        {
            var shown = conflicts.Take(MaxShown).ToList();
            if (conflicts.Count > MaxShown)
                shown.Add($"... and {conflicts.Count - MaxShown} more.");

            var result = new List<string>();
            foreach (string text in shown)
                result.AddRange(Game1.parseText(text, Game1.smallFont, WrapWidth).Split('\n'));
            return result;
        }

        private static string ButtonName(SButton button) => button.ToString();

        // ---- reading the keys ----

        /// <summary>The game's own controls, as the player has set them (Options page).</summary>
        private static List<(string Name, SButton Button)> ReadGameKeys()
        {
            var result = new List<(string, SButton)>();
            var options = Game1.options;
            if (options == null) return result;

            void Add(string name, InputButton[]? buttons)
            {
                if (buttons == null) return;
                foreach (var input in buttons)
                {
                    SButton button = input.mouseLeft ? SButton.MouseLeft
                        : input.mouseRight ? SButton.MouseRight
                        : input.key != Keys.None ? input.key.ToSButton()
                        : SButton.None;
                    if (button != SButton.None) result.Add((name, button));
                }
            }

            Add("Check / Do Action", options.actionButton);
            Add("Cancel", options.cancelButton);
            Add("Use Tool", options.useToolButton);
            Add("Move Up", options.moveUpButton);
            Add("Move Right", options.moveRightButton);
            Add("Move Down", options.moveDownButton);
            Add("Move Left", options.moveLeftButton);
            Add("Menu", options.menuButton);
            Add("Run", options.runButton);
            Add("Chat", options.chatButton);
            Add("Map", options.mapButton);
            Add("Journal", options.journalButton);
            Add("Emote", options.emoteButton);
            Add("Toolbar Swap", options.toolbarSwap);
            Add("Inventory Slot 1", options.inventorySlot1);
            Add("Inventory Slot 2", options.inventorySlot2);
            Add("Inventory Slot 3", options.inventorySlot3);
            Add("Inventory Slot 4", options.inventorySlot4);
            Add("Inventory Slot 5", options.inventorySlot5);
            Add("Inventory Slot 6", options.inventorySlot6);
            Add("Inventory Slot 7", options.inventorySlot7);
            Add("Inventory Slot 8", options.inventorySlot8);
            Add("Inventory Slot 9", options.inventorySlot9);
            Add("Inventory Slot 10", options.inventorySlot10);
            Add("Inventory Slot 11", options.inventorySlot11);
            Add("Inventory Slot 12", options.inventorySlot12);
            return result;
        }

        /// <summary>The keybinds in the config.json of every other loaded mod (content packs have none of their own).</summary>
        private List<(string Mod, string Setting, KeybindList Keys)> ReadModKeys()
        {
            var result = new List<(string, string, KeybindList)>();
            foreach (IModInfo info in helper.ModRegistry.GetAll())
            {
                if (info.IsContentPack || string.Equals(info.Manifest.UniqueID, ownModId, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    // SMAPI does not expose the mod folder in IModInfo; its implementation has a public DirectoryPath
                    string? folder = info.GetType().GetProperty("DirectoryPath")?.GetValue(info) as string;
                    if (string.IsNullOrEmpty(folder)) continue;
                    string file = Path.Combine(folder, "config.json");
                    if (!File.Exists(file)) continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                    CollectKeys(doc.RootElement, "", info.Manifest.Name, result);
                }
                catch
                {
                    // only reading other mods' files: anything unreadable is skipped
                }
            }
            return result;
        }

        private static void CollectKeys(JsonElement element, string path, string mod, List<(string, string, KeybindList)> result)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var property in element.EnumerateObject())
            {
                string setting = path.Length == 0 ? property.Name : path + "." + property.Name;
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    CollectKeys(value, setting, mod, result);
                    continue;
                }
                if (!KeyWords.Any(w => property.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    continue;

                IEnumerable<string> texts = value.ValueKind switch
                {
                    JsonValueKind.String => new[] { value.GetString() ?? "" },
                    JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? ""),
                    _ => Array.Empty<string>()
                };
                foreach (string text in texts)
                {
                    // plain numbers would parse as button codes: not keys
                    if (string.IsNullOrWhiteSpace(text) || text.Trim().All(char.IsDigit)) continue;
                    if (KeybindList.TryParse(text, out KeybindList? keys, out _) && keys != null && keys.IsBound)
                        result.Add((mod, setting, keys));
                }
            }
        }
    }
}