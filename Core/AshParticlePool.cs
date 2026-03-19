using System;
using ScavShrapnelMod.Projectiles;
using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Zero-GC object pool for AshParticlePooled.
    ///
    /// All operations O(1):
    ///   Get():    Pop free stack or steal oldest from active ring
    ///   Return(): Push to free stack, unmark in active map
    ///
    /// Memory layout:
    ///   _all[]:       Master array, indexed by PoolIndex
    ///   _freeStack[]: Indices of available particles (LIFO)
    ///   _activeRing[]: Indices of in-use particles (FIFO, oldest at head)
    ///   _activeMap[]:  _all[i] is active? (O(1) check for ring skip)
    ///
    /// Eviction: When pool is full, steals oldest active particle.
    /// This particle gets ForceRecycle() and is immediately reused.
    /// </summary>
    public sealed class AshParticlePool
    {
        private readonly AshParticlePooled[] _all;
        private readonly int[] _freeStack;
        private readonly int[] _activeRing;
        private readonly bool[] _activeMap;

        private int _freeTop;
        private int _activeHead;
        private int _activeTail;
        private int _activeCount;
        private readonly int _capacity;

        private readonly Transform _container;
        private readonly Material _material;
        private readonly string _name;

        public int ActiveCount => _activeCount;
        public int FreeCount => _freeTop;
        public int Capacity => _capacity;

        /// <summary>
        /// Creates pool and pre-warms all GameObjects.
        /// All GC allocations happen here — after this, Get/Return are zero-GC.
        /// </summary>
        public AshParticlePool(string name, Material material, int capacity)
        {
            _name = name;
            _material = material;
            _capacity = capacity;

            _all = new AshParticlePooled[capacity];
            _freeStack = new int[capacity];
            _activeRing = new int[capacity];
            _activeMap = new bool[capacity];

            GameObject containerObj = new GameObject($"AshPool_{name}");
            UnityEngine.Object.DontDestroyOnLoad(containerObj);
            containerObj.hideFlags = HideFlags.HideAndDontSave;
            _container = containerObj.transform;

            for (int i = 0; i < capacity; i++)
            {
                _all[i] = CreateParticle(i);
                _freeStack[i] = i;
            }
            _freeTop = capacity;

            Console.Log($"[AshPool:{_name}] Pre-warmed {capacity} particles");
        }

        private AshParticlePooled CreateParticle(int index)
        {
            GameObject obj = new GameObject($"Ash_{_name}_{index}");
            obj.transform.SetParent(_container, true);
            obj.SetActive(false);

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sharedMaterial = _material;

            AshParticlePooled p = obj.AddComponent<AshParticlePooled>();
            p.Pool = this;
            p.SR = sr;
            p.PoolIndex = index;

            return p;
        }

        /// <summary>Material assigned to all particles in this pool.</summary>
        public Material PoolMaterial => _material;

        /// <summary>
        /// Gets a particle. O(1).
        /// Pops from free stack, or steals oldest active if empty.
        /// Returned particle is SetActive(true) — caller must call Initialize().
        /// </summary>
        public AshParticlePooled Get()
        {
            int index;

            if (_freeTop > 0)
            {
                // Fast path: pop free stack
                _freeTop--;
                index = _freeStack[_freeTop];
            }
            else
            {
                // Steal oldest active
                index = StealOldest();
                if (index < 0)
                {
                    Plugin.Log.LogError($"[AshPool:{_name}] Pool fully exhausted");
                    return null;
                }
            }

            AshParticlePooled particle = _all[index];
            particle.gameObject.SetActive(true);

            // Track in active ring
            _activeRing[_activeTail] = index;
            _activeTail = (_activeTail + 1) % _capacity;
            _activeCount++;
            _activeMap[index] = true;

            return particle;
        }

        /// <summary>
        /// Scans active ring from head, finds first still-active particle,
        /// force-recycles it and returns its index.
        /// Skips already-returned entries (activeMap=false).
        /// </summary>
        private int StealOldest()
        {
            int scanned = 0;
            while (scanned < _capacity)
            {
                int index = _activeRing[_activeHead];
                _activeHead = (_activeHead + 1) % _capacity;

                if (_activeMap[index])
                {
                    // Found active particle — steal it
                    _activeMap[index] = false;
                    _activeCount--;
                    _all[index].ForceRecycle();
                    _all[index].gameObject.SetActive(false);
                    return index;
                }

                // Already returned — skip (decrement count was done in Return)
                scanned++;
            }
            return -1;
        }

        /// <summary>
        /// Returns particle to free stack. O(1).
        /// Called by AshParticlePooled.Recycle().
        /// </summary>
        public void Return(AshParticlePooled particle)
        {
            int index = particle.PoolIndex;

            if (!_activeMap[index]) return; // Already returned

            _activeMap[index] = false;
            _activeCount--;

            _freeStack[_freeTop] = index;
            _freeTop++;
        }

        /// <summary>Recycles all active particles back to pool.</summary>
        public void Clear()
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_activeMap[i])
                {
                    _activeMap[i] = false;
                    _all[i].ForceRecycle();
                    _all[i].gameObject.SetActive(false);
                }
                _freeStack[i] = i;
            }
            _freeTop = _capacity;
            _activeHead = 0;
            _activeTail = 0;
            _activeCount = 0;
        }

        /// <summary>Destroys pool container and all GameObjects.</summary>
        public void Destroy()
        {
            if (_container != null)
                UnityEngine.Object.Destroy(_container.gameObject);
        }

        /// <summary>
        /// Self-heals material if shader was unloaded by vanilla chunk destruction.
        /// Call periodically (e.g. every 60 frames from manager).
        /// </summary>
        public void HealMaterial(Material freshMaterial)
        {
            if (_material != null && _material.shader == null && freshMaterial != null)
            {
                for (int i = 0; i < _capacity; i++)
                {
                    if (_all[i].SR != null)
                        _all[i].SR.sharedMaterial = freshMaterial;
                }
            }
        }
    }
}