# ChestItemFinder

A Stardew Valley mod (C#, SMAPI) that finds which chests contain a given item.

## Features
- Search all chests across every location, including Ginger Island
- Matches both localized display names and internal English names
- Case-insensitive partial matching (e.g. `gem` finds all gem items)
- Matching chests pulse yellow in-world; on-screen message shows how many were found and where
- Highlights clear automatically after opening a chest

## Usage
In the SMAPI console:
`find_item Ancient Fruit`

## Tech
C#, .NET 6, SMAPI, Stardew Valley 1.6
