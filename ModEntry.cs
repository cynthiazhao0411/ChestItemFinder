using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestItemFinder
{
    public class ModEntry : Mod
    {
        private List<(GameLocation location, Vector2 position, Chest chest)> highlightedChests = new();
        private string notifyTitle = "";
        private string notifyBody = "";
        // Real-world clock: not affected by in-game time (CJB Cheats Menu, TimeSpeed, Time Master,
        // Turbo, ...), so freezing, slowing or speeding up game time never changes these timers.
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long notifyUntilMs = 0;
        private long highlightUntilMs = -1;   // -1 = highlights never expire on their own
        private Vector2 lastGrabTile = new Vector2(-1, -1);
        private ModConfig Config = new();

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
            ["IslandSouth"] = "Ginger Island (South)",
            ["IslandSouthEast"] = "Ginger Island (Southeast)",
            ["IslandEast"] = "Ginger Island (East)",
            ["IslandWest"] = "Ginger Island (West)",
            ["IslandNorth"] = "Ginger Island (North)",
            ["IslandFarmHouse"] = "Ginger Island Farmhouse",
            ["IslandFarmCave"] = "Ginger Island Farm Cave",
            ["IslandHut"] = "Ginger Island Hut",
            ["IslandFieldOffice"] = "Ginger Island Field Office"
        };

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            Config.HighlightTimeoutMinutes = Math.Clamp(Config.HighlightTimeoutMinutes, 0, 120);

            helper.ConsoleCommands.Add("find_item", "Search for an item in all chests. Usage: find_item <item name>", OnFindItem);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearHighlights();
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.RenderedHud += OnRenderedHud;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        // Optional integration: lets players change the key in-game via Generic Mod Config Menu.
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null) return;

            api.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            api.AddSectionTitle(ModManifest, () => "ChestItemFinder");

            api.AddKeybind(
                mod: ModManifest,
                getValue: () => Config.SearchKey,
                setValue: value => Config.SearchKey = value,
                name: () => "Search key",
                tooltip: () => "Press this key in-game to open the chest search box."
            );

            api.AddNumberOption(
                mod: ModManifest,
                getValue: () => Config.HighlightTimeoutMinutes,
                setValue: value => Config.HighlightTimeoutMinutes = value,
                name: () => "Highlight timeout (minutes)",
                tooltip: () => "Real-world minutes before chest highlights disappear by themselves. 0 = never. Not affected by in-game time speed.",
                min: 0,
                max: 120,
                interval: 1
            );
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

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // remember which tile the player is interacting with (used to find the opened chest)
            if (Game1.activeClickableMenu == null)
                lastGrabTile = e.Cursor.GrabTile;

            if (e.Button != Config.SearchKey) return;
            if (Game1.activeClickableMenu != null) return;

            Helper.Input.Suppress(e.Button);
            Game1.activeClickableMenu = new SearchMenu(ScanChests, GetLeakedChar(Config.SearchKey));
        }

        // Opening ANY chest that contains the target clears ALL highlights.
        // Opening a chest that is not a target does nothing.
        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (highlightedChests.Count == 0) return;
            if (e.NewMenu is not ItemGrabMenu menu) return;

            bool openedTarget;
            if (menu.context is Chest opened)
                openedTarget = highlightedChests.Any(h => ReferenceEquals(h.chest, opened));
            else
                openedTarget = highlightedChests.Any(h => h.location == Game1.currentLocation && h.position == lastGrabTile);

            if (openedTarget)
                ClearHighlights();
        }

        private void ClearHighlights()
        {
            highlightedChests.Clear();
            highlightUntilMs = -1;
        }

        // Highlights fade away on their own after the configured number of REAL minutes.
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (highlightedChests.Count > 0 && highlightUntilMs >= 0 && clock.ElapsedMilliseconds >= highlightUntilMs)
            {
                ClearHighlights();
                Monitor.Log("Chest highlights expired.", LogLevel.Debug);
            }
        }

        private void OnFindItem(string command, string[] args)
        {
            if (args.Length == 0)
            {
                Monitor.Log("Usage: find_item <item name>", LogLevel.Info);
                return;
            }

            ScanChests(string.Join(" ", args));
        }

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
            ["IslandNorth"] = "N",
            ["IslandEast"] = "E",
            ["IslandSouth"] = "S",
            ["IslandSouthEast"] = "SE",
            ["IslandWest"] = "W"
        };

        private static readonly string[] IslandAreaOrder = { "N", "E", "SE", "S", "W" };

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

        private static string BuildPlaceSummary(Dictionary<string, int> mainCounts, Dictionary<string, int> islandCounts)
        {
            var parts = mainCounts.Select(p => FormatCount(p.Key, p.Value)).ToList();

            if (islandCounts.Count > 0)
            {
                var areas = islandCounts
                    .OrderBy(p => { int i = Array.IndexOf(IslandAreaOrder, p.Key); return i < 0 ? IslandAreaOrder.Length : i; })
                    .ThenBy(p => p.Key)
                    .Select(p => FormatCount(p.Key, p.Value));
                parts.Add("Ginger Island (" + string.Join(", ", areas) + ")");
            }

            return string.Join(", ", parts);
        }

        private static string GetFriendlyName(string internalName)
        {
            if (FriendlyNames.TryGetValue(internalName, out string? friendly))
                return friendly;
            if (internalName.StartsWith("Island", StringComparison.OrdinalIgnoreCase))
                return "Ginger Island (" + internalName.Substring(6) + ")";
            return internalName;
        }

        private static bool NameEquals(Item item, string query)
        {
            return item.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)
                || item.Name.Equals(query, StringComparison.OrdinalIgnoreCase);
        }

        private static bool NameContains(Item item, string query)
        {
            return item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void ScanChests(string rawQuery)
        {
            string itemName = rawQuery.Trim();
            highlightedChests.Clear();
            if (itemName.Length == 0) return;

            var locations = GetAllLocations().ToList();

            // If some item is named exactly what the player typed, match exactly
            // ("Starfruit" will not also match "Starfruit Wine").
            // Otherwise fall back to partial matching ("gem" finds all gems).
            bool exact = false;
            foreach (var (location, _) in locations)
            {
                foreach (var obj in location.objects.Pairs)
                {
                    if (obj.Value is Chest c && c.Items.Any(i => i != null && NameEquals(i, itemName)))
                    {
                        exact = true;
                        break;
                    }
                }
                if (exact) break;
            }

            var mainCounts = new Dictionary<string, int>();
            var islandCounts = new Dictionary<string, int>();
            int found = 0;

            foreach (var (location, label) in locations)
            {
                foreach (var obj in location.objects.Pairs)
                {
                    if (obj.Value is Chest chest)
                    {
                        bool chestHasItem = chest.Items.Any(i => i != null && (exact ? NameEquals(i, itemName) : NameContains(i, itemName)));
                        if (chestHasItem)
                        {
                            highlightedChests.Add((location, obj.Key, chest));
                            string? islandArea = GetIslandArea(location.Name);
                            if (islandArea != null)
                                islandCounts[islandArea] = islandCounts.GetValueOrDefault(islandArea) + 1;
                            else
                                mainCounts[label] = mainCounts.GetValueOrDefault(label) + 1;
                            found++;
                        }
                    }
                }
            }

            if (found == 0)
            {
                notifyTitle = $"'{itemName}' not found in any chest.";
                notifyBody = "";
            }
            else
            {
                string chestWord = found == 1 ? "chest" : "chests";
                notifyTitle = $"Found {found} {chestWord} containing '{itemName}'";
                notifyBody = BuildPlaceSummary(mainCounts, islandCounts);
            }

            notifyUntilMs = clock.ElapsedMilliseconds + 8000;
            highlightUntilMs = (found > 0 && Config.HighlightTimeoutMinutes > 0)
                ? clock.ElapsedMilliseconds + Config.HighlightTimeoutMinutes * 60_000L
                : -1;
            Monitor.Log(notifyTitle + (notifyBody.Length > 0 ? " | " + notifyBody : ""), LogLevel.Info);
        }

        // Draws the result as ONE compact parchment panel (the same box the game's own menus use).
        // The box is sized exactly around the text: title, a thin divider, then the wrapped location list.
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            float remaining = (notifyUntilMs - clock.ElapsedMilliseconds) / 1000f;
            if (remaining <= 0f || string.IsNullOrEmpty(notifyTitle)) return;

            SpriteBatch b = e.SpriteBatch;
            float alpha = Math.Min(1f, remaining / 0.6f);   // fade out during the last 0.6 seconds

            const float titleScale = 0.8f;
            const int padX = 32;
            const int padY = 26;
            const int gap = 10;

            int screenWidth = Game1.uiViewport.Width;
            int maxTextWidth = Math.Min(760, screenWidth - 160) - padX * 2;

            string title = Game1.parseText(notifyTitle, Game1.dialogueFont, (int)(maxTextWidth / titleScale));
            string body = notifyBody.Length > 0 ? Game1.parseText(notifyBody, Game1.smallFont, maxTextWidth) : "";

            Vector2 titleSize = Game1.dialogueFont.MeasureString(title) * titleScale;
            Vector2 bodySize = body.Length > 0 ? Game1.smallFont.MeasureString(body) : Vector2.Zero;

            int textWidth = (int)Math.Max(titleSize.X, bodySize.X);
            int boxWidth = textWidth + padX * 2;
            int boxHeight = (int)titleSize.Y + padY * 2;
            if (body.Length > 0)
                boxHeight += gap * 2 + 2 + (int)bodySize.Y;

            int boxX = (screenWidth - boxWidth) / 2;
            int boxY = 20;

            // the game's standard menu box
            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                boxX, boxY, boxWidth, boxHeight,
                Color.White * alpha);

            Vector2 textPos = new Vector2(boxX + padX, boxY + padY);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, textPos, Game1.textColor * alpha, titleScale);

            if (body.Length > 0)
            {
                int dividerY = (int)(textPos.Y + titleSize.Y) + gap;
                b.Draw(Game1.staminaRect, new Rectangle(boxX + padX, dividerY, textWidth, 2), Game1.textColor * 0.35f * alpha);

                Utility.drawTextWithShadow(b, body, Game1.smallFont, new Vector2(textPos.X, dividerY + 2 + gap), Game1.textColor * alpha);
            }
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (highlightedChests.Count == 0) return;

            float pulse = (float)(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4) + 1) / 2f;
            float alpha = 0.3f + pulse * 0.5f;

            foreach (var (location, position, _) in highlightedChests)
            {
                if (location != Game1.currentLocation) continue;

                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, position * 64f);

                e.SpriteBatch.Draw(
                    Game1.fadeToBlackRect,
                    new Rectangle((int)screenPos.X - 4, (int)screenPos.Y - 4, 72, 72),
                    Color.Yellow * alpha
                );
            }
        }
    }
}