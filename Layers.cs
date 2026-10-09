using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;
using NeoRune;

namespace Waypoints;

/// <summary>Helpers for the layers the mod adds into the game's maps.</summary>
public static class Layers
{
    /// <summary>Makes a slot cover the same area as a sibling's slot, whatever panel they're in.</summary>
    public static void CoverLike(UPanelSlot? source, UPanelSlot? slot)
    {
        var overlaySlot = slot as UOverlaySlot;
        if (overlaySlot != null)
        {
            overlaySlot.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Fill);
            overlaySlot.SetVerticalAlignment(EVerticalAlignment.VAlign_Fill);
            return;
        }
        var gridSlot = slot as UGridSlot;
        if (gridSlot != null)
        {
            var sourceGrid = source as UGridSlot;
            if (sourceGrid != null)
            {
                gridSlot.SetRow(sourceGrid.Row);
                gridSlot.SetColumn(sourceGrid.Column);
                gridSlot.SetRowSpan(sourceGrid.RowSpan);
                gridSlot.SetColumnSpan(sourceGrid.ColumnSpan);
                gridSlot.SetLayer(sourceGrid.Layer + 1);
            }
            gridSlot.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Fill);
            gridSlot.SetVerticalAlignment(EVerticalAlignment.VAlign_Fill);
            return;
        }
        var canvasSlot = slot as UCanvasPanelSlot;
        if (canvasSlot != null)
        {
            var sourceCanvas = source as UCanvasPanelSlot;
            if (sourceCanvas != null) canvasSlot.SetLayout(sourceCanvas.GetLayout());
            else
            {
                canvasSlot.SetAnchors(new UE.Slate.FAnchors { Minimum = MapMath.Vec2(0, 0), Maximum = MapMath.Vec2(1, 1) });
                canvasSlot.SetOffsets(Ui.Margin(0));
            }
        }
    }

    /// <summary>
    /// The root for a layer inside one of the game's widgets: a box that asks for no space, holding the canvas the layer
    /// draws on. The game's minimap scales its content to fit the space that content asks for, so a layer that asked
    /// for any (its markers are placed on a canvas, which asks for room to reach them) would rescale the game's minimap,
    /// and a bad position once (mid-animation) would wreck it for good. The box still fills its slot, so the canvas
    /// covers the same area as before.
    /// </summary>
    public static USizeBox? LayoutNeutral(UWidgetTree tree, UCanvasPanel? canvas)
    {
        var box = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (box == null || canvas == null) return null;
        box.SetWidthOverride(0);
        box.SetHeightOverride(0);
        var slot = box.AddChild(canvas) as USizeBoxSlot;
        slot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Fill);
        slot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Fill);
        return box;
    }

    /// <summary>Whether a position is a real number of a sane size (not NaN or infinite from a degenerate geometry).</summary>
#pragma warning disable CS1718   // x == x is the NaN test (NaN isn't equal to itself)
    public static bool Sane(FVector2D p) => p.X == p.X && p.Y == p.Y && MapMath.Abs(p.X) < 100000 && MapMath.Abs(p.Y) < 100000;
#pragma warning restore CS1718

    /// <summary>
    /// Whether a layer's own geometry can convert positions: a real place and scale on screen. Its size doesn't matter
    /// (a layer asks for no space, so it can be laid out at no size; its canvas still draws around its corner).
    /// </summary>
    public static bool UsableTransform(FGeometry geometry)
    {
        var from = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(0, 0));
        var to = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(100, 100));
        return Sane(from) && Sane(to) && MapMath.DistUi(from, to) > 0.5;
    }

    /// <summary>Whether a widget's geometry can be used: laid out, with a real size and scale.</summary>
    public static bool Usable(FGeometry geometry)
    {
        var size = USlateBlueprintLibrary.GetLocalSize(geometry);
        if (!(size.X > 0.5) || !(size.Y > 0.5)) return false;
        var from = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(0, 0));
        var to = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(size.X, size.Y));
        if (!Sane(from) || !Sane(to)) return false;
        return MapMath.DistUi(from, to) > 0.5;
    }

    /// <summary>A point in another widget's coordinates, in <paramref name="mine"/>'s.</summary>
    public static FVector2D ToLocal(FGeometry mine, FGeometry other, FVector2D local) =>
        USlateBlueprintLibrary.AbsoluteToLocal(mine, USlateBlueprintLibrary.LocalToAbsolute(other, local));

    /// <summary>The centre of a widget, in <paramref name="mine"/>'s coordinates.</summary>
    public static FVector2D Centre(FGeometry mine, UWidget widget)
    {
        var geometry = widget.GetCachedGeometry();
        var size = USlateBlueprintLibrary.GetLocalSize(geometry);
        return ToLocal(mine, geometry, MapMath.Vec2(size.X / 2, size.Y / 2));
    }

    /// <summary>The centre of a widget in absolute (desktop) pixels.</summary>
    public static FVector2D AbsoluteCentre(UWidget widget)
    {
        var geometry = widget.GetCachedGeometry();
        var size = USlateBlueprintLibrary.GetLocalSize(geometry);
        return USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(size.X / 2, size.Y / 2));
    }

    public static UImage? Image(UWidgetTree tree, UTexture2D? texture)
    {
        var image = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        image?.SetBrushFromTexture(texture, false);
        return image;
    }

    /// <summary>Adds an image to a canvas at a fixed size, centred on the positions it's given.</summary>
    public static UCanvasPanelSlot? Place(UCanvasPanel? canvas, UImage? image, float size)
    {
        if (image == null || canvas == null) return null;
        var slot = canvas.AddChildToCanvas(image);
        slot?.SetAutoSize(false);
        slot?.SetSize(MapMath.Vec2(size, size));
        slot?.SetAlignment(MapMath.Vec2(0.5, 0.5));
        return slot;
    }
}
