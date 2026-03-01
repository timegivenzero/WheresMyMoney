using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WheresMyMoney
{
    public class NinjaItem
    {
        public string Name { get; set; } = "";
        public float ChaosValue { get; set; }
        public string Icon { get; set; } = "";
    }

    public static class NinjaFetcher
    {
        private static readonly HttpClient _http = new HttpClient();
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(30);
        private static volatile bool _isFetching = false;

        public static Dictionary<string, NinjaItem> Prices { get; private set; } = new Dictionary<string, NinjaItem>(StringComparer.OrdinalIgnoreCase);
        public static bool IsLoaded { get; private set; } = false;
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
            IsLoaded = false;
            _isFetching = true;
            Task.Run(() => FetchAll(league));
        }

        private static async Task FetchAll(string league)
        {
            StatusMessage = $"Fetching prices for '{league}'...";
            var newPrices = new Dictionary<string, NinjaItem>(StringComparer.OrdinalIgnoreCase);

            try
            {
                await FetchCurrencyEndpoint(league, "Currency", newPrices);
                await FetchCurrencyEndpoint(league, "Fragment", newPrices);

                string[] itemTypes = new[]
                {
                    "Scarab", "Fossil", "Resonator", "Essence",
                    "DivinationCard", "Oil", "Incubator", "DeliriumOrb",
                    "Artifact", "Invitation", "Beast", "Map", "BlightedMap",
                    "BlightRavagedMap", "UniqueMap", "UniqueJewel", "UniqueFlask",
                    "UniqueWeapon", "UniqueArmour", "UniqueAccessory", "SkillGem",
                    "ClusterJewel", "BaseType", "Vial"
                };

                foreach (var itemType in itemTypes)
                    await FetchItemEndpoint(league, itemType, newPrices);

                Prices = newPrices;
                _lastFetch = DateTime.Now;
                IsLoaded = true;

                if (newPrices.Count == 0)
                    StatusMessage = $"⚠ 0 items loaded — check league name '{league}'";
                else
                    StatusMessage = $"Loaded {newPrices.Count} items [{league}] @ {DateTime.Now:HH:mm}";
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
                // poe.ninja moved PoE1 endpoints under /poe1/api/data/ after the PoE1/PoE2 split
                var url = $"https://poe.ninja/poe1/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type={type}";
                string json;
                try
                {
                    json = await _http.GetStringAsync(url);
                }
                catch
                {
                    // Fall back to legacy path
                    url = $"https://poe.ninja/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type={type}";
                    json = await _http.GetStringAsync(url);
                }
                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null) return;

                var iconLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (obj["currencyDetails"] is JsonArray details)
                {
                    foreach (var d in details)
                    {
                        var name = d?["name"]?.ToString();
                        var icon = d?["icon"]?.ToString();
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(icon))
                            iconLookup[name] = icon;
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
                var url = $"https://poe.ninja/poe1/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}";
                string json;
                try
                {
                    json = await _http.GetStringAsync(url);
                }
                catch
                {
                    // Fall back to legacy path
                    url = $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}";
                    json = await _http.GetStringAsync(url);
                }
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

        public static float GetChaosValue(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return 0f;
            return Prices.TryGetValue(itemName, out var item) ? item.ChaosValue : 0f;
        }
    }
}