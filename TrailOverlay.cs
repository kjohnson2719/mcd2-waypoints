using System.Collections.Generic;
using NeoRune;
using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// The guide in the world, drawn on the player's screen under the game's UI: dots flowing along the route on the
/// ground, a marker over the waypoint with its distance, and an arrow at the screen's edge when the waypoint is off
/// screen. ModActor calls <see cref="Refresh"/> every frame, after the camera has moved.
/// </summary>
public class TrailOverlay : ModWidget
{
    const int DotCount = 64;
    const float DotSize = 14;
    const float BeaconSize = 40;
    const float ArrowSize = 36;
    const double EdgeMargin = 56;
    const double FlowSpeed = 250;   // cm per second

    ModActor? mod;
    UCanvasPanel? canvas;
    List<UImage> dots;
    List<UCanvasPanelSlot> dotSlots;
    UImage? beacon;
    UCanvasPanelSlot? beaconSlot;
    UImage? arrow;
    UCanvasPanelSlot? arrowSlot;
    UTextBlock? label;
    UCanvasPanelSlot? labelSlot;
    bool hidden;

    protected override UWidget? Build(UWidgetTree tree)
    {
        // Widgets can't have field initializers: set up here.
        dots = new List<UImage>();
        dotSlots = new List<UCanvasPanelSlot>();
        canvas = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCanvasPanel>(), tree) as UCanvasPanel;
        if (canvas == null) return null;
        var dotTexture = Images.Load(this, "dot.png");
        for (int i = 0; i < DotCount; i++)
        {
            var dot = MakeImage(tree, dotTexture, DotSize);
            var slot = Place(dot, DotSize);
            if (dot == null || slot == null) continue;
            dots.Add(dot);
            dotSlots.Add(slot);
        }
        beacon = MakeImage(tree, Images.Load(this, "waypoint.png"), BeaconSize);
        beaconSlot = Place(beacon, BeaconSize);
        arrow = MakeImage(tree, Images.Load(this, "arrow.png"), ArrowSize);
        arrowSlot = Place(arrow, ArrowSize);
        label = Ui.Text(tree, "", 18);
        if (label != null)
        {
            label.SetShadowOffset(MapMath.Vec2(1.5, 1.5));
            label.SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.85f));
            labelSlot = canvas.AddChildToCanvas(label);
            labelSlot?.SetAutoSize(true);
            labelSlot?.SetAlignment(MapMath.Vec2(0.5, 0));
        }
        HideAll();
        return canvas;
    }

    UImage? MakeImage(UWidgetTree tree, UTexture2D? texture, float size)
    {
        var image = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        if (image == null) return null;
        image.SetBrushFromTexture(texture, false);
        return image;
    }

    UCanvasPanelSlot? Place(UImage? image, float size)
    {
        if (image == null || canvas == null) return null;
        var slot = canvas.AddChildToCanvas(image);
        slot?.SetAutoSize(false);
        slot?.SetSize(MapMath.Vec2(size, size));
        slot?.SetAlignment(MapMath.Vec2(0.5, 0.5));
        return slot;
    }

    /// <summary>Puts the overlay on the player's screen (and back on it whenever the game rebuilds its UI).</summary>
    public void Begin(ModActor owner)
    {
        mod = owner;
        // It only draws: clicks go through to the game (Dungeons moves and attacks with the mouse).
        SetVisibility(ESlateVisibility.HitTestInvisible);
        KeepShown();
        Timer.Start(this, nameof(KeepShown), 0.5f, true);
    }

    void KeepShown()
    {
        if (IsInViewport()) return;
        var player = World.PlayerController(this);
        if (player == null) return;
        SetOwningPlayer(player);
        AddToPlayerScreen(-10);
    }

    /// <summary>Every frame; hidden while the map screen is open (it shows the waypoint itself).</summary>
    public void Refresh(bool mapOpen)
    {
        var player = World.Player(this);
        var controller = World.PlayerController(this);
        if (mod == null || player == null || controller == null || !mod.Active() || mapOpen)
        {
            HideAll();
            return;
        }
        hidden = false;
        var colour = mod.Colour;
        double time = World.RealTime(this);
        var at = player.K2_GetActorLocation();
        double scale = UWidgetLayoutLibrary.GetViewportScale(this);
        if (scale <= 0) scale = 1;
        var viewport = UWidgetLayoutLibrary.GetViewportSize(this);
        var screen = MapMath.Vec2(viewport.X / scale, viewport.Y / scale);

        DrawTrail(controller, colour, time);
        DrawBeacon(controller, at, screen, colour, time);
    }

    void DrawTrail(APlayerController controller, FLinearColor colour, double time)
    {
        int used = 0;
        if (mod != null && mod.ShowDots() && mod.Path.Count >= 2)
        {
            double length = mod.TrailLength * 100;
            double spacing = length / DotCount;
            if (spacing < 110) spacing = 110;
            UKismetMathLibrary.FMod(time * FlowSpeed, spacing, out double phase);
            var points = MapMath.PointsAlong(mod.Path, mod.StartSeg, mod.StartPoint, phase, spacing, DotCount, length);
            for (int i = 0; i < points.Count; i++)
            {
                if (!UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, points[i], out FVector2D position, true)) continue;
                double along = phase + i * spacing;
                // Fade in by the player's feet and out towards the end of the trail.
                double alpha = 1;
                if (along < 150) alpha = along / 150;
                if (along > length * 0.7) alpha = (length - along) / (length * 0.3);
                if (alpha < 0) alpha = 0;
                dots[used].SetColorAndOpacity(Ui.Color(colour.R, colour.G, colour.B, (float)(alpha * 0.9)));
                dotSlots[used].SetPosition(position);
                dots[used].SetVisibility(ESlateVisibility.HitTestInvisible);
                used++;
            }
        }
        for (int i = used; i < dots.Count; i++) dots[i].SetVisibility(ESlateVisibility.Collapsed);
    }

    void DrawBeacon(APlayerController controller, FVector at, FVector2D screen, FLinearColor colour, double time)
    {
        if (mod == null || beacon == null || arrow == null || label == null) return;
        var target = MapMath.Vec(mod.Waypoint.X, mod.Waypoint.Y, mod.Waypoint.Z + 160);
        double metres = MapMath.Dist2D(at, mod.Waypoint) / 100;
        label.SetText($"{metres:0} m");
        label.SetColorAndOpacity(Ui.SlateColor(Ui.Color(1, 1, 1, 1)));

        bool projected = UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, target, out FVector2D position, true);
        bool onScreen = projected && position.X > EdgeMargin && position.Y > EdgeMargin
            && position.X < screen.X - EdgeMargin && position.Y < screen.Y - EdgeMargin;

        if (onScreen)
        {
            double pulse = 1 + 0.08 * UKismetMathLibrary.sin(time * 4);
            beacon.SetRenderScale(MapMath.Vec2(pulse, pulse));
            beacon.SetColorAndOpacity(colour);
            beaconSlot?.SetPosition(position);
            beacon.SetVisibility(ESlateVisibility.HitTestInvisible);
            labelSlot?.SetPosition(MapMath.Vec2(position.X, position.Y + BeaconSize * 0.6));
            label.SetVisibility(mod.ShowArrow ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
            arrow.SetVisibility(ESlateVisibility.Collapsed);
            return;
        }

        beacon.SetVisibility(ESlateVisibility.Collapsed);
        if (!mod.ShowArrow)
        {
            arrow.SetVisibility(ESlateVisibility.Collapsed);
            label.SetVisibility(ESlateVisibility.Collapsed);
            return;
        }

        // The direction on screen: from the player towards the waypoint. Behind the camera the waypoint's own projection
        // flips, so take a point a few metres from the player in the waypoint's direction instead.
        var centre = MapMath.Vec2(screen.X / 2, screen.Y / 2);
        double dx = position.X - centre.X;
        double dy = position.Y - centre.Y;
        if (!projected)
        {
            double flat = MapMath.Dist2D(at, mod.Waypoint);
            if (flat < 1) flat = 1;
            var near = MapMath.Vec(at.X + (mod.Waypoint.X - at.X) / flat * 500, at.Y + (mod.Waypoint.Y - at.Y) / flat * 500, at.Z);
            bool fromOk = UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, at, out FVector2D from, true);
            bool toOk = UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, near, out FVector2D to, true);
            if (!fromOk || !toOk)
            {
                arrow.SetVisibility(ESlateVisibility.Collapsed);
                label.SetVisibility(ESlateVisibility.Collapsed);
                return;
            }
            dx = to.X - from.X;
            dy = to.Y - from.Y;
        }
        double len = MapMath.Length2(dx, dy);
        if (len < 0.001) len = 0.001;
        dx = dx / len;
        dy = dy / len;
        // Push out from the centre until the arrow meets the edge of the screen (less a margin).
        double halfW = centre.X - EdgeMargin;
        double halfH = centre.Y - EdgeMargin;
        double tx = dx > 0.0001 || dx < -0.0001 ? halfW / MapMath.Abs(dx) : 100000;
        double ty = dy > 0.0001 || dy < -0.0001 ? halfH / MapMath.Abs(dy) : 100000;
        double t = tx < ty ? tx : ty;
        var edge = MapMath.Vec2(centre.X + dx * t, centre.Y + dy * t);

        arrow.SetColorAndOpacity(colour);
        arrow.SetRenderTransformAngle((float)UKismetMathLibrary.DegAtan2(dx, -dy));
        arrowSlot?.SetPosition(edge);
        arrow.SetVisibility(ESlateVisibility.HitTestInvisible);
        labelSlot?.SetPosition(MapMath.Vec2(edge.X - dx * 34, edge.Y - dy * 34 - 10));
        label.SetVisibility(ESlateVisibility.HitTestInvisible);
    }

    void HideAll()
    {
        if (hidden) return;
        hidden = true;
        foreach (var dot in dots) dot.SetVisibility(ESlateVisibility.Collapsed);
        beacon?.SetVisibility(ESlateVisibility.Collapsed);
        arrow?.SetVisibility(ESlateVisibility.Collapsed);
        label?.SetVisibility(ESlateVisibility.Collapsed);
    }
}
