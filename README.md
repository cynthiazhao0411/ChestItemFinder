# ChestItemFinder

A Stardew Valley mod that tells you **which chest your item is in**.

Type what you are looking for, and every chest, fridge, Auto-Grabber, dresser or mannequin that holds it lights up, wherever it is: on the farm, in the buildings, in town or on Ginger Island. Your backpack is searched too.

**Download:** [Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/55016)

---

## Requirements

- Stardew Valley 1.6
- [SMAPI](https://smapi.io/) 4.0 or later
- Optional: [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098), to change the settings in game

## Installation

1. Install SMAPI.
2. Download the mod from Nexus Mods and unzip it into your `Stardew Valley/Mods` folder.
3. Start the game through SMAPI.

---

## How to use

Press **Left Shift + T** in game to open the search box (you can change the key in the settings).

Type an item name and press **Enter**. Each container that holds the item flashes, and a panel at the top of the screen tells you how many were found and where they are.

**Searched:** every chest (big, stone, Junimo, mini-fridge ...), the fridge of the farmhouse and cabins, Auto-Grabbers, the Mill's output bin, Junimo Huts, dressers, mannequins, the Lost and Found box in the Mayor's Manor, and **your backpack** (including what you are wearing). Kegs, jars and other machines are not storage and are not searched.

### What you can search for

| You type | It finds |
|---|---|
| `starfruit` | Starfruit, and every item with "starfruit" in its name |
| `fruit` | Every fruit (a whole category) |
| `wine`, `jelly`, `honey` ... | Every kind of that artisan good |
| `fruit wine` | Every wine made from fruit (also `vegetable juice`, `fish roe` ...) |
| `fruit, fish` | Fruit **or** fish (separate words with commas, up to 3), each in its own color |
| `dried grape`, `honey wine` | Raisins, Mead (common names for items the game calls differently) |
| `Egg (item)` | Only the item Egg, not the whole egg category |
| `gold star apple` | Only gold-star apples |
| `268` / `-79` | An item ID / a category ID |

- **Auto-complete:** suggestions appear while you type, with item icons. Hover a suggestion to see its category.
- **Typos are fine:** if nothing matches, the box suggests what you probably meant ("Did you mean: Starfruit").
- **Star filter:** the star button next to the box filters by quality (any, silver, gold, iridium, no star). You can also type the quality, e.g. `iridium star`.
- **History:** your last searches are shown under the box. Click one to search it again; pin the ones you use often.
- **Names that don't exist:** type `corn jelly` and the box tells you what corn really becomes (`Corn becomes Pickled Corn in a Preserves Jar.`), with the other options to click.
- **Singular or plural** never matters: `cranberry jelly` finds Cranberries Jelly.

### Several words, several colors

Search two or three words (`fruit, fish, gem`) and each word flashes in its own color, so you can tell at a glance which chest holds what. A chest that holds more than one of the words is split into stripes. Hover a place to see the words marked in the same colors. A single word looks exactly as before.

### The result panel

- Lists every place where the item was found, nearest first (by real walking distance).
- **Hover a place** to see exactly which items are there and how many. Searching one item shows one row per quality, with its star.
- **Keep highlights:** tick this box to keep the chests flashing while you walk to other maps. Hover it to bring the panel back.
- If a chest is off screen, an **arrow** at the edge of the screen points the way.
- **Containers inside containers:** a dresser or chest stored in your backpack or in another chest is searched too (one level deep). The panel shows it like `Backpack (in Birch Dresser)`.

### Your backpack

Your backpack is a place like any other, and it is listed first. If it holds a match, your character flashes and an arrow labelled **Backpack** floats above your head. Hover your character to list the matching items. Equipped hats, shirts, pants, shoes, rings and trinkets count as well (`Equipped`). Both the flash and the arrow can be switched off in the settings.

Opening the backpack page of the game menu counts as "opening" it, just like opening a chest.

### Chests that move or disappear

The highlight follows the chest itself, not the tile where it was found. If it is pushed, knocked one tile by a tool, moved by Robin or picked up and placed again, the flash, arrow and hover list move with it. If it is destroyed or its building is torn down, its highlight ends and a short note appears at the bottom left.

Opening a chest that holds the item clears the highlights (unless "Keep highlights" is ticked).

### Console command

`find_item <search>` in the SMAPI console does the same search as the box: several words, categories, aliases, typo help, and stars (`find_item gold star apple`). It still highlights and points the way in game, and it prints a list you can read and copy: place, container, tile, item count and stars, nearest first. It only reads; it never creates or moves items.

### Key conflicts

If your search key is also used by the game or by another mod, Generic Mod Config Menu shows the conflict in red under **Search key**, and the SMAPI console prints a warning when the game starts (so you also get it without Generic Mod Config Menu). Only mods that keep their keys in `config.json` can be checked.

**Controller:** press **A** on the "Keep highlights" box or on the red X of the result panel; press **B** to close the search box.

---

## Settings

Open them from the game's title screen or options with Generic Mod Config Menu, or edit `config.json` in the mod folder.

| Setting | Default | What it does |
|---|---|---|
| Search key | `LeftShift + T` | Opens and closes the search box |
| Highlight timeout | 3 min | How long chests keep flashing (or never stop) |
| Highlight opacity | 60% | How strong the flash is (0% hides it; the arrows still show the way) |
| Flash color | Yellow | The color of the first word (and of a single-word search) |
| Secondary color 1 | Pink | The color of the second word |
| Secondary color 2 | Sky Blue | The color of the third word |
| Flash for backpack match | On | Your character flashes while the backpack holds a match |
| Arrow for backpack match | On | The "Backpack" arrow above your head |

Colors: Yellow, Orange, Red, Pink, Purple, Blue, Sky Blue, Green, White, Black. If two words share a color, a small red note tells you.

If you update from an older version and want the new default colors, press **Reset** in Generic Mod Config Menu or delete `config.json`: colors you saved before are kept.

---

## Troubleshooting

**The "Keep highlights" box could not be clicked.**
Fixed in 1.6.1. It happened when the game's *Zoom Level* and *UI Scale* were set to different values. Please update to the latest version.

**The red "same key as ..." line under Search key.**
Another mod or the game uses the same key. Pick another key for the search box, or for the other mod.

**Something else is wrong?**
Please report it on the [Nexus Mods bugs page](https://www.nexusmods.com/stardewvalley/mods/55016?tab=bugs). If you can, add your SMAPI log (upload it at [smapi.io/log](https://smapi.io/log) and paste the link). It shows the error and your other mods, which makes the problem much faster to find.

---

## Changelog

### 1.8.0
- **Several colors:** a search with 2 or 3 words flashes each word in its own color (defaults: yellow, pink, sky blue). Chests that hold several words are striped. Pick the colors in Generic Mod Config Menu.
- **Backpack searched like a chest:** it is the first place in the list, your character flashes, an arrow above your head says "Backpack", and hovering your character lists the items. Equipped items count too. Two new settings turn the flash and the arrow off. The "Check Backpack" button is gone because it is no longer needed.
- **Containers inside containers:** dressers and chests stored in your backpack or in another chest are searched one level deep.
- **Highlights follow moved chests** and end when a chest is destroyed or picked up, with a short note.
- **Also searched:** mannequins and the Lost and Found box in the Mayor's Manor. Items lost when you died are listed with a reminder to buy them back from Marlon.
- **Key conflict warning** under Search key in Generic Mod Config Menu and in the SMAPI console at start-up.
- **Console command `find_item`** now prints a full list (place, container, tile, count, stars).
- **Search box:** grey "Type something to start!" when it is empty; the title above it is gone.
- **Settings page:** new "General" and "Backpack search" sections; a note when two colors are the same and when the opacity is 0%; tooltips no longer show a star where the font has no "=".
- **More complete item list:** lobster, crab and the other crab pot seafood now give roe, aged roe and bait; wild forage that restores energy can be juiced or pickled; wild flowers no longer make honey; seaweed and algae no longer give roe, smoked fish or bait.
- Cleaned up code that was no longer used. No change to how the mod looks or sounds.

### 1.7.1
- Fixed: the download included an old 1.0.0 copy of the mod in a subfolder, so mod managers (e.g. Stardrop) showed the wrong version and kept asking to update. No gameplay changes.

### 1.7.0
- **More places searched:** fridges (farmhouse, cabins, island farmhouse), mini-fridges, Auto-Grabbers, the Mill's output bin, Junimo Huts and dressers.
- **Nearest first by real walking distance**; the arrow always points to the nearest one. Ginger Island areas use the parrot-express names (Docks, Farm, Dig Site, Jungle).
- **Clearer names:** the panel and arrows say what each container is ("2 fridges", "Auto-Grabber: Barn").
- **Quality rows:** searching one item lists each quality on its own row, with its star.
- **New searches:** `fruit wine`-style categories, `Egg (item)`, every shirt / bed / tank top by family name, names with brackets, up to 3 words at once.
- **Helpful hints:** names that don't exist (`corn jelly`, `carp wine`) explain what the item really becomes; common names (`dried grape`, `honey wine`) find Raisins, Mead and more; singular / plural never matters.
- **Auto-complete:** every storable item (clothing, mannequins, wallpaper, flooring), correct icon colors (Apple Wine is red), order matches the tooltips.
- **Controller:** A for "Keep highlights" and the red X, B closes the search box.
- Added legendary fish and Sea Urchin roe, smoked fish and bait; removed Wheat / Hops / Tea Leaves Juice (they don't exist).
- Fixed: dyed shirt and pants icons, the recalled result panel now works like the normal one, the first click while the Copy bubble is open counts, and many smaller fixes.

### 1.6.1
- Fixed: the "Keep highlights" box could not be clicked when Zoom Level and UI Scale were different.
- The whole "Keep highlights" box is now clickable, not only the small checkbox.

Earlier versions: see the [Nexus Mods page](https://www.nexusmods.com/stardewvalley/mods/55016?tab=logs).

---

## Building from source

The project targets .NET 6 and uses [Pathoschild.Stardew.ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig).

1. Clone this repository.
2. Open `ChestItemFinder.csproj` in Visual Studio (or run `dotnet restore`).
3. Build. The mod is copied into your game's `Mods` folder automatically.

## Author

Cynthia Zhao
