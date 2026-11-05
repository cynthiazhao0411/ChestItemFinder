using System;
using System.Collections.Generic;
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
        private string notifyMessage = "";
        private float notifyTimer = 0f;
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

            helper.ConsoleCommands.Add("find_item", "Search for an item in all chests. Usage: find_item <item name>", OnFindItem);

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
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
                highlightedChests.Clear();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (notifyTimer > 0f)
                notifyTimer -= (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
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

            var countsByPlace = new Dictionary<string, int>();
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
                            countsByPlace[label] = countsByPlace.GetValueOrDefault(label) + 1;
                            found++;
                        }
                    }
                }
            }

            if (found == 0)
            {
                notifyMessage = $"'{itemName}' not found in any chest.";
            }
            else
            {
                string where = string.Join(", ", countsByPlace.Select(p => p.Value > 1 ? $"{p.Key} x{p.Value}" : p.Key));
                notifyMessage = $"Found {found} chest(s) containing '{itemName}'! ({where})";
            }

            notifyTimer = 6f;
            Monitor.Log(notifyMessage, LogLevel.Info);
        }

        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (notifyTimer <= 0f || string.IsNullOrEmpty(notifyMessage)) return;

            SpriteText.drawStringWithScrollCenteredAt(
                e.SpriteBatch,
                notifyMessage,
                Game1.viewport.Width / 2,
                120,
                alpha: Math.Min(1f, notifyTimer)
            );
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