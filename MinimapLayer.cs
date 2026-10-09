using System.Collections.Generic;
using NeoRune;
using UE.Angelscript;
using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// The waypoint and the route on the game's minimap: a layer added next to the minimap's own markers, so it moves,
/// fades and clips with the minimap.
/// <para>
/// The game doesn't expose how the minimap maps the world, so the layer works it out (and keeps it up to date, e.g.
/// after a zoom): as the player walks, the game's markers for things that stay put (quest givers, stations, doors...)
/// slide across the minimap by the opposite of the player's movement. Each step gives pairs (world movement, map
/// movement); a least-squares fit over them is the 2x2 matrix A with map = player marker + A * (world - player).
/// A ping on the minimap gives a pair too (its actor's location against its marker).
/// </para>
/// </summary>
public class MinimapLayer : ModWidget
{
    const int DotCount = 40;
    const float DotSize = 7;
    const float MarkerSize = 20;
    const float ArrowSize = 18;
    const double EdgeInset = 12;       // UI pixels between the frame's edge and the arrow (and the end of the dots)
    const double DotSpacingUi = 8;
    const double SampleStep = 120;     // cm the player walks between calibration samples
    const double SampleInterval = 0.1;

    ModActor? mod;
    UAS_MiniMap? minimap;
    UCanvasPanel? canvas;
    List<UImage> dots;
    List<UCanvasPanelSlot> dotSlots;
    UImage? marker;
    UCanvasPanelSlot? markerSlot;
    UImage? edgeArrow;
    UCanvasPanelSlot? edgeArrowSlot;
    // Soulstorm generators: an icon each inside the frame, an arrow on its edge for those beyond it.
    List<UImage> generatorIcons;
    List<UCanvasPanelSlot> generatorIconSlots;
    List<UImage> generatorArrows;
    List<UCanvasPanelSlot> generatorArrowSlots;
    bool generatorsHidden;
    // Why the layer isn't drawing ("" while it is), for the log.
    string hiddenBecause;
    int hideLogs;
    bool hidden;

    // This frame's minimap, in the layer's own coordinates.
    bool geometryOk;
    FVector2D anchor;
    FVector2D mapCentre;
    double mapRadius;
    // The minimap's frame: half its width and height along its own axes (unit vectors), in this layer's coordinates.
    FVector2D axisX;
    FVector2D axisY;
    double halfWidth;
    double halfHeight;
    UWidget? playerMarker;

    // Calibration: sums of u u^T and v u^T (u = world movement, v = map movement), and the last sample.
    double uxx;
    double uxy;
    double uyy;
    double vxux;
    double vxuy;
    double vyux;
    double vyuy;
    int pairs;
    double accepted;
    double rejected;
    bool hasPrev;
    FVector prevPlayer;
    List<UWidget> prevMarkers;
    List<FVector2D> prevOffsets;
    double lastSample;
    double lastPingPair;

    protected override UWidget? Build(UWidgetTree tree)
    {
        // Widgets can't have field initializers: set up here.
        dots = new List<UImage>();
        dotSlots = new List<UCanvasPanelSlot>();
        prevMarkers = new List<UWidget>();
        prevOffsets = new List<FVector2D>();
        lastPingPair = -100;
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
        marker = Layers.Image(tree, Images.Load(this, "waypoint.png"));
        markerSlot = Layers.Place(canvas, marker, MarkerSize);
        edgeArrow = Layers.Image(tree, Images.Load(this, "arrow.png"));
        edgeArrowSlot = Layers.Place(canvas, edgeArrow, ArrowSize);
        generatorIcons = new List<UImage>();
        generatorIconSlots = new List<UCanvasPanelSlot>();
        generatorArrows = new List<UImage>();
        generatorArrowSlots = new List<UCanvasPanelSlot>();
        var generatorTexture = Images.Load(this, "generator.png");
        var arrowTexture = Images.Load(this, "arrow.png");
        for (int i = 0; i < 8; i++)
        {
            var icon = Layers.Image(tree, generatorTexture);
            var iconSlot = Layers.Place(canvas, icon, 18);
            var arrow = Layers.Image(tree, arrowTexture);
            var arrowSlot = Layers.Place(canvas, arrow, 14);
            if (icon == null || iconSlot == null || arrow == null || arrowSlot == null) continue;
            generatorIcons.Add(icon);
            generatorIconSlots.Add(iconSlot);
            generatorArrows.Add(arrow);
            generatorArrowSlots.Add(arrowSlot);
        }
        HideAll();
        return Layers.LayoutNeutral(tree, canvas);
    }

    /// <summary>Adds the layer to a minimap, over its marker overlay. False if the minimap isn't built the expected way.</summary>
    public bool Attach(ModActor owner, UAS_MiniMap map)
    {
        mod = owner;
        minimap = map;
        var overlay = map.MarkerOverlay;
        if (overlay == null) return false;
        Trail.Begin("Waypoints minimap layer");
        bool added = GameUI.InsertNextTo(overlay, this, true);
        Trail.End("Waypoints minimap layer");
        if (!added) return false;
        SetVisibility(ESlateVisibility.HitTestInvisible);
        Layers.CoverLike(overlay.Slot, Slot);
        if (owner.Debug)
        {
            var parent = overlay.GetParent();
            Log.Write($"minimap: overlay slot {GameUI.ClassPath(overlay.Slot)}, parent {GameUI.ClassPath(parent)}");
        }
        return true;
    }

    /// <summary>False once the game has destroyed the minimap the layer was added to.</summary>
    public bool IsAlive() => minimap != null && UKismetSystemLibrary.IsValid(minimap);

    /// <summary>Every frame: measure the minimap, learn its transform, draw the waypoint and the route.</summary>
    public void Refresh()
    {
        geometryOk = false;
        var player = World.Player(this);
        if (mod == null || minimap == null || player == null || !UKismetSystemLibrary.IsValid(minimap))
        {
            Hide("no minimap or player");
            HideGenerators();
            return;
        }
        if (!minimap.IsVisible())
        {
            Hide("the game hid the minimap");
            HideGenerators();
            return;
        }
        Measure();
        if (!geometryOk)
        {
            Hide($"minimap not measurable (radius {mapRadius:0.0})");
            HideGenerators();
            return;
        }
        var at = player.K2_GetActorLocation();
        double now = World.RealTime(this);
        if (now - lastSample >= SampleInterval)
        {
            lastSample = now;
            Learn(at, now);
        }
        Draw(at);
        DrawGenerators(at);
    }

    /// <summary>The minimap's centre and radius, and the player marker, in this layer's coordinates.</summary>
    void Measure()
    {
        if (minimap == null) return;
        var image = minimap.MiniMapImage;
        if (image == null) return;
        var mine = GetCachedGeometry();
        var imageGeometry = image.GetCachedGeometry();
        // Mid-animation (or before the first paint) a geometry can be empty: measuring it would give nonsense.
        if (!Layers.UsableTransform(mine) || !Layers.Usable(imageGeometry)) return;
        var size = USlateBlueprintLibrary.GetLocalSize(imageGeometry);
        mapCentre = Layers.ToLocal(mine, imageGeometry, MapMath.Vec2(size.X / 2, size.Y / 2));
        var left = Layers.ToLocal(mine, imageGeometry, MapMath.Vec2(0, size.Y / 2));
        var right = Layers.ToLocal(mine, imageGeometry, MapMath.Vec2(size.X, size.Y / 2));
        var top = Layers.ToLocal(mine, imageGeometry, MapMath.Vec2(size.X / 2, 0));
        var bottom = Layers.ToLocal(mine, imageGeometry, MapMath.Vec2(size.X / 2, size.Y));
        double width = MapMath.DistUi(left, right);
        double height = MapMath.DistUi(top, bottom);
        mapRadius = (width < height ? width : height) / 2;
        if (mapRadius < 4) return;
        halfWidth = width / 2;
        halfHeight = height / 2;
        axisX = MapMath.Vec2((right.X - left.X) / width, (right.Y - left.Y) / width);
        axisY = MapMath.Vec2((bottom.X - top.X) / height, (bottom.Y - top.Y) / height);

        if (playerMarker == null || !UKismetSystemLibrary.IsValid(playerMarker) || !playerMarker.IsVisible()) playerMarker = FindPlayerMarker(mine);
        anchor = playerMarker != null ? Layers.Centre(mine, playerMarker) : mapCentre;
        if (!Layers.Sane(anchor)) anchor = mapCentre;
        geometryOk = Layers.Sane(mapCentre) && Layers.Sane(MapMath.Vec2(halfWidth, halfHeight)) && halfWidth > 2 && halfHeight > 2;
    }

    UWidget? FindPlayerMarker(FGeometry mine)
    {
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var widgets, Unreal.ClassOf<UAS_PlayerMinimapMarkerWidget>(), false);
        UWidget? best = null;
        double bestDistance = 1000000;
        foreach (var widget in widgets)
        {
            if (widget == null || !widget.IsVisible()) continue;
            // In co-op the local player's marker is the one the minimap is centred on.
            double distance = MapMath.DistUi(Layers.Centre(mine, widget), mapCentre);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = widget;
            }
        }
        return best;
    }

    void Learn(FVector at, double now)
    {
        if (mod == null) return;
        var mine = GetCachedGeometry();
        // The game's markers inside the map, away from its edge (markers out of range are pinned to the edge).
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var all, Unreal.ClassOf<UAS_MinimapMarkerWidgetInterface>(), false);
        var markers = new List<UWidget>();
        var offsets = new List<FVector2D>();
        UWidget? pingMarker = null;
        int pingMarkers = 0;
        FVector2D pingOffset = anchor;
        foreach (var widget in all)
        {
            if (widget == null || widget == playerMarker || !widget.IsVisible()) continue;
            if (widget is UAS_PlayerMinimapMarkerWidget) continue;   // other players move
            var centre = Layers.Centre(mine, widget);
            if (!Layers.Sane(centre)) continue;
            if (MapMath.DistUi(centre, mapCentre) > mapRadius * 0.8) continue;
            var offset = MapMath.Vec2(centre.X - anchor.X, centre.Y - anchor.Y);
            if (widget is UAS_PingMinimapMarkerWidget)
            {
                pingMarker = widget;
                pingMarkers++;
                pingOffset = offset;
            }
            markers.Add(widget);
            offsets.Add(offset);
        }

        // A fresh ping: its actor's location against its marker (only when there's one of each, so they match).
        if (pingMarkers == 1 && pingMarker != null && now - mod.LastPingTime < 8 && now - lastPingPair > 0.5)
        {
            lastPingPair = now;
            Add(mod.LastPing.X - at.X, mod.LastPing.Y - at.Y, pingOffset.X * mod.MinimapZoom, pingOffset.Y * mod.MinimapZoom, true);
            Solve();
        }

        if (!hasPrev)
        {
            Remember(at, markers, offsets);
            return;
        }
        double moved = MapMath.Dist2D(at, prevPlayer);
        if (moved < SampleStep) return;
        if (moved > 3000)
        {
            // Teleported (or loaded somewhere else): the old sample says nothing about this one.
            Remember(at, markers, offsets);
            return;
        }
        double ux = prevPlayer.X - at.X;
        double uy = prevPlayer.Y - at.Y;
        Decay(0.97);
        for (int i = 0; i < markers.Count; i++)
        {
            int before = prevMarkers.IndexOf(markers[i]);
            if (before < 0) continue;
            Add(ux, uy, (offsets[i].X - prevOffsets[before].X) * mod.MinimapZoom, (offsets[i].Y - prevOffsets[before].Y) * mod.MinimapZoom, true);
        }
        Solve();
        Remember(at, markers, offsets);
    }

    void Remember(FVector at, List<UWidget> markers, List<FVector2D> offsets)
    {
        hasPrev = true;
        prevPlayer = at;
        prevMarkers = markers;
        prevOffsets = offsets;
    }

    void Decay(double factor)
    {
        uxx = uxx * factor;
        uxy = uxy * factor;
        uyy = uyy * factor;
        vxux = vxux * factor;
        vxuy = vxuy * factor;
        vyux = vyux * factor;
        vyuy = vyuy * factor;
        accepted = accepted * factor;
        rejected = rejected * factor;
    }

    /// <summary>Adds a pair (world u, map v), unless it disagrees with what's known (a moving marker, a reused widget).</summary>
    void Add(double ux, double uy, double vx, double vy, bool check)
    {
        if (mod == null) return;
        if (check && mod.Calibrated)
        {
            double px = mod.N11 * ux + mod.N12 * uy;
            double py = mod.N21 * ux + mod.N22 * uy;
            double error = MapMath.Length2(vx - px, vy - py);
            if (error > MapMath.Length2(px, py) * 0.35 + 2 * mod.MinimapZoom)
            {
                rejected = rejected + 1;
                // Mostly disagreeing: the map changed (zoom, another kind of area). Start learning again.
                if (rejected > 30 && rejected > accepted * 4) Reset();
                return;
            }
            accepted = accepted + 1;
        }
        uxx = uxx + ux * ux;
        uxy = uxy + ux * uy;
        uyy = uyy + uy * uy;
        vxux = vxux + vx * ux;
        vxuy = vxuy + vx * uy;
        vyux = vyux + vy * ux;
        vyuy = vyuy + vy * uy;
        pairs++;
    }

    void Reset()
    {
        if (mod != null && mod.Debug) Log.Write("minimap: transform no longer fits the markers, learning it again");
        uxx = 0;
        uxy = 0;
        uyy = 0;
        vxux = 0;
        vxuy = 0;
        vyux = 0;
        vyuy = 0;
        pairs = 0;
        accepted = 0;
        rejected = 0;
        mod?.ForgetCalibration();
    }

    /// <summary>A = (sum v u^T)(sum u u^T)^-1, once the player has moved in two different directions.</summary>
    void Solve()
    {
        if (mod == null || pairs < 10) return;
        double det = uxx * uyy - uxy * uxy;
        double trace = uxx + uyy;
        if (trace <= 0 || det < 0.04 * trace * trace) return;
        double a11 = (vxux * uyy - vxuy * uxy) / det;
        double a12 = (vxuy * uxx - vxux * uxy) / det;
        double a21 = (vyux * uyy - vyuy * uxy) / det;
        double a22 = (vyuy * uxx - vyux * uxy) / det;
        // UI pixels per cm at the current zoom: a minimap shows somewhere between a few metres and a few kilometres.
        double scale = UKismetMathLibrary.sqrt(MapMath.Abs(a11 * a22 - a12 * a21)) / mod.MinimapZoom;
        if (!(scale >= 0.00005 && scale <= 2)) return;   // also false for NaN
        mod.SetCalibration(a11, a12, a21, a22);
    }

    /// <summary>The world point under a position on screen (absolute desktop pixels), if it's on the minimap.</summary>
    public bool ScreenToWorld(FVector2D absolute, FVector at, out FVector world)
    {
        world = at;
        if (mod == null || !geometryOk || !mod.Calibrated) return false;
        var local = USlateBlueprintLibrary.AbsoluteToLocal(GetCachedGeometry(), absolute);
        if (MapMath.DistUi(local, mapCentre) > mapRadius) return false;
        double det = mod.A11 * mod.A22 - mod.A12 * mod.A21;
        if (det > -0.0000000001 && det < 0.0000000001) return false;
        double vx = local.X - anchor.X;
        double vy = local.Y - anchor.Y;
        double wx = (mod.A22 * vx - mod.A12 * vy) / det;
        double wy = (-mod.A21 * vx + mod.A11 * vy) / det;
        world = MapMath.Vec(at.X + wx, at.Y + wy, at.Z);
        return true;
    }

    FVector2D ToMap(FVector point, FVector at)
    {
        double dx = point.X - at.X;
        double dy = point.Y - at.Y;
        return MapMath.Vec2(anchor.X + mod!.A11 * dx + mod.A12 * dy, anchor.Y + mod.A21 * dx + mod.A22 * dy);
    }

    void Draw(FVector at)
    {
        if (mod == null || marker == null || !mod.Active() || !mod.ShowMinimap)
        {
            Hide("no waypoint");
            return;
        }
        if (!mod.Calibrated)
        {
            Hide("minimap transform not learned (walk in a couple of directions)");
            return;
        }
        if (hiddenBecause != "" && mod.Debug && hideLogs < 20)
        {
            hideLogs++;
            Log.Write("minimap: waypoint layer showing");
        }
        hiddenBecause = "";
        hidden = false;
        var colour = mod.Colour;
        var spot = ToMap(mod.Waypoint, at);
        if (!Layers.Sane(spot))
        {
            Hide("waypoint position not computable");
            return;
        }
        if (Inside(spot, EdgeInset))
        {
            // On the minimap: the waypoint's marker where it is.
            marker.SetColorAndOpacity(colour);
            markerSlot?.SetPosition(spot);
            marker.SetVisibility(ESlateVisibility.HitTestInvisible);
            edgeArrow?.SetVisibility(ESlateVisibility.Collapsed);
        }
        else if (edgeArrow != null)
        {
            // Beyond it: an arrow on the frame's edge, pointing from the player towards it.
            var edge = ToEdge(spot, EdgeInset);
            if (!Layers.Sane(edge)) edge = anchor;
            edgeArrow.SetColorAndOpacity(colour);
            edgeArrow.SetRenderTransformAngle((float)UKismetMathLibrary.DegAtan2(spot.X - anchor.X, anchor.Y - spot.Y));
            edgeArrowSlot?.SetPosition(edge);
            edgeArrow.SetVisibility(ESlateVisibility.HitTestInvisible);
            marker.SetVisibility(ESlateVisibility.Collapsed);
        }

        int used = 0;
        if (mod.Path.Count >= 2)
        {
            double scale = UKismetMathLibrary.sqrt(MapMath.Abs(mod.A11 * mod.A22 - mod.A12 * mod.A21));
            double spacing = DotSpacingUi / scale;
            var points = MapMath.PointsAlong(mod.Path, mod.StartSeg, mod.StartPoint, spacing, spacing, DotCount, mapRadius / scale * 2);
            foreach (var point in points)
            {
                var position = ToMap(point, at);
                // The route stops short of the edge (and of the arrow there).
                if (!Layers.Sane(position) || !Inside(position, EdgeInset + ArrowSize)) break;
                dots[used].SetColorAndOpacity(colour);
                dotSlots[used].SetPosition(position);
                dots[used].SetVisibility(ESlateVisibility.HitTestInvisible);
                used++;
                if (used >= dots.Count) break;
            }
        }
        for (int i = used; i < dots.Count; i++) dots[i].SetVisibility(ESlateVisibility.Collapsed);
    }

    /// <summary>Whether a point is inside the minimap's frame, at least <paramref name="inset"/> from its edges.</summary>
    bool Inside(FVector2D point, double inset)
    {
        double dx = point.X - mapCentre.X;
        double dy = point.Y - mapCentre.Y;
        return MapMath.Abs(dx * axisX.X + dy * axisX.Y) <= halfWidth - inset && MapMath.Abs(dx * axisY.X + dy * axisY.Y) <= halfHeight - inset;
    }

    /// <summary>Where the line from the player's marker to a point leaves the frame (less an inset).</summary>
    FVector2D ToEdge(FVector2D point, double inset)
    {
        double ox = anchor.X - mapCentre.X;
        double oy = anchor.Y - mapCentre.Y;
        double dx = point.X - anchor.X;
        double dy = point.Y - anchor.Y;
        // Along each of the frame's axes: where the player is, and how far the point is from them.
        double u0 = ox * axisX.X + oy * axisX.Y;
        double v0 = ox * axisY.X + oy * axisY.Y;
        double du = dx * axisX.X + dy * axisX.Y;
        double dv = dx * axisY.X + dy * axisY.Y;
        double t = 1;
        if (du > 0.0001) t = MapMath.Min(t, (halfWidth - inset - u0) / du);
        if (du < -0.0001) t = MapMath.Min(t, (-halfWidth + inset - u0) / du);
        if (dv > 0.0001) t = MapMath.Min(t, (halfHeight - inset - v0) / dv);
        if (dv < -0.0001) t = MapMath.Min(t, (-halfHeight + inset - v0) / dv);
        if (t < 0) t = 0;
        return MapMath.Vec2(anchor.X + dx * t, anchor.Y + dy * t);
    }

    void Hide(string why)
    {
        if (why != hiddenBecause && mod != null && mod.Debug && hideLogs < 20 && why != "no waypoint")
        {
            hideLogs++;
            Log.Write($"minimap: waypoint layer hidden: {why}");
        }
        hiddenBecause = why;
        HideAll();
    }

    /// <summary>The soulstorm's generators: an icon where each is, or an arrow on the frame's edge towards it.</summary>
    void DrawGenerators(FVector at)
    {
        if (mod == null || !mod.Calibrated || !mod.ShowGenerators || mod.Generators.Count == 0)
        {
            HideGenerators();
            return;
        }
        generatorsHidden = false;
        var colour = ModActor.GeneratorColour();
        int icons = 0;
        int arrows = 0;
        foreach (var generator in mod.Generators)
        {
            var spot = ToMap(generator, at);
            if (!Layers.Sane(spot)) continue;
            if (Inside(spot, EdgeInset))
            {
                if (icons >= generatorIcons.Count) continue;
                generatorIcons[icons].SetColorAndOpacity(colour);
                generatorIconSlots[icons].SetPosition(spot);
                generatorIcons[icons].SetVisibility(ESlateVisibility.HitTestInvisible);
                icons++;
                continue;
            }
            if (arrows >= generatorArrows.Count) continue;
            var edge = ToEdge(spot, EdgeInset);
            if (!Layers.Sane(edge)) continue;
            generatorArrows[arrows].SetColorAndOpacity(colour);
            generatorArrows[arrows].SetRenderTransformAngle((float)UKismetMathLibrary.DegAtan2(spot.X - anchor.X, anchor.Y - spot.Y));
            generatorArrowSlots[arrows].SetPosition(edge);
            generatorArrows[arrows].SetVisibility(ESlateVisibility.HitTestInvisible);
            arrows++;
        }
        for (int i = icons; i < generatorIcons.Count; i++) generatorIcons[i].SetVisibility(ESlateVisibility.Collapsed);
        for (int i = arrows; i < generatorArrows.Count; i++) generatorArrows[i].SetVisibility(ESlateVisibility.Collapsed);
    }

    void HideGenerators()
    {
        if (generatorsHidden) return;
        generatorsHidden = true;
        foreach (var icon in generatorIcons) icon.SetVisibility(ESlateVisibility.Collapsed);
        foreach (var arrow in generatorArrows) arrow.SetVisibility(ESlateVisibility.Collapsed);
    }

    void HideAll()
    {
        if (hidden) return;
        hidden = true;
        foreach (var dot in dots) dot.SetVisibility(ESlateVisibility.Collapsed);
        marker?.SetVisibility(ESlateVisibility.Collapsed);
        edgeArrow?.SetVisibility(ESlateVisibility.Collapsed);
    }
}
