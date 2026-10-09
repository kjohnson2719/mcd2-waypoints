using System.Collections.Generic;
using NeoRune;
using UE.Angelscript;
using UE.AngelscriptEnhancedInput;
using UE.CommonInput;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Dungeons;
using UE.EnhancedInput;
using UE.Engine;
using UE.Game.Spicewood.Cues.Abilities.GCN_ShowGuidance;
using UE.GameplayMessageRuntime;
using UE.GameplayTags;
using UE.InputCore;
using UE.MapSystems;
using UE.Minimap;
using UE.NavigationSystem;
using UE.SpicewoodPlayerPing;
using UE.SpicewoodUI;
using UE.UMG;

namespace Waypoints;

/// <summary>The waypoint of a level, saved between sessions.</summary>
public struct SavedWaypoint
{
    public bool Active;
    public double X;
    public double Y;
    public double Z;
    // False while its height is a guess (set far away on the map, where the ground isn't loaded).
    public bool Grounded;
    // The region it's in (e.g. SW.Region.Overworld): it only guides while the player is in that region.
    public string Region;
    // Whether its height is where it is (else it's a map spot's: the ground's top surface there).
    public bool HeightKnown;
}

/// <summary>Which region each of the map screen's dimension tabs shows, learned while the player's own is shown.</summary>
public struct SavedTabs
{
    public List<string> Tabs;
    public List<string> Regions;
}

/// <summary>The minimap's learned world-to-map transform, one per region (normalised by the minimap's zoom).</summary>
public struct SavedCalibrations
{
    public List<string> Regions;
    public List<double> N11;
    public List<double> N12;
    public List<double> N21;
    public List<double> N22;
}

/// <summary>The first versions' single transform (the Overworld's, at the minimap's usual zoom).</summary>
public struct SavedCalibration
{
    public bool Valid;
    public double A11;
    public double A12;
    public double A21;
    public double A22;
}

/// <summary>
/// Waypoints: set a waypoint on the map screen (or under the mouse in the world, or by pinging) and follow the guide to
/// it: a route found on the game's navigation mesh, shown on the map, on the minimap and as a trail on the ground, with
/// an arrow at the screen's edge.
/// </summary>
[ModSetting.Heading("Waypoint")]
[ModSetting.Keybind("setKey", "Set or remove the waypoint on the map (at the crosshair or cursor)", Default = "F6", Secondary = "Gamepad_LeftThumbstick")]
[ModSetting.Keybind("clearKey", "Clear the waypoint", Default = "F7")]
[ModSetting.Toggle("fromPings", "Pinging also sets the waypoint", Default = true)]
[ModSetting.Heading("Guide")]
[ModSetting.Toggle("trail", "Path on the ground", Default = true)]
[ModSetting.Select("trailStyle", "Path style", new[] { "Guidance line when you press guidance (like quests)", "Dots, always shown" }, Default = 0)]
[ModSetting.Keybind("showKey", "Show the path to the waypoint (like the guidance button)", Default = "G", Secondary = "Gamepad_LeftThumbstick")]
[ModSetting.Toggle("questLine", "With a waypoint set, guidance also shows the quest's line", Default = false)]
[ModSetting.Toggle("minimap", "Waypoint and route on the minimap", Default = true)]
[ModSetting.Toggle("arrow", "Distance, and an arrow when it's off screen", Default = true)]
[ModSetting.Slider("trailLength", "Path length (metres)", Default = 100, Min = 20, Max = 200, Step = 10)]
[ModSetting.Colour("colour", "Colour", Default = "#FFD24A")]
[ModSetting.Heading("Soulstorms")]
[ModSetting.Toggle("generators", "Show where the generators are (on screen, the minimap and the map)", Default = true)]
[ModSetting.Heading("Troubleshooting")]
[ModSetting.Toggle("debug", "Write diagnostics to the mod's log", Default = true)]
public class ModActor : AActor, IModSettings
{
    const double ArriveDistance = 350;
    const double RepathDistance = 500;
    const double RepathSeconds = 2.5;
    const double Horizon = 12000;       // cm: how far ahead routes are found on the nav mesh (it's only loaded near the player)
    const double SnapRange = 8000;      // cm of a straight route that gets put on the ground
    const double GroundRange = 15000;   // cm: close enough for the ground under a waypoint to be loaded
    const double KeyGap = 0.2;          // seconds without input before a key counts as pressed again
    const string GuidanceClass = "/Game/Spicewood/Cues/Abilities/GCN_ShowGuidance.GCN_ShowGuidance_C";

    // Settings, with the same defaults as above (Blueprint Loader only sends the ones the player changed).
    public bool ShowTrail = true;
    public bool ShowMinimap = true;
    public bool ShowArrow = true;
    public bool FromPings = true;
    public double TrailLength = 100;
    // 0: the game's own guidance line, shown for a while when the guidance button is pressed (as for quests);
    // 1: the mod's dots, always shown.
    public int TrailStyle;
    public bool ShowQuestLine;
    public bool ShowGenerators = true;
    public FLinearColor Colour = new FLinearColor { R = 1, G = 0.644f, B = 0.068f, A = 1 };
    public bool Debug = true;
    FKey setKey = new FKey { KeyName = "F6" };
    FKey setPadKey = new FKey { KeyName = "Gamepad_LeftThumbstick" };
    FKey clearKey = new FKey { KeyName = "F7" };
    FKey clearPadKey = new FKey { KeyName = "None" };
    FKey showKey = new FKey { KeyName = "G" };
    FKey showPadKey = new FKey { KeyName = "Gamepad_LeftThumbstick" };

    // Keys: in the world, the usual in-game listeners (they don't take the key from the game: its guidance still gets
    // left stick click). On the map screen, a menu, those don't hear anything: there the game's own key tracker does,
    // switched on only while the map is open, as it keeps the keys it tracks from the game's menus (found in game:
    // the inventory couldn't sort with left stick click while it was always on).
    UKeyboardDirectionTickWrapper? keyTracker;
    List<FKey> trackedKeys = new();
    KeyListener? setListener;
    KeyListener? showListener;
    KeyListener? showPadListener;
    List<KeyListener> guidanceListeners = new();
    bool trackerOn;
    KeyListener? clearListener;
    // The keys the game binds to its guidance action (left stick click, and whatever it is on the keyboard): they set the
    // waypoint on the map and show the path in the world too. The HUD's guidance slot also gives the button's icon.
    List<FKey> guidanceKeys = new();
    UCommonActionWidget? guidanceIcon;
    UInputAction? guidanceAction;
    // Rows of the game's input action table bound to the set keys: the map hint shows their icons, as the game's do.
    FDataTableRowHandle padIconRow;
    FDataTableRowHandle keyIconRow;
    bool padIconKnown;
    bool keyIconKnown;
    bool iconRowsSearched;
    int iconSearches;
    // An Enhanced Input action bound to the pad's set key (the game's guidance action, found on some icon of its UI).
    UInputAction? padIconAction;
    bool loggedNoGuidanceAction;
    bool guidanceKeysKnown;
    List<FKey> pressKeys = new();
    List<double> pressTimes = new();
    double lastAction = -100;
    int loggedKeyEvents;

    // The waypoint, and the route to it from where the player was at pathFrom.
    public bool HasWaypoint;
    public FVector Waypoint;
    // The waypoint's region, and the player's (both gameplay tag names, e.g. SW.Region.Overworld).
    public string WaypointRegion = "";
    public string PlayerRegion = "";
    bool waypointHeightKnown;
    SavedTabs tabs;
    bool waypointGrounded;
    public List<FVector> Path = new();
    public bool PathFromNav;
    // Where the player is on the route this frame: a point on segment StartSeg.
    public int StartSeg;
    public FVector StartPoint;
    // How far the player is from the route (cm, on the ground).
    public double OffRoute = 100000000;
    FVector pathFrom;
    double pathTime = -100;
    bool loggedPath;
    double lastAngle;
    string routeSummary = "";

    // The game's guidance line (GCN_ShowGuidance, what the guidance button shows for quests), run along the route for a
    // while. A fresh one each time, as the game does: the effect fades out on its own when its time is up.
    AGCN_ShowGuidance_C? guidance;
    bool guidanceStarted;
    bool guidanceFailed;
    double guidanceShownAt = -100;
    double guidanceUntil;
    double guidanceSeconds = 10;
    int guidanceLogs;
    // The game's own guidance lines, to notice the guidance button: a line that has just become active.
    List<AGCN_ShowGuidance_C> gameLines = new();
    List<bool> gameLinesActive = new();
    int gameLineLogs;
    bool mapOpen;

    // The minimap's transform: map = player marker + A * (world - player), in UI pixels per cm. The game zooms the
    // minimap (it reports how wide a view it shows): N is A times that view width, which stays the same through zooms,
    // and A is worked out from it every frame. One N per region (each region's map has its own scale).
    public bool Calibrated;
    public double A11;
    public double A12;
    public double A21;
    public double A22;
    public double N11;
    public double N12;
    public double N21;
    public double N22;
    public double MinimapZoom = 20.1042;
    SavedCalibrations calibrations;
    string calibrationRegion = "";
    bool calibrationDirty;
    double lastCalibrationLog = -100;

    // Ping actors and the timestamps they had: the game keeps one per player and moves it for each ping.
    List<ASWPingActor> seenPings = new();
    List<double> seenStamps = new();
    public FVector LastPing;
    public double LastPingTime = -100;

    bool started;
    string level = "";
    TrailOverlay? trail;
    List<MinimapLayer> minimaps = new();
    List<WorldMapLayer> worldMaps = new();
    WidgetWatcher? minimapWatcher;
    WidgetWatcher? worldMapWatcher;
    bool dumpedMinimap;
    bool dumpedWorldMap;
    // The game's area banner look, once it has shown one; and the map screen's hint.
    BannerStyle bannerStyle;
    WidgetWatcher? areaBannerWatcher;
    MapHint? mapHint;

    // The soulstorm's generators (its "Storminators"): every one seen during the storm, where it stands, until it's
    // destroyed. The game only has those near the player loaded (it seems to make them as the player comes near), so
    // one that vanishes far away is still there: its spot is kept. One that vanishes close by was destroyed.
    public List<FVector> Generators = new();
    List<AActor> generatorActors = new();
    const double GeneratorGoneRange = 8000;
    Dictionary<TSubclassOf<UObject>, bool> generatorClasses = new();
    // The generator classes found so far, looked up directly every second (their actors aren't characters).
    List<TSubclassOf<AActor>> generatorTypes = new();
    List<string> generatorTypePaths = new();
    bool seededGeneratorTypes;
    GeneratorOverlay? generatorOverlay;
    bool stormOn;
    double lastWideScan = -100;
    bool loggedStormSearch;
    UAsyncAction_ListenForGameplayMessage? stormEvents;

    protected override void ReceiveBeginPlay()
    {
        // After the camera has moved this frame, so the trail sits still on the ground while the camera follows. And
        // also while the game is paused, so the map screen still shows the waypoint if it pauses the game.
        SetTickGroup(ETickingGroup.TG_PostUpdateWork);
        SetTickableWhenPaused(true);
        level = World.LevelName(this);
        LoadCalibrations();
        Timer.Start(this, nameof(TryStart), 0.5f, true);
    }

    /// <summary>Waits for the player's character (there's none on the main menu), then starts everything once.</summary>
    void TryStart()
    {
        if (started) return;
        var player = World.Player(this);
        if (player == null || World.PlayerController(this) == null) return;
        started = true;
        Timer.Stop(this, nameof(TryStart));
        if (Debug) Log.Write($"level {level}: started, player at {Describe(player.K2_GetActorLocation())}, minimap transform {(Calibrated ? "loaded" : "not learned yet")}");

        UpdatePlayerRegion();
        if (ModSave.Load("tabs", out SavedTabs savedTabs)) tabs = savedTabs;
        else tabs = new SavedTabs { Tabs = new List<string>(), Regions = new List<string>() };
        LoadWaypoint();

        trail = Ui.Create(this, Unreal.ClassOf<TrailOverlay>()) as TrailOverlay;
        trail?.Begin(this);
        ListenToKeys();

        minimapWatcher = WidgetWatcher.Start(this, Unreal.ClassOf<UAS_MiniMap>(), 0.5f);
        if (minimapWatcher != null) minimapWatcher.Found += OnMinimap;
        worldMapWatcher = WidgetWatcher.Start(this, Unreal.ClassOf<UAS_WorldMap>(), 0.5f);
        if (worldMapWatcher != null) worldMapWatcher.Found += OnWorldMap;
        areaBannerWatcher = WidgetWatcher.Start(this, Unreal.ClassOf<UAS_AreaEnteredNotification>(), 0.25f);
        if (areaBannerWatcher != null) areaBannerWatcher.Found += OnAreaBanner;
        Timer.Start(this, nameof(FindGuidanceKeys), 2f, true);
        Timer.Start(this, nameof(FindGenerators), 1f, true);
        stormEvents = GameEvents.Listen(this, GameChannels.SoulStorm, null, true);
        if (stormEvents != null) stormEvents.OnMessageReceived += OnStormMessage;
        generatorOverlay = Ui.Create(this, Unreal.ClassOf<GeneratorOverlay>()) as GeneratorOverlay;
        generatorOverlay?.Begin(this);

        Timer.Start(this, nameof(UpdatePath), 0.2f, true);
        Timer.Start(this, nameof(UpdateGuidance), 0.1f, true);
        Timer.Start(this, nameof(WatchGameGuidance), 0.1f, true);
        Timer.Start(this, nameof(PollPings), 0.25f, true);
        Timer.Start(this, nameof(SaveCalibration), 15f, true);
        if (Debug) Timer.Start(this, nameof(ProbeNavigation), 3f, false);
    }

    protected override void ReceiveEndPlay(EEndPlayReason EndPlayReason)
    {
        keyTracker?.SetIsEnabled(false);
        trackerOn = false;
        SaveCalibration();
    }

    public override void ReceiveTick(float deltaSeconds)
    {
        if (!started) return;
        UpdateMinimapScale();
        FindPlayerOnRoute();
        mapOpen = false;
        for (int i = worldMaps.Count - 1; i >= 0; i--)
        {
            if (!worldMaps[i].IsAlive())
            {
                worldMaps.RemoveAt(i);
                continue;
            }
            worldMaps[i].Refresh();
            if (worldMaps[i].IsOpen()) mapOpen = true;
        }
        if (keyTracker != null && mapOpen != trackerOn)
        {
            trackerOn = mapOpen;
            keyTracker.SetIsEnabled(mapOpen);
        }
        trail?.Refresh(mapOpen);
        generatorOverlay?.Refresh(mapOpen);
        UpdateMapHint();
        for (int i = minimaps.Count - 1; i >= 0; i--)
        {
            if (!minimaps[i].IsAlive()) minimaps.RemoveAt(i);
            else minimaps[i].Refresh();
        }
    }

    // ---------------------------------------------------------------- Settings

    public void OnSettingChanged(string id, string value)
    {
        switch (id)
        {
            case "fromPings": FromPings = ModSettings.ToBool(value); break;
            case "trail": ShowTrail = ModSettings.ToBool(value); break;
            case "minimap": ShowMinimap = ModSettings.ToBool(value); break;
            case "arrow": ShowArrow = ModSettings.ToBool(value); break;
            case "trailLength": TrailLength = ModSettings.ToNumber(value); break;
            case "trailStyle": TrailStyle = ModSettings.ToInt(value); break;
            case "questLine": ShowQuestLine = ModSettings.ToBool(value); break;
            case "generators": ShowGenerators = ModSettings.ToBool(value); break;
            case "colour": Colour = ModSettings.ToColour(value); break;
            case "debug": Debug = ModSettings.ToBool(value); break;
        }
    }

    public void OnKeybindChanged(string id, FKey key, FKey secondaryKey)
    {
        if (id == "setKey")
        {
            setKey = key;
            setPadKey = secondaryKey;
        }
        else if (id == "clearKey")
        {
            clearKey = key;
            clearPadKey = secondaryKey;
        }
        else if (id == "showKey")
        {
            showKey = key;
            showPadKey = secondaryKey;
        }
        else return;
        if (started) ListenToKeys();
    }

    public void OnSettingsReset()
    {
        ShowTrail = true;
        ShowMinimap = true;
        ShowArrow = true;
        FromPings = true;
        TrailLength = 100;
        TrailStyle = 0;
        ShowQuestLine = false;
        ShowGenerators = true;
        showKey = new FKey { KeyName = "G" };
        showPadKey = new FKey { KeyName = "Gamepad_LeftThumbstick" };
        Colour = ModSettings.ToColour("#FFD24A");
        Debug = true;
        setKey = new FKey { KeyName = "F6" };
        setPadKey = new FKey { KeyName = "Gamepad_LeftThumbstick" };
        clearKey = new FKey { KeyName = "F7" };
        clearPadKey = new FKey { KeyName = "None" };
        if (started) ListenToKeys();
    }

    void ListenToKeys()
    {
        // The keys may have changed: look for their icons again.
        iconRowsSearched = false;
        iconSearches = 0;
        padIconAction = null;
        padIconKnown = false;
        keyIconKnown = false;
        setListener?.Stop();
        clearListener?.Stop();
        showListener?.Stop();
        showPadListener?.Stop();
        foreach (var listener in guidanceListeners) listener.Stop();
        guidanceListeners = new List<KeyListener>();
        setListener = Input.OnKey(this, setKey);
        if (setListener != null) setListener.Pressed += OnSetKey;
        clearListener = Input.OnKey(this, clearKey);
        if (clearListener != null) clearListener.Pressed += OnClearKey;
        showListener = Input.OnKey(this, showKey);
        if (showListener != null) showListener.Pressed += OnShowKey;
        showPadListener = Input.OnKey(this, showPadKey);
        if (showPadListener != null) showPadListener.Pressed += OnShowKey;
        foreach (var key in guidanceKeys)
        {
            var listener = Input.OnKey(this, key);
            if (listener == null) continue;
            listener.Pressed += OnShowKey;
            guidanceListeners.Add(listener);
        }

        if (keyTracker == null)
        {
            keyTracker = UKeyboardDirectionTickWrapper.Create();
            var localPlayer = trail?.GetOwningLocalPlayer();
            if (keyTracker != null && localPlayer != null)
            {
                keyTracker.SetOwningPlayer(localPlayer);
                keyTracker.OnInputPerformedThisTick = OnTrackedKey;
                keyTracker.SetIsEnabled(false);
                trackerOn = false;
            }
            else if (Debug) Log.Write("keys: couldn't set up the game's key tracker, keys won't work on the map screen");
        }
        if (keyTracker == null) return;
        foreach (var key in trackedKeys) keyTracker.UntrackKey(key);
        trackedKeys = new List<FKey>();
        Track(setKey);
        Track(setPadKey);
        Track(clearKey);
        Track(clearPadKey);
        foreach (var key in guidanceKeys) Track(key);
    }

    void Track(FKey key)
    {
        // An unbound key is "None" (an empty name can't be compiled: NeoRune 1.0 can't write empty names).
        if (keyTracker == null || key.KeyName == "None") return;
        foreach (var tracked in trackedKeys)
            if (UKismetInputLibrary.EqualEqual_KeyKey(tracked, key)) return;
        keyTracker.TrackKey(key);
        trackedKeys.Add(key);
    }

    /// <summary>
    /// The game's key tracker reports tracked keys while they're down (also on the map screen). A key counts as pressed
    /// when it's reported after a short gap.
    /// </summary>
    void OnTrackedKey(float DeltaTime, FKey Key, EInputEvent InputEvent)
    {
        if (Debug && loggedKeyEvents < 12)
        {
            loggedKeyEvents++;
            Log.Write($"keys: tracker reported {Key.KeyName} {InputEvent}");
        }
        if (InputEvent == EInputEvent.IE_Released || !mapOpen) return;
        if (!NewPress(Key, World.RealTime(this))) return;
        bool isSet = IsGuidanceKey(Key) || UKismetInputLibrary.EqualEqual_KeyKey(Key, setKey) || UKismetInputLibrary.EqualEqual_KeyKey(Key, setPadKey);
        bool isClear = UKismetInputLibrary.EqualEqual_KeyKey(Key, clearKey) || UKismetInputLibrary.EqualEqual_KeyKey(Key, clearPadKey);
        if (isSet) OnSetPressed(UKismetInputLibrary.Key_IsGamepadKey(Key));
        else if (isClear) OnClearPressed();
    }

    /// <summary>Whether a report of a key is a new press: the first after a short gap (the tracker reports held keys every tick).</summary>
    bool NewPress(FKey key, double now)
    {
        for (int i = 0; i < pressKeys.Count; i++)
        {
            if (!UKismetInputLibrary.EqualEqual_KeyKey(pressKeys[i], key)) continue;
            bool pressed = now - pressTimes[i] > KeyGap;
            pressTimes[i] = now;
            return pressed;
        }
        pressKeys.Add(key);
        pressTimes.Add(now);
        return true;
    }

    void OnSetKey() => OnSetPressed(false);

    void OnShowKey() => ShowGuidance(guidanceSeconds, "show key");

    void OnClearKey() => OnClearPressed();

    /// <summary>True (and starts the moment) unless an action just happened: one press can arrive through two routes.</summary>
    bool ActionAllowed()
    {
        double now = World.RealTime(this);
        if (now - lastAction < 0.3) return false;
        lastAction = now;
        return true;
    }

    // ---------------------------------------------------------------- Setting and clearing

    /// <summary>
    /// The set key. On the map screen: removes the waypoint under the crosshair or cursor, or puts one there. In the
    /// world (mouse only): the point on the minimap under the mouse, else the ground under it.
    /// </summary>
    void OnSetPressed(bool gamepad)
    {
        var player = World.Player(this);
        var controller = World.PlayerController(this);
        if (player == null || controller == null || !ActionAllowed()) return;
        var at = player.K2_GetActorLocation();

        foreach (var worldMap in worldMaps)
        {
            if (!worldMap.IsOpen()) continue;
            if (worldMap.PickWorld(at, out FVector picked, out string pickedRegion))
            {
                if (HasWaypoint && worldMap.IsUnderPick(Waypoint))
                {
                    ClearWaypoint();
                    Sounds.Click(this);
                }
                // On a marker, its place has its height; a plain spot on the map doesn't.
                else SetWaypointIn(picked, worldMap.LastSnap == "" ? "map" : "map, on " + worldMap.LastSnap, pickedRegion, worldMap.LastSnap != "");
            }
            else
            {
                Sounds.Forbidden(this);
                if (Debug) Log.Write("map: couldn't work out where the crosshair or cursor is in the world");
            }
            return;
        }

        // Outside the map screen, waypoints are set with the mouse.
        if (gamepad) return;
        var mouse = UWidgetLayoutLibrary.GetMousePositionOnPlatform();
        foreach (var minimap in minimaps)
        {
            if (minimap.ScreenToWorld(mouse, at, out FVector onMap))
            {
                SetWaypoint(onMap, "minimap", false);
                return;
            }
        }
        if (controller.GetHitResultUnderCursor(ECollisionChannel.ECC_Visibility, false, out FHitResult hit) && hit.bBlockingHit)
            SetWaypoint(MapMath.Vec(hit.ImpactPoint.X, hit.ImpactPoint.Y, hit.ImpactPoint.Z), "mouse", true);
    }

    void OnClearPressed()
    {
        if (!HasWaypoint || !ActionAllowed()) return;
        ClearWaypoint();
        Sounds.Click(this);
    }

    public void SetWaypoint(FVector target, string how, bool heightKnown) => SetWaypointIn(target, how, PlayerRegion, heightKnown);

    /// <summary>
    /// Sets the waypoint. <paramref name="heightKnown"/>: the target's height is where it is (the ground under the mouse,
    /// a marker); else it's only a place on a map, at the player's height, and goes onto the ground's top surface there.
    /// </summary>
    public void SetWaypointIn(FVector target, string how, string region, bool heightKnown)
    {
        var player = World.Player(this);
        if (player == null) return;
        var at = player.K2_GetActorLocation();
        HasWaypoint = true;
        WaypointRegion = region;
        waypointHeightKnown = heightKnown;
        // Another region's ground isn't here to find: it gets its height when the player is there.
        var spot = target;
        waypointGrounded = false;
        if (region == PlayerRegion && OnGround(target, heightKnown, out FVector grounded))
        {
            spot = grounded;
            waypointGrounded = true;
        }
        Waypoint = spot;
        pathTime = -100;
        loggedPath = false;
        SaveWaypoint();
        UpdatePath();
        Sounds.Confirm(this);
        if (Debug) Log.Write($"waypoint set from {how} in {region}: {Describe(target)} -> {Describe(spot)}{(waypointGrounded ? "" : " (ground not loaded there yet)")}");
    }

    /// <summary>Whether to guide: there's a waypoint, and the player is in its region.</summary>
    public bool Active() => HasWaypoint && (WaypointRegion == "" || WaypointRegion == PlayerRegion);

    void UpdatePlayerRegion()
    {
        var maps = USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<UClientMapsSubsystem>()) as UClientMapsSubsystem;
        if (maps == null) return;
        string region = $"{maps.GetPrimaryPlayerRegionContext().PrimaryRegion.TagName}";
        if (region == "None" || region == PlayerRegion) return;
        PlayerRegion = region;
        if (Debug) Log.Write($"region: {PlayerRegion}");
        UseCalibrationFor(PlayerRegion);
    }

    /// <summary>The region a dimension tab of the map screen shows ("" when it isn't known yet).</summary>
    public string RegionForTab(string tab)
    {
        // Found in game: the tabs are named after their regions (SW.Region.Overworld, SW.Region.Sift).
        if (tab.StartsWith("SW.Region.")) return tab;
        for (int i = 0; i < tabs.Tabs.Count; i++)
            if (tabs.Tabs[i] == tab) return tabs.Regions[i];
        // Else the tab may share the region's last part (e.g. ...Dimension.Sift and ...Region.Sift): look among the
        // regions the game's maps know.
        string last = LastPart(tab);
        var regions = USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<URegionViewModelSubsystem>()) as URegionViewModelSubsystem;
        if (regions == null) return "";
        foreach (var pair in regions.RegionViewModels)
        {
            string name = $"{pair.Key.TagName}";
            if (LastPart(name) == last) return name;
        }
        return "";
    }

    /// <summary>The last part of a gameplay tag's name: "Sift" for "SW.Region.Sift".</summary>
    static string LastPart(string tag)
    {
        int dot = UKismetStringLibrary.FindSubstring(tag, ".", false, true, -1);
        return dot < 0 ? tag : UKismetStringLibrary.GetSubstring(tag, dot + 1, UKismetStringLibrary.Len(tag) - dot - 1);
    }

    /// <summary>The map screen shows the player's own region under a tab: remember which region that tab is.</summary>
    public void LearnTab(string tab, string region)
    {
        for (int i = 0; i < tabs.Tabs.Count; i++)
        {
            if (tabs.Tabs[i] != tab) continue;
            if (tabs.Regions[i] == region) return;
            tabs.Regions[i] = region;
            ModSave.Save("tabs", tabs);
            return;
        }
        tabs.Tabs.Add(tab);
        tabs.Regions.Add(region);
        ModSave.Save("tabs", tabs);
        if (Debug) Log.Write($"map: the {tab} tab shows {region}");
    }

    public void ClearWaypoint()
    {
        HasWaypoint = false;
        Path.Clear();
        EndGuidance();
        SaveWaypoint();
    }

    /// <summary>
    /// A point moved onto the walkable ground, else onto whatever the ground is. Without a known height, the ground's top
    /// surface there (not a cave far below: found in game, a map pick went 34 m underground). False (and the point
    /// unchanged) when the ground there isn't loaded.
    /// </summary>
    bool OnGround(FVector point, bool heightKnown, out FVector spot)
    {
        if (!heightKnown && GroundZ(point, out double surface))
        {
            var top = MapMath.Vec(point.X, point.Y, surface);
            spot = top;
            if (UNavigationSystemV1.K2_ProjectPointToNavigation(this, top, out FVector walkable, null!, null!, MapMath.Vec(300, 300, 400))) spot = walkable;
            return true;
        }
        if (UNavigationSystemV1.K2_ProjectPointToNavigation(this, point, out FVector projected, null!, null!, MapMath.Vec(300, 300, heightKnown ? 600 : 50000)))
        {
            spot = projected;
            return true;
        }
        spot = point;
        if (!GroundZ(point, out double z)) return false;
        spot = MapMath.Vec(point.X, point.Y, z);
        return true;
    }

    bool GroundZ(FVector point, out double z)
    {
        z = point.Z;
        var ignore = new List<AActor>();
        var player = World.Player(this);
        if (player != null) ignore.Add(player);
        var none = Ui.Color(0, 0, 0, 0);
        if (!UKismetSystemLibrary.LineTraceSingle(this, MapMath.Vec(point.X, point.Y, point.Z + 50000), MapMath.Vec(point.X, point.Y, point.Z - 50000),
                ETraceTypeQuery.TraceTypeQuery1, false, ignore, EDrawDebugTrace.None, out FHitResult hit, true, none, none, 0) || !hit.bBlockingHit)
            return false;
        z = hit.ImpactPoint.Z;
        return true;
    }

    void SaveWaypoint()
    {
        ModSave.Save("waypoint_" + level, new SavedWaypoint { Active = HasWaypoint, X = Waypoint.X, Y = Waypoint.Y, Z = Waypoint.Z, Grounded = waypointGrounded, Region = WaypointRegion, HeightKnown = waypointHeightKnown });
    }

    void LoadWaypoint()
    {
        if (!ModSave.Load("waypoint_" + level, out SavedWaypoint saved) || !saved.Active) return;
        // The first version could save the unused ping actor's spot at the world's origin.
        if (MapMath.Abs(saved.X) < 1 && MapMath.Abs(saved.Y) < 1) return;
        HasWaypoint = true;
        Waypoint = MapMath.Vec(saved.X, saved.Y, saved.Z);
        waypointGrounded = saved.Grounded;
        waypointHeightKnown = saved.HeightKnown;
        // Saved before waypoints had regions: the player's (it was set where they were).
        WaypointRegion = saved.Region == "" ? PlayerRegion : saved.Region;
        if (Debug) Log.Write($"waypoint loaded in {WaypointRegion}: {Describe(Waypoint)}");
    }

    // ---------------------------------------------------------------- The route

    /// <summary>Finds the route again when the player has moved on or a while has passed; clears the waypoint on arrival.</summary>
    void UpdatePath()
    {
        UpdatePlayerRegion();
        if (HasWaypoint && WaypointRegion == "" && PlayerRegion != "")
        {
            WaypointRegion = PlayerRegion;
            SaveWaypoint();
        }
        var player = World.Player(this);
        if (!Active() || player == null)
        {
            Path.Clear();
            return;
        }
        var at = player.K2_GetActorLocation();
        double distance = MapMath.Dist2D(at, Waypoint);
        // A waypoint set far away gets its height once the ground there has loaded.
        if (!waypointGrounded && distance < GroundRange && OnGround(Waypoint, waypointHeightKnown, out FVector grounded))
        {
            waypointGrounded = true;
            Waypoint = grounded;
            pathTime = -100;
            SaveWaypoint();
            if (Debug) Log.Write($"waypoint put on the ground: {Describe(Waypoint)}");
        }
        if (distance < ArriveDistance && MapMath.Abs(at.Z - Waypoint.Z) < 600)
        {
            ClearWaypoint();
            var banner = World.Spawn(this, Unreal.ClassOf<BannerShow>(), at) as BannerShow;
            banner?.Begin(bannerStyle, Colour);
            return;
        }
        double now = World.RealTime(this);
        if (Path.Count >= 2 && MapMath.Dist2D(at, pathFrom) < RepathDistance && now - pathTime < RepathSeconds) return;
        pathFrom = at;
        pathTime = now;

        var planned = PlanRoute(player, at, distance, out bool viaNav, out string kind);
        // Off the walkable area for a moment (a jump, water, a rock), no route is found: keep the last walkable one while
        // the player is still near it, instead of flicking to a straight line.
        if (!viaNav && PathFromNav && OffRoute < 1500) return;
        Path = planned;
        PathFromNav = viaNav;
        FindPlayerOnRoute();
        UpdateGuidance();
        if (Debug && (!loggedPath || kind != routeSummary))
        {
            loggedPath = true;
            routeSummary = kind;
            Log.Write($"route: {kind}, {Path.Count} points, {distance / 100:0} m away, heading {lastAngle:0} deg off the straight line");
        }
    }

    /// <summary>
    /// The route: on the nav mesh as far as it's loaded (about <see cref="Horizon"/> ahead), then straight on. Goals to
    /// either side of the straight line are tried too, and the one that ends closest to the waypoint (counting the walk
    /// there) wins, so the route goes round lakes, cliffs and walls instead of into them. It's found again every few
    /// metres, so the far part keeps turning into a walkable route as the player goes.
    /// </summary>
    List<FVector> PlanRoute(APawn player, FVector at, double distance, out bool viaNav, out string kind)
    {
        viaNav = false;
        kind = "line";
        double dx = (Waypoint.X - at.X) / distance;
        double dy = (Waypoint.Y - at.Y) / distance;
        // Start from the walkable ground nearest the player (they may be a little off it).
        var start = at;
        if (UNavigationSystemV1.K2_ProjectPointToNavigation(this, at, out FVector startOnNav, null!, null!, MapMath.Vec(600, 600, 1500))) start = startOnNav;
        List<FVector> best = new List<FVector>();
        bool found = false;
        double bestCost = 1000000000000.0;
        double bestAngle = 0;
        for (int ring = 0; ring < 3 && !found; ring++)
        {
            double radius = distance < Horizon ? distance : Horizon;
            if (ring == 1) radius = radius * 0.6;
            if (ring == 2) radius = radius * 0.3;
            for (int k = 0; k < 9; k++)
            {
                // Close by, just go to the waypoint.
                if (k > 0 && distance < 2000) break;
                // 0, +20, -20, +40, -40 ... +80, -80 degrees.
                double angle = ((k + 1) / 2) * 20;
                if (k % 2 == 0) angle = -angle;
                double c = UKismetMathLibrary.DegCos(angle);
                double s = UKismetMathLibrary.DegSin(angle);
                var goal = k == 0 && radius >= distance ? Waypoint
                    : MapMath.Vec(at.X + (dx * c - dy * s) * radius, at.Y + (dx * s + dy * c) * radius, at.Z);
                if (!UNavigationSystemV1.K2_ProjectPointToNavigation(this, goal, out FVector onNav, null!, null!, MapMath.Vec(2000, 2000, 8000))) continue;
                var route = UNavigationSystemV1.FindPathToLocationSynchronously(this, start, onNav, player, null!);
                if (route == null || !route.IsValid() || route.PathPoints.Count < 2) continue;
                var points = route.PathPoints;
                var end = points[points.Count - 1];
                double cost = route.GetPathLength() + MapMath.Dist2D(end, Waypoint) + MapMath.Abs(angle - lastAngle) * 20;
                if (route.IsPartial()) cost = cost + 3000;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = points;
                    bestAngle = angle;
                    found = true;
                }
            }
        }
        if (!found) return StraightLine(at, Waypoint);
        viaNav = true;
        lastAngle = bestAngle;
        var last = best[best.Count - 1];
        if (MapMath.Dist2D(last, Waypoint) <= 300)
        {
            kind = "nav";
            return best;
        }
        kind = "nav then line";
        var rest = StraightLine(last, Waypoint);
        rest.RemoveAt(0);
        best.AddRange(rest);
        return best;
    }

    /// <summary>A straight route, with its first stretch put on the ground so the trail follows the terrain.</summary>
    List<FVector> StraightLine(FVector from, FVector to)
    {
        var points = new List<FVector>();
        double length = MapMath.Dist2D(from, to);
        int steps = (int)(length / 400);
        if (steps > 200) steps = 200;
        points.Add(from);
        for (int i = 1; i < steps; i++)
        {
            var point = MapMath.Lerp(from, to, (double)i / steps);
            if (i * 400 < SnapRange && GroundZ(point, out double z)) point = MapMath.Vec(point.X, point.Y, z + 30);
            points.Add(point);
        }
        points.Add(to);
        return points;
    }

    /// <summary>Where the player is on the route now (it was found from where they were a moment ago).</summary>
    void FindPlayerOnRoute()
    {
        StartSeg = 0;
        OffRoute = 100000000;
        var player = World.Player(this);
        if (player == null || Path.Count < 2) return;
        var at = player.K2_GetActorLocation();
        StartPoint = Path[0];
        double best = 100000000;
        int last = Path.Count - 2;
        if (last > 4) last = 4;
        for (int i = 0; i <= last; i++)
        {
            var point = MapMath.Lerp(Path[i], Path[i + 1], MapMath.ClosestT(Path[i], Path[i + 1], at));
            double distance = MapMath.Dist2D(point, at);
            if (distance < best)
            {
                best = distance;
                StartSeg = i;
                StartPoint = point;
            }
        }
        OffRoute = best;
    }

    // ---------------------------------------------------------------- The game's guidance line

    /// <summary>Whether the route is drawn with the mod's own dots (chosen, or the game's line couldn't be used).</summary>
    public bool ShowDots() => ShowTrail && (TrailStyle == 1 || guidanceFailed);

    /// <summary>
    /// Shows the route with the game's guidance line for a while, as the guidance button does for quests: a fresh line
    /// each time, along the route ahead, following the player until its time is up.
    /// </summary>
    public void ShowGuidance(double seconds, string why)
    {
        if (!Active() || !ShowTrail || TrailStyle != 0 || guidanceFailed) return;
        double now = World.RealTime(this);
        // One press can arrive twice: through the show key and through the game's own line appearing.
        if (now - guidanceShownAt < 0.5)
        {
            guidanceUntil = guidanceShownAt + seconds;
            return;
        }
        EndGuidance();
        guidanceShownAt = now;
        guidanceUntil = now + seconds;
        if (Debug && guidanceLogs < 5)
        {
            guidanceLogs++;
            Log.Write($"guidance line: shown for {seconds:0.0} s ({why})");
        }
        UpdateGuidance();
    }

    /// <summary>
    /// Lays the line along the route ahead once, when it's shown, and ends it when its time is up. Like the game's, it
    /// then stays where it was laid: the effect leaves its glow in the world, so moving or reshaping it while it shows
    /// would leave a trail of copies behind.
    /// </summary>
    void UpdateGuidance()
    {
        double now = World.RealTime(this);
        bool wanted = now < guidanceUntil && Active() && ShowTrail && TrailStyle == 0 && !guidanceFailed && !mapOpen;
        if (!wanted)
        {
            EndGuidance();
            return;
        }
        if (guidanceStarted || Path.Count < 2) return;
        var player = World.Player(this);
        if (player == null) return;
        var at = player.K2_GetActorLocation();
        if (guidance == null || !UKismetSystemLibrary.IsValid(guidance))
        {
            var guidanceClass = Unreal.LoadClass<AActor>(GuidanceClass);
            if (guidanceClass != null)
            {
                Trail.Begin("Waypoints guidance line");
                guidance = World.Spawn(this, guidanceClass, at) as AGCN_ShowGuidance_C;
                Trail.End("Waypoints guidance line");
            }
            if (guidance == null || guidance.SplineComponent == null)
            {
                guidanceFailed = true;
                if (Debug) Log.Write($"guidance line: couldn't use the game's guidance line ({(guidanceClass == null ? "class not found" : "no spline")}), drawing dots instead");
                return;
            }
            guidance.TotalEffectTime = (float)(guidanceUntil - guidanceShownAt);
            guidanceStarted = false;
        }
        var points = MapMath.PointsAlong(Path, StartSeg, StartPoint, 0, 250, 100, TrailLength * 100);
        var spline = guidance.SplineComponent;
        if (points.Count < 2 || spline == null) return;
        spline.SetSplinePoints(points, ESplineCoordinateSpace.World, true);
        guidance.NormalizedRemainingTime = 1;
        guidanceStarted = true;
        guidance.EnableNiagaraSystem();
    }

    void EndGuidance()
    {
        guidanceUntil = 0;
        if (guidance != null && UKismetSystemLibrary.IsValid(guidance))
        {
            guidance.DisableNiagaraSystem();
            guidance.K2_DestroyActor();
        }
        guidance = null;
        guidanceStarted = false;
    }

    /// <summary>
    /// The guidance button also makes the game show the quest's line. Right after the path is shown, a game line that
    /// comes on is hidden, so only the waypoint's shows (unless the player wants both). The game's lines are left alone
    /// otherwise: the game turns them on and off itself, which says nothing about the button.
    /// </summary>
    void WatchGameGuidance()
    {
        for (int i = gameLines.Count - 1; i >= 0; i--)
        {
            if (UKismetSystemLibrary.IsValid(gameLines[i])) continue;
            gameLines.RemoveAt(i);
            gameLinesActive.RemoveAt(i);
        }
        double now = World.RealTime(this);
        var actors = World.FindAll(this, Unreal.ClassOf<AGCN_ShowGuidance_C>());
        foreach (var actor in actors)
        {
            var line = actor as AGCN_ShowGuidance_C;
            if (line == null || line == guidance) continue;
            var niagara = line.Niagara;
            bool active = niagara != null && niagara.IsActive();
            int index = gameLines.IndexOf(line);
            if (index < 0)
            {
                gameLines.Add(line);
                gameLinesActive.Add(false);
                index = gameLines.Count - 1;
            }
            bool was = gameLinesActive[index];
            gameLinesActive[index] = active;
            if (!active || was) continue;
            if (line.TotalEffectTime > 1) guidanceSeconds = line.TotalEffectTime;
            if (Debug && gameLineLogs < 3)
            {
                gameLineLogs++;
                Log.Write($"guidance button: the game showed its line for {line.TotalEffectTime:0.0} s");
            }
            bool justShown = now - guidanceShownAt < 1.5;
            if (justShown && Active() && ShowTrail && TrailStyle == 0 && !ShowQuestLine) line.DisableNiagaraSystem();
        }
    }

    // ---------------------------------------------------------------- Looking like the game

    /// <summary>The game showed its area banner: remember its look, for the "Waypoint reached" banner.</summary>
    void OnAreaBanner(UUserWidget widget)
    {
        var game = widget as UAS_AreaEnteredNotification;
        if (game == null || bannerStyle.Known) return;
        var name = game.AreaName;
        var border = game.AreaBannerBorder;
        if (name == null || border == null) return;
        bannerStyle = new BannerStyle
        {
            Known = true,
            Font = name.Font,
            TextColour = name.ColorAndOpacity,
            ShadowOffset = name.ShadowOffset,
            ShadowColour = name.ShadowColorAndOpacity,
            Backdrop = border.Background,
            BackdropColour = border.BrushColor,
            Sound = $"{game.AppearSound.TagName}",
        };
        if (Debug) Log.Write($"banner style: copied from {GameUI.ClassPath(game)} (sound {game.AppearSound.TagName})");
    }

    /// <summary>
    /// Finds the game's guidance action through the HUD's guidance slot: its icon (for the map hint) and the keys bound
    /// to it (to listen to). Tries every couple of seconds until the slot exists.
    /// </summary>
    void FindGuidanceKeys()
    {
        if (guidanceKeysKnown)
        {
            Timer.Stop(this, nameof(FindGuidanceKeys));
            return;
        }
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var slots, Unreal.ClassOf<UAS_GuidanceSlot>(), false);
        foreach (var slot in slots)
        {
            var icon = GameUI.FindOfClass(slot, Unreal.ClassOf<UCommonActionWidget>()) as UCommonActionWidget;
            if (icon == null) continue;
            guidanceIcon = icon;
            var action = icon.GetEnhancedInputAction();
            var spicewood = icon as USpicewoodActionWidget;
            if (action == null && spicewood != null) action = spicewood.AssociatedInputAction;
            var controller = World.PlayerController(this);
            var input = controller == null ? null
                : USubsystemBlueprintLibrary.GetLocalPlayerSubSystemFromPlayerController(controller, Unreal.ClassOf<UEnhancedInputLocalPlayerSubsystem>()) as UEnhancedInputLocalPlayerSubsystem;
            if (action == null || input == null)
            {
                if (Debug && !loggedNoGuidanceAction)
                {
                    loggedNoGuidanceAction = true;
                    Log.Write($"guidance action: slot {GameUI.ClassPath(slot)}, icon {GameUI.ClassPath(icon)}, action {(action == null ? "not found" : "found")}, input {(input == null ? "not found" : "found")}");
                }
                continue;
            }
            guidanceAction = action;
            guidanceKeys = UEnhancedInputLocalPlayerSubsystemScriptMixinLibrary.QueryKeysMappedToAction(input, action);
            guidanceKeysKnown = true;
            string names = "";
            foreach (var key in guidanceKeys) names = names + " " + $"{key.KeyName}";
            if (Debug) Log.Write($"guidance action: {UKismetSystemLibrary.GetObjectName(action)}, keys:{names}");
            if (started) ListenToKeys();
            return;
        }
    }

    bool IsGuidanceKey(FKey key)
    {
        foreach (var guidanceKey in guidanceKeys)
            if (UKismetInputLibrary.EqualEqual_KeyKey(guidanceKey, key)) return true;
        return false;
    }

    /// <summary>The map screen's "Set Waypoint" hint, next to the game's, while the map is open.</summary>
    void UpdateMapHint()
    {
        WorldMapLayer? open = null;
        foreach (var worldMap in worldMaps)
            if (worldMap.IsOpen()) open = worldMap;
        if (open == null)
        {
            if (mapHint != null && mapHint.IsInViewport()) mapHint.RemoveFromParent();
            return;
        }
        if (mapHint == null) mapHint = Ui.Create(this, Unreal.ClassOf<MapHint>()) as MapHint;
        if (mapHint == null) return;
        // Above the map screen (it's a menu).
        if (!mapHint.IsInViewport()) mapHint.AddToViewport(10000);
        var controller = World.PlayerController(this);
        var input = controller == null ? null
            : USubsystemBlueprintLibrary.GetLocalPlayerSubSystemFromPlayerController(controller, Unreal.ClassOf<UCommonInputSubsystem>()) as UCommonInputSubsystem;
        bool gamepad = input != null && input.GetCurrentInputType() == ECommonInputType.Gamepad;
        if (!iconRowsSearched) FindIconRows(open);
        string key = gamepad ? KeyLabel(setPadKey) : KeyLabel(setKey);
        var iconRow = gamepad ? padIconRow : keyIconRow;
        bool iconRowOk = gamepad ? padIconKnown : keyIconKnown;
        var iconAction = gamepad ? (padIconAction ?? guidanceAction) : null;
        mapHint.Refresh(HasWaypoint && open.IsUnderPick(Waypoint), iconRow, iconRowOk, iconAction, guidanceIcon, key);
    }

    /// <summary>
    /// Finds rows of the game's input action table whose buttons are the set keys, for their icons (an action table row
    /// is what the game's hints show icons for). Once a map screen is open, as that's where the table comes from.
    /// </summary>
    void FindIconRows(WorldMapLayer layer)
    {
        // A few tries (on the first map openings), as more of the game's UI gets built.
        iconSearches++;
        if (iconSearches >= 3) iconRowsSearched = true;
        var tables = new List<UDataTable>();
        var actions = new List<UInputAction>();
        var mapTable = layer.InputTable();
        if (mapTable != null) tables.Add(mapTable);
        CollectActionIcons(tables, actions);

        int rows = 0;
        foreach (var table in tables)
        {
            UDataTableFunctionLibrary.GetDataTableRowNames(table, out var names);
            foreach (var name in names)
            {
                if (!Unreal.DataTableRow(table, name, out FCommonInputActionDataBase row)) continue;
                rows++;
                if (!padIconKnown && UKismetInputLibrary.EqualEqual_KeyKey(row.DefaultGamepadInputTypeInfo.Key, setPadKey))
                {
                    padIconRow = new FDataTableRowHandle { DataTable = table, RowName = name };
                    padIconKnown = true;
                }
                if (!keyIconKnown && UKismetInputLibrary.EqualEqual_KeyKey(row.KeyboardInputTypeInfo.Key, setKey))
                {
                    keyIconRow = new FDataTableRowHandle { DataTable = table, RowName = name };
                    keyIconKnown = true;
                }
            }
        }
        // No action table row for the pad's key: an Enhanced Input action bound to it (the game's guidance action).
        var controller = World.PlayerController(this);
        var input = controller == null ? null
            : USubsystemBlueprintLibrary.GetLocalPlayerSubSystemFromPlayerController(controller, Unreal.ClassOf<UEnhancedInputLocalPlayerSubsystem>()) as UEnhancedInputLocalPlayerSubsystem;
        if (!padIconKnown && padIconAction == null && input != null)
        {
            foreach (var action in actions)
            {
                foreach (var key in UEnhancedInputLocalPlayerSubsystemScriptMixinLibrary.QueryKeysMappedToAction(input, action))
                    if (UKismetInputLibrary.EqualEqual_KeyKey(key, setPadKey)) padIconAction = action;
                if (padIconAction != null) break;
            }
        }
        if (padIconKnown || padIconAction != null) iconRowsSearched = true;
        if (Debug) Log.Write($"map hint icons: {tables.Count} action tables ({rows} actions), {actions.Count} input actions; {setPadKey.KeyName} {(padIconKnown ? "in " + padIconRow.RowName : padIconAction != null ? "on " + UKismetSystemLibrary.GetObjectName(padIconAction) : "not found")}, {setKey.KeyName} {(keyIconKnown ? "in " + keyIconRow.RowName : "not found")}");
    }

    /// <summary>
    /// Every action icon in the game's UI (the widgets that show a button's icon): the action tables their actions come
    /// from, and the Enhanced Input actions they show. Each user widget's own tree, down to the user widgets inside it
    /// (those are in the list of user widgets too).
    /// </summary>
    void CollectActionIcons(List<UDataTable> tables, List<UInputAction> actions)
    {
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var roots, Unreal.ClassOf<UUserWidget>(), false);
        var pending = new List<UWidget>();
        int visited = 0;
        foreach (var root in roots)
        {
            if (root == null || root.WidgetTree == null || root.WidgetTree.RootWidget == null) continue;
            pending.Add(root.WidgetTree.RootWidget);
            while (pending.Count > 0 && visited < 50000)
            {
                var widget = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                visited++;
                if (widget == null || widget is UUserWidget) continue;
                var icon = widget as UCommonActionWidget;
                if (icon != null)
                {
                    foreach (var row in icon.InputActions)
                        if (row.DataTable != null && !tables.Contains(row.DataTable)) tables.Add(row.DataTable);
                    var action = icon.GetEnhancedInputAction();
                    var spicewood = icon as USpicewoodActionWidget;
                    if (action == null && spicewood != null) action = spicewood.AssociatedInputAction;
                    if (action != null && !actions.Contains(action)) actions.Add(action);
                    continue;
                }
                var panel = widget as UPanelWidget;
                if (panel != null) pending.AddRange(panel.GetAllChildren());
            }
            pending.Clear();
        }
    }

    /// <summary>A key's short name for the hint when there's no icon for it: "L3" for the left stick's click, "F6"...</summary>
    static string KeyLabel(FKey key)
    {
        string name = $"{key.KeyName}";
        if (name == "Gamepad_LeftThumbstick") return "L3";
        if (name == "Gamepad_RightThumbstick") return "R3";
        return $"{UKismetInputLibrary.Key_GetDisplayName(key, true)}";
    }

    // ---------------------------------------------------------------- Soulstorm generators

    /// <summary>The generators' colour: the soulstorm's blue.</summary>
    public static FLinearColor GeneratorColour() => new FLinearColor { R = 0.3f, G = 0.8f, B = 1, A = 1 };

    /// <summary>
    /// Finds the soulstorm's generators while a storm is on (the game's soulstorm tracker is on the HUD): the characters
    /// whose class is a Storminator, still standing. Class checks are remembered, so a scan only looks each class up once.
    /// </summary>
    void FindGenerators()
    {
        if (!ShowGenerators)
        {
            Generators.Clear();
            generatorActors.Clear();
            return;
        }
        // During a storm: the generator classes found in game (Easy, Medium, Hard pillars), and now and then a look at
        // every actor's class for others (an update may add some).
        double now = World.RealTime(this);
        if (stormOn && !seededGeneratorTypes)
        {
            seededGeneratorTypes = true;
            AddGeneratorType("/Game/Spicewood/Art/Characters/InWorldEvents/SoulStorm/Storminator/BP_Soulstorm_Pillar_Easy.BP_Soulstorm_Pillar_Easy_C");
            AddGeneratorType("/Game/Spicewood/Art/Characters/InWorldEvents/SoulStorm/Storminator/BP_Soulstorm_Pillar_Medium.BP_Soulstorm_Pillar_Medium_C");
            AddGeneratorType("/Game/Spicewood/Art/Characters/InWorldEvents/SoulStorm/Storminator/BP_Soulstorm_Pillar_Hard.BP_Soulstorm_Pillar_Hard_C");
        }
        if (stormOn && now - lastWideScan > 3)
        {
            lastWideScan = now;
            foreach (var actor in World.FindAll(this, Unreal.ClassOf<AActor>())) IsStandingGenerator(actor);
        }
        var found = new List<AActor>();
        foreach (var type in generatorTypes)
            foreach (var actor in World.FindAll(this, type))
                if (actor != null && !found.Contains(actor) && IsStandingGenerator(actor)) found.Add(actor);
        if (found.Count == 0 && stormOn && Debug && !loggedStormSearch && generatorTypes.Count == 0)
        {
            loggedStormSearch = true;
            LogNearbyClasses();
        }
        var player = World.Player(this);
        var at = player != null ? player.K2_GetActorLocation() : new FVector();
        // Ones no longer here: destroyed if the player is close (else just not loaded where the player is now).
        for (int i = generatorActors.Count - 1; i >= 0; i--)
        {
            if (found.Contains(generatorActors[i])) continue;
            bool loaded = UKismetSystemLibrary.IsValid(generatorActors[i]);
            if (!loaded && MapMath.Dist2D(at, Generators[i]) > GeneratorGoneRange) continue;
            if (Debug) Log.Write($"soulstorm: generator at {Describe(Generators[i])} gone ({MapMath.Dist2D(at, Generators[i]) / 100:0} m away)");
            generatorActors.RemoveAt(i);
            Generators.RemoveAt(i);
        }
        foreach (var actor in found)
        {
            int index = generatorActors.IndexOf(actor);
            var where = actor.K2_GetActorLocation();
            if (index >= 0)
            {
                Generators[index] = where;
                continue;
            }
            generatorActors.Add(actor);
            Generators.Add(where);
            if (Debug) Log.Write($"soulstorm: generator {GameUI.ClassPath(actor)} at {Describe(where)}, {MapMath.Dist2D(at, where) / 100:0} m away ({Generators.Count} known)");
        }
    }

    /// <summary>A generator class to look up directly (if the game has it loaded: no generators otherwise).</summary>
    void AddGeneratorType(string path)
    {
        if (generatorTypePaths.Contains(path)) return;
        generatorTypePaths.Add(path);
        var type = Unreal.LoadClass<AActor>(path);
        if (type != null) generatorTypes.Add(type);
    }

    /// <summary>The game's soulstorm messages: entering or starting one, leaving it, its end.</summary>
    void OnStormMessage(UAsyncAction_ListenForGameplayMessage? message, FGameplayTag channel)
    {
        string name = $"{channel.TagName}";
        bool on = !name.EndsWith(".Exit") && !name.EndsWith(".End");
        if (on != stormOn && Debug) Log.Write($"soulstorm: {name}");
        if (name.EndsWith(".End"))
        {
            Generators.Clear();
            generatorActors.Clear();
        }
        stormOn = on;
    }

    bool IsStandingGenerator(AActor? actor)
    {
        if (actor == null) return false;
        var actorClass = UGameplayStatics.GetObjectClass(actor);
        if (actorClass == null) return false;
        bool generator;
        if (generatorClasses.ContainsKey(actorClass)) generator = generatorClasses[actorClass];
        else
        {
            string path = GameUI.ClassPath(actor);
            // The generators, not their death effects (BP_Soulstorm_Pillar_*_Death) or cues.
            generator = UKismetStringLibrary.Contains(path, "Storminator", false, false) && !UKismetStringLibrary.Contains(path, "Death", false, false)
                && !UKismetStringLibrary.Contains(path, "GCN_", false, false);
            generatorClasses.Add(actorClass, generator);
            if (generator)
            {
                AddGeneratorType(path);
                if (Debug) Log.Write($"soulstorm: generator class {path}");
            }
            else if (Debug && UKismetStringLibrary.Contains(path, "storm", false, false)) Log.Write($"soulstorm: related class {path} at {Describe(actor.K2_GetActorLocation())}");
        }
        if (!generator) return false;
        // A defeated one may stay a moment: only those that can still be hit.
        var character = actor as ABaseCharacter;
        return character == null || character.IsTargetable();
    }

    /// <summary>For finding the generators' class: the kinds of actor within 60 m of the player during a storm.</summary>
    void LogNearbyClasses()
    {
        var player = World.Player(this);
        if (player == null) return;
        var at = player.K2_GetActorLocation();
        var seen = new List<string>();
        foreach (var actor in World.FindAll(this, Unreal.ClassOf<AActor>()))
        {
            if (actor == null || MapMath.Dist2D(actor.K2_GetActorLocation(), at) > 6000) continue;
            string path = GameUI.ClassPath(actor);
            if (seen.Contains(path) || seen.Count >= 60) continue;
            seen.Add(path);
        }
        Log.Write("soulstorm: no generators found; actors nearby:");
        Log.WriteAll(seen);
    }

    // ---------------------------------------------------------------- Pings

    /// <summary>
    /// Notices pings: they help learn the minimap, and the player's own can set the waypoint. The game keeps a ping actor
    /// per player (waiting at the world's origin) and moves it for each ping, so a ping is a new timestamp, not a new actor.
    /// </summary>
    void PollPings()
    {
        var actors = World.FindAll(this, Unreal.ClassOf<ASWPingActor>());
        foreach (var actor in actors)
        {
            var ping = actor as ASWPingActor;
            if (ping == null) continue;
            int index = seenPings.IndexOf(ping);
            if (index < 0)
            {
                // A ping already showing when we start (or a new actor made for a ping) counts once it's away from the origin.
                seenPings.Add(ping);
                seenStamps.Add(0);
                index = seenPings.Count - 1;
            }
            double stamp = ping.Timestamp;
            if (stamp == seenStamps[index]) continue;
            seenStamps[index] = stamp;
            var where = ping.K2_GetActorLocation();
            if (stamp <= 0 || (MapMath.Abs(where.X) < 1 && MapMath.Abs(where.Y) < 1)) continue;
            LastPing = where;
            LastPingTime = World.RealTime(this);
            var owner = ping.GetOwner();
            bool mine = IsMine(owner);
            if (Debug) Log.Write($"ping at {Describe(where)}, time {stamp:0.0}, owner {GameUI.ClassPath(owner)} ({(mine ? "mine" : "someone else's")}), emote {ping.EmoteTag.TagName}");
            if (FromPings && mine) SetWaypoint(where, "ping", true);
        }
    }

    bool IsMine(AActor? owner)
    {
        if (owner == null) return true;
        var controller = World.PlayerController(this);
        if (owner == controller || owner == World.Player(this)) return true;
        if (controller != null && owner == controller.PlayerState) return true;
        // A ping can be owned by something the player owns: follow the chain once.
        var outer = owner.GetOwner();
        return outer != null && (outer == controller || outer == World.Player(this));
    }

    // ---------------------------------------------------------------- Minimap and map screen

    void OnMinimap(UUserWidget widget)
    {
        var minimap = widget as UAS_MiniMap;
        if (minimap == null) return;
        if (Debug && !dumpedMinimap)
        {
            dumpedMinimap = true;
            Log.Write("minimap found");
        }
        var layer = Ui.Create(this, Unreal.ClassOf<MinimapLayer>()) as MinimapLayer;
        if (layer == null) return;
        if (layer.Attach(this, minimap)) minimaps.Add(layer);
        else if (Debug) Log.Write("minimap: couldn't add the waypoint layer");
    }

    void OnWorldMap(UUserWidget widget)
    {
        var worldMap = widget as UAS_WorldMap;
        if (worldMap == null) return;
        var layer = Ui.Create(this, Unreal.ClassOf<WorldMapLayer>()) as WorldMapLayer;
        if (layer == null) return;
        if (layer.Attach(this, worldMap)) worldMaps.Add(layer);
        else if (Debug) Log.Write("map screen: couldn't add the waypoint layer");
        if (Debug && !dumpedWorldMap)
        {
            dumpedWorldMap = true;
            Log.Write("map screen found");
            layer.LogInputs();
        }
    }

    /// <summary>The minimap's zoom (the game's view width) and the transform for it, every frame.</summary>
    void UpdateMinimapScale()
    {
        var maps = USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<UClientMapsSubsystem>()) as UClientMapsSubsystem;
        if (maps != null && maps.GetMinimapDisplayDetails(out FVector2D lookAt, out float viewWidth, out float rotation, out float tilt) && viewWidth > 0.01)
            MinimapZoom = viewWidth;
        A11 = N11 / MinimapZoom;
        A12 = N12 / MinimapZoom;
        A21 = N21 / MinimapZoom;
        A22 = N22 / MinimapZoom;
    }

    void LoadCalibrations()
    {
        if (ModSave.Load("calibrations", out SavedCalibrations saved) && saved.Regions != null) calibrations = saved;
        else
        {
            calibrations = new SavedCalibrations { Regions = new List<string>(), N11 = new List<double>(), N12 = new List<double>(), N21 = new List<double>(), N22 = new List<double>() };
            // The first versions' transform was the Overworld's at the usual zoom (20.1): kept if it's sensible (1 to 50 px/m).
            if (ModSave.Load("calibration", out SavedCalibration old) && old.Valid)
            {
                double scale = UKismetMathLibrary.sqrt(MapMath.Abs(old.A11 * old.A22 - old.A12 * old.A21)) * 100;
                if (scale > 1 && scale < 50) StoreCalibration("SW.Region.Overworld", old.A11 * 20.1042, old.A12 * 20.1042, old.A21 * 20.1042, old.A22 * 20.1042);
            }
        }
    }

    /// <summary>Takes the transform learned for a region (none yet: the minimap part waits until it's learned).</summary>
    void UseCalibrationFor(string region)
    {
        calibrationRegion = region;
        Calibrated = false;
        for (int i = 0; i < calibrations.Regions.Count; i++)
        {
            if (calibrations.Regions[i] != region) continue;
            Calibrated = true;
            N11 = calibrations.N11[i];
            N12 = calibrations.N12[i];
            N21 = calibrations.N21[i];
            N22 = calibrations.N22[i];
        }
        if (Debug) Log.Write($"minimap transform for {region}: {(Calibrated ? "loaded" : "not learned yet")}");
    }

    void StoreCalibration(string region, double n11, double n12, double n21, double n22)
    {
        for (int i = 0; i < calibrations.Regions.Count; i++)
        {
            if (calibrations.Regions[i] != region) continue;
            calibrations.N11[i] = n11;
            calibrations.N12[i] = n12;
            calibrations.N21[i] = n21;
            calibrations.N22[i] = n22;
            calibrationDirty = true;
            return;
        }
        calibrations.Regions.Add(region);
        calibrations.N11.Add(n11);
        calibrations.N12.Add(n12);
        calibrations.N21.Add(n21);
        calibrations.N22.Add(n22);
        calibrationDirty = true;
    }

    /// <summary>A transform learned by the minimap layer, normalised by the zoom (see <see cref="N11"/>).</summary>
    public void SetCalibration(double a11, double a12, double a21, double a22)
    {
        bool first = !Calibrated;
        Calibrated = true;
        N11 = a11;
        N12 = a12;
        N21 = a21;
        N22 = a22;
        if (calibrationRegion != "") StoreCalibration(calibrationRegion, a11, a12, a21, a22);
        UpdateMinimapScale();
        a11 = A11;
        a12 = A12;
        a21 = A21;
        a22 = A22;
        double now = World.RealTime(this);
        if (Debug && (first || now - lastCalibrationLog > 60))
        {
            lastCalibrationLog = now;
            double scale = UKismetMathLibrary.sqrt(MapMath.Abs(a11 * a22 - a12 * a21));
            double angle = UKismetMathLibrary.DegAtan2(a21, a11);
            Log.Write($"minimap transform{(first ? " learned" : "")} for {calibrationRegion} at zoom {MinimapZoom:0.00}: [{a11:0.0000} {a12:0.0000}; {a21:0.0000} {a22:0.0000}], {scale * 100:0.00} px/m, {angle:0} deg, {(a11 * a22 - a12 * a21 < 0 ? "mirrored" : "not mirrored")}");
            // What the game itself says about the minimap, to compare with the learned transform.
            var maps = USubsystemBlueprintLibrary.GetWorldSubsystem(this, Unreal.ClassOf<UClientMapsSubsystem>()) as UClientMapsSubsystem;
            if (maps != null && maps.GetMinimapDisplayDetails(out FVector2D lookAt, out float viewWidth, out float rotation, out float tilt))
                Log.Write($"minimap display: look-at uv ({lookAt.X:0.0000}, {lookAt.Y:0.0000}), view width {viewWidth:0.0000}, rotation {rotation:0.00}, tilt {tilt:0.00}");
        }
        if (first) SaveCalibration();
    }

    public void ForgetCalibration()
    {
        Calibrated = false;
        for (int i = calibrations.Regions.Count - 1; i >= 0; i--)
        {
            if (calibrations.Regions[i] != calibrationRegion) continue;
            calibrations.Regions.RemoveAt(i);
            calibrations.N11.RemoveAt(i);
            calibrations.N12.RemoveAt(i);
            calibrations.N21.RemoveAt(i);
            calibrations.N22.RemoveAt(i);
        }
        calibrationDirty = true;
    }

    void SaveCalibration()
    {
        if (!calibrationDirty) return;
        calibrationDirty = false;
        ModSave.Save("calibrations", calibrations);
    }

    // ---------------------------------------------------------------- Diagnostics

    /// <summary>Checks the level has a nav mesh: a route to a point 20 m in front of the player.</summary>
    void ProbeNavigation()
    {
        var player = World.Player(this);
        if (player == null) return;
        var at = player.K2_GetActorLocation();
        var forward = player.GetActorForwardVector();
        var ahead = MapMath.Vec(at.X + forward.X * 2000, at.Y + forward.Y * 2000, at.Z);
        bool hasNav = UNavigationSystemV1.GetNavigationSystem(this) != null;
        bool onNav = UNavigationSystemV1.K2_ProjectPointToNavigation(this, at, out FVector projected, null!, null!, MapMath.Vec(100, 100, 300));
        var found = UNavigationSystemV1.FindPathToLocationSynchronously(this, at, ahead, player, null!);
        string route = found == null ? "none" : $"valid {found.IsValid()}, partial {found.IsPartial()}, {found.PathPoints.Count} points, {found.GetPathLength() / 100:0.0} m";
        Log.Write($"nav probe: system {hasNav}, player on nav mesh {onNav}, route 20 m ahead: {route}");
    }

    static string Describe(FVector v) => $"({v.X:0}, {v.Y:0}, {v.Z:0})";
}
