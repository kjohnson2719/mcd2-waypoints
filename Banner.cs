using NeoRune;
using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;

namespace Waypoints;

/// <summary>
/// The look of the game's area banner (the one shown on entering an area or dungeon), copied from one the game showed:
/// its text font and colours, its backdrop and its sound. Kept on the mod actor, so it outlives the game's banner.
/// </summary>
public struct BannerStyle
{
    public bool Known;
    public FSlateFontInfo Font;
    public FSlateColor TextColour;
    public FVector2D ShadowOffset;
    public FLinearColor ShadowColour;
    public FSlateBrush Backdrop;
    public FLinearColor BackdropColour;
    public string Sound;
}

/// <summary>
/// The "Waypoint reached" banner: the waypoint's mark over a title on a band, in the game's banner style when it's known.
/// It sits where the game shows its area banners (the HUD's top notification slot).
/// </summary>
public class ReachedBanner : ModWidget
{
    const float IconSize = 40;

    UBorder? band;
    UImage? icon;
    UTextBlock? title;

    protected override UWidget? Build(UWidgetTree tree)
    {
        var column = Ui.Column(tree);
        icon = Layers.Image(tree, Images.Load(this, "waypoint.png"));
        var iconBox = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (iconBox != null && icon != null)
        {
            iconBox.SetWidthOverride(IconSize);
            iconBox.SetHeightOverride(IconSize);
            iconBox.AddChild(icon);
            var slot = column?.AddChild(iconBox) as UVerticalBoxSlot;
            slot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Center);
            slot?.SetPadding(new FMargin { Bottom = 4 });
        }
        title = Ui.Text(tree, "Waypoint Reached", 36);
        if (title != null)
        {
            title.SetShadowOffset(MapMath.Vec2(2, 2));
            title.SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.8f));
            var slot = column?.AddChild(title) as UVerticalBoxSlot;
            slot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Center);
        }
        band = UGameplayStatics.SpawnObject(Unreal.ClassOf<UBorder>(), tree) as UBorder;
        if (band == null) return column;
        band.SetBrushFromTexture(Images.Load(this, "band.png"));
        band.SetBrushColor(Ui.Color(0, 0, 0, 0.7f));
        band.SetPadding(new FMargin { Left = 160, Right = 160, Top = 18, Bottom = 22 });
        band.AddChild(column);
        band.SetRenderOpacity(0);
        // Centred at the top of whatever space it's given (the game's slot, or the whole screen as a fallback).
        var root = UGameplayStatics.SpawnObject(Unreal.ClassOf<UOverlay>(), tree) as UOverlay;
        if (root == null) return band;
        var bandSlot = root.AddChildToOverlay(band);
        bandSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Center);
        bandSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Top);
        return root;
    }

    /// <summary>Takes on the game's banner style (when known) and the waypoint's colour.</summary>
    public void Style(BannerStyle style, FLinearColor colour)
    {
        icon?.SetColorAndOpacity(colour);
        if (!style.Known || title == null || band == null) return;
        title.SetFont(style.Font);
        title.SetColorAndOpacity(style.TextColour);
        title.SetShadowOffset(style.ShadowOffset);
        title.SetShadowColorAndOpacity(style.ShadowColour);
        band.SetBrush(style.Backdrop);
        band.SetBrushColor(style.BackdropColour);
    }

    /// <summary>Pushes the band down from the top (when shown on the whole screen instead of the game's slot).</summary>
    public void SetTopOffset(float top)
    {
        var slot = band?.Slot as UOverlaySlot;
        slot?.SetPadding(new FMargin { Top = top });
    }

    /// <summary>The banner <paramref name="t"/> seconds in: it grows in, holds, then fades out.</summary>
    public void Animate(double t, double hold)
    {
        if (band == null) return;
        double opacity = 1;
        double scale = 1;
        if (t < 0.35)
        {
            double k = t / 0.35;
            opacity = k;
            scale = 1.12 - 0.12 * k * (2 - k);
        }
        else if (t > hold) opacity = 1 - (t - hold) / 0.6;
        if (opacity < 0) opacity = 0;
        band.SetRenderOpacity((float)opacity);
        band.SetRenderScale(MapMath.Vec2(scale, scale));
    }
}

/// <summary>
/// Shows a <see cref="ReachedBanner"/> for a few seconds, then takes it away. It goes in the game's top notification
/// slot; if the HUD doesn't have that slot right now, on the player's screen at the same place.
/// </summary>
public class BannerShow : AActor
{
    const double Hold = 3.4;
    const double Total = 4.1;

    GameSlot? slot;
    ReachedBanner? onScreen;
    BannerStyle style;
    FLinearColor colour;
    double startedAt;
    bool styled;

    public void Begin(BannerStyle bannerStyle, FLinearColor waypointColour)
    {
        style = bannerStyle;
        colour = waypointColour;
        startedAt = World.RealTime(this);
        slot = GameUI.AddToSlot(this, GameSlots.TopTemporaryNotifications, Unreal.ClassOf<ReachedBanner>(), 100);
        if (style.Known && UKismetStringLibrary.Len(style.Sound) > 0 && style.Sound != "None") Sounds.Play(this, style.Sound);
        else Sounds.Confirm(this);
        Timer.Start(this, nameof(Step), 0.02f, true);
    }

    void Step()
    {
        double t = World.RealTime(this) - startedAt;
        var banner = slot?.Widget as ReachedBanner;
        // No slot to show in after a moment (another screen is up): show it on the player's screen instead.
        if (banner == null && onScreen == null && t > 0.3)
        {
            slot?.Remove();
            slot = null;
            onScreen = Ui.Create(this, Unreal.ClassOf<ReachedBanner>()) as ReachedBanner;
            onScreen?.AddToPlayerScreen(5);
            onScreen?.SetVisibility(ESlateVisibility.HitTestInvisible);
            onScreen?.SetTopOffset(140);
        }
        if (onScreen != null) banner = onScreen;
        if (banner != null)
        {
            if (!styled)
            {
                styled = true;
                banner.Style(style, colour);
            }
            banner.Animate(t, Hold);
        }
        if (t < Total) return;
        Timer.Stop(this, nameof(Step));
        slot?.Remove();
        onScreen?.RemoveFromParent();
        K2_DestroyActor();
    }
}
