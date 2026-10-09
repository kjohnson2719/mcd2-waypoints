# Waypoints (Minecraft Dungeons II)

A mod for Minecraft Dungeons II. Set a waypoint and follow a guide to it, like in most open world games.

## Install

1. Install [Blueprint Loader](https://www.nexusmods.com/minecraftdungeons2/mods/2).
2. Download `Waypoints-<version>.zip` from the [latest release](https://github.com/kjohnson2719/mcd2-waypoints/releases/latest).
3. Extract its `Waypoints` folder into the game's mods folder, so you have `~mods\Waypoints\Waypoints_P.pak`:
   - Steam: `...\steamapps\common\Minecraft Dungeons II\Dungeons\Content\Paks\~mods`
   - Xbox app: `...\Minecraft Dungeons II\Content\Dungeons\Content\Paks\~mods`

Settings and keys are in the game's Settings, Mods tab. To uninstall, delete the `Waypoints` folder.

## What it does

- **Set it on the map screen:** open the map and press **F6**, or **click the left stick** on a controller. The
  waypoint goes under the cursor (mouse) or the crosshair (controller): exactly on a marker (a dungeon, a station...) when
  one is hovered. Pressing again on the waypoint removes it. A waypoint belongs to the region (the Overworld, the Sift)
  it was set in, and only guides while you're there.
- **In the world** (mouse only), F6 sets it on the minimap or the ground under the cursor. Pinging also sets it.
  **F7** clears it. All the buttons can be changed in the game's Settings, Mods tab.
- **The guide:** a walkable route is found on the game's navigation mesh, which only exists about 120 m around you. Goals
  ahead and to either side are tried, and the route goes the way that gets closest to the waypoint. It's found again as
  you go, so the far part keeps becoming a real path. On the ground, the route shows the way quests do: press the game's
  guidance button (left stick click on a controller), or the mod's key (G), and the game's own guidance line runs
  along the route for a few seconds; press again for a fresh line. With a waypoint set, that replaces the quest's line unless you choose to see both.
  You can also choose dots that are always shown. The route always shows on the map and the minimap.
  The waypoint gets a marker showing its distance, and an arrow at the edge of the screen when it's off screen.
- **Arriving** shows a "Waypoint Reached" banner where the game shows its area banners, in the same style once the game
  has shown one this session, and the waypoint clears itself. Each level keeps its own waypoint between sessions.
- **On the map screen,** a "Set Waypoint" / "Remove Waypoint" hint sits next to the game's own control hints, with the
  game's icon for the button. The game's guidance keys (whatever they're bound to) set the waypoint on the map and show
  the path in the world.
- **Soulstorms:** while one is on, its generators show on screen (with their distance, or an arrow at the edge), on
  the minimap and on the map, where a waypoint can be snapped to them.

Built with [NeoRune](https://www.nexusmods.com/minecraftdungeons2/mods/47) (C# compiled to Blueprint bytecode).
It runs on [Blueprint Loader](https://www.nexusmods.com/minecraftdungeons2/mods/2).

## How the minimap part works

The game doesn't expose how its minimap maps the world, so the mod works it out as you walk. The game's own markers for
things that stay put (quest givers, stations, doors) slide across the minimap opposite to your movement. A least-squares fit
of those movements gives the world-to-minimap transform. The mod keeps it up to date (for example after a zoom) and
saves it, so it's ready straight away the next time. Until it has learned the transform (a few seconds of walking in
two different directions), the minimap part stays hidden.

## Developing

Requirements: Windows, the .NET 10 SDK, and the NeoRune tool (`dotnet tool install -g NeoRune.Tool`).

```
dotnet build                 # compile, pack, and install into Paks\~mods\Waypoints (close the game first)
neorune log Waypoints        # what the mod wrote to its log (add --follow while playing)
neorune doctor               # check the setup
neorune pack                 # zip a release (Release build: diagnostics off by default)
node tools/make-icons.js     # regenerate Images/*.png
```

Development builds (`dotnet build`) write diagnostics to the mod's log by default; release builds don't (the setting
is in the Mods tab either way).

### Releasing

GitHub Actions builds the mod on every push. Pushing a version tag publishes a release with the zip:

```
git tag v1.0
git push origin v1.0
```

The tag sets the mod's version (shown in the Mods tab and in the zip's name).

| File | What it does |
| --- | --- |
| `ModActor.cs` | Settings, keys, the waypoint, finding the route, pings, diagnostics |
| `TrailOverlay.cs` | The trail on the ground, the waypoint marker, the off-screen arrow |
| `MinimapLayer.cs` | The layer added to the game's minimap, and how it learns the minimap's transform |
| `WorldMapLayer.cs` | The layer added to the map screen, and picking a waypoint there |
| `Layers.cs` | Shared helpers for the layers added to the game's maps |
| `Banner.cs` | The "Waypoint Reached" banner, in the game's area banner style |
| `Generators.cs` | The soulstorm generators on screen |
| `MapHint.cs` | The control hint on the map screen |
| `MapMath.cs` | Vector helpers, and spacing points along the route |

On the map screen, keys reach the mod through the game's own key tracker (`KeyboardDirectionTickWrapper`, which the
map uses for keyboard panning). The usual in-game key listeners stop while a menu is open, so they can't be used there.
Map positions come from the game's `ClientMapsSubsystem.GetNormalizedPositionInRegion`, checked against the game's
player marker.

## License

[MIT](LICENSE).
