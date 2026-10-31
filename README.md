# Pfadfinder (smart path tool) Timberborn mod

![Pfadfinder routing a path and a tubeway around obstacles](thumbnail.jpg)

A path dragged from one tile to another is routed around trees, buildings, rubble, cliffs and
anything else that can't be built on, instead of the vanilla straight L.

> **Disclaimer: this mod is AI coded.** It was written largely by an AI coding assistant (Claude), not hand-written and reviewed.
> It's play-tested and code reviewed but not exhaustively.

- Works with the Path, Tubeway and Vertical Tubeway tools, in game and map editor. No new buttons.
- A clear vanilla L stays the vanilla L. Routes prefer few turns.
- Existing paths (tubes, for tubeways), bridges, gates and airlocks are reused, in the directions
  they can actually be walked.
- A drag that ends on another height level is routed through existing stairs (or vertical
  tubeways). No new stairs are built.
- Goes under platforms and overhangs, over stumps and dead bushes.
- If no route exists, the vanilla L is shown. Routes stop at the tool's 40-tile preview limit, like vanilla.

## Settings

Needs the **Mod Settings** mod (`eMka.ModSettings`):

- **Hold Alt to route** (off): swaps Alt's role — drags are vanilla, Alt routes.
- **Route across levels** (on): uses the height level the cursor points at, via existing stairs.
- **Route tubeways** (on): routes the tubeway tools too.
- **Hide build range while routing** (off): skips the district range overlay for routed drags.

## How it works

A Harmony prefix on `AreaPicker.PickBlockObjectArea` wraps the path tool's preview and action
callbacks, so the game still does input, previews, validation, unlocking and placing; only the list
of tiles is replaced. For each drag:

1. The ends are the drag start and the far end of the vanilla L — or, across levels, the tile under
   the cursor (end ray captured from `AreaPicker.GetBlocks`).
2. A* runs between them (turn penalty, existing pieces cheap, vanilla L slightly preferred). A step
   needs both tiles to allow its direction.
3. Each tile is checked once per drag, at most 1,500 per frame (a click finishes the search):
   - **existing** if its Path-slot object has a `PathSpec` and matches the tool (tube or not). The
     walkable directions come from the object's navigation edges (`BlockObjectNavMeshSettingsSpec`,
     read by reflection), so stairs link levels and bridges/gates are one-axis. Zipline towers have a
     `PathSpec` but no edges, and buildings with an entrance (district center, tubeway and zipline
     stations) are entered through their door, not walked across, so they're all obstacles;
   - otherwise **free** if a hidden preview of the piece passes `BlockObject.IsValid()`, the same
     check the tool's own previews use.

Source: `GridRouter` (pure A*), `RoutingService` (tile checks, placements), `NavEdgeReader`,
`PathToolPatch`, `RangeOverlayPatch`, `PfadfinderSettings`, `ModStarter` — all in `src/`.
Search tuning (`Margin`, costs, budget) is at the top of `GridRouter.cs`.

## Build

Needs Timberborn 1.1+, the **Harmony** and **Mod Settings** workshop mods (subscribed, so their DLLs
are on disk) and the .NET SDK 6+. The build deploys `Pfadfinder.dll` and `manifest.json` to
`Documents/Timberborn/Mods/Pfadfinder/`, where it is enabled in the mod manager together with
Harmony and Mod Settings.

```bash
dotnet build -c Release                                          # default Steam location (Windows)
dotnet build -c Release -p:SteamDir="D:\SteamLibrary\steamapps"  # other library
```

Linux (Steam + Proton; Flatpak: `STEAM=~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps`):

```bash
STEAM=~/.steam/steam/steamapps
dotnet build -c Release \
  -p:ManagedDir="$STEAM/common/Timberborn/Timberborn_Data/Managed" \
  -p:HarmonyDll="$STEAM/workshop/content/1062090/3284904751/0Harmony.dll" \
  -p:ModSettingsDir="$STEAM/workshop/content/1062090/3283831040/version-1.1/Scripts" \
  -p:ModOutputDir="$STEAM/compatdata/1062090/pfx/drive_c/users/steamuser/Documents/Timberborn/Mods/Pfadfinder"
```

macOS: `ManagedDir` is `Timberborn.app/Contents/Resources/Data/Managed`.

## Publishing updates

The mod is [Workshop item 3831217281](https://steamcommunity.com/sharedfiles/filedetails/?id=3831217281).
The build copies `workshop_data.json` into the mod folder: it links uploads to that item (without it
the game's uploader creates a new one) and keeps the Steam page's description and visibility.
The page text lives in `workshop_description.bbcode` and is pasted into Steam by hand when it changes.

1. The version is raised in `manifest.json` and `Pfadfinder.csproj`, and the project built.
2. A subscribed Workshop copy is unsubscribed first, so the game doesn't load the mod twice.
3. Upload: main menu → Mods → upload → Pfadfinder → Upload to Steam Workshop.

## Troubleshooting

`Player.log` lines start with `[Pfadfinder]` (Windows: `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\`,
Linux/Proton: `$STEAM/compatdata/1062090/pfx/drive_c/users/steamuser/AppData/LocalLow/Mechanistry/Timberborn/`).

- *"Failed to apply patches"* — a game update changed the patched code; the mod is inactive and the
  vanilla tools work as before.
- *"Routing failed, falling back…"* — logged once; that drag uses vanilla.
- Compile errors after a game update usually name a type that moved; the fix is usually a `using` line.

## Ideas for later

- Place new stairs where a route needs to change level.
- Avoid water / prefer dry tiles.
