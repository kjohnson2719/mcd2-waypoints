using System.Collections.Generic;
using NeoRune;
using UE.Angelscript;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Engine;
using UE.EnhancedInput;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// "Set Waypoint" (or "Remove Waypoint") with its button, next to the map screen's own control hints, in their style.
/// The button's icon is the game's own for the guidance action (left stick click on a controller), taken from the
/// HUD's guidance slot, so it changes with the controller like the game's hints do. Without it, the key's name.
/// </summary>
public class MapHint : ModWidget
{
    const float Gap = 28;   // UI pixels between this hint and the game's

    USizeBox? iconBox;
    UCommonActionWidget? actionIcon;
    UImage? icon;
    UTextBlock? keyName;
    UBorder? keyCap;
    string shownRow;
    UTextBlock? label;
    bool styled;

    protected override UWidget? Build(UWidgetTree tree)
    {
        // Like the game's hints: the action's name, then its button.
        var row = Ui.Row(tree);
        if (row == null) return null;
        label = Ui.Text(tree, "Set Waypoint", 20);
        if (label != null)
        {
            label.SetShadowOffset(MapMath.Vec2(1, 1));
            label.SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.75f));
            var slot = row.AddChild(label) as UHorizontalBoxSlot;
            slot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        }
        // The button: the game's own action icon widget (it follows the controller in use), or a copy of the HUD's.
        actionIcon = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCommonActionWidget>(), tree) as UCommonActionWidget;
        icon = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        var icons = UGameplayStatics.SpawnObject(Unreal.ClassOf<UOverlay>(), tree) as UOverlay;
        iconBox = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (iconBox != null && icon != null && icons != null)
        {
            iconBox.SetWidthOverride(32);
            iconBox.SetHeightOverride(32);
            if (actionIcon != null) icons.AddChildToOverlay(actionIcon);
            icons.AddChildToOverlay(icon);
            iconBox.AddChild(icons);
            var slot = row.AddChild(iconBox) as UHorizontalBoxSlot;
            slot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
            slot?.SetPadding(new FMargin { Left = 12 });
        }
        // Without an icon: the key's name on a small key-like plate.
        keyName = Ui.Text(tree, "", 16);
        keyCap = Ui.Panel(tree, keyName, Ui.Color(0.08f, 0.08f, 0.1f, 0.85f), 3);
        if (keyCap != null)
        {
            keyCap.SetPadding(new FMargin { Left = 7, Right = 7, Top = 2, Bottom = 2 });
            var slot = row.AddChild(keyCap) as UHorizontalBoxSlot;
            slot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
            slot?.SetPadding(new FMargin { Left = 12 });
        }
        SetVisibility(ESlateVisibility.HitTestInvisible);
        return row;
    }

    /// <summary>
    /// Updates the hint and puts it just left of the game's own map hints (Zoom in, Zoom out...), or in the bottom right
    /// corner when there are none.
    /// </summary>
    public void Refresh(bool removing, FDataTableRowHandle iconRow, bool iconRowOk, UInputAction? action, UCommonActionWidget? hudIcon, string fallbackKey)
    {
        label?.SetText(removing ? "Remove Waypoint" : "Set Waypoint");
        // The button's icon: the game's action icon for the guidance action, which changes with the controller in use;
        // else the HUD guidance button's current icon; else the key's name.
        // First choice: the icon of a game action bound to the same button (the way the game's hints get theirs).
        // Then an Enhanced Input action bound to it. The icon widget shows one or the other: the unused one is cleared.
        if (actionIcon != null && iconRowOk)
        {
            string rowName = $"{iconRow.RowName}";
            if (rowName != shownRow)
            {
                shownRow = rowName;
                actionIcon.SetEnhancedInputAction(null!);
                actionIcon.SetInputAction(iconRow);
            }
        }
        else if (actionIcon != null && action != null && actionIcon.GetEnhancedInputAction() != action)
        {
            shownRow = "";
            actionIcon.SetInputActions(new List<FDataTableRowHandle>());
            actionIcon.SetEnhancedInputAction(action);
        }
        bool native = actionIcon != null && (iconRowOk || action != null) && actionIcon.GetIcon().ResourceObject != null;
        var brush = new FSlateBrush();
        if (!native && hudIcon != null && UKismetSystemLibrary.IsValid(hudIcon))
        {
            brush = hudIcon.Icon;
            if (brush.ResourceObject == null) brush = hudIcon.GetIcon();
        }
        bool copied = !native && brush.ResourceObject != null;
        if (copied && icon != null) icon.SetBrush(brush);
        actionIcon?.SetVisibility(native ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
        icon?.SetVisibility(copied ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
        bool hasIcon = native || copied;
        iconBox?.SetVisibility(hasIcon ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
        keyName?.SetText(fallbackKey);
        keyCap?.SetVisibility(hasIcon ? ESlateVisibility.Collapsed : ESlateVisibility.HitTestInvisible);

        // The game's hints are its action bar's buttons, in the lowest row of those on screen. The bar splits them into
        // groups (Back on its own at the left, the map's actions at the right): join the rightmost group, at its left.
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var buttons, Unreal.ClassOf<UAS_SpicewoodBoundActionButton>(), false);
        var shown = new List<UUserWidget>();
        var lefts = new List<double>();
        var rights = new List<double>();
        var middles = new List<double>();
        double lowest = -1000000;
        foreach (var button in buttons)
        {
            if (button == null || !button.IsVisible()) continue;
            var geometry = button.GetCachedGeometry();
            if (!Layers.Usable(geometry)) continue;
            var size = USlateBlueprintLibrary.GetLocalSize(geometry);
            var start = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(0, size.Y / 2));
            var end = USlateBlueprintLibrary.LocalToAbsolute(geometry, MapMath.Vec2(size.X, size.Y / 2));
            shown.Add(button);
            lefts.Add(start.X);
            rights.Add(end.X);
            middles.Add(start.Y);
            if (start.Y > lowest) lowest = start.Y;
        }
        // The rightmost hint in the lowest row, then leftwards while the next one is close (a group's spacing, not the
        // wide gap between groups).
        double groupGap = 150 * UWidgetLayoutLibrary.GetViewportScale(this);
        UWidget? leftmost = null;
        double left = -1000000;
        double centreY = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            if (middles[i] < lowest - 20 || lefts[i] <= left) continue;
            left = lefts[i];
            centreY = middles[i];
            leftmost = shown[i];
        }
        for (int step = 0; step < shown.Count && leftmost != null; step++)
        {
            int next = -1;
            for (int i = 0; i < shown.Count; i++)
            {
                if (middles[i] < lowest - 20 || rights[i] > left + 1) continue;
                if (next < 0 || rights[i] > rights[next]) next = i;
            }
            if (next < 0 || left - rights[next] > groupGap) break;
            left = lefts[next];
            centreY = middles[next];
            leftmost = shown[next];
        }
        if (!styled && leftmost != null) StyleLike(leftmost);

        ForceLayoutPrepass();
        var own = GetDesiredSize();
        if (leftmost == null)
        {
            // No hints of the game's to sit by: the bottom right corner, above the screen's edge.
            double scale = UWidgetLayoutLibrary.GetViewportScale(this);
            if (scale <= 0) scale = 1;
            var screen = UWidgetLayoutLibrary.GetViewportSize(this);
            SetPositionInViewport(MapMath.Vec2(screen.X / scale - own.X - 64, screen.Y / scale - own.Y - 48), false);
            return;
        }
        USlateBlueprintLibrary.AbsoluteToViewport(this, MapMath.Vec2(left, centreY), out FVector2D pixel, out FVector2D at);
        SetPositionInViewport(MapMath.Vec2(at.X - own.X - Gap, at.Y - own.Y / 2), false);
    }

    /// <summary>Takes on the text style and icon size of one of the game's hints.</summary>
    void StyleLike(UWidget hint)
    {
        styled = true;
        var root = hint as UUserWidget;
        if (root == null) return;
        var text = GameUI.FindOfClass(root, Unreal.ClassOf<UTextBlock>()) as UTextBlock;
        if (text != null && label != null)
        {
            Ui.StyleLike(label, text);
            label.SetShadowOffset(text.ShadowOffset);
            label.SetShadowColorAndOpacity(text.ShadowColorAndOpacity);
            Ui.StyleLike(keyName, text);
        }
        var gameIcon = GameUI.FindOfClass(root, Unreal.ClassOf<UCommonActionWidget>());
        if (gameIcon == null || iconBox == null) return;
        var size = USlateBlueprintLibrary.GetLocalSize(gameIcon.GetCachedGeometry());
        if (size.X > 4 && size.X < 200 && size.Y > 4 && size.Y < 200)
        {
            iconBox.SetWidthOverride((float)size.X);
            iconBox.SetHeightOverride((float)size.Y);
        }
    }
}
