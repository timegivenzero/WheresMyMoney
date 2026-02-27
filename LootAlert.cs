using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace LootAlert
{
    public class LootAlert : BaseSettingsPlugin<LootAlertSettings>
    {
        // ── State ─────────────────────────────────────────────────────────
        private readonly MapTally _tally = new MapTally();

        // Ground items we saw last frame: entityId -> GroundItem
        // Rebuilt from scratch every frame from ItemsOnGroundLabels.
        private Dictionary<uint, GroundItem> _lastFrameItems = new Dictionary<uint, GroundItem>();

        // Known currency entity IDs for pickup detection
        private Dictionary<uint, (string name, int stack)> _trackedCurrencyEntities = new Dictionary<uint, (string, int)>();

        private string _currentLeague = "";

        // ── Blocked items cache ───────────────────────────────────────────
        private HashSet<string> _blockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _lastBlockedItemsValue = "";

        private HashSet<string> GetBlockedItems()
        {
            var raw = Settings.BlockedItems.Value ?? "";
            if (raw == _lastBlockedItemsValue) return _blockedItems;
            _lastBlockedItemsValue = raw;
            _blockedItems = new HashSet<string>(
                raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            return _blockedItems;
        }

        // ── Inventory snapshot ────────────────────────────────────────────
        // We track the player's inventory each tick and diff against the previous
        // snapshot to reliably detect pickups regardless of ground label behaviour.
        private Dictionary<string, int> _lastInventorySnapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ── Inner types ───────────────────────────────────────────────────
        private class GroundItem
        {
            public uint EntityId;
            public string Name;      // Full display name (may include magic/rare affixes)
            public string BaseName;  // Clean base type name for poe.ninja lookup
            public string Path;
            public int ItemLevel;
            public ItemRarity Rarity;
            public int StackSize;
            public float ChaosValue;
            public bool IsCurrency;
            public float DistanceToPlayer;
        }

        // ── League list ───────────────────────────────────────────────────
        // Dates are approximate — Keepers ends ~03/05/26, Mirage starts ~03/06/26.
        // Entries with a future start date are hidden until that date; entries with
        // a past end date are hidden after it. All times compared against system date.
        private static readonly (string Name, DateTime? ShowFrom, DateTime? HideAfter)[] LeagueOptions =
        {
            ("Standard",          null,                          null),
            ("Hardcore",          null,                          null),
            ("Keepers",           null,                          new DateTime(2026, 3, 5)),
            ("Hardcore Keepers",  null,                          new DateTime(2026, 3, 5)),
            ("Mirage",            new DateTime(2026, 3, 6),      null),
            ("Hardcore Mirage",   new DateTime(2026, 3, 6),      null),
        };

        private static string[] GetVisibleLeagues()
        {
            var today = DateTime.Today;
            var visible = new System.Collections.Generic.List<string>();
            foreach (var (name, from, until) in LeagueOptions)
            {
                if (from.HasValue && today < from.Value) continue;
                if (until.HasValue && today > until.Value) continue;
                visible.Add(name);
            }
            return visible.ToArray();
        }

        // Draw a league dropdown at the top of the settings panel
        public override void DrawSettings()
        {
            var leagues = GetVisibleLeagues();
            var current = Settings.LeagueName.Value;

            int idx = System.Array.IndexOf(leagues, current);
            if (idx < 0) idx = 0;

            ImGui.Text("League");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("##league", ref idx, leagues, leagues.Length))
            {
                Settings.LeagueName.Value = leagues[idx];
                NinjaFetcher.ForceRefresh(leagues[idx]);
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Draw the rest of the auto-generated settings
            base.DrawSettings();

            // Override the chaos value sliders with 0.5-increment versions
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), "Chaos Value Thresholds");
            ImGui.Separator();

            DrawSteppedSlider("Minimum Currency Value##stepped", Settings.MinCurrencyValue, 0f, 100f, 0.5f);
            DrawSteppedSlider("Minimum Item Base Value##stepped",     Settings.MinBaseValue,     0f, 500f, 0.5f);
            DrawSteppedSlider("Left Behind Currency Minimum Value##stepped", Settings.LeftBehindMinValue, 0f, 100f, 0.5f);
            DrawSteppedSlider("Total Item Pickups Minimum Value##stepped",    Settings.TallyMinValue,    0f, 50f,  0.5f);
        }

        private static void DrawSteppedSlider(string label, ExileCore.Shared.Nodes.RangeNode<float> node, float min, float max, float step)
        {
            float val = node.Value;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat(label, ref val, min, max, $"{val:0.0}c"))
            {
                // Snap to nearest step increment
                val = (float)Math.Round(val / step) * step;
                val = Math.Max(min, Math.Min(max, val));
                node.Value = val;
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────
        public override bool Initialise()
        {
            // Validate the saved league name against the current visible list.
            // If it's not in the list (e.g. league ended), reset to the first available league.
            var visible = GetVisibleLeagues();
            if (visible.Length > 0 && System.Array.IndexOf(visible, Settings.LeagueName.Value) < 0)
                Settings.LeagueName.Value = visible[0];

            _currentLeague = Settings.LeagueName.Value;
            NinjaFetcher.TryRefresh(_currentLeague);
            return true;
        }

        public override Job Tick()
        {
            if (!Settings.Enable.Value) return null;

            // Refresh prices if league changed in settings
            if (Settings.LeagueName.Value != _currentLeague)
            {
                _currentLeague = Settings.LeagueName.Value;
                NinjaFetcher.ForceRefresh(_currentLeague);
            }
            else
            {
                NinjaFetcher.TryRefresh(_currentLeague);
            }

            // Check for area change (resets tally)
            try
            {
                var areaName = GameController.Area.CurrentArea?.DisplayName ?? "";
                if (_tally.CheckArea(areaName))
                    _lastInventorySnapshot.Clear(); // reset baseline on new map
            }
            catch { }

            // Check inventory for pickups (more reliable than ground label diffing)
            CheckInventoryForPickups();

            // Scan ground items
            ScanGroundItems();

            return null;
        }

        // ── Inventory scanning ────────────────────────────────────────────

        /// <summary>
        /// Reads the player's main inventory via ServerData — works without the UI panel open.
        /// </summary>
        private Dictionary<string, int> GetInventorySnapshot()
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var inventories = GameController.Game.IngameState.ServerData.PlayerInventories;
                if (inventories == null) return snapshot;

                var mainInv = inventories
                    .FirstOrDefault(x => x?.Inventory?.InventType == InventoryTypeE.MainInventory);
                if (mainInv?.Inventory?.Items == null) return snapshot;

                foreach (var item in mainInv.Inventory.Items)
                {
                    try
                    {
                        if (item == null || !item.IsValid) continue;

                        var baseComp = item.GetComponent<Base>();
                        var name = baseComp?.Name ?? "";
                        if (string.IsNullOrEmpty(name)) continue;

                        var stackComp = item.GetComponent<Stack>();
                        int stack = stackComp?.Size ?? 1;

                        if (snapshot.ContainsKey(name))
                            snapshot[name] += stack;
                        else
                            snapshot[name] = stack;
                    }
                    catch { }
                }
            }
            catch { }
            return snapshot;
        }

        /// <summary>
        /// Diffs current inventory against last snapshot.
        /// Any item whose count increased is recorded as a pickup.
        /// </summary>
        private void CheckInventoryForPickups()
        {
            var current = GetInventorySnapshot();
            var blocked = GetBlockedItems();

            foreach (var kvp in current)
            {
                if (blocked.Contains(kvp.Key)) continue;
                _lastInventorySnapshot.TryGetValue(kvp.Key, out int previous);
                int gained = kvp.Value - previous;
                float chaosVal = NinjaFetcher.GetChaosValue(kvp.Key);
                if (gained > 0 && chaosVal >= Settings.MinBaseValue.Value)
                {
                    bool cap = _tally.GroundSeen.ContainsKey(kvp.Key);
                    _tally.RecordInventoryPickup(kvp.Key, gained, cap);
                }
            }

            _lastInventorySnapshot = current;
        }

        // ── Ground scanning ───────────────────────────────────────────────
        public List<string> DebugLines { get; private set; } = new List<string>();

        private static readonly Regex StackPrefixRegex = new Regex(@"^(\d+)x\s+", RegexOptions.Compiled);

        private void ScanGroundItems()
        {
            var currentItems = new Dictionary<uint, GroundItem>();
            var debugLines   = new List<string>();

            try
            {
                var ingameUi = GameController.Game.IngameState.IngameUi;
                var labels   = ingameUi.ItemsOnGroundLabels;
                if (labels == null)
                {
                    debugLines.Add("ItemsOnGroundLabels is NULL");
                    DebugLines = debugLines;
                    // Always replace so stale entries never linger across frames
                    _lastFrameItems = currentItems;
                    return;
                }

                debugLines.Add($"Label count: {labels.Count}");

                Vector2 playerPos = Vector2.Zero;
                try
                {
                    var playerRender = GameController.Player?.GetComponent<Render>();
                    if (playerRender != null)
                        playerPos = new Vector2(playerRender.Pos.X, playerRender.Pos.Y);
                }
                catch { }

                foreach (var label in labels)
                {
                    try
                    {
                        var item = label?.ItemOnGround;
                        if (item == null || !item.IsValid)
                        {
                            debugLines.Add("  Skipped: item null or invalid");
                            continue;
                        }

                        // Skip non-item ground objects — chests, terrain, waypoints, area transitions.
                        var groundPath = item.Path ?? "";
                        if (groundPath.Contains("/Chests/")
                         || groundPath.Contains("/Terrain/")
                         || groundPath.Contains("/Waypoint")
                         || groundPath.Contains("/AreaTransition"))
                        {
                            debugLines.Add($"  Skipped (non-item): {groundPath}");
                            continue;
                        }

                        // Only process actual item drops that have a WorldItem component.
                        var worldItemComp = item.GetComponent<WorldItem>();
                        if (worldItemComp == null)
                        {
                            debugLines.Add($"  Skipped (no WorldItem): {groundPath}");
                            continue;
                        }

                        var itemEntity = worldItemComp.ItemEntity;
                        if (itemEntity == null || !itemEntity.IsValid)
                        {
                            debugLines.Add($"  Skipped (WorldItem entity null/invalid): {groundPath}");
                            continue;
                        }

                        var itemPath = itemEntity.Path ?? groundPath;

                        debugLines.Add($"  GroundPath: {groundPath} | ItemPath: {itemPath}");

                        // Name: label text is most reliable.
                        // Strip leading stack prefix e.g. "20x Orb of Fusing" -> "Orb of Fusing"
                        var labelText  = label.Label?.Text ?? "";
                        var stackMatch = StackPrefixRegex.Match(labelText);
                        var itemName   = stackMatch.Success
                            ? labelText.Substring(stackMatch.Length).Trim()
                            : labelText.Trim();

                        if (string.IsNullOrEmpty(itemName))
                        {
                            var parts = itemPath.Split('/');
                            itemName = parts.Length > 0 ? parts[parts.Length - 1] : itemPath;
                        }

                        debugLines.Add($"    Name: '{itemName}' | LabelText: '{labelText}'");

                        // Rarity + ItemLevel
                        var modsComp  = itemEntity.GetComponent<Mods>();
                        var rarity    = modsComp?.ItemRarity ?? ItemRarity.Normal;
                        int itemLevel = modsComp?.ItemLevel ?? 0;

                        // Stack size: prefer the Stack component; fall back to the label prefix
                        var stackComp = itemEntity?.GetComponent<Stack>();
                        int stackSize = stackComp?.Size ?? 1;
                        if (stackSize <= 1 && stackMatch.Success
                            && int.TryParse(stackMatch.Groups[1].Value, out int parsedStack))
                            stackSize = parsedStack;

                        // Currency detection uses the resolved item path
                        bool isCurrency = itemPath.Contains("/Currency/")
                                       || itemPath.Contains("/Stackable/")
                                       || itemPath.Contains("/DivinationCard")
                                       || itemPath.Contains("/Essence/")
                                       || itemPath.Contains("/Scarab")
                                       || itemPath.Contains("/Fossil")
                                       || itemPath.Contains("/Oils/")
                                       || itemPath.Contains("/DeliriumOrb")
                                       || itemPath.Contains("/Resonator")
                                       || itemPath.Contains("/Invitation")
                                       || itemPath.Contains("/MapFragment")
                                       || itemPath.Contains("/AtlasUpgrades")
                                       || itemPath.Contains("/Maps/");

                        debugLines.Add($"    IsCurrency: {isCurrency} | Stack: {stackSize} | Rarity: {rarity} | iLvl: {itemLevel} | ModsFound: {modsComp != null}");

                        // For non-currency items, use the Base component name (strips magic/rare affixes)
                        // so poe.ninja lookup finds "Accumulator Wand" not "Accumulator Wand of the Hyperboreal"
                        string baseName = itemName;
                        if (!isCurrency)
                        {
                            try
                            {
                                var baseComp = itemEntity.GetComponent<Base>();
                                if (!string.IsNullOrEmpty(baseComp?.Name))
                                    baseName = baseComp.Name;
                            }
                            catch { }
                        }

                        float chaosValue = NinjaFetcher.GetChaosValue(baseName);
                        debugLines.Add($"    BaseName: '{baseName}' | ChaosValue: {chaosValue} | NinjaLoaded: {NinjaFetcher.IsLoaded}");

                        // Distance to player (3D world coords)
                        float distance = 0f;
                        try
                        {
                            var renderComp = item.GetComponent<Render>();
                            if (renderComp != null && playerPos != Vector2.Zero)
                            {
                                var itemPos = new Vector2(renderComp.Pos.X, renderComp.Pos.Y);
                                distance = Vector2.Distance(playerPos, itemPos);
                            }
                        }
                        catch { }

                        debugLines.Add($"    Dist: {distance:0.#}");

                        var gi = new GroundItem
                        {
                            EntityId         = item.Id,
                            Name             = itemName,
                            BaseName         = baseName,
                            Path             = itemPath,
                            ItemLevel        = itemLevel,
                            Rarity           = rarity,
                            StackSize        = stackSize,
                            ChaosValue       = chaosValue,
                            IsCurrency       = isCurrency,
                            DistanceToPlayer = distance
                        };

                        currentItems[item.Id] = gi;

                        if (isCurrency)
                            _trackedCurrencyEntities[item.Id] = (itemName, stackSize);
                    }
                    catch (Exception ex)
                    {
                        debugLines.Add($"  ERROR: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                debugLines.Add($"OUTER ERROR: {ex.Message}");
            }

            DebugLines = debugLines;

            // Feed THIS frame's visible items (currency + valuable bases) into ground-seen.
            // Use BaseName as the tally key so magic/rare affixes don't fragment counts.
            var blocked = GetBlockedItems();
            var minBaseValue = Settings.MinBaseValue.Value;
            _tally.UpdateGroundSeen(
                currentItems.Values
                    .Where(i => !blocked.Contains(i.BaseName))
                    .Where(i => i.IsCurrency || NinjaFetcher.GetChaosValue(i.BaseName) >= minBaseValue)
                    .Select(i => (i.BaseName, i.StackSize))
            );

            // Always replace — never keep a stale dict across frames
            _lastFrameItems = currentItems;
        }

        // ── Rendering ─────────────────────────────────────────────────────
        public override void Render()
        {
            if (!Settings.Enable.Value) return;
            if (!GameController.Game.IngameState.InGame) return;

            // Don't show overlay if inventory/stash is open
            try
            {
                var ui = GameController.Game.IngameState.IngameUi;
                if (ui.InventoryPanel?.IsVisible == true) return;
                if (ui.StashElement?.IsVisible == true) return;
            }
            catch { }

            DrawOverlay();
        }

        private void DrawOverlay()
        {
            var windowPos  = new Vector2(Settings.OverlayX.Value, Settings.OverlayY.Value);
            var windowSize = new Vector2(Settings.OverlayWidth.Value, 0); // 0 = auto height

            ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.75f);

            var flags = ImGuiWindowFlags.NoTitleBar
                      | ImGuiWindowFlags.NoResize
                      | ImGuiWindowFlags.NoScrollbar
                      | ImGuiWindowFlags.NoInputs
                      | ImGuiWindowFlags.NoCollapse
                      | ImGuiWindowFlags.NoNav
                      | ImGuiWindowFlags.NoFocusOnAppearing;

            if (!ImGui.Begin("LootAlert##overlay", flags))
            {
                ImGui.End();
                return;
            }

            bool anythingDrawn = false;

            // ── Nearby Items (currency + valuable bases merged, sorted by total value) ─
            if (Settings.ShowCurrencyOverlay.Value || Settings.ShowBasesOverlay.Value)
            {
                var nearbyRows = new System.Collections.Generic.List<(string Label, float TotalValue, Vector4 Color)>();

                if (Settings.ShowCurrencyOverlay.Value)
                {
                    var grouped = GetFilteredCurrencyItems()
                        .GroupBy(i => i.Name)
                        .Select(g => new { Name = g.Key, TotalStack = g.Sum(i => i.StackSize), ChaosEach = g.First().ChaosValue });

                    foreach (var g in grouped)
                    {
                        float  total    = g.ChaosEach * g.TotalStack;
                        string stackStr = g.TotalStack > 1 ? $" x{g.TotalStack}" : "";
                        string valueStr = g.ChaosEach >= 0.01f ? $"  ~{total:0.#}c" : "";
                        nearbyRows.Add(($"  {g.Name}{stackStr}{valueStr}", total, GetValueColor(g.ChaosEach)));
                    }
                }

                if (Settings.ShowBasesOverlay.Value)
                {
                    foreach (var b in GetFilteredBaseItems())
                    {
                        string ilvlStr  = b.ItemLevel > 0 ? $" [ilvl {b.ItemLevel}]" : "";
                        string valueStr = b.ChaosValue >= 0.01f ? $"  ~{b.ChaosValue:0.#}c" : "";
                        nearbyRows.Add(($"  {b.Name}{ilvlStr}{valueStr}", b.ChaosValue, GetRarityColor(b.Rarity)));
                    }
                }

                if (nearbyRows.Count > 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), "Nearby Items");
                    ImGui.Separator();
                    foreach (var row in nearbyRows.OrderByDescending(r => r.TotalValue))
                        ImGui.TextColored(row.Color, row.Label);
                    anythingDrawn = true;
                }
            }

            // ── Left behind ───────────────────────────────────────────────
            // Items seen on the ground this map that you walked away from without picking up.
            // "Visible right now" items are excluded — those show in Currency Nearby already.
            if (Settings.ShowLeftBehind.Value)
            {
                var visibleNow = new HashSet<string>(
                    _lastFrameItems.Values.Select(i => i.BaseName),
                    StringComparer.OrdinalIgnoreCase);

                var leftBehind = _tally.GroundSeen
                    .Select(kvp => new
                    {
                        Name       = kvp.Key,
                        SeenCount  = kvp.Value,
                        PickedUp   = _tally.Counts.TryGetValue(kvp.Key, out int p) ? p : 0,
                        ValueEach  = NinjaFetcher.GetChaosValue(kvp.Key)
                    })
                    .Select(r => new
                    {
                        r.Name,
                        r.ValueEach,
                        Remaining  = r.SeenCount - r.PickedUp
                    })
                    .Where(r => r.Remaining > 0)                                          // not fully picked up
                    .Where(r => !visibleNow.Contains(r.Name))                             // not currently nearby
                    .Where(r => r.ValueEach * r.Remaining >= Settings.LeftBehindMinValue.Value)
                    .OrderByDescending(r => r.ValueEach * r.Remaining)
                    .ToList();

                if (leftBehind.Count > 0)
                {
                    if (anythingDrawn) ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Left Behind");
                    ImGui.Separator();

                    foreach (var item in leftBehind)
                    {
                        float  total    = item.ValueEach * item.Remaining;
                        string countStr = item.Remaining > 1 ? $" x{item.Remaining}" : "";
                        string valStr   = item.ValueEach >= 0.01f ? $"  ~{total:0.#}c" : "";
                        ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"  {item.Name}{countStr}{valStr}");
                    }

                    anythingDrawn = true;
                }
            }

            // ── Map tally ─────────────────────────────────────────────────
            // Both Counts (picked up) and GroundSeen (seen on ground this map) persist
            // for the whole map regardless of player distance. The tally never disappears.
            if (Settings.ShowMapTally.Value && (_tally.Counts.Count > 0 || _tally.GroundSeen.Count > 0))
            {
                if (anythingDrawn) ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), $"{_tally.CurrentArea}");
                ImGui.Separator();

                // Merge picked-up and ground-seen into one combined view
                var combined = new Dictionary<string, (int picked, int onGround)>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in _tally.Counts)
                    combined[kvp.Key] = (kvp.Value, 0);

                foreach (var kvp in _tally.GroundSeen)
                {
                    combined.TryGetValue(kvp.Key, out var existing);
                    // Only show as "on ground" what hasn't been picked up yet
                    int stillOnGround = Math.Max(0, kvp.Value - existing.picked);
                    combined[kvp.Key] = (existing.picked, stillOnGround);
                }

                var tallyRows = combined
                    .Select(kvp => new
                    {
                        Name      = kvp.Key,
                        Picked    = kvp.Value.picked,
                        OnGround  = kvp.Value.onGround,
                        ValueEach = NinjaFetcher.GetChaosValue(kvp.Key)
                    })
                    .Where(r => r.ValueEach * r.Picked >= Settings.TallyMinValue.Value
                             || r.ValueEach * r.OnGround >= Settings.TallyMinValue.Value)
                    .OrderByDescending(r => r.ValueEach * r.Picked)
                    .ThenByDescending(r => r.ValueEach * r.OnGround)
                    .ToList();

                float mapTotal = 0f;
                foreach (var row in tallyRows)
                {
                    float pickedTotal   = row.ValueEach * row.Picked;
                    float onGroundTotal = row.ValueEach * row.OnGround;
                    mapTotal += pickedTotal;

                    if (row.Picked > 0 && row.OnGround > 0)
                    {
                        string pickedStr = row.Picked > 1 ? $" x{row.Picked}" : "";
                        string valStr    = row.ValueEach >= 0.01f ? $"  = {pickedTotal:0.#}c" : "";
                        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), $"  {row.Name}{pickedStr}{valStr}");
                        string groundStr = row.OnGround > 1 ? $" x{row.OnGround}" : "";
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"    +{row.Name}{groundStr}  ~{onGroundTotal:0.#}c  [on ground]");
                    }
                    else if (row.OnGround > 0)
                    {
                        string countStr = row.OnGround > 1 ? $" x{row.OnGround}" : "";
                        string valStr   = row.ValueEach >= 0.01f ? $"  ~{onGroundTotal:0.#}c" : "";
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"  {row.Name}{countStr}{valStr}  [on ground]");
                    }
                    else
                    {
                        string countStr = row.Picked > 1 ? $" x{row.Picked}" : "";
                        string valStr   = row.ValueEach >= 0.01f ? $"  = {pickedTotal:0.#}c" : "";
                        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), $"  {row.Name}{countStr}{valStr}");
                    }
                }

                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"  Total  ~{mapTotal:0.#}c");
            }

            // Status / ninja price status
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), NinjaFetcher.StatusMessage);

            // Debug section
            if (Settings.ShowDebug.Value)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"[DEBUG] Items in dict: {_lastFrameItems.Count}");
                foreach (var line in DebugLines.Take(20))
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), line);

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"[DEBUG] GroundSeen: {_tally.GroundSeen.Count} | Counts: {_tally.Counts.Count}");
                foreach (var kvp in _tally.GroundSeen)
                {
                    int picked = _tally.Counts.TryGetValue(kvp.Key, out int p) ? p : 0;
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  {kvp.Key}: seen={kvp.Value} picked={picked} remaining={kvp.Value - picked}");
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"[DEBUG] Inventory snapshot: {_lastInventorySnapshot.Count} item types");
                foreach (var kvp in _lastInventorySnapshot.OrderByDescending(k => k.Value).Take(15))
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  {kvp.Key}: x{kvp.Value}");
            }

            ImGui.End();
        }

        // ── Filtering helpers ─────────────────────────────────────────────

        private List<GroundItem> GetFilteredCurrencyItems()
        {
            var blocked = GetBlockedItems();
            return _lastFrameItems.Values
                .Where(i => i.IsCurrency)
                .Where(i => !blocked.Contains(i.BaseName))
                .Where(i => Settings.CurrencyMaxDistance.Value == 0 || i.DistanceToPlayer <= Settings.CurrencyMaxDistance.Value)
                .GroupBy(i => i.BaseName)
                .Where(g => !NinjaFetcher.IsLoaded || g.First().ChaosValue * g.Sum(i => i.StackSize) >= Settings.MinCurrencyValue.Value)
                .SelectMany(g => g)
                .ToList();
        }

        private List<GroundItem> GetFilteredBaseItems()
        {
            return _lastFrameItems.Values
                .Where(i => !i.IsCurrency)
                .Where(i => i.ItemLevel == 0 || i.ItemLevel >= Settings.MinItemLevel.Value)
                .Where(i => !NinjaFetcher.IsLoaded || i.ChaosValue >= Settings.MinBaseValue.Value)
                .Where(i => i.Rarity >= ItemRarity.Magic || i.ChaosValue >= Settings.MinBaseValue.Value)
                .Where(i => Settings.BasesMaxDistance.Value == 0 || i.DistanceToPlayer <= Settings.BasesMaxDistance.Value)
                .ToList();
        }

        // ── Color helpers ─────────────────────────────────────────────────

        private static Vector4 GetValueColor(float chaosValue)
        {
            if (chaosValue >= 100f) return new Vector4(1f, 0.2f, 0.2f, 1f);   // red
            if (chaosValue >= 20f)  return new Vector4(1f, 0.65f, 0f, 1f);    // orange
            if (chaosValue >= 5f)   return new Vector4(1f, 1f, 0.3f, 1f);     // yellow
            if (chaosValue >= 1f)   return new Vector4(0.9f, 0.9f, 0.9f, 1f); // white
            return new Vector4(0.5f, 0.5f, 0.5f, 1f);                          // grey
        }

        private static Vector4 GetRarityColor(ItemRarity rarity)
        {
            return rarity switch
            {
                ItemRarity.Unique => new Vector4(0.99f, 0.5f, 0.1f, 1f),
                ItemRarity.Rare   => new Vector4(1f, 1f, 0.2f, 1f),
                ItemRarity.Magic  => new Vector4(0.5f, 0.5f, 1f, 1f),
                _                 => new Vector4(0.9f, 0.9f, 0.9f, 1f)
            };
        }
    }
}