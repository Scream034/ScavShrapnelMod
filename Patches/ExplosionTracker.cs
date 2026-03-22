using System.Collections.Generic;
using UnityEngine;

namespace ScavShrapnelMod.Patches
{
    /// <summary>
    /// Deduplication tracker for explosion positions.
    /// Prevents double-spawning when multiple detection layers catch the same explosion.
    ///
    /// Hash uses rounded position (×10) to absorb float drift.
    /// Queue evicts old entries to prevent unbounded growth.
    /// </summary>
    public static class ExplosionTracker
    {
        private static readonly HashSet<int> _recent = new();
        private static readonly Queue<int> _cleanup = new();
        private const int MaxTracked = 100;

        /// <summary>
        /// Generates position hash with 0.1 unit precision.
        /// Two explosions within 0.1 units produce the same hash.
        /// </summary>
        public static int GetHash(Vector2 pos)
        {
            int px = Mathf.RoundToInt(pos.x * 10f);
            int py = Mathf.RoundToInt(pos.y * 10f);
            return unchecked(px * 397 ^ py);
        }

        public static bool WasHandled(Vector2 pos)
            => _recent.Contains(GetHash(pos));

        public static void Track(Vector2 pos)
        {
            int hash = GetHash(pos);
            _recent.Add(hash);
            _cleanup.Enqueue(hash);

            while (_cleanup.Count > MaxTracked)
            {
                int old = _cleanup.Dequeue();
                _recent.Remove(old);
            }
        }
    }
}