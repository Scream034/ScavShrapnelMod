using UnityEngine;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Dual tracker for debris objects: separates physical shrapnel from visual particles.
    ///
    /// Physical pool: ShrapnelProjectile objects (persistent, deal damage, break blocks).
    ///   Always tracked as GameObjects with ring buffer eviction.
    ///
    /// Visual pool: Fallback AshParticle/VisualShrapnel GameObjects only.
    ///   When ParticleSystem pools are active (ParticlePoolManager.Initialized),
    ///   most visual particles bypass this tracker entirely — they live inside
    ///   the ParticleSystem and are managed by Unity's particle lifecycle.
    ///   This pool only handles fallback GameObjects created when pools aren't ready.
    ///
    /// PERF: Uses ring buffers instead of List for O(1) enqueue/dequeue.
    /// </summary>
    public static class DebrisTracker
    {
        private static readonly RingBuffer _physical = new RingBuffer(512);
        private static readonly RingBuffer _visual = new RingBuffer(4096);

        /// <summary>
        /// Registers a physical shrapnel object (ShrapnelProjectile component).
        /// Protected pool — never evicted by visual particle overflow.
        /// </summary>
        /// <param name="obj">The physical shrapnel GameObject.</param>
        public static void Register(GameObject obj)
        {
            if (obj == null) return;
            int max = ShrapnelConfig.MaxAliveDebris.Value;
            _physical.Enqueue(obj, max);
        }

        /// <summary>
        /// Registers a visual particle object (fallback AshParticle/VisualShrapnel).
        /// Only used when ParticleSystem pools are not initialized.
        /// When pools are active, visual particles are GPU-managed and don't need tracking.
        /// </summary>
        /// <param name="obj">The visual particle GameObject.</param>
        public static void RegisterVisual(GameObject obj)
        {
            if (obj == null) return;
            int max = ShrapnelConfig.MaxAliveVisualParticles.Value;
            _visual.Enqueue(obj, max);
        }

        /// <summary>Current number of tracked physical shrapnel GameObjects.</summary>
        public static int PhysicalCount => _physical.Count;

        /// <summary>Current number of tracked fallback visual particle GameObjects.</summary>
        public static int VisualCount => _visual.Count;

        /// <summary>Total tracked GameObjects across both pools.</summary>
        public static int Count => _physical.Count + _visual.Count;

        /// <summary>
        /// Total alive particles including GPU pool particles.
        /// Useful for performance monitoring and debug display.
        /// </summary>
        public static int TotalAliveParticles
        {
            get
            {
                int total = _physical.Count + _visual.Count;
                if (ParticlePoolManager.Initialized)
                {
                    if (ParticlePoolManager.Debris != null)
                        total += ParticlePoolManager.Debris.AliveCount;
                    if (ParticlePoolManager.Glow != null)
                        total += ParticlePoolManager.Glow.AliveCount;
                    if (ParticlePoolManager.Spark != null)
                        total += ParticlePoolManager.Spark.AliveCount;
                }
                return total;
            }
        }

        /// <summary>
        /// Returns formatted stats string for debug display.
        /// Includes both GameObject-tracked and GPU pool particle counts.
        /// </summary>
        public static string GetStats()
        {
            string stats = $"Phys:{_physical.Count} FallbackVis:{_visual.Count}";
            if (ParticlePoolManager.Initialized)
            {
                int debris = ParticlePoolManager.Debris?.AliveCount ?? 0;
                int glow = ParticlePoolManager.Glow?.AliveCount ?? 0;
                int spark = ParticlePoolManager.Spark?.AliveCount ?? 0;
                stats += $" Pool[D:{debris} G:{glow} S:{spark}]";
            }
            else
            {
                stats += " Pools:OFF";
            }
            return stats;
        }

        /// <summary>
        /// Forcibly destroys all tracked GameObjects, clears both pools,
        /// and clears all ParticleSystem pool particles.
        /// </summary>
        public static void Clear()
        {
            _physical.DestroyAllAndClear();
            _visual.DestroyAllAndClear();
            ParticlePoolManager.ClearAll();
        }

        /// <summary>
        /// PERF: Ring buffer with O(1) enqueue/dequeue for GameObject tracking.
        /// Automatically evicts oldest entries when capacity limit is exceeded.
        /// Skips already-destroyed (null) entries during eviction.
        /// Grows backing array if needed (doubling strategy).
        /// </summary>
        private sealed class RingBuffer
        {
            private GameObject[] _buffer;
            private int _head;
            private int _tail;
            private int _count;

            /// <summary>Number of slots occupied (includes potential nulls from Unity destruction).</summary>
            public int Count => _count;

            public RingBuffer(int initialCapacity)
            {
                _buffer = new GameObject[initialCapacity];
                _head = 0;
                _tail = 0;
                _count = 0;
            }

            /// <summary>
            /// Enqueues an object. If count exceeds maxAlive, dequeues and destroys oldest.
            /// </summary>
            /// <param name="obj">GameObject to track.</param>
            /// <param name="maxAlive">Maximum number of alive objects allowed.</param>
            public void Enqueue(GameObject obj, int maxAlive)
            {
                if (_count == _buffer.Length)
                    Grow();

                _buffer[_tail] = obj;
                _tail = (_tail + 1) % _buffer.Length;
                _count++;

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

            /// <summary>Destroys all tracked objects and resets the buffer.</summary>
            public void DestroyAllAndClear()
            {
                for (int i = 0; i < _buffer.Length; i++)
                {
                    if (_buffer[i] != null)
                        Object.Destroy(_buffer[i]);
                    _buffer[i] = null;
                }
                _head = 0;
                _tail = 0;
                _count = 0;
            }

            /// <summary>
            /// Doubles backing array capacity, preserving FIFO order.
            /// Compacts out Unity-destroyed nulls during grow.
            /// </summary>
            private void Grow()
            {
                int newCap = _buffer.Length * 2;
                var newBuf = new GameObject[newCap];
                int write = 0;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _buffer.Length;
                    GameObject obj = _buffer[idx];
                    if (obj != null)
                        newBuf[write++] = obj;
                }
                _buffer = newBuf;
                _head = 0;
                _tail = write;
                _count = write;
            }
        }
    }
}