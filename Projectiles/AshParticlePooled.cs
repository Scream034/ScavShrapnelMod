using UnityEngine;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Pooled ash particle with zero-GC lifecycle.
    ///
    /// Physics model (identical to original AshParticle):
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
    ///   - Decomposed Vector2/Color → float fields (no struct copies)
    ///   - PoolIndex stored in particle → O(1) pool operations
    ///   - Frame-staggered Perlin (every 3 frames, ×3 compensation)
    ///
    /// Lifecycle:
    ///   Pool.Get() → SetActive(true) → Initialize() → Update → Recycle() → SetActive(false) → Pool
    /// </summary>
    public sealed class AshParticlePooled : MonoBehaviour
    {
        //  SET ONCE AT POOL CREATION — never changes
        internal AshParticlePool Pool;
        internal SpriteRenderer SR;
        internal Transform CachedTransform;
        internal int PoolIndex;

        //  PHYSICS STATE (flat fields, zero struct copies)
        private float _velX, _velY;
        private float _gravity;
        private float _drag;
        private float _turbStrength;
        private float _turbScale;
        private float _windX, _windY;
        private float _thermalLift;
        private float _perlinSeedX, _perlinSeedY;
        private float _rotSpeed;

        //  LIFECYCLE
        private float _life;
        private float _maxLife;
        private float _startDelay;
        private bool _alive;

        //  COLOR (decomposed — avoids Color struct in hot loop)
        private float _baseR, _baseG, _baseB, _baseA;

        //  FRAME STAGGER
        private int _frameSlot;
        private const int TurbInterval = 3;

        private void Awake()
        {
            CachedTransform = transform;
            _frameSlot = Mathf.Abs(GetInstanceID()) % TurbInterval;
        }

        /// <summary>
        /// Configures particle for new emission. Zero allocations.
        /// Must be called immediately after Pool.Get() in the same frame.
        /// </summary>
        public void Initialize(
            Vector2 worldPos, Vector2 velocity, float lifetime, Color color,
            float scale, float gravity, float drag,
            float turbulenceStrength, float turbulenceScale,
            Vector2 wind, float thermalLift,
            float perlinSeed, float startDelay,
            Sprite sprite, int sortingOrder)
        {
            CachedTransform.position = new Vector3(worldPos.x, worldPos.y, 0f);
            CachedTransform.localScale = new Vector3(scale, scale, 1f);
            CachedTransform.rotation = Quaternion.identity;

            _velX = velocity.x;
            _velY = velocity.y;
            _gravity = gravity;
            _drag = drag;
            _turbStrength = turbulenceStrength;
            _turbScale = turbulenceScale;
            _windX = wind.x;
            _windY = wind.y;
            _thermalLift = thermalLift;

            _perlinSeedX = perlinSeed * 17.31f;
            _perlinSeedY = perlinSeed * 31.17f;
            _rotSpeed = (perlinSeed % 6.28f - 3.14f) * 60f;

            _life = lifetime;
            _maxLife = lifetime;
            _startDelay = startDelay;

            _baseR = color.r;
            _baseG = color.g;
            _baseB = color.b;
            _baseA = color.a;

            SR.sprite = sprite;
            SR.sortingOrder = sortingOrder;

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

            //  Start delay — invisible and motionless
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

            //  Lifetime
            _life -= Time.deltaTime;
            if (_life <= 0f)
            {
                Recycle();
                return;
            }

            float dt = Time.deltaTime;
            float t = _life / _maxLife;     // 1→0
            float age = 1f - t;              // 0→1

            //  Gravity
            _velY -= _gravity * dt;

            //  Quadratic drag
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

            //  Wind drift (increases with age²)
            if (_windX != 0f || _windY != 0f)
            {
                float windInf = age * age;
                _velX += _windX * windInf * dt;
                _velY += _windY * windInf * dt;
            }

            //  Thermal lift (decays with t²)
            if (_thermalLift > 0f)
            {
                _velY += _thermalLift * (t * t) * dt;
            }

            //  Perlin turbulence (frame-staggered)
            if (Time.frameCount % TurbInterval == _frameSlot)
            {
                float time = Time.time * _turbScale;
                float px = Mathf.PerlinNoise(time + _perlinSeedX, _perlinSeedY) - 0.5f;
                float py = Mathf.PerlinNoise(_perlinSeedX, time + _perlinSeedY) - 0.5f;
                float turbMult = _turbStrength * 2f * dt * TurbInterval;
                _velX += px * turbMult;
                _velY += py * turbMult * 0.5f;
            }

            //  Position
            Vector3 pos = CachedTransform.position;
            pos.x += _velX * dt;
            pos.y += _velY * dt;
            CachedTransform.position = pos;

            //  Rotation
            CachedTransform.Rotate(0f, 0f, _rotSpeed * dt);

            //  Alpha fade (smoothstep)
            float alpha = t * t * (3f - 2f * t);
            SR.color = new Color(_baseR, _baseG, _baseB, _baseA * alpha);
        }

        private void Recycle()
        {
            _alive = false;
            SR.enabled = false;
            gameObject.SetActive(false);
            Pool.Return(this);
        }

        internal void ForceRecycle()
        {
            _alive = false;
            SR.enabled = false;
        }

        internal bool IsAlive => _alive;
    }
}