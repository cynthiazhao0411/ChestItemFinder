using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Menus;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using StardewValley.BellsAndWhistles;

namespace ChestItemFinder
{
    public class ModEntry : Mod
    {
        private List<(GameLocation location, Vector2 position)> highlightedChests = new();
        private string lastSearchQuery = "";
        private string notifyMessage = "";
        private float notifyTimer = 0f;

        public override void Entry(IModHelper helper)
        {
            helper.ConsoleCommands.Add("find_item", "Search for an item in all chests. Usage: find_item <item name>", OnFindItem);
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Display.RenderedHud += OnRenderedHud;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is ItemGrabMenu && e.NewMenu == null)
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

            lastSearchQuery = string.Join(" ", args);
            ScanChests(lastSearchQuery);
        }

        private IEnumerable<GameLocation> GetAllLocations()
        {
            foreach (var location in Game1.locations)
                yield return location;

            string[] gingerIslandLocations = {
                "IslandSouth", "IslandNorth", "IslandWest", "IslandEast", "IslandFarmHouse"
            };

            foreach (var name in gingerIslandLocations)
            {
                var loc = Game1.getLocationFromName(name);
                if (loc != null)
                    yield return loc;
            }
        }

        private void ScanChests(string itemName)
        {
            highlightedChests.Clear();
            int found = 0;
            var foundLocations = new List<string>();

            foreach (GameLocation location in GetAllLocations())
            {
                foreach (var obj in location.objects.Pairs)
                {
                    if (obj.Value is Chest chest)
                    {
                        bool chestHasItem = false;
                        foreach (var item in chest.Items)
                        {
                            if (item != null && (item.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(itemName, StringComparison.OrdinalIgnoreCase)))
                            {
                                chestHasItem = true;
                                break;
                            }
                        }
                        if (chestHasItem)
                        {
                            highlightedChests.Add((location, obj.Key));
                            if (!foundLocations.Contains(location.Name))
                                foundLocations.Add(location.Name);
                            found++;
                        }
                    }
                }
            }

            if (found == 0)
                notifyMessage = $"'{itemName}' not found in any chest.";
            else
            {
                string locations = string.Join(", ", foundLocations);
                notifyMessage = $"Found {found} chest(s) containing '{itemName}'! ({locations})";
            }

            notifyTimer = 5f;
        }

        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            if (string.IsNullOrEmpty(lastSearchQuery)) return;
            if (Game1.activeClickableMenu is not ItemGrabMenu menu) return;

            float pulse = (float)(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4) + 1) / 2f;
            float alpha = 0.3f + pulse * 0.5f;

            foreach (var slot in menu.ItemsToGrabMenu.inventory)
            {
                if (slot.item == null) continue;

                if (slot.item.DisplayName.Contains(lastSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                    slot.item.Name.Contains(lastSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    e.SpriteBatch.Draw(
                        Game1.fadeToBlackRect,
                        new Rectangle(slot.bounds.X, slot.bounds.Y, slot.bounds.Width, slot.bounds.Height),
                        Color.Yellow * alpha
                    );
                }
            }
        }

        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (notifyTimer <= 0f || string.IsNullOrEmpty(notifyMessage)) return;

            float alpha = Math.Min(1f, notifyTimer);

            SpriteText.drawStringWithScrollCenteredAt(
                e.SpriteBatch,
                notifyMessage,
                Game1.viewport.Width / 2,
                120,
                alpha: alpha
            );
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (highlightedChests.Count == 0) return;

            float pulse = (float)(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4) + 1) / 2f;
            float alpha = 0.3f + pulse * 0.5f;

            foreach (var (location, position) in highlightedChests)
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