using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Tracks physical shrapnel GameObjects via ring buffer eviction.
    ///
    /// Visual particles are managed by:
    ///   • AshParticlePoolManager (debris, dust, smoke — zero-GC object pool)
    ///   • ParticlePoolManager (sparks — GPU-batched Unity ParticleSystem)
    ///
    /// This tracker only handles physics shrapnel GameObjects that need
    /// Destroy() eviction when count exceeds MaxAliveDebris.
    ///
    /// PERF: Ring buffer gives O(1) enqueue/dequeue.
    /// Grows backing array by doubling if capacity exceeded (rare).
    /// </summary>
    public static class DebrisTracker
    {
        private static readonly RingBuffer _physical = new(512);

        /// <summary>
        /// Registers a physical shrapnel GameObject (ShrapnelProjectile).
        /// Evicts oldest if count exceeds MaxAliveDebris config value.
        /// </summary>
        public static void Register(GameObject obj)
        {
            if (obj == null) return;
            _physical.Enqueue(obj, ShrapnelConfig.MaxAliveDebris.Value);
        }

        /// <summary>Current number of tracked physics shrapnel GameObjects.</summary>
        public static int PhysicalCount => _physical.Count;

        /// <summary>
        /// Total alive particles across ALL systems:
        ///   • Physical shrapnel GameObjects (this tracker)
        ///   • Pooled AshParticles (AshParticlePoolManager)
        ///   • GPU sparks (ParticlePoolManager)
        ///
        /// Useful for performance monitoring and debug overlay.
        /// </summary>
        public static int TotalAliveParticles
        {
            get
            {
                int total = _physical.Count;

                if (AshParticlePoolManager.Initialized)
                    total += AshParticlePoolManager.TotalActive;

                if (ParticlePoolManager.Initialized && ParticlePoolManager.Spark != null)
                    total += ParticlePoolManager.Spark.AliveCount;

                return total;
            }
        }

        /// <summary>
        /// Formatted stats string for debug display.
        /// Shows counts per system with pool capacities.
        /// </summary>
        public static string GetStats()
        {
            string stats = $"Phys:{_physical.Count}";

            if (AshParticlePoolManager.Initialized)
                stats += $" {AshParticlePoolManager.GetStats()}";
            else
                stats += " AshPools:OFF";

            if (ParticlePoolManager.Initialized)
                stats += $" Spark:{ParticlePoolManager.Spark?.AliveCount ?? 0}";
            else
                stats += " Spark:OFF";

            return stats;
        }

        /// <summary>
        /// Destroys all tracked physics GameObjects and clears all particle pools.
        /// Call on world unload or manual cleanup command.
        /// </summary>
        public static void Clear()
        {
            _physical.DestroyAllAndClear();
            AshParticlePoolManager.ClearAll();
            ParticlePoolManager.ClearAll();
        }

        /// <summary>
        /// O(1) ring buffer for GameObject tracking with max-count eviction.
        ///
        /// When count exceeds maxAlive:
        ///   1. Dequeues oldest entry from head
        ///   2. Calls Object.Destroy if not already destroyed
        ///   3. Advances head pointer
        ///
        /// Skips null entries (Unity may destroy objects externally).
        /// Grows backing array by doubling if enqueue hits capacity.
        /// </summary>
        private sealed class RingBuffer
        {
            private GameObject[] _buffer;
            private int _head, _tail, _count;

            public int Count => _count;

            public RingBuffer(int initialCapacity)
            {
                _buffer = new GameObject[initialCapacity];
            }

            public void Enqueue(GameObject obj, int maxAlive)
            {
                if (_count == _buffer.Length) Grow();

                _buffer[_tail] = obj;
                _tail = (_tail + 1) % _buffer.Length;
                _count++;

                // Evict oldest entries if over limit
                while (_count > maxAlive)
                {
                    GameObject oldest = _buffer[_head];
                    _buffer[_head] = null;
                    _head = (_head + 1) % _buffer.Length;
                    _count--;

                    if (oldest != null)
                        Object.Destroy(oldest);
                }
            }

            public void DestroyAllAndClear()
            {
                for (int i = 0; i < _buffer.Length; i++)
                {
                    if (_buffer[i] != null)
                        Object.Destroy(_buffer[i]);
                    _buffer[i] = null;
                }
                _head = _tail = _count = 0;
            }

            /// <summary>
            /// Doubles backing array capacity.
            /// Compacts out externally-destroyed (null) entries during copy.
            /// </summary>
            private void Grow()
            {
                int newCap = _buffer.Length * 2;
                var newBuf = new GameObject[newCap];
                int write = 0;

                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _buffer.Length;
                    if (_buffer[idx] != null)
                        newBuf[write++] = _buffer[idx];
                }

                _buffer = newBuf;
                _head = 0;
                _tail = write;
                _count = write;
            }
        }
    }
}