# Wheres My Money - ExileAPI Plugin

A ground loot overlay for Path of Exile that shows you what's on the floor, 
prices everything via poe.ninja, and tallies your currency per map.

## Features

- **Currency Overlay** — All currency/scarabs/fragments/essences etc. on the ground, 
  grouped by type with stack counts and chaos values. Color coded by value.
- **Valuable Bases Overlay** — Shows rare/magic items on the ground that meet your 
  minimum ilvl and chaos value threshold (e.g. ilvl 86 Vermillion Ring).
- **Per-Map Tally** — Tracks every currency item you pick up during the map with a 
  running chaos total. Resets automatically on area change. Manual reset button included.
- **Live prices** — Fetches from poe.ninja on startup and refreshes every 60 minutes.

## Installation

1. Drop the `Wheres-My-Money` folder into your `Plugins/Source/` directory.
2. Reload plugins in ExileAPI.
3. Enable the plugin and set your league name in Settings (default: **Keepers**).

## Settings

| Setting | Default | Description |
|---|---|---|
| League Name | Keepers | League name for poe.ninja. Change to `Mirage` next league. |
| Show Currency on Ground | On | Toggle currency overlay |
| Min Currency Value | 1c | Hide currency worth less than this |
| Currency Max Distance | 0 (off) | Only show currency within X units. 0 = all |
| Show Valuable Bases | On | Toggle bases overlay |
| Min Base Value | 5c | Only show bases worth at least this |
| Min Item Level | 82 | Minimum ilvl for bases to be shown |
| Show Map Tally | On | Toggle per-map pickup tally |
| Tally Min Value | 0c | Only show items above this value in tally |
| Overlay X/Y | 20, 200 | Screen position of the overlay window |
| Overlay Width | 280 | Width of the overlay |

## Color Coding

**Currency values:**
- 🔴 Red = 100c+
- 🟠 Orange = 20-99c  
- 🟡 Yellow = 5-19c
- ⚪ White = 1-4c
- ⚫ Grey = under 1c

**Base rarity:**
- 🟠 Orange = Unique
- 🟡 Yellow = Rare
- 🔵 Blue = Magic

## Notes

- Prices are fetched from poe.ninja. Items not on poe.ninja show 0c.
- Prices fetched from poe.ninja can vary and might be inaccurate at times.
- The plugin detects pickups by watching for items that disappear from the ground.
  In very rare cases a dropped item despawning could be counted as a pickup.
- The map tally resets when you change area.
