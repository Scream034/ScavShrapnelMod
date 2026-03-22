using UnityEngine;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Pooled ash particle with zero-GC lifecycle.
    ///
    /// Physics model:
    ///   - Quadratic air drag (F_drag ∝ v²)
    ///   - Perlin-noise turbulence (frame-staggered, 3-frame interval)
    ///   - Wind drift (increases with age²)
    ///   - Thermal lift (decays over lifetime via t²)
    ///   - Configurable gravity (negative = rises)
    ///   - Start delay for shockwave propagation
    ///
    /// PERF vs original AshParticle:
    ///   - No Instantiate/Destroy (reuse via SetActive)
    ///   - No GetComponent (references cached at pool creation)
    ///   - Decomposed Vector2/Color = float fields (no struct copies)
    ///   - PoolIndex stored in particle = O(1) pool operations
    ///   - Frame-staggered Perlin (every 3 frames, ×3 compensation)
    ///
    /// Lifecycle:
    ///   Pool.Get() → SetActive(true) → Initialize() → Update → Recycle() → SetActive(false) → Pool
    /// </summary>
    public sealed class AshParticlePooled : MonoBehaviour
    {
        #region Pool References (set once at creation)

        internal AshParticlePool Pool;
        internal SpriteRenderer SR;
        internal Transform CachedTransform;
        internal int PoolIndex;

        #endregion

        #region Physics State (flat fields, zero struct copies)

        private float _velX, _velY;
        private float _gravity;
        private float _drag;
        private float _turbStrength;
        private float _turbScale;
        private float _windX, _windY;
        private float _thermalLift;
        private float _perlinSeedX, _perlinSeedY;
        private float _rotSpeed;

        #endregion

        #region Lifecycle State

        private float _life;
        private float _maxLife;
        private float _startDelay;
        private bool _alive;

        #endregion

        #region Color State (decomposed — avoids Color struct in hot loop)

        private float _baseR, _baseG, _baseB, _baseA;

        #endregion

        #region Frame Stagger

        private int _frameSlot;
        private const int TurbInterval = 3;

        #endregion

        private void Awake()
        {
            CachedTransform = transform;
            _frameSlot = Mathf.Abs(GetInstanceID()) % TurbInterval;
        }

        /// <summary>
        /// Configures particle for new emission. Zero allocations.
        /// Must be called immediately after Pool.Get() in the same frame.
        ///
        /// CRITICAL: Revalidates SR.sharedMaterial against Pool.PoolMaterial
        /// on every init. Prevents corrupted shader references from vanilla
        /// chunk unloading causing invisible particles.
        /// Cost: one reference comparison per emit — negligible vs visual bugs.
        /// </summary>
        public void Initialize(
            Vector2 worldPos, Vector2 velocity, float lifetime, Color color,
            float scale, float gravity, float drag,
            float turbulenceStrength, float turbulenceScale,
            Vector2 wind, float thermalLift,
            float perlinSeed, float startDelay,
            Sprite sprite, int sortingOrder)
        {
            // Transform setup
            CachedTransform.position = new Vector3(worldPos.x, worldPos.y, 0f);
            CachedTransform.localScale = new Vector3(scale, scale, 1f);
            CachedTransform.rotation = Quaternion.identity;

            // Physics state
            _velX = velocity.x;
            _velY = velocity.y;
            _gravity = gravity;
            _drag = drag;
            _turbStrength = turbulenceStrength;
            _turbScale = turbulenceScale;
            _windX = wind.x;
            _windY = wind.y;
            _thermalLift = thermalLift;

            // Perlin seeds — deterministic per-particle variation
            _perlinSeedX = perlinSeed * 17.31f;
            _perlinSeedY = perlinSeed * 31.17f;
            _rotSpeed = (perlinSeed % 6.28f - 3.14f) * 60f;

            // Lifecycle
            _life = lifetime;
            _maxLife = lifetime;
            _startDelay = startDelay;

            // Color decomposition (avoids Color struct in Update hot loop)
            _baseR = color.r;
            _baseG = color.g;
            _baseB = color.b;
            _baseA = color.a;

            // WHY: Revalidate material on every Initialize().
            // Pool.PoolMaterial is updated by HealMaterial() when shader corruption
            // is detected. Without this check, particles reused from free stack
            // would still reference the old corrupted material.
            // PERF: Single reference comparison — branch predicted (almost always equal).
            if (Pool != null)
            {
                Material poolMat = Pool.PoolMaterial;
                if (poolMat != null && SR.sharedMaterial != poolMat)
                    SR.sharedMaterial = poolMat;
            }

            SR.sprite = sprite;
            SR.sortingOrder = sortingOrder;

            // Delayed particles start invisible
            if (_startDelay > 0f)
            {
                SR.enabled = false;
            }
            else
            {
                SR.enabled = true;
                SR.color = color;
            }

            _alive = true;
        }

        private void Update()
        {
            if (!_alive) return;

            // Start delay — invisible and motionless
            if (_startDelay > 0f)
            {
                _startDelay -= Time.deltaTime;
                if (_startDelay <= 0f)
                {
                    SR.enabled = true;
                    SR.color = new Color(_baseR, _baseG, _baseB, _baseA);
                }
                return;
            }

            // Lifetime check
            _life -= Time.deltaTime;
            if (_life <= 0f)
            {
                Recycle();
                return;
            }

            float dt = Time.deltaTime;
            float t = _life / _maxLife;     // 1→0 over lifetime
            float age = 1f - t;              // 0→1 over lifetime

            // Gravity
            _velY -= _gravity * dt;

            // Quadratic drag: F_drag ∝ v², applied as speed reduction
            float speedSqr = _velX * _velX + _velY * _velY;
            if (speedSqr > 0.0001f)
            {
                float speed = Mathf.Sqrt(speedSqr);
                float newSpeed = speed - _drag * speed * dt;
                if (newSpeed > 0f)
                {
                    float ratio = newSpeed / speed;
                    _velX *= ratio;
                    _velY *= ratio;
                }
                else
                {
                    _velX = 0f;
                    _velY = 0f;
                }
            }

            // Wind drift (increases with age²)
            if (_windX != 0f || _windY != 0f)
            {
                float windInf = age * age;
                _velX += _windX * windInf * dt;
                _velY += _windY * windInf * dt;
            }

            // Thermal lift (decays with t² — strongest at start)
            if (_thermalLift > 0f)
            {
                _velY += _thermalLift * (t * t) * dt;
            }

            // Perlin turbulence (frame-staggered for performance)
            // WHY: Computing Perlin every frame is expensive. Staggering across
            // 3 frames with ×3 compensation gives same visual result at 1/3 cost.
            if (Time.frameCount % TurbInterval == _frameSlot)
            {
                float time = Time.time * _turbScale;
                float px = Mathf.PerlinNoise(time + _perlinSeedX, _perlinSeedY) - 0.5f;
                float py = Mathf.PerlinNoise(_perlinSeedX, time + _perlinSeedY) - 0.5f;
                float turbMult = _turbStrength * 2f * dt * TurbInterval;
                _velX += px * turbMult;
                _velY += py * turbMult * 0.5f;  // Less vertical turbulence
            }

            // Position integration
            Vector3 pos = CachedTransform.position;
            pos.x += _velX * dt;
            pos.y += _velY * dt;
            CachedTransform.position = pos;

            // Rotation
            CachedTransform.Rotate(0f, 0f, _rotSpeed * dt);

            // Alpha fade (smoothstep for natural falloff)
            float alpha = t * t * (3f - 2f * t);
            SR.color = new Color(_baseR, _baseG, _baseB, _baseA * alpha);
        }

        /// <summary>
        /// Returns particle to pool. Called when lifetime expires.
        /// </summary>
        private void Recycle()
        {
            _alive = false;
            SR.enabled = false;
            gameObject.SetActive(false);
            Pool.Return(this);
        }

        /// <summary>
        /// Force-recycles without returning to pool. Used by pool eviction.
        /// Pool.StealOldest() handles the actual return.
        /// </summary>
        internal void ForceRecycle()
        {
            _alive = false;
            SR.enabled = false;
        }

        /// <summary>Whether this particle is currently active.</summary>
        internal bool IsAlive => _alive;
    }
}