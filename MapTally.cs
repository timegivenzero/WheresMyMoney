using System;
using System.Collections.Generic;

namespace WheresMyMoney
{
    /// <summary>
    /// Tracks currency items picked up during the current map session.
    /// Resets automatically when entering a new map/area.
    /// </summary>
    public class MapTally
    {
        // item name -> count picked up
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        // item name -> max stack size seen on ground this map (persists even when out of range)
        private readonly Dictionary<string, int> _groundSeen = new Dictionary<string, int>();

        private string _currentArea = "";

        public string CurrentArea => _currentArea;
        public IReadOnlyDictionary<string, int> Counts => _counts;
        public IReadOnlyDictionary<string, int> GroundSeen => _groundSeen;

        /// <summary>
        /// Call this every Tick with the current area name.
        /// Resets tally automatically on area change.
        /// </summary>
        public bool CheckArea(string areaName)
        {
            if (areaName == _currentArea) return false;
            Reset(areaName);
            return true;
        }

        public void Reset(string areaName = "")
        {
            _counts.Clear();
            _groundSeen.Clear();
            _currentArea = areaName;
        }

        /// <summary>
        /// Call each frame with the current visible ground currency.
        /// Updates the ground-seen count for each visible item.
        /// For items no longer visible, their last-seen count is kept so Left Behind still works.
        /// </summary>
        public void UpdateGroundSeen(IEnumerable<(string name, int stack)> visibleItems)
        {
            foreach (var (name, stack) in visibleItems)
            {
                if (string.IsNullOrEmpty(name)) continue;
                // Always take the max so a frame with a bad stack read doesn't overwrite a good one
                if (!_groundSeen.TryGetValue(name, out int existing) || stack > existing)
                    _groundSeen[name] = stack;
            }
        }

        /// <summary>
        /// Record a currency pickup detected via inventory diff.
        /// Uses the gained amount directly — more reliable than ground detection.
        /// </summary>
        public void RecordInventoryPickup(string itemName, int gained, bool capToGroundSeen = true)
        {
            if (string.IsNullOrEmpty(itemName) || gained <= 0) return;
            if (_counts.ContainsKey(itemName))
                _counts[itemName] += gained;
            else
                _counts[itemName] = gained;

            if (capToGroundSeen && _groundSeen.TryGetValue(itemName, out int seen))
                _counts[itemName] = Math.Min(_counts[itemName], seen);
        }

        /// <summary>
        /// Record a currency pickup. stackSize is usually 1 but can be more.
        /// </summary>
        public void RecordPickup(string itemName, int stackSize = 1)
        {
            if (string.IsNullOrEmpty(itemName)) return;
            if (_counts.ContainsKey(itemName))
                _counts[itemName] += stackSize;
            else
                _counts[itemName] = stackSize;

            // Cap against GroundSeen so false-positive pickups (e.g. item despawned)
            // don't inflate the tally beyond what was actually seen on the ground.
            if (_groundSeen.TryGetValue(itemName, out int seen))
                _counts[itemName] = Math.Min(_counts[itemName], seen);
        }

        /// <summary>
        /// Total chaos value of everything picked up this map.
        /// </summary>
        public float TotalChaosValue()
        {
            float total = 0f;
            foreach (var kvp in _counts)
                total += NinjaFetcher.GetChaosValue(kvp.Key) * kvp.Value;
            return total;
        }
    }
}
