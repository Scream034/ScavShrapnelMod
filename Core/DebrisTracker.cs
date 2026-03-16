using UnityEngine;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Dual tracker for debris objects: separates physical shrapnel from visual particles.
    ///
    /// WHY: A single tracker caused physical shrapnel (the pieces that do damage)
    /// to be instantly destroyed when visual particle count exploded (1000+ ground
    /// debris per explosion filling the 2000 limit). Now physical shrapnel has
    /// its own protected pool.
    ///
    /// Physical pool: ShrapnelProjectile objects (persistent, deal damage, break blocks).
    /// Visual pool: AshParticle, VisualShrapnel, ground debris (temporary, no damage).
    ///
    /// PERF: Uses ring buffers instead of List for O(1) enqueue/dequeue.
    /// Previous implementation used List.RemoveAt(0) which is O(n) per eviction.
    /// With 3000 visual particles, this caused severe frame drops during explosions.
    /// </summary>
    public static class DebrisTracker
    {
        // PERF: Ring buffer for O(1) FIFO operations.
        // Previous List-based approach had O(n) RemoveAt(0) and O(n²) PurgeNulls.
        private static readonly RingBuffer _physical = new RingBuffer(512);
        private static readonly RingBuffer _visual = new RingBuffer(4096);

        /// <summary>
        /// Registers a physical shrapnel object (ShrapnelProjectile component).
        /// This is a protected pool — objects here are never evicted by visual particle overflow.
        /// </summary>
        /// <param name="obj">The physical shrapnel GameObject.</param>
        public static void Register(GameObject obj)
        {
            if (obj == null) return;

            int max = ShrapnelConfig.MaxAliveDebris.Value;
            _physical.Enqueue(obj, max);
        }

        /// <summary>
        /// Registers a visual particle object (AshParticle, VisualShrapnel, ground debris).
        /// Separate pool from physical shrapnel — overflow here only evicts other visual particles.
        /// </summary>
        /// <param name="obj">The visual particle GameObject.</param>
        public static void RegisterVisual(GameObject obj)
        {
            if (obj == null) return;

            // WHY: Visual limit is higher because particles are lightweight
            // and self-destruct quickly. A high cap prevents unbounded memory
            // growth from multiple rapid explosions.
            int max = ShrapnelConfig.MaxAliveVisualParticles.Value;
            _visual.Enqueue(obj, max);
        }

        /// <summary>Current number of tracked physical shrapnel objects (includes nulls awaiting compaction).</summary>
        public static int PhysicalCount => _physical.Count;

        /// <summary>Current number of tracked visual particle objects (includes nulls awaiting compaction).</summary>
        public static int VisualCount => _visual.Count;

        /// <summary>Total number of tracked objects across both pools.</summary>
        public static int Count => _physical.Count + _visual.Count;

        /// <summary>
        /// Forcibly destroys all tracked objects and clears both pools.
        /// Useful during scene transitions or cleanup commands.
        /// </summary>
        public static void Clear()
        {
            _physical.DestroyAllAndClear();
            _visual.DestroyAllAndClear();
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

            /// <summary>Number of slots currently occupied (includes potential nulls from Unity destruction).</summary>
            public int Count => _count;

            public RingBuffer(int initialCapacity)
            {
                _buffer = new GameObject[initialCapacity];
                _head = 0;
                _tail = 0;
                _count = 0;
            }

            /// <summary>
            /// Enqueues an object. If count exceeds maxAlive, dequeues and destroys oldest entries.
            /// </summary>
            /// <param name="obj">GameObject to track.</param>
            /// <param name="maxAlive">Maximum number of alive objects allowed.</param>
            public void Enqueue(GameObject obj, int maxAlive)
            {
                // Grow if backing array is full
                if (_count == _buffer.Length)
                    Grow();

                _buffer[_tail] = obj;
                _tail = (_tail + 1) % _buffer.Length;
                _count++;

                // Evict oldest until we're under the cap
                while (_count > maxAlive)
                {
                    GameObject oldest = _buffer[_head];
                    _buffer[_head] = null;
                    _head = (_head + 1) % _buffer.Length;
                    _count--;

                    // WHY: Unity null check — object may have self-destructed
                    if (oldest != null)
                        Object.Destroy(oldest);
                }
            }

            /// <summary>
            /// Destroys all tracked objects and resets the buffer.
            /// </summary>
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
            /// PERF: Doubles backing array capacity, preserving FIFO order.
            /// Called rarely — only when concurrent alive objects exceed initial capacity.
            /// </summary>
            private void Grow()
            {
                int newCapacity = _buffer.Length * 2;
                var newBuffer = new GameObject[newCapacity];

                // PERF: Compact during grow — skip nulls to reclaim dead slots
                int writeIndex = 0;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _buffer.Length;
                    GameObject obj = _buffer[idx];
                    // WHY: Skip Unity-destroyed objects during compaction
                    if (obj != null)
                    {
                        newBuffer[writeIndex++] = obj;
                    }
                }

                _buffer = newBuffer;
                _head = 0;
                _tail = writeIndex;
                _count = writeIndex;
            }
        }
    }
}