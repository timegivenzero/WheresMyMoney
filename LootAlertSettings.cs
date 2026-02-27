using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace LootAlert
{
    public class LootAlertSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(true);

        // Stored as text so NinjaFetcher can use it directly.
        // The dropdown in DrawSettings keeps this in sync.
        [Menu("League Name", "Current league name for poe.ninja prices")]
        public TextNode LeagueName { get; set; } = new TextNode("Keepers");

        // Tracks which dropdown index is selected (not shown in auto-generated settings UI)
        public int LeagueIndex { get; set; } = 2; // default = "Keepers"

        // ── Currency overlay ──────────────────────────────────────────────
        [Menu("Show Currency on Ground", "Show currency items currently on the ground")]
        public ToggleNode ShowCurrencyOverlay { get; set; } = new ToggleNode(true);

        [IgnoreMenu]
        public RangeNode<float> MinCurrencyValue { get; set; } = new RangeNode<float>(1f, 0f, 100f);

        [Menu("Currency Max Distance", "Max distance (game units) to show currency items. 0 = unlimited")]
        public RangeNode<int> CurrencyMaxDistance { get; set; } = new RangeNode<int>(0, 0, 200);

        // ── Valuable bases overlay ────────────────────────────────────────
        [Menu("Show Valuable Bases", "Show rare/magic item bases with good ilvl and chaos value")]
        public ToggleNode ShowBasesOverlay { get; set; } = new ToggleNode(true);

        [IgnoreMenu]
        public RangeNode<float> MinBaseValue { get; set; } = new RangeNode<float>(5f, 1f, 500f);

        [Menu("Min Item Level for Bases", "Minimum ilvl to consider a base valuable")]
        public RangeNode<int> MinItemLevel { get; set; } = new RangeNode<int>(82, 1, 100);

        [Menu("Bases Max Distance", "Max distance (game units) to show base items. 0 = unlimited")]
        public RangeNode<int> BasesMaxDistance { get; set; } = new RangeNode<int>(0, 0, 200);

        // ── Per-map tally ─────────────────────────────────────────────────
        [Menu("Show Map Tally", "Show per-map currency pickup tally")]
        public ToggleNode ShowMapTally { get; set; } = new ToggleNode(true);

        [IgnoreMenu]
        public RangeNode<float> TallyMinValue { get; set; } = new RangeNode<float>(0f, 0f, 50f);

        // ── Left behind ───────────────────────────────────────────────────
        [Menu("Show Left Behind", "Remind you of currency you walked away from this map")]
        public ToggleNode ShowLeftBehind { get; set; } = new ToggleNode(true);

        [IgnoreMenu]
        public RangeNode<float> LeftBehindMinValue { get; set; } = new RangeNode<float>(1f, 0f, 100f);

        // ── Blocked items ─────────────────────────────────────────────────
        [Menu("Blocked Items", "Comma-separated item names to exclude from tally and left behind (e.g. janky ninja prices)")]
        public TextNode BlockedItems { get; set; } = new TextNode("Scroll of Wisdom,Portal Scroll,Scroll Fragment");

        // ── Overlay position ──────────────────────────────────────────────
        [Menu("Overlay X Position")]
        public RangeNode<int> OverlayX { get; set; } = new RangeNode<int>(20, 0, 3840);

        [Menu("Overlay Y Position")]
        public RangeNode<int> OverlayY { get; set; } = new RangeNode<int>(200, 0, 2160);

        [Menu("Overlay Width")]
        public RangeNode<int> OverlayWidth { get; set; } = new RangeNode<int>(300, 100, 800);

        [Menu("Debug Mode", "Show raw scan data in the overlay to diagnose issues")]
        public ToggleNode ShowDebug { get; set; } = new ToggleNode(false);
    }
}