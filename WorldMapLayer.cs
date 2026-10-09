using System.Collections.Generic;
using NeoRune;
using UE.Angelscript;
using UE.CommonInput;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Engine;
using UE.GameplayTags;
using UE.MapSystems;
using UE.MapSystemTypes;
using UE.Minimap;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// The waypoint and the route on the game's map screen, and picking a waypoint there (under the crosshair on a
/// controller, under the cursor with a mouse).
/// <para>
/// The game converts world positions to its map with ClientMapsSubsystem.GetNormalizedPositionInRegion (0 to 1 across
/// the region). How those map onto the map's widgets isn't exposed, so the layer tries the likely ways (the texture grid
/// or the marker overlay, with or without the "fancy map" ratio) and keeps the one that puts the player exactly on the
/// game's player marker.
/// </para>
/// </summary>
public class WorldMapLayer : ModWidget
{
    const int DotCount = 160;
    const float DotSize = 7;
    const float MarkerSize = 26;
    const double DotSpacingUi = 11;
    const double Probe = 100000;        // cm, for working out the map's slope
    const double MaxMarkerError = 30;   // UI pixels between the predicted and the game's player marker
    const double MaxSwitchError = 6;    // ... for another way to replace the current one
    const double SnapReach = 24;        // UI pixels from the crosshair or cursor to a marker it snaps to
    const float GeneratorSize = 24;

    ModActor? mod;
    UAS_WorldMap? map;
    UAS_WorldMapMapGrid? grid;
    UAS_WorldMap_Activatable? screen;
    UCanvasPanel? canvas;
    List<UImage> dots;
    List<UCanvasPanelSlot> dotSlots;
    UImage? marker;
    UCanvasPanelSlot? markerSlot;
    bool hidden;

    // The mapping: map = origin + u * right + v * down, uv from the region's normalised position by `mode`.
    bool mappingOk;
    bool modeKnown;
    int mode;
    int reference;        // 0: the texture grid, 1: the marker overlay
    FGameplayTag region;
    double ratioX;
    double ratioY;
    FVector2D origin;
    FVector2D right;
    FVector2D down;
    UWidget? playerMarker;
    bool loggedFit;
    // The region the map shows (a gameplay tag name; "" while it isn't known), and the last one logged.
    public string ShownRegion;
    // What the last pick snapped to ("" for nothing: a plain spot on the map), for the log.
    public string LastSnap;
    List<UImage> generatorIcons;
    List<UCanvasPanelSlot> generatorSlots;
    bool generatorsHidden;
    string loggedShown;

    protected override UWidget? Build(UWidgetTree tree)
    {
        dots = new List<UImage>();
        dotSlots = new List<UCanvasPanelSlot>();
        // Found in game: the marker overlay, taking the region position as is, puts the player exactly on their marker.
        modeKnown = true;
        reference = 1;
        mode = 0;
        canvas = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCanvasPanel>(), tree) as UCanvasPanel;
        if (canvas == null) return null;
        var dotTexture = Images.Load(this, "dot.png");
        for (int i = 0; i < DotCount; i++)
        {
            var dot = Layers.Image(tree, dotTexture);
            var slot = Layers.Place(canvas, dot, DotSize);
            if (dot == null || slot == null) continue;
            dots.Add(dot);
            dotSlots.Add(slot);
        }
        generatorIcons = new List<UImage>();
        generatorSlots = new List<UCanvasPanelSlot>();
        var generatorTexture = Images.Load(this, "generator.png");
        for (int i = 0; i < 8; i++)
        {
            var icon = Layers.Image(tree, generatorTexture);
            var slot = Layers.Place(canvas, icon, GeneratorSize);
            if (icon == null || slot == null) continue;
            generatorIcons.Add(icon);
            generatorSlots.Add(slot);
        }
        marker = Layers.Image(tree, Images.Load(this, "waypoint.png"));
        markerSlot = Layers.Place(canvas, marker, MarkerSize);
        HideAll();
        return Layers.LayoutNeutral(tree, canvas);
    }

    /// <summary>Adds the layer over the map's markers. False if the map isn't built the expected way.</summary>
    public bool Attach(ModActor owner, UAS_WorldMap worldMap)
    {
        mod = owner;
        map = worldMap;
        grid = worldMap.WorldMapMapGrid;
        if (grid == null || grid.MapMarkerOverlay == null) return false;
        Trail.Begin("Waypoints world map layer");
        bool added = GameUI.InsertNextTo(grid.MapMarkerOverlay, this, true);
        Trail.End("Waypoints world map layer");
        if (!added) return false;
        SetVisibility(ESlateVisibility.HitTestInvisible);
        Layers.CoverLike(grid.MapMarkerOverlay.Slot, Slot);
        return true;
    }

    public bool IsAlive() => map != null && UKismetSystemLibrary.IsValid(map);

    /// <summary>The game's input action table, the one the map screen's own hints come from (null if unknown).</summary>
    public UDataTable? InputTable() => map == null ? null : map.ZoomInInput.DataTable;

    /// <summary>Whether the map screen is open (the map widget can outlive it, hidden).</summary>
    public bool IsOpen()
    {
        if (map == null || !UKismetSystemLibrary.IsValid(map)) return false;
        if (screen == null || !UKismetSystemLibrary.IsValid(screen)) screen = FindScreen();
        if (screen != null) return screen.IsActivated();
        return map.IsVisible() && USlateBlueprintLibrary.GetLocalSize(map.GetCachedGeometry()).X > 1;
    }

    UAS_WorldMap_Activatable? FindScreen()
    {
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var widgets, Unreal.ClassOf<UAS_WorldMap_Activatable>(), false);
        foreach (var widget in widgets)
        {
            var activatable = widget as UAS_WorldMap_Activatable;
            if (activatable != null && activatable.WorldMap == map) return activatable;
        }
        return null;
    }

    UClientMapsSubsystem? Maps() =>
        USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<UClientMapsSubsystem>()) as UClientMapsSubsystem;

    public void Refresh()
    {
        var player = World.Player(this);
        if (mod == null || player == null || !IsOpen())
        {
            mappingOk = false;
            playerMarker = null;
            HideAll();
            HideGenerators();
            return;
        }
        var maps = Maps();
        if (maps == null)
        {
            HideAll();
            HideGenerators();
            return;
        }
        var at = player.K2_GetActorLocation();
        Fit(maps, at);
        if (!mappingOk)
        {
            HideAll();
            HideGenerators();
            return;
        }
        Draw(maps, at);
        DrawGenerators(maps);
    }

    // ---------------------------------------------------------------- Working out the mapping

    /// <summary>Picks the mapping that puts the player on the game's player marker, then measures it this frame.</summary>
    void Fit(UClientMapsSubsystem maps, FVector at)
    {
        mappingOk = false;
        if (grid == null || mod == null) return;
        // First against the player's region: if the player's marker is where it should be, that's the region shown.
        region = maps.GetPrimaryPlayerRegionContext().PrimaryRegion;
        string playerRegion = $"{region.TagName}";
        var ratio = maps.GetFancyMapToAreaBoundsRatio(region);
        ratioX = ratio.X > 0.0001 ? ratio.X : 1;
        ratioY = ratio.Y > 0.0001 ? ratio.Y : 1;
        var mine = GetCachedGeometry();
        var n = maps.GetNormalizedPositionInRegion(region, at);

        if (playerMarker == null || !UKismetSystemLibrary.IsValid(playerMarker) || !playerMarker.IsVisible()) playerMarker = FindPlayerMarker();
        // Keep the current way while it fits; look at the others only when it doesn't (e.g. after a game update).
        // While the map opens its parts animate separately, so a single frame that doesn't fit changes nothing.
        bool currentFits = false;
        if (playerMarker != null && Measure(mine, reference))
        {
            double error = MapMath.DistUi(MapPoint(ToUv(n, mode)), Layers.Centre(mine, playerMarker));
            currentFits = error <= MaxMarkerError;
            if (currentFits && mod != null && mod.Debug && !loggedFit)
            {
                loggedFit = true;
                Log.Write($"world map fit: region {region.TagName}, {(reference == 0 ? "grid" : "overlay")}/{mode}, {error:0.0} px from the player marker");
            }
        }
        if (playerMarker != null && !currentFits)
        {
            var actual = Layers.Centre(mine, playerMarker);
            double best = 1000000;
            int bestMode = 0;
            int bestReference = 0;
            string errors = "";
            for (int r = 0; r < 2; r++)
            {
                if (!Measure(mine, r)) continue;
                for (int m = 0; m < 5; m++)
                {
                    double error = MapMath.DistUi(MapPoint(ToUv(n, m)), actual);
                    errors = errors + $" {(r == 0 ? "grid" : "overlay")}/{m}={error:0}";
                    if (error < best)
                    {
                        best = error;
                        bestMode = m;
                        bestReference = r;
                    }
                }
            }
            // Only a clear winner replaces the current way: while the map opens and closes everything is off by about the
            // same amount, and that says nothing about which way is right.
            if (best <= MaxSwitchError)
            {
                if (mod != null && mod.Debug) Log.Write($"world map fit changed: region {region.TagName}, ratio ({ratioX:0.000}, {ratioY:0.000}), errors px:{errors}");
                modeKnown = true;
                mode = bestMode;
                reference = bestReference;
            }
        }
        if (!modeKnown || mod == null) return;

        // Which region the map shows: the player's when their marker is on it; else the selected dimension tab's (learned
        // while it showed the player's region, or named after its region). No tabs: there's only the player's.
        // A tab named after its region says it outright. (The player's marker can't decide it: when another dimension's
        // tab is picked, the marker keeps its last place on screen for a moment, as if it were still shown.)
        string tab = SelectedTab();
        if (tab.StartsWith("SW.Region.")) ShownRegion = tab;
        else if (currentFits)
        {
            ShownRegion = playerRegion;
            if (tab != "") mod.LearnTab(tab, playerRegion);
        }
        else ShownRegion = tab == "" ? playerRegion : mod.RegionForTab(tab);
        if (mod.Debug && ShownRegion != loggedShown)
        {
            loggedShown = ShownRegion;
            Log.Write($"map: showing {(ShownRegion == "" ? "an unknown region" : ShownRegion)} (tab {(tab == "" ? "none" : tab)}), player in {playerRegion}");
        }
        if (ShownRegion == "") return;
        if (ShownRegion != playerRegion)
        {
            region = GameUI.Tag(ShownRegion);
            var shownRatio = maps.GetFancyMapToAreaBoundsRatio(region);
            ratioX = shownRatio.X > 0.0001 ? shownRatio.X : 1;
            ratioY = shownRatio.Y > 0.0001 ? shownRatio.Y : 1;
        }
        mappingOk = Measure(mine, reference);
    }

    /// <summary>The map screen's selected dimension tab ("" when there are none).</summary>
    string SelectedTab()
    {
        var tabs = map?.DimensionSwitcher?.DimensionSwitcher;
        if (tabs == null) return "";
        // The game's tab list keeps each tab's name (a gameplay tag) with its button: the selected button's.
        foreach (var info in tabs.RegisteredTabs)
        {
            var button = info.CreatedButton;
            if (button != null && UKismetSystemLibrary.IsValid(button) && button.GetSelected()) return $"{info.RegisteredName.TagName}";
        }
        return "";
    }

    /// <summary>Whether the map shows the waypoint's region.</summary>
    bool ShowsWaypointRegion() => mod != null && (mod.WaypointRegion == "" || mod.WaypointRegion == ShownRegion);

    UWidget? FindPlayerMarker()
    {
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var widgets, Unreal.ClassOf<UAS_PlayerWorldMapMarkerWidget>(), false);
        foreach (var widget in widgets)
            if (widget != null && widget.IsVisible()) return widget;
        return null;
    }

    /// <summary>The corners of the reference widget (the texture grid or the marker overlay) in this layer's coordinates.</summary>
    bool Measure(FGeometry mine, int which)
    {
        if (grid == null) return false;
        UWidget? widget = which == 0 ? grid.TextureGrid : grid.MapMarkerOverlay;
        if (widget == null) return false;
        var geometry = widget.GetCachedGeometry();
        if (!Layers.Usable(geometry) || !Layers.UsableTransform(mine)) return false;
        var size = USlateBlueprintLibrary.GetLocalSize(geometry);
        origin = Layers.ToLocal(mine, geometry, MapMath.Vec2(0, 0));
        var topRight = Layers.ToLocal(mine, geometry, MapMath.Vec2(size.X, 0));
        var bottomLeft = Layers.ToLocal(mine, geometry, MapMath.Vec2(0, size.Y));
        right = MapMath.Vec2(topRight.X - origin.X, topRight.Y - origin.Y);
        down = MapMath.Vec2(bottomLeft.X - origin.X, bottomLeft.Y - origin.Y);
        return Layers.Sane(origin) && Layers.Sane(right) && Layers.Sane(down);
    }

    /// <summary>Region position (0 to 1) to map texture position (0 to 1), in one of the candidate ways.</summary>
    FVector2D ToUv(FVector2D n, int how)
    {
        if (how == 1) return MapMath.Vec2(0.5 + (n.X - 0.5) * ratioX, 0.5 + (n.Y - 0.5) * ratioY);
        if (how == 2) return MapMath.Vec2(0.5 + (n.X - 0.5) / ratioX, 0.5 + (n.Y - 0.5) / ratioY);
        if (how == 3) return MapMath.Vec2(n.X * ratioX, n.Y * ratioY);
        if (how == 4) return MapMath.Vec2(n.X / ratioX, n.Y / ratioY);
        return n;
    }

    FVector2D FromUv(FVector2D uv, int how)
    {
        if (how == 1) return MapMath.Vec2(0.5 + (uv.X - 0.5) / ratioX, 0.5 + (uv.Y - 0.5) / ratioY);
        if (how == 2) return MapMath.Vec2(0.5 + (uv.X - 0.5) * ratioX, 0.5 + (uv.Y - 0.5) * ratioY);
        if (how == 3) return MapMath.Vec2(uv.X / ratioX, uv.Y / ratioY);
        if (how == 4) return MapMath.Vec2(uv.X * ratioX, uv.Y * ratioY);
        return uv;
    }

    FVector2D MapPoint(FVector2D uv) =>
        MapMath.Vec2(origin.X + uv.X * right.X + uv.Y * down.X, origin.Y + uv.X * right.Y + uv.Y * down.Y);

    FVector2D ToMap(UClientMapsSubsystem maps, FVector world) => MapPoint(ToUv(maps.GetNormalizedPositionInRegion(region, world), mode));

    // ---------------------------------------------------------------- Picking

    /// <summary>
    /// The world point under the crosshair (controller) or the cursor (mouse), if the map is open and understood.
    /// Its height is the player's: the caller puts it on the ground.
    /// </summary>
    public bool PickWorld(FVector at, out FVector world, out string pickedRegion)
    {
        world = at;
        pickedRegion = "";
        var maps = Maps();
        if (maps == null || !IsOpen()) return false;
        Fit(maps, at);
        if (!mappingOk) return false;
        var local = USlateBlueprintLibrary.AbsoluteToLocal(GetCachedGeometry(), PickPoint());
        pickedRegion = ShownRegion;
        // On one of the game's markers (or a generator): exactly where it is.
        if (SnapToMarker(maps, local, at, out world)) return true;
        LastSnap = "";
        return LocalToWorld(maps, local, at, out world);
    }

    /// <summary>A point on the map (this layer's coordinates) as a place in the world, at the player's height.</summary>
    bool LocalToWorld(UClientMapsSubsystem maps, FVector2D local, FVector at, out FVector world)
    {
        world = at;
        // Map position -> texture position: solve local - origin = u * right + v * down.
        double det = right.X * down.Y - down.X * right.Y;
        if (MapMath.Abs(det) < 0.000001) return false;
        double dx = local.X - origin.X;
        double dy = local.Y - origin.Y;
        var uv = MapMath.Vec2((dx * down.Y - down.X * dy) / det, (right.X * dy - dx * right.Y) / det);
        var n = FromUv(uv, mode);
        // Region position -> world: the game's conversion is linear, so measure its slope around the player and invert it.
        var n0 = maps.GetNormalizedPositionInRegion(region, at);
        var nx = maps.GetNormalizedPositionInRegion(region, MapMath.Vec(at.X + Probe, at.Y, at.Z));
        var ny = maps.GetNormalizedPositionInRegion(region, MapMath.Vec(at.X, at.Y + Probe, at.Z));
        double j11 = (nx.X - n0.X) / Probe;
        double j12 = (ny.X - n0.X) / Probe;
        double j21 = (nx.Y - n0.Y) / Probe;
        double j22 = (ny.Y - n0.Y) / Probe;
        double jdet = j11 * j22 - j12 * j21;
        if (MapMath.Abs(jdet) < 0.000000000001) return false;
        double ex = n.X - n0.X;
        double ey = n.Y - n0.Y;
        world = MapMath.Vec(at.X + (j22 * ex - j12 * ey) / jdet, at.Y + (-j21 * ex + j11 * ey) / jdet, at.Z);
        return true;
    }

    /// <summary>
    /// The marker under the crosshair or cursor, as a place: the one the game shows hovered (it grows), else the nearest
    /// within a marker's size; or a soulstorm generator. Its exact place (height too) comes from the game's list of the
    /// region's markers, matched by where they sit on the map.
    /// </summary>
    bool SnapToMarker(UClientMapsSubsystem maps, FVector2D local, FVector at, out FVector world)
    {
        world = at;
        LastSnap = "";
        if (mod == null) return false;
        var mine = GetCachedGeometry();
        double reach = SnapReach * UiToLocal();
        UUserWidget? best = null;
        double bestDistance = reach;
        bool bestHovered = false;
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var widgets, Unreal.ClassOf<UAS_MapMarkerWidgetInterface>(), false);
        foreach (var widget in widgets)
        {
            if (widget == null || !widget.IsVisible()) continue;
            if (widget is UAS_PlayerWorldMapMarkerWidget || widget is UAS_AreaLabelWorldMapMarkerWidget) continue;
            var centre = Layers.Centre(mine, widget);
            if (!Layers.Sane(centre)) continue;
            var button = (widget as UAS_MapMarkerWidgetInterface)?.WorldMapMarkerBase?.InvisibleButton;
            bool hovered = button != null && button.IsHovered();
            double distance = MapMath.DistUi(centre, local);
            if (hovered && !bestHovered)
            {
                best = widget;
                bestHovered = true;
                bestDistance = distance;
                continue;
            }
            if (bestHovered && !hovered) continue;
            if (distance >= bestDistance) continue;
            best = widget;
            bestDistance = distance;
        }
        // The soulstorm's generators, which this layer draws itself.
        if (!bestHovered && ShownRegion == mod.PlayerRegion)
        {
            bool snapped = false;
            foreach (var candidate in mod.Generators)
            {
                var spot = ToMap(maps, candidate);
                double distance = MapMath.DistUi(spot, local);
                if (!Layers.Sane(spot) || distance >= bestDistance) continue;
                world = candidate;
                snapped = true;
                bestDistance = distance;
            }
            if (snapped)
            {
                LastSnap = "generator";
                return true;
            }
        }
        if (best == null) return false;
        var markerCentre = Layers.Centre(mine, best);
        // The region's markers, from the map's region models. (Not through GetOrAddRegionViewModel: it returns a weak
        // pointer, which NeoRune 1.0 reads as a plain one, and that crashed the game.)
        Trail.Begin("Waypoints snap to marker");
        URegionViewModelInstance? model = null;
        var models = USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<URegionViewModelSubsystem>()) as URegionViewModelSubsystem;
        if (models != null)
            foreach (var pair in models.RegionViewModels)
                if (pair.Key.TagName == region.TagName) model = pair.Value;
        if (model != null && UKismetSystemLibrary.IsValid(model))
        {
            double nearest = reach;
            bool found = false;
            foreach (var pair in model.GetMapMarkers())
            {
                var data = pair.Value;
                if (data.MarkerType == EMarkerType.Player || data.MarkerType == EMarkerType.AreaLabel || data.MarkerType == EMarkerType.Ping) continue;
                var spot = ToMap(maps, data.Location);
                double distance = MapMath.DistUi(spot, markerCentre);
                if (!Layers.Sane(spot) || distance >= nearest) continue;
                nearest = distance;
                world = data.Location;
                LastSnap = $"marker {data.MarkerId} (type {data.MarkerType})";
                found = true;
            }
            if (found)
            {
                Trail.End("Waypoints snap to marker");
                return true;
            }
        }
        Trail.End("Waypoints snap to marker");
        // Not in the list: the marker's place on the map.
        if (!LocalToWorld(maps, markerCentre, at, out world)) return false;
        LastSnap = $"{GameUI.ClassPath(best)} (no exact place)";
        return true;
    }

    /// <summary>Whether a world point is under the crosshair or cursor (within a marker's size), e.g. to remove that waypoint.</summary>
    public bool IsUnderPick(FVector world)
    {
        var maps = Maps();
        if (maps == null || !mappingOk || !ShowsWaypointRegion()) return false;
        var local = USlateBlueprintLibrary.AbsoluteToLocal(GetCachedGeometry(), PickPoint());
        return MapMath.DistUi(local, ToMap(maps, world)) < MarkerSize * 0.75 * UiToLocal();
    }

    /// <summary>
    /// This layer's units per UI unit. The map zooms by scaling the panel the layer is in, so sizes given in this layer's
    /// units zoom with it: multiply by this to keep icons the same size on screen.
    /// The zoom is a render transform, which only the point conversions include (the scalar ones use the layout scale
    /// alone), so it's measured from two points.
    /// </summary>
    double UiToLocal()
    {
        var geometry = GetCachedGeometry();
        var from = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(0, 0));
        var to = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(100, 0));
        double absolutePerLocal = MapMath.DistUi(from, to) / 100;
        double absolutePerUi = UWidgetLayoutLibrary.GetViewportScale(this);
        if (absolutePerLocal <= 0.000001 || absolutePerUi <= 0) return 1;
        return absolutePerUi / absolutePerLocal;
    }

    /// <summary>The crosshair's centre on a controller, the cursor with a mouse (absolute desktop pixels).</summary>
    FVector2D PickPoint()
    {
        var input = USubsystemBlueprintLibrary.GetLocalPlayerSubsystem(this, Unreal.ClassOf<UCommonInputSubsystem>()) as UCommonInputSubsystem;
        bool gamepad = input != null && input.GetCurrentInputType() == ECommonInputType.Gamepad;
        if (!gamepad || map == null || map.Crosshair == null) return UWidgetLayoutLibrary.GetMousePositionOnPlatform();
        return Layers.AbsoluteCentre(map.Crosshair);
    }

    // ---------------------------------------------------------------- Drawing

    void Draw(UClientMapsSubsystem maps, FVector at)
    {
        if (mod == null || marker == null || !mod.HasWaypoint || !ShowsWaypointRegion())
        {
            HideAll();
            return;
        }
        hidden = false;
        var colour = mod.Colour;
        double k = UiToLocal();
        double spacing = DotSpacingUi * k;
        marker.SetColorAndOpacity(colour);
        markerSlot?.SetSize(MapMath.Vec2(MarkerSize * k, MarkerSize * k));
        var spot = ToMap(maps, mod.Waypoint);
        if (!Layers.Sane(spot) || !(k > 0 && k < 1000))
        {
            HideAll();
            return;
        }
        markerSlot?.SetPosition(spot);
        marker.SetVisibility(ESlateVisibility.HitTestInvisible);

        // The route: each corner mapped once, dots every few pixels along the lines between them.
        int used = 0;
        var path = mod.Path;
        if (path.Count >= 2 && mod.Active() && ShownRegion == mod.PlayerRegion)
        {
            var previous = ToMap(maps, mod.StartPoint);
            double carry = spacing;
            var dotSize = MapMath.Vec2(DotSize * k, DotSize * k);
            for (int i = mod.StartSeg + 1; i < path.Count && used < dots.Count; i++)
            {
                var next = ToMap(maps, path[i]);
                if (!Layers.Sane(next)) break;
                double length = MapMath.DistUi(previous, next);
                double along = carry;
                while (along <= length && used < dots.Count)
                {
                    double t = length > 0.001 ? along / length : 0;
                    dots[used].SetColorAndOpacity(colour);
                    dotSlots[used].SetSize(dotSize);
                    dotSlots[used].SetPosition(MapMath.Vec2(previous.X + (next.X - previous.X) * t, previous.Y + (next.Y - previous.Y) * t));
                    dots[used].SetVisibility(ESlateVisibility.HitTestInvisible);
                    used++;
                    along = along + spacing;
                }
                carry = along - length;
                previous = next;
            }
        }
        for (int i = used; i < dots.Count; i++) dots[i].SetVisibility(ESlateVisibility.Collapsed);
    }

    /// <summary>The soulstorm's generators on the player's region's map.</summary>
    void DrawGenerators(UClientMapsSubsystem maps)
    {
        if (mod == null || !mod.ShowGenerators || mod.Generators.Count == 0 || ShownRegion != mod.PlayerRegion)
        {
            HideGenerators();
            return;
        }
        generatorsHidden = false;
        double k = UiToLocal();
        var size = MapMath.Vec2(GeneratorSize * k, GeneratorSize * k);
        var colour = ModActor.GeneratorColour();
        int used = 0;
        foreach (var generator in mod.Generators)
        {
            if (used >= generatorIcons.Count) break;
            var spot = ToMap(maps, generator);
            if (!Layers.Sane(spot)) continue;
            generatorIcons[used].SetColorAndOpacity(colour);
            generatorSlots[used].SetSize(size);
            generatorSlots[used].SetPosition(spot);
            generatorIcons[used].SetVisibility(ESlateVisibility.HitTestInvisible);
            used++;
        }
        for (int i = used; i < generatorIcons.Count; i++) generatorIcons[i].SetVisibility(ESlateVisibility.Collapsed);
    }

    void HideGenerators()
    {
        if (generatorsHidden) return;
        generatorsHidden = true;
        foreach (var icon in generatorIcons) icon.SetVisibility(ESlateVisibility.Collapsed);
    }

    void HideAll()
    {
        if (hidden) return;
        hidden = true;
        foreach (var dot in dots) dot.SetVisibility(ESlateVisibility.Collapsed);
        marker?.SetVisibility(ESlateVisibility.Collapsed);
    }

    /// <summary>Logs the map screen's own input actions and their keys, to spot clashes with the mod's buttons.</summary>
    public void LogInputs()
    {
        if (map == null) return;
        LogInput("zoom in", map.ZoomInInput);
        LogInput("zoom out", map.ZoomOutInput);
        LogInput("fast travel", map.ToggleFastTravelInput);
        LogInput("centre on player", map.CenterOnPlayerInput);
        LogInput("cheat", map.ToggleCheatInput);
        LogInput("zoom in (keyboard)", map.KB_ZoomInInput);
        LogInput("zoom out (keyboard)", map.KB_ZoomOutInput);
    }

    void LogInput(string what, FDataTableRowHandle row)
    {
        if (row.DataTable == null) return;
        if (Unreal.DataTableRow(row.DataTable, row.RowName, out FCommonInputActionDataBase action))
            Log.Write($"map input {what}: keyboard {action.KeyboardInputTypeInfo.Key.KeyName}, gamepad {action.DefaultGamepadInputTypeInfo.Key.KeyName}");
    }
}
