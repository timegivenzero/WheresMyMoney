using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace WheresMyMoney
{
    public class WheresMyMoney : BaseSettingsPlugin<WheresMyMoneySettings>
    {
        // ── State ─────────────────────────────────────────────────────────
        private readonly MapTally _tally = new MapTally();

        // Ground items persist across frames — updated in-place, never replaced wholesale.
        // This prevents a bad Mods-read frame from destroying previously good data.
        private Dictionary<uint, GroundItem> _lastFrameItems = new Dictionary<uint, GroundItem>();

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
        private Dictionary<string, int> _lastInventorySnapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ── Inner types ───────────────────────────────────────────────────
        private class GroundItem
        {
            public uint       EntityId;
            public string     Name;        // Unique name or display name from label
            public string     BaseName;    // Base type name for non-unique poe.ninja lookup
            public string     Path;
            public int        ItemLevel;
            public ItemRarity Rarity;
            public int        StackSize;
            public string     Influence;
            public bool       IsCurrency;
            public float      DistanceToPlayer;
            public bool       ModsResolved; // true once we've had a valid Mods read

            // ChaosValue is NOT stored here — it is always computed live from NinjaFetcher
            // so that the moment prices load after a cold start, the correct value is used
            // and items are not incorrectly hidden by the value threshold filter.
        }

        // ── Live chaos value lookup ───────────────────────────────────────
        // Always call this instead of a cached field so the value reflects the
        // current NinjaFetcher state regardless of when the item was first scanned.
        private float GetLiveChaosValue(GroundItem i)
        {
            if (i.Rarity == ItemRarity.Unique)
            {
                float v = NinjaFetcher.GetChaosValue(i.Name);
                if (v > 0f) return v;
                return NinjaFetcher.GetBaseValue(i.BaseName, i.ItemLevel, i.Influence);
            }
            if (i.IsCurrency)
                return NinjaFetcher.GetChaosValue(i.BaseName);
            return NinjaFetcher.GetBaseValue(i.BaseName, i.ItemLevel, i.Influence);
        }

        // ── League list ───────────────────────────────────────────────────
        private static readonly (string Name, DateTime? ShowFrom, DateTime? HideAfter)[] LeagueOptions =
        {
            ("Standard",         null,                     null),
            ("Hardcore",         null,                     null),
            ("Keepers",          null,                     new DateTime(2026, 3, 5)),
            ("Hardcore Keepers", null,                     new DateTime(2026, 3, 5)),
            ("Mirage",           new DateTime(2026, 3, 6), null),
            ("Hardcore Mirage",  new DateTime(2026, 3, 6), null),
        };

        private static string[] GetVisibleLeagues()
        {
            var today = DateTime.Today;
            var visible = new List<string>();
            foreach (var (name, from, until) in LeagueOptions)
            {
                if (from.HasValue  && today < from.Value)  continue;
                if (until.HasValue && today > until.Value) continue;
                visible.Add(name);
            }
            return visible.ToArray();
        }

        // ── Settings UI ───────────────────────────────────────────────────
        public override void DrawSettings()
        {
            // ── League ───────────────────────────────────────────────────
            SectionHeader("League");
            var leagues = GetVisibleLeagues();
            var current = Settings.LeagueName.Value;
            int idx = Array.IndexOf(leagues, current);
            if (idx < 0) idx = 0;
            ImGui.SetNextItemWidth(SliderWidth);
            if (ImGui.Combo("##league", ref idx, leagues, leagues.Length))
            {
                Settings.LeagueName.Value = leagues[idx];
                NinjaFetcher.ForceRefresh(leagues[idx]);
            }

            // ── Currency on Ground ───────────────────────────────────────
            SectionHeader("Currency on Ground");
            var showCurrency = Settings.ShowCurrencyOverlay.Value;
            if (ImGui.Checkbox("Show Currency on Ground##chk", ref showCurrency))
                Settings.ShowCurrencyOverlay.Value = showCurrency;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show currency items currently on the ground");
            DrawSteppedSlider("Minimum Value##currency",    Settings.MinCurrencyValue,  0f, 100f, 0.5f);
            DrawIntSlider    ("Max Distance##currency",     Settings.CurrencyMaxDistance, "0 = unlimited");

            // ── Valuable Bases ───────────────────────────────────────────
            SectionHeader("Valuable Bases");
            var showBases = Settings.ShowBasesOverlay.Value;
            if (ImGui.Checkbox("Show Valuable Bases##chk", ref showBases))
                Settings.ShowBasesOverlay.Value = showBases;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show rare/magic item bases with good ilvl and chaos value");
            DrawSteppedSlider("Minimum Value##bases",       Settings.MinBaseValue,       0f, 500f, 0.5f);
            DrawIntSlider    ("Min Item Level##bases",      Settings.MinItemLevel,       "Minimum ilvl to consider a base valuable");
            DrawIntSlider    ("Max Distance##bases",        Settings.BasesMaxDistance,   "0 = unlimited");

            // ── Map Tally ────────────────────────────────────────────────
            SectionHeader("Map Tally");
            var showTally = Settings.ShowMapTally.Value;
            if (ImGui.Checkbox("Show Map Tally##chk", ref showTally))
                Settings.ShowMapTally.Value = showTally;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show per-map currency pickup tally");
            DrawSteppedSlider("Minimum Value##tally",       Settings.TallyMinValue,      0f, 50f,  0.5f);

            // ── Left Behind ──────────────────────────────────────────────
            SectionHeader("Left Behind");
            var showLeft = Settings.ShowLeftBehind.Value;
            if (ImGui.Checkbox("Show Left Behind##chk", ref showLeft))
                Settings.ShowLeftBehind.Value = showLeft;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remind you of currency you walked away from this map");
            DrawSteppedSlider("Minimum Value##leftbehind",  Settings.LeftBehindMinValue, 0f, 100f, 0.5f);

            // ── Blocked Items ────────────────────────────────────────────
            SectionHeader("Blocked Items");
            ImGui.TextDisabled("Comma-separated names to exclude from tally and left behind");
            var blockedStr = Settings.BlockedItems.Value ?? "";
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##blockeditems", ref blockedStr, 512))
                Settings.BlockedItems.Value = blockedStr;

            // ── Overlay Position ─────────────────────────────────────────
            SectionHeader("Overlay Position");
            DrawIntSlider("X Position##pos",   Settings.OverlayX,     "");
            DrawIntSlider("Y Position##pos",   Settings.OverlayY,     "");
            DrawIntSlider("Width##pos",        Settings.OverlayWidth, "");

            // ── Visibility ───────────────────────────────────────────────
            SectionHeader("Visibility");
            var showTown    = Settings.ShowInTown.Value;
            var showHideout = Settings.ShowInHideout.Value;
            if (ImGui.Checkbox("Show in Town##chk",    ref showTown))    Settings.ShowInTown.Value    = showTown;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the overlay while in town areas");
            if (ImGui.Checkbox("Show in Hideout##chk", ref showHideout)) Settings.ShowInHideout.Value = showHideout;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the overlay while in your hideout");

            // ── Debug ────────────────────────────────────────────────────
            SectionHeader("Debug");
            var showDebug = Settings.ShowDebug.Value;
            if (ImGui.Checkbox("Debug Mode##chk", ref showDebug))
                Settings.ShowDebug.Value = showDebug;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show raw scan data in the overlay to diagnose issues");
        }

        private static void SectionHeader(string title)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.1f, 1f), title);
            ImGui.Separator();
        }

        private static void DrawIntSlider(string label, ExileCore.Shared.Nodes.RangeNode<int> node, string tooltip)
        {
            int val = node.Value;
            ImGui.SetNextItemWidth(SliderWidth);
            if (ImGui.SliderInt(label, ref val, node.Min, node.Max))
                node.Value = val;
            if (!string.IsNullOrEmpty(tooltip))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
            }
        }

        private const float SliderWidth = 300f;

        private static void DrawSteppedSlider(string label, ExileCore.Shared.Nodes.RangeNode<float> node, float min, float max, float step)
        {
            float val = node.Value;
            ImGui.SetNextItemWidth(SliderWidth);
            if (ImGui.SliderFloat(label, ref val, min, max, $"{val:0.0}c"))
            {
                val = (float)Math.Round(val / step) * step;
                val = Math.Max(min, Math.Min(max, val));
                node.Value = val;
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────
        public override bool Initialise()
        {
            var visible = GetVisibleLeagues();
            if (visible.Length > 0 && Array.IndexOf(visible, Settings.LeagueName.Value) < 0)
                Settings.LeagueName.Value = visible[0];

            _currentLeague = Settings.LeagueName.Value;
            NinjaFetcher.TryRefresh(_currentLeague);
            return true;
        }

        public override Job Tick()
        {
            if (!Settings.Enable.Value) return null;

            if (Settings.LeagueName.Value != _currentLeague)
            {
                _currentLeague = Settings.LeagueName.Value;
                NinjaFetcher.ForceRefresh(_currentLeague);
            }
            else
            {
                NinjaFetcher.TryRefresh(_currentLeague);
            }

            try
            {
                var areaName = GameController.Area.CurrentArea?.DisplayName ?? "";
                if (_tally.CheckArea(areaName))
                {
                    _lastInventorySnapshot.Clear();
                    _lastFrameItems.Clear();
                }
            }
            catch { }

            CheckInventoryForPickups();
            ScanGroundItems();

            return null;
        }

        // ── Inventory scanning ────────────────────────────────────────────
        // For uniques the tally key must be the unique name ("Headhunter") not the
        // base type ("Leather Belt"), otherwise poe.ninja returns 0 and the pickup
        // is ignored. We resolve the unique name by matching the item's base type
        // against ground items already identified in _lastFrameItems.

        private Dictionary<string, int> GetInventorySnapshot()
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var inventories = GameController.Game.IngameState.ServerData.PlayerInventories;
                if (inventories == null) return snapshot;

                var mainInv = inventories.FirstOrDefault(x => x?.Inventory?.InventType == InventoryTypeE.MainInventory);
                if (mainInv?.Inventory?.Items == null) return snapshot;

                foreach (var item in mainInv.Inventory.Items)
                {
                    try
                    {
                        if (item == null || !item.IsValid) continue;

                        var baseName = item.GetComponent<Base>()?.Name ?? "";
                        if (string.IsNullOrEmpty(baseName)) continue;

                        int stack = item.GetComponent<Stack>()?.Size ?? 1;

                        // Default tally key is the base name; override for uniques.
                        string tallyKey = baseName;
                        try
                        {
                            var modsComp = item.GetComponent<Mods>();
                            if (modsComp?.ItemRarity == ItemRarity.Unique)
                            {
                                // Look up the unique name from ground items we already resolved.
                                var resolved = _lastFrameItems.Values.FirstOrDefault(
                                    gi => gi.Rarity == ItemRarity.Unique
                                       && gi.BaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                                       && !string.IsNullOrEmpty(gi.Name));
                                if (resolved != null)
                                    tallyKey = resolved.Name;
                            }
                        }
                        catch { }

                        if (snapshot.ContainsKey(tallyKey)) snapshot[tallyKey] += stack;
                        else snapshot[tallyKey] = stack;
                    }
                    catch { }
                }
            }
            catch { }
            return snapshot;
        }

        private void CheckInventoryForPickups()
        {
            var current = GetInventorySnapshot();
            var blocked = GetBlockedItems();

            foreach (var kvp in current)
            {
                if (blocked.Contains(kvp.Key)) continue;
                _lastInventorySnapshot.TryGetValue(kvp.Key, out int previous);
                int gained = kvp.Value - previous;
                if (gained <= 0) continue;

                // Try unique/currency name lookup first, fall back to base-type lookup.
                float chaosVal = NinjaFetcher.GetChaosValue(kvp.Key);
                if (chaosVal <= 0f) chaosVal = NinjaFetcher.GetBaseValue(kvp.Key, 0, "");

                if (chaosVal >= Settings.MinBaseValue.Value)
                    _tally.RecordInventoryPickup(kvp.Key, gained, _tally.GroundSeen.ContainsKey(kvp.Key));
            }

            _lastInventorySnapshot = current;
        }

        // ── Ground scanning ───────────────────────────────────────────────
        public List<string> DebugLines { get; private set; } = new List<string>();

        private static readonly Regex StackPrefixRegex = new Regex(@"^(\d+)x\s+", RegexOptions.Compiled);

        private void ScanGroundItems()
        {
            var seenThisFrame = new HashSet<uint>();
            var debugLines    = new List<string>();

            try
            {
                var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabels;
                if (labels == null)
                {
                    debugLines.Add("ItemsOnGroundLabels is NULL");
                    DebugLines = debugLines;
                    _lastFrameItems.Clear();
                    return;
                }

                debugLines.Add($"Label count: {labels.Count}");

                Vector2 playerPos = Vector2.Zero;
                try
                {
                    var pr = GameController.Player?.GetComponent<Render>();
                    if (pr != null) playerPos = new Vector2(pr.Pos.X, pr.Pos.Y);
                }
                catch { }

                foreach (var label in labels)
                {
                    try
                    {
                        var item = label?.ItemOnGround;
                        if (item == null || !item.IsValid) { debugLines.Add("  Skipped: null/invalid"); continue; }

                        var groundPath = item.Path ?? "";
                        if (groundPath.Contains("/Chests/") || groundPath.Contains("/Terrain/")
                         || groundPath.Contains("/Waypoint") || groundPath.Contains("/AreaTransition"))
                        {
                            debugLines.Add($"  Skipped (non-item): {groundPath}");
                            continue;
                        }

                        var worldItemComp = item.GetComponent<WorldItem>();
                        if (worldItemComp == null) { debugLines.Add($"  Skipped (no WorldItem): {groundPath}"); continue; }

                        var itemEntity = worldItemComp.ItemEntity;
                        if (itemEntity == null || !itemEntity.IsValid) { debugLines.Add($"  Skipped (entity invalid): {groundPath}"); continue; }

                        uint entityId = item.Id;
                        seenThisFrame.Add(entityId);

                        var itemPath  = itemEntity.Path ?? groundPath;
                        var labelText = label.Label?.Text ?? "";

                        // Unique item labels are multiline: first line is the unique name,
                        // second line is the base type (e.g. "Headhunter\nLeather Belt").
                        // Always take only the first line so we look up "Headhunter" not
                        // "Headhunter\nLeather Belt" which returns 0 from poe.ninja.
                        var firstLine  = labelText.Split('\n')[0].Trim();
                        var stackMatch = StackPrefixRegex.Match(firstLine);
                        var itemName   = stackMatch.Success
                            ? firstLine.Substring(stackMatch.Length).Trim()
                            : firstLine;

                        if (string.IsNullOrEmpty(itemName))
                        {
                            var parts = itemPath.Split('/');
                            itemName = parts.Length > 0 ? parts[parts.Length - 1] : itemPath;
                        }

                        debugLines.Add($"  GroundPath: {groundPath} | ItemPath: {itemPath}");
                        debugLines.Add($"    Name: '{itemName}' | LabelText: '{labelText}'");

                        // ── Mods — carry forward on bad frames ────────────
                        // The Mods component can return Normal/0 for several frames after
                        // an item appears. We keep the last confirmed-good read so the item
                        // does not flicker or get incorrectly filtered while Mods resolves.
                        _lastFrameItems.TryGetValue(entityId, out var existing);

                        var modsComp = itemEntity.GetComponent<Mods>();
                        var rawRarity = modsComp?.ItemRarity ?? ItemRarity.Normal;
                        int rawIlvl   = modsComp?.ItemLevel  ?? 0;
                        bool modsGoodThisFrame = modsComp != null && (rawRarity != ItemRarity.Normal || rawIlvl > 0);

                        ItemRarity rarity;
                        int        itemLevel;
                        string     influence;
                        bool       modsResolved;

                        if (existing != null && existing.ModsResolved && !modsGoodThisFrame)
                        {
                            // Keep previously confirmed values — Mods is unreliable this frame
                            rarity       = existing.Rarity;
                            itemLevel    = existing.ItemLevel;
                            influence    = existing.Influence;
                            modsResolved = true;
                        }
                        else
                        {
                            rarity    = rawRarity;
                            itemLevel = rawIlvl;
                            influence = "";
                            try
                            {
                                if (modsComp != null)
                                {
                                    var allMods = modsComp.ItemMods?.Select(m => m.Name ?? "").ToList()
                                                 ?? new List<string>();
                                    bool HasMod(string p) =>
                                        allMods.Any(m => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                                    if      (HasMod("ShaperItem"))   influence = "Shaper";
                                    else if (HasMod("ElderItem"))    influence = "Elder";
                                    else if (HasMod("CrusaderItem")) influence = "Crusader";
                                    else if (HasMod("HunterItem"))   influence = "Hunter";
                                    else if (HasMod("RedeemerItem")) influence = "Redeemer";
                                    else if (HasMod("WarlordItem"))  influence = "Warlord";
                                }
                            }
                            catch { }
                            modsResolved = modsGoodThisFrame;
                        }

                        // ── Stack size ────────────────────────────────────
                        int stackSize = itemEntity.GetComponent<Stack>()?.Size ?? 1;
                        if (stackSize <= 1 && stackMatch.Success
                            && int.TryParse(stackMatch.Groups[1].Value, out int ps))
                            stackSize = ps;

                        // ── Currency detection ────────────────────────────
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

                        debugLines.Add($"    IsCurrency: {isCurrency} | Stack: {stackSize} | Rarity: {rarity} | iLvl: {itemLevel} | ModsResolved: {modsResolved}");

                        // ── Base name ─────────────────────────────────────
                        string baseName = itemName;
                        if (!isCurrency)
                        {
                            try
                            {
                                var bc = itemEntity.GetComponent<Base>();
                                if (!string.IsNullOrEmpty(bc?.Name)) baseName = bc.Name;
                            }
                            catch { }
                        }

                        // ── Distance ──────────────────────────────────────
                        float distance = 0f;
                        try
                        {
                            var rc = item.GetComponent<Render>();
                            if (rc != null && playerPos != Vector2.Zero)
                                distance = Vector2.Distance(playerPos, new Vector2(rc.Pos.X, rc.Pos.Y));
                        }
                        catch { }

                        debugLines.Add($"    BaseName: '{baseName}' | UniqueName: '{(rarity == ItemRarity.Unique ? itemName : "n/a")}' | Influence: '{influence}' | Dist: {distance:0.#}");

                        _lastFrameItems[entityId] = new GroundItem
                        {
                            EntityId         = entityId,
                            Name             = itemName,
                            BaseName         = baseName,
                            Path             = itemPath,
                            ItemLevel        = itemLevel,
                            Rarity           = rarity,
                            StackSize        = stackSize,
                            Influence        = influence,
                            IsCurrency       = isCurrency,
                            DistanceToPlayer = distance,
                            ModsResolved     = modsResolved,
                        };

                        if (isCurrency)
                            _trackedCurrencyEntities[entityId] = (itemName, stackSize);
                    }
                    catch (Exception ex) { debugLines.Add($"  ERROR: {ex.Message}"); }
                }
            }
            catch (Exception ex) { debugLines.Add($"OUTER ERROR: {ex.Message}"); }

            // Evict items no longer in the label list (picked up or out of range)
            foreach (var id in _lastFrameItems.Keys.Where(id => !seenThisFrame.Contains(id)).ToList())
                _lastFrameItems.Remove(id);

            DebugLines = debugLines;

            // Update ground-seen tally using live chaos values
            var blocked      = GetBlockedItems();
            var minBaseValue = Settings.MinBaseValue.Value;
            _tally.UpdateGroundSeen(
                _lastFrameItems.Values
                    .Where(i => !blocked.Contains(i.BaseName))
                    .Where(i => i.IsCurrency || (
                        (i.Rarity == ItemRarity.Unique || i.ItemLevel == 0 || i.ItemLevel >= Settings.MinItemLevel.Value)
                        && GetLiveChaosValue(i) >= minBaseValue))
                    // Use the unique name as the tally key for uniques so GroundSeen stores
                    // "Headhunter" not "Leather Belt" — matching what the pickup tally records.
                    .Select(i => (i.Rarity == ItemRarity.Unique ? i.Name : i.BaseName, i.StackSize))
            );
        }

        // ── Rendering ─────────────────────────────────────────────────────
        public override void Render()
        {
            if (!Settings.Enable.Value) return;
            if (!GameController.Game.IngameState.InGame) return;
            try
            {
                var area = GameController.Area.CurrentArea;
                if (area != null)
                {
                    if (area.IsTown    && !Settings.ShowInTown.Value)    return;
                    if (area.IsHideout && !Settings.ShowInHideout.Value) return;
                }
            }
            catch { }
            try
            {
                var ui = GameController.Game.IngameState.IngameUi;
                if (ui.InventoryPanel?.IsVisible == true) return;
                if (ui.StashElement?.IsVisible    == true) return;
            }
            catch { }
            DrawOverlay();
        }

        private void DrawOverlay()
        {
            ImGui.SetNextWindowPos(new Vector2(Settings.OverlayX.Value, Settings.OverlayY.Value), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(Settings.OverlayWidth.Value, 0), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.75f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoInputs
                      | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                      | ImGuiWindowFlags.NoFocusOnAppearing;

            if (!ImGui.Begin("WheresMyMoney##overlay", flags)) { ImGui.End(); return; }

            bool anythingDrawn = false;

            // ── Nearby Items ──────────────────────────────────────────────
            if (Settings.ShowCurrencyOverlay.Value || Settings.ShowBasesOverlay.Value)
            {
                var nearbyRows = new List<(string Label, float TotalValue, Vector4 Color)>();

                if (Settings.ShowCurrencyOverlay.Value)
                {
                    foreach (var g in GetFilteredCurrencyItems().GroupBy(i => i.Name))
                    {
                        float cv    = GetLiveChaosValue(g.First());
                        int   total = g.Sum(i => i.StackSize);
                        float val   = cv * total;
                        string s    = total > 1 ? $" x{total}" : "";
                        string v    = cv >= 0.01f ? $"  ~{val:0.#}c" : "";
                        nearbyRows.Add(($"  {g.Key}{s}{v}", val, GetValueColor(cv)));
                    }
                }

                if (Settings.ShowBasesOverlay.Value)
                {
                    foreach (var b in GetFilteredBaseItems())
                    {
                        float cv      = GetLiveChaosValue(b);
                        string ilvlS  = b.ItemLevel > 0 ? $" [ilvl {b.ItemLevel}]" : "";
                        string valS   = cv >= 0.01f ? $"  ~{cv:0.#}c" : "";
                        nearbyRows.Add(($"  {b.Name}{ilvlS}{valS}", cv, GetRarityColor(b.Rarity)));
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
            if (Settings.ShowLeftBehind.Value)
            {
                var visibleNow = new HashSet<string>(
                    _lastFrameItems.Values.Select(i => i.Rarity == ItemRarity.Unique ? i.Name : i.BaseName),
                    StringComparer.OrdinalIgnoreCase);

                var leftBehind = _tally.GroundSeen
                    .Select(kvp => new
                    {
                        Name      = kvp.Key,
                        ValueEach = NinjaFetcher.GetChaosValue(kvp.Key),
                        Remaining = kvp.Value - (_tally.Counts.TryGetValue(kvp.Key, out int p) ? p : 0)
                    })
                    .Where(r => r.Remaining > 0 && !visibleNow.Contains(r.Name)
                             && r.ValueEach * r.Remaining >= Settings.LeftBehindMinValue.Value)
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
            if (Settings.ShowMapTally.Value && (_tally.Counts.Count > 0 || _tally.GroundSeen.Count > 0))
            {
                if (anythingDrawn) ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), _tally.CurrentArea);
                ImGui.Separator();

                var combined = new Dictionary<string, (int picked, int onGround)>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _tally.Counts)
                    combined[kvp.Key] = (kvp.Value, 0);
                foreach (var kvp in _tally.GroundSeen)
                {
                    combined.TryGetValue(kvp.Key, out var ex);
                    combined[kvp.Key] = (ex.picked, Math.Max(0, kvp.Value - ex.picked));
                }

                var tallyRows = combined
                    .Select(kvp => new
                    {
                        Name      = kvp.Key,
                        Picked    = kvp.Value.picked,
                        OnGround  = kvp.Value.onGround,
                        ValueEach = NinjaFetcher.GetChaosValue(kvp.Key)
                    })
                    .Where(r => r.ValueEach * r.Picked    >= Settings.TallyMinValue.Value
                             || r.ValueEach * r.OnGround  >= Settings.TallyMinValue.Value)
                    .OrderByDescending(r => r.ValueEach * r.Picked)
                    .ThenByDescending(r => r.ValueEach * r.OnGround)
                    .ToList();

                float mapTotal = 0f;
                foreach (var row in tallyRows)
                {
                    float pt = row.ValueEach * row.Picked;
                    float gt = row.ValueEach * row.OnGround;
                    mapTotal += pt;

                    if (row.Picked > 0 && row.OnGround > 0)
                    {
                        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f),
                            $"  {row.Name}{(row.Picked > 1 ? $" x{row.Picked}" : "")}{(row.ValueEach >= 0.01f ? $"  = {pt:0.#}c" : "")}");
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
                            $"    +{row.Name}{(row.OnGround > 1 ? $" x{row.OnGround}" : "")}  ~{gt:0.#}c  [on ground]");
                    }
                    else if (row.OnGround > 0)
                    {
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
                            $"  {row.Name}{(row.OnGround > 1 ? $" x{row.OnGround}" : "")}  ~{gt:0.#}c  [on ground]");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f),
                            $"  {row.Name}{(row.Picked > 1 ? $" x{row.Picked}" : "")}{(row.ValueEach >= 0.01f ? $"  = {pt:0.#}c" : "")}");
                    }
                }
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"  Total  ~{mapTotal:0.#}c");
            }

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), NinjaFetcher.StatusMessage);

            // ── Debug ─────────────────────────────────────────────────────
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
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f),
                        $"  {kvp.Key}: seen={kvp.Value} picked={picked} remaining={kvp.Value - picked}");
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0f, 1f), $"[DEBUG] Inventory snapshot: {_lastInventorySnapshot.Count} item types");
                foreach (var kvp in _lastInventorySnapshot.OrderByDescending(k => k.Value).Take(15))
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"  {kvp.Key}: x{kvp.Value}");
            }

            ImGui.End();
        }

        // ── Filtering helpers ─────────────────────────────────────────────
        // All filters use GetLiveChaosValue() so price changes take effect immediately.

        private List<GroundItem> GetFilteredCurrencyItems()
        {
            var blocked = GetBlockedItems();
            return _lastFrameItems.Values
                .Where(i => i.IsCurrency && !blocked.Contains(i.BaseName))
                .Where(i => Settings.CurrencyMaxDistance.Value == 0 || i.DistanceToPlayer <= Settings.CurrencyMaxDistance.Value)
                .GroupBy(i => i.BaseName)
                .Where(g => !NinjaFetcher.IsLoaded || GetLiveChaosValue(g.First()) * g.Sum(i => i.StackSize) >= Settings.MinCurrencyValue.Value)
                .SelectMany(g => g)
                .ToList();
        }

        private List<GroundItem> GetFilteredBaseItems()
        {
            return _lastFrameItems.Values
                .Where(i => !i.IsCurrency)
                .Where(i => i.Rarity == ItemRarity.Unique || i.ItemLevel == 0 || i.ItemLevel >= Settings.MinItemLevel.Value)
                .Where(i => !NinjaFetcher.IsLoaded || GetLiveChaosValue(i) >= Settings.MinBaseValue.Value)
                .Where(i => Settings.BasesMaxDistance.Value == 0 || i.DistanceToPlayer <= Settings.BasesMaxDistance.Value)
                .ToList();
        }

        // ── Color helpers ─────────────────────────────────────────────────
        private static Vector4 GetValueColor(float cv)
        {
            if (cv >= 100f) return new Vector4(1f, 0.2f, 0.2f, 1f);
            if (cv >= 20f)  return new Vector4(1f, 0.65f, 0f, 1f);
            if (cv >= 5f)   return new Vector4(1f, 1f, 0.3f, 1f);
            if (cv >= 1f)   return new Vector4(0.9f, 0.9f, 0.9f, 1f);
            return new Vector4(0.5f, 0.5f, 0.5f, 1f);
        }

        private static Vector4 GetRarityColor(ItemRarity rarity) => rarity switch
        {
            ItemRarity.Unique => new Vector4(0.99f, 0.5f, 0.1f, 1f),
            ItemRarity.Rare   => new Vector4(1f, 1f, 0.2f, 1f),
            ItemRarity.Magic  => new Vector4(0.5f, 0.5f, 1f, 1f),
            _                 => new Vector4(0.9f, 0.9f, 0.9f, 1f)
        };
    }
}
