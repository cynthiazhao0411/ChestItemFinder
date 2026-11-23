# ChestItemFinder

A Stardew Valley mod that tells you **which chest your item is in**.

Type what you are looking for, and every chest, fridge, Auto-Grabber or dresser that holds it lights up, wherever it is: on the farm, in the buildings, in town or on Ginger Island.

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

**Searched:** every chest (big, stone, Junimo, mini-fridge ...), the fridge of the farmhouse and cabins, Auto-Grabbers, the Mill's output bin, Junimo Huts and dressers. Kegs, jars and other machines are not storage and are not searched.

### What you can search for

| You type | It finds |
|---|---|
| `starfruit` | Starfruit, and every item with "starfruit" in its name |
| `fruit` | Every fruit (a whole category) |
| `wine`, `jelly`, `honey` ... | Every kind of that artisan good |
| `fruit wine` | Every wine made from fruit (also `vegetable juice`, `fish roe` ...) |
| `fruit, fish` | Fruit **or** fish (separate words with commas, up to 3) |
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

### The result panel

- Lists every place where the item was found, nearest first (by real walking distance).
- **Hover a place** to see exactly which items are there and how many. Searching one item shows one row per quality, with its star.
- **Keep highlights:** tick this box to keep the chests flashing while you walk to other maps. Hover it to bring the panel back.
- If a chest is off screen, an **arrow** at the edge of the screen points the way.
- Nothing in your chests? If the item is in your backpack, the panel offers **Check Backpack**.

Opening a chest that holds the item clears the highlights (unless "Keep highlights" is ticked).

**Controller:** press **A** on the "Keep highlights" box or on the red X of the result panel; press **B** to close the search box.

---

## Settings

Open them from the game's title screen or options with Generic Mod Config Menu, or edit `config.json` in the mod folder.

| Setting | Default | What it does |
|---|---|---|
| Search key | `LeftShift + T` | Opens and closes the search box |
| Highlight timeout | 3 min | How long chests keep flashing (or never stop) |
| Highlight opacity | 60% | How strong the flash is (0% hides it) |
| Highlight color | Yellow | Yellow, Red, Orange, Green, Blue, Purple, Black, White, Gray or Brown |

---

## Troubleshooting

**The "Keep highlights" box could not be clicked.**
Fixed in 1.6.1. It happened when the game's *Zoom Level* and *UI Scale* were set to different values. Please update to the latest version.

**Something else is wrong?**
Please report it on the [Nexus Mods bugs page](https://www.nexusmods.com/stardewvalley/mods/55016?tab=bugs). If you can, add your SMAPI log (upload it at [smapi.io/log](https://smapi.io/log) and paste the link). It shows the error and your other mods, which makes the problem much faster to find.

---

## Changelog

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
### 1.7.1
- Fixed: the download included an old 1.0.0 copy of the mod in a subfolder, so mod managers (e.g. Stardrop) showed the wrong version and kept asking to update. No gameplay changes.

---

## Building from source

The project targets .NET 6 and uses [Pathoschild.Stardew.ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig).

1. Clone this repository.
2. Open `ChestItemFinder.csproj` in Visual Studio (or run `dotnet restore`).
3. Build. The mod is copied into your game's `Mods` folder automatically.

## Author

Cynthia Zhao
