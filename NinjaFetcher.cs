using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WheresMyMoney
{
    public class NinjaItem
    {
        public string Name       { get; set; } = "";
        public float  ChaosValue { get; set; }
        public string Icon       { get; set; } = "";
    }

    /// <summary>
    /// A versioned base type entry from poe.ninja — one per (name, ilvl tier, influence) combo.
    /// e.g. "Decimation Bow" has separate entries for ilvl 83/85/86+ with/without Shaper.
    /// </summary>
    public class NinjaBaseEntry
    {
        public string Name       { get; set; } = "";
        public float  ChaosValue { get; set; }
        public int    LevelRequired { get; set; }  // ilvl tier this price applies to
        public string Variant    { get; set; } = ""; // "" = no influence, "Shaper Item", "Elder Item", etc.
    }

    public static class NinjaFetcher
    {
        private static readonly HttpClient _http = new HttpClient();
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(30);
        private static volatile bool _isFetching = false;

        // Standard items (currency, scarabs, div cards, etc.) — keyed by name
        public static Dictionary<string, NinjaItem> Prices { get; private set; }
            = new Dictionary<string, NinjaItem>(StringComparer.OrdinalIgnoreCase);

        // Base types — keyed by name, multiple entries per base (different ilvl tiers + influences)
        // Sorted descending by LevelRequired so we can find the best matching tier quickly.
        public static Dictionary<string, List<NinjaBaseEntry>> BasePrices { get; private set; }
            = new Dictionary<string, List<NinjaBaseEntry>>(StringComparer.OrdinalIgnoreCase);

        public static bool   IsLoaded      { get; private set; } = false;
        public static string StatusMessage { get; private set; } = "Not loaded";

        public static void TryRefresh(string league)
        {
            if (_isFetching) return;
            if (DateTime.Now - _lastFetch < _refreshInterval && IsLoaded) return;
            _isFetching = true;
            Task.Run(() => FetchAll(league));
        }

        public static void ForceRefresh(string league)
        {
            if (_isFetching) return;
            _lastFetch = DateTime.MinValue;
            IsLoaded   = false;
            _isFetching = true;
            Task.Run(() => FetchAll(league));
        }

        private static async Task FetchAll(string league)
        {
            StatusMessage = $"Fetching prices for '{league}'...";
            var newPrices     = new Dictionary<string, NinjaItem>(StringComparer.OrdinalIgnoreCase);
            var newBasePrices = new Dictionary<string, List<NinjaBaseEntry>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                await FetchCurrencyEndpoint(league, "Currency",  newPrices);
                await FetchCurrencyEndpoint(league, "Fragment",  newPrices);

                string[] itemTypes =
                {
                    "Scarab", "Fossil", "Resonator", "Essence",
                    "DivinationCard", "Oil", "Incubator", "DeliriumOrb",
                    "Artifact", "Invitation", "Beast", "Map", "BlightedMap",
                    "BlightRavagedMap", "UniqueMap", "UniqueJewel", "UniqueFlask",
                    "UniqueWeapon", "UniqueArmour", "UniqueAccessory", "SkillGem",
                    "ClusterJewel", "Vial"
                };

                foreach (var itemType in itemTypes)
                    await FetchItemEndpoint(league, itemType, newPrices);

                // BaseType gets its own fetch with ilvl-aware storage
                await FetchBaseTypeEndpoint(league, newBasePrices);

                Prices     = newPrices;
                BasePrices = newBasePrices;
                _lastFetch = DateTime.Now;
                IsLoaded   = true;

                int total = newPrices.Count + newBasePrices.Count;
                StatusMessage = total == 0
                    ? $"⚠ 0 items loaded — check league name '{league}'"
                    : $"Loaded {newPrices.Count} items + {newBasePrices.Count} bases [{league}] @ {DateTime.Now:HH:mm}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fetch error ({league}): {ex.Message}";
                if (!IsLoaded) Prices = newPrices;
            }
            finally
            {
                _isFetching = false;
            }
        }

        private static async Task FetchCurrencyEndpoint(string league, string type, Dictionary<string, NinjaItem> prices)
        {
            try
            {
                var json = await FetchWithFallback(
                    $"https://poe.ninja/poe1/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type={type}",
                    $"https://poe.ninja/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type={type}");

                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null) return;

                var iconLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (obj["currencyDetails"] is JsonArray details)
                {
                    foreach (var d in details)
                    {
                        var n = d?["name"]?.ToString();
                        var i = d?["icon"]?.ToString();
                        if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(i))
                            iconLookup[n] = i;
                    }
                }

                if (obj["lines"] is JsonArray lines)
                {
                    foreach (var line in lines)
                    {
                        var name = line?["currencyTypeName"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        float chaosVal = 0f;
                        try { chaosVal = line?["chaosEquivalent"]?.GetValue<float>() ?? 0f; } catch { }
                        iconLookup.TryGetValue(name, out var icon);
                        if (!prices.ContainsKey(name))
                            prices[name] = new NinjaItem { Name = name, ChaosValue = chaosVal, Icon = icon ?? "" };
                    }
                }
            }
            catch { }
        }

        private static async Task FetchItemEndpoint(string league, string type, Dictionary<string, NinjaItem> prices)
        {
            try
            {
                var json = await FetchWithFallback(
                    $"https://poe.ninja/poe1/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}",
                    $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}");

                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj?["lines"] is not JsonArray lines) return;

                foreach (var line in lines)
                {
                    var name = line?["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    float chaosVal = 0f;
                    try { chaosVal = line?["chaosValue"]?.GetValue<float>() ?? 0f; } catch { }
                    var icon = line?["icon"]?.ToString() ?? "";
                    if (!prices.ContainsKey(name))
                        prices[name] = new NinjaItem { Name = name, ChaosValue = chaosVal, Icon = icon };
                }
            }
            catch { }
        }

        /// <summary>
        /// Fetches BaseType endpoint and stores ALL ilvl/influence variants per base name.
        /// poe.ninja returns one line per (name, levelRequired, variant) combination.
        /// </summary>
        private static async Task FetchBaseTypeEndpoint(string league, Dictionary<string, List<NinjaBaseEntry>> basePrices)
        {
            try
            {
                var json = await FetchWithFallback(
                    $"https://poe.ninja/poe1/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type=BaseType",
                    $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type=BaseType");

                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj?["lines"] is not JsonArray lines) return;

                foreach (var line in lines)
                {
                    var name = line?["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    float chaosVal = 0f;
                    try { chaosVal = line?["chaosValue"]?.GetValue<float>() ?? 0f; } catch { }

                    int ilvl = 0;
                    try { ilvl = line?["levelRequired"]?.GetValue<int>() ?? 0; } catch { }

                    // "variant" holds influence: "Shaper Item", "Elder Item", "Crusader Item", etc.
                    // null/missing = no influence
                    var variant = line?["variant"]?.ToString() ?? "";

                    var entry = new NinjaBaseEntry
                    {
                        Name          = name,
                        ChaosValue    = chaosVal,
                        LevelRequired = ilvl,
                        Variant       = variant,
                    };

                    if (!basePrices.TryGetValue(name, out var list))
                    {
                        list = new List<NinjaBaseEntry>();
                        basePrices[name] = list;
                    }
                    list.Add(entry);
                }

                // Sort each base's entries descending by ilvl so we can match from top down
                foreach (var list in basePrices.Values)
                    list.Sort((a, b) => b.LevelRequired.CompareTo(a.LevelRequired));
            }
            catch { }
        }

        private static async Task<string> FetchWithFallback(string primary, string fallback)
        {
            try   { return await _http.GetStringAsync(primary); }
            catch { return await _http.GetStringAsync(fallback); }
        }

        // ── Lookups ───────────────────────────────────────────────────────

        /// <summary>
        /// Standard price lookup for currency, scarabs, div cards, etc.
        /// </summary>
        public static float GetChaosValue(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return 0f;
            return Prices.TryGetValue(itemName, out var item) ? item.ChaosValue : 0f;
        }

        /// <summary>
        /// Base type price lookup that respects ilvl and influence.
        /// Finds the highest ilvl tier that is <= the item's actual ilvl,
        /// matching the item's influence (or falling back to no-influence price).
        ///
        /// If itemIlvl is 0 (unknown), returns the lowest-tier no-influence price
        /// as a conservative estimate.
        /// </summary>
        public static float GetBaseValue(string baseName, int itemIlvl, string influence = "")
        {
            if (string.IsNullOrEmpty(baseName)) return 0f;

            // Try base type table first
            if (BasePrices.TryGetValue(baseName, out var entries) && entries.Count > 0)
            {
                // Normalise influence to match poe.ninja's variant strings
                // e.g. "Shaper" → "Shaper Item", "" → ""
                var wantVariant = NormaliseInfluence(influence);

                if (itemIlvl > 0)
                {
                    // Pass 1: exact influence match, highest ilvl tier <= item ilvl
                    foreach (var e in entries) // sorted desc by ilvl
                    {
                        if (e.LevelRequired <= itemIlvl
                            && string.Equals(e.Variant, wantVariant, StringComparison.OrdinalIgnoreCase))
                            return e.ChaosValue;
                    }

                    // Pass 2: no-influence fallback (most common case — non-influenced bases)
                    if (!string.IsNullOrEmpty(wantVariant))
                    {
                        foreach (var e in entries)
                        {
                            if (e.LevelRequired <= itemIlvl && string.IsNullOrEmpty(e.Variant))
                                return e.ChaosValue;
                        }
                    }

                    // Pass 3: any entry within ilvl (shouldn't usually be needed)
                    foreach (var e in entries)
                    {
                        if (e.LevelRequired <= itemIlvl)
                            return e.ChaosValue;
                    }
                }
                else
                {
                    // ilvl unknown — return lowest tier no-influence price as conservative estimate
                    var noInfluence = entries
                        .Where(e => string.IsNullOrEmpty(e.Variant))
                        .OrderBy(e => e.LevelRequired)
                        .FirstOrDefault();
                    if (noInfluence != null) return noInfluence.ChaosValue;
                    return entries.Last().ChaosValue; // absolute fallback
                }

                return 0f;
            }

            // Fall back to standard prices dict (for non-base items looking up by name)
            return GetChaosValue(baseName);
        }

        private static string NormaliseInfluence(string influence)
        {
            if (string.IsNullOrEmpty(influence)) return "";
            // Map ExileCore influence names to poe.ninja variant strings
            return influence.ToLowerInvariant() switch
            {
                "shaper"   => "Shaper Item",
                "elder"    => "Elder Item",
                "crusader" => "Crusader Item",
                "hunter"   => "Hunter Item",
                "redeemer" => "Redeemer Item",
                "warlord"  => "Warlord Item",
                _          => influence  // pass through if already formatted
            };
        }
    }
}
