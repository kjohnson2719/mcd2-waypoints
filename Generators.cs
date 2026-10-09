using System.Collections.Generic;
using NeoRune;
using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// The soulstorm's generators on the player's screen, under the game's UI: each one's icon over it with its distance, or
/// an arrow at the screen's edge pointing to it when it's off screen. ModActor calls <see cref="Refresh"/> every frame.
/// </summary>
public class GeneratorOverlay : ModWidget
{
    const int Count = 8;
    const float IconSize = 30;
    const float ArrowSize = 26;
    const double EdgeMargin = 70;

    ModActor? mod;
    UCanvasPanel? canvas;
    List<UImage> icons;
    List<UCanvasPanelSlot> iconSlots;
    List<UImage> arrows;
    List<UCanvasPanelSlot> arrowSlots;
    List<UTextBlock> labels;
    List<UCanvasPanelSlot> labelSlots;
    bool hidden;

    protected override UWidget? Build(UWidgetTree tree)
    {
        icons = new List<UImage>();
        iconSlots = new List<UCanvasPanelSlot>();
        arrows = new List<UImage>();
        arrowSlots = new List<UCanvasPanelSlot>();
        labels = new List<UTextBlock>();
        labelSlots = new List<UCanvasPanelSlot>();
        canvas = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCanvasPanel>(), tree) as UCanvasPanel;
        if (canvas == null) return null;
        var iconTexture = Images.Load(this, "generator.png");
        var arrowTexture = Images.Load(this, "arrow.png");
        for (int i = 0; i < Count; i++)
        {
            var icon = Layers.Image(tree, iconTexture);
            var iconSlot = Layers.Place(canvas, icon, IconSize);
            var arrow = Layers.Image(tree, arrowTexture);
            var arrowSlot = Layers.Place(canvas, arrow, ArrowSize);
            var label = Ui.Text(tree, "", 15);
            if (icon == null || iconSlot == null || arrow == null || arrowSlot == null || label == null) continue;
            label.SetShadowOffset(MapMath.Vec2(1, 1));
            label.SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.85f));
            var labelSlot = canvas.AddChildToCanvas(label);
            if (labelSlot == null) continue;
            labelSlot.SetAutoSize(true);
            labelSlot.SetAlignment(MapMath.Vec2(0.5, 0));
            icons.Add(icon);
            iconSlots.Add(iconSlot);
            arrows.Add(arrow);
            arrowSlots.Add(arrowSlot);
            labels.Add(label);
            labelSlots.Add(labelSlot);
        }
        HideAll();
        return canvas;
    }

    public void Begin(ModActor owner)
    {
        mod = owner;
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
        AddToPlayerScreen(-9);
    }

    public void Refresh(bool mapOpen)
    {
        var player = World.Player(this);
        var controller = World.PlayerController(this);
        if (mod == null || player == null || controller == null || mapOpen || !mod.ShowGenerators || mod.Generators.Count == 0)
        {
            HideAll();
            return;
        }
        hidden = false;
        var at = player.K2_GetActorLocation();
        double scale = UWidgetLayoutLibrary.GetViewportScale(this);
        if (scale <= 0) scale = 1;
        var viewport = UWidgetLayoutLibrary.GetViewportSize(this);
        var screen = MapMath.Vec2(viewport.X / scale, viewport.Y / scale);
        var centre = MapMath.Vec2(screen.X / 2, screen.Y / 2);
        var colour = ModActor.GeneratorColour();
        int used = 0;
        foreach (var where in mod.Generators)
        {
            if (used >= icons.Count) break;
            var above = MapMath.Vec(where.X, where.Y, where.Z + 300);
            labels[used].SetText($"{MapMath.Dist2D(at, where) / 100:0} m");
            icons[used].SetColorAndOpacity(colour);
            arrows[used].SetColorAndOpacity(colour);
            bool projected = UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, above, out FVector2D position, true);
            bool onScreen = projected && position.X > EdgeMargin && position.Y > EdgeMargin
                && position.X < screen.X - EdgeMargin && position.Y < screen.Y - EdgeMargin;
            if (onScreen)
            {
                iconSlots[used].SetPosition(position);
                labelSlots[used].SetPosition(MapMath.Vec2(position.X, position.Y + IconSize * 0.6));
                icons[used].SetVisibility(ESlateVisibility.HitTestInvisible);
                arrows[used].SetVisibility(ESlateVisibility.Collapsed);
                labels[used].SetVisibility(ESlateVisibility.HitTestInvisible);
                used++;
                continue;
            }
            // Off screen: towards it from the player, pushed out to the screen's edge, its icon just inside the arrow.
            if (!UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, at, out FVector2D from, true)) continue;
            double flat = MapMath.Dist2D(at, where);
            if (flat < 1) flat = 1;
            var near = MapMath.Vec(at.X + (where.X - at.X) / flat * 500, at.Y + (where.Y - at.Y) / flat * 500, at.Z);
            if (!UWidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(controller, near, out FVector2D to, true)) continue;
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double len = MapMath.Length2(dx, dy);
            if (len < 0.001) continue;
            dx = dx / len;
            dy = dy / len;
            double halfW = centre.X - EdgeMargin;
            double halfH = centre.Y - EdgeMargin;
            double tx = MapMath.Abs(dx) > 0.0001 ? halfW / MapMath.Abs(dx) : 100000;
            double ty = MapMath.Abs(dy) > 0.0001 ? halfH / MapMath.Abs(dy) : 100000;
            double t = MapMath.Min(tx, ty);
            var edge = MapMath.Vec2(centre.X + dx * t, centre.Y + dy * t);
            arrows[used].SetRenderTransformAngle((float)UKismetMathLibrary.DegAtan2(dx, -dy));
            arrowSlots[used].SetPosition(edge);
            var inside = MapMath.Vec2(edge.X - dx * 34, edge.Y - dy * 34);
            iconSlots[used].SetPosition(inside);
            labelSlots[used].SetPosition(MapMath.Vec2(inside.X, inside.Y + IconSize * 0.6));
            icons[used].SetVisibility(ESlateVisibility.HitTestInvisible);
            arrows[used].SetVisibility(ESlateVisibility.HitTestInvisible);
            labels[used].SetVisibility(ESlateVisibility.HitTestInvisible);
            used++;
        }
        for (int i = used; i < icons.Count; i++)
        {
            icons[i].SetVisibility(ESlateVisibility.Collapsed);
            arrows[i].SetVisibility(ESlateVisibility.Collapsed);
            labels[i].SetVisibility(ESlateVisibility.Collapsed);
        }
    }

    void HideAll()
    {
        if (hidden) return;
        hidden = true;
        for (int i = 0; i < icons.Count; i++)
        {
            icons[i].SetVisibility(ESlateVisibility.Collapsed);
            arrows[i].SetVisibility(ESlateVisibility.Collapsed);
            labels[i].SetVisibility(ESlateVisibility.Collapsed);
        }
    }
}
