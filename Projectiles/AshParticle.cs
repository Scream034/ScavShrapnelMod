using UnityEngine;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Realistic visual particle for ash, dust, debris, and steam.
    ///
    /// Physics model (transform-only, no Rigidbody):
    /// - Quadratic air drag (velocity-dependent resistance)
    /// - Perlin-noise turbulence (smooth, natural air currents)
    /// - Wind drift (increases with age as momentum fades)
    /// - Thermal lift (hot particles rise before settling)
    /// - Configurable gravity (negative = rises)
    ///
    /// Performance optimizations:
    /// - Frame-staggered turbulence (every 3 frames, ×3 compensation)
    /// - Cached component references
    /// - No allocations in Update
    /// </summary>
    public sealed class AshParticle : MonoBehaviour
    {
        // State
        private float _lifetime;
        private float _maxLifetime;
        private Vector3 _velocity;
        private Color _baseColor;

        // Physics params (set once)
        private float _gravity;
        private float _drag;
        private float _turbulenceStrength;
        private float _turbulenceScale;
        private Vector2 _wind;
        private float _thermalLift;
        private float _perlinSeedX;
        private float _perlinSeedY;
        private float _rotationSpeed;

        // Cached components
        private SpriteRenderer _sr;
        private Transform _transform;

        // PERF: Frame-staggering for Perlin noise
        private int _frameSlot;
        private const int TurbulenceInterval = 3;

        /// <summary>Default gravity for standard ash.</summary>
        private const float DefaultGravity = 1.5f;

        /// <summary>
        /// Basic initialization (backward compatible).
        /// Derives drag/turbulence from gravity automatically.
        /// </summary>
        public void Initialize(Vector2 velocity, float lifetime, Color color, float wobblePhase)
        {
            Initialize(velocity, lifetime, color, wobblePhase, DefaultGravity);
        }

        /// <summary>
        /// Initialization with custom gravity (backward compatible).
        /// </summary>
        public void Initialize(Vector2 velocity, float lifetime, Color color,
            float wobblePhase, float gravity)
        {
            float normalizedGravity = Mathf.Clamp01(Mathf.Abs(gravity) / 2f);
            float derivedDrag = Mathf.Lerp(0.8f, 0.1f, normalizedGravity);
            float derivedTurb = Mathf.Lerp(1.2f, 0.2f, normalizedGravity);

            InitializeFull(velocity, lifetime, color, gravity, derivedDrag,
                derivedTurb, 2.0f, Vector2.zero, 0f, wobblePhase);
        }

        /// <summary>
        /// Full initialization with all realistic physics parameters.
        /// </summary>
        /// <param name="velocity">Initial velocity vector (m/s).</param>
        /// <param name="lifetime">Time to live (seconds).</param>
        /// <param name="color">Base RGBA color.</param>
        /// <param name="gravity">Gravity acceleration. Negative = rises.</param>
        /// <param name="drag">Air resistance. 0=vacuum, 1=thick atmosphere.</param>
        /// <param name="turbulenceStrength">Random air current amplitude.</param>
        /// <param name="turbulenceScale">Spatial frequency of Perlin noise.</param>
        /// <param name="wind">Constant wind vector. Applied increasingly with age.</param>
        /// <param name="thermalLift">Upward thermal force. Decays over lifetime.</param>
        /// <param name="perlinSeed">Unique seed for Perlin noise offset.</param>
        public void InitializeFull(Vector2 velocity, float lifetime, Color color,
            float gravity, float drag, float turbulenceStrength, float turbulenceScale,
            Vector2 wind, float thermalLift, float perlinSeed)
        {
            _velocity = velocity;
            _lifetime = lifetime;
            _maxLifetime = lifetime;
            _baseColor = color;
            _gravity = gravity;
            _drag = drag;
            _turbulenceStrength = turbulenceStrength;
            _turbulenceScale = turbulenceScale;
            _wind = wind;
            _thermalLift = thermalLift;

            // WHY: Unique Perlin seeds prevent synchronized swarm movement
            _perlinSeedX = perlinSeed * 17.31f;
            _perlinSeedY = perlinSeed * 31.17f;
            _rotationSpeed = (perlinSeed % 6.28f - 3.14f) * 60f;

            // Cache components
            _transform = transform;
            _sr = GetComponent<SpriteRenderer>();
            if (_sr != null) _sr.color = color;

            // PERF: Distribute turbulence calculations across frames
            _frameSlot = Mathf.Abs(GetInstanceID()) % TurbulenceInterval;
        }

        private void Update()
        {
            _lifetime -= Time.deltaTime;
            if (_lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;
            float t = _lifetime / _maxLifetime; // 1→0 over lifetime
            float age = 1f - t;                  // 0→1 over lifetime

            //  Gravity 
            _velocity.y -= _gravity * dt;

            //  Quadratic air drag 
            // WHY: F_drag ∝ v² is physically accurate for particles in air
            float speedSqr = _velocity.x * _velocity.x + _velocity.y * _velocity.y;
            if (speedSqr > 0.0001f)
            {
                float speed = Mathf.Sqrt(speedSqr);
                float dragDecel = _drag * speed * dt;
                float newSpeed = speed - dragDecel;
                if (newSpeed > 0f)
                {
                    float ratio = newSpeed / speed;
                    _velocity.x *= ratio;
                    _velocity.y *= ratio;
                }
                else
                {
                    _velocity.x = 0f;
                    _velocity.y = 0f;
                }
            }

            //  Wind drift (increases with age²) 
            if (_wind.x != 0f || _wind.y != 0f)
            {
                float windInfluence = age * age;
                _velocity.x += _wind.x * windInfluence * dt;
                _velocity.y += _wind.y * windInfluence * dt;
            }

            //  Thermal lift (decays with age) 
            if (_thermalLift > 0f)
            {
                float liftFactor = t * t;
                _velocity.y += _thermalLift * liftFactor * dt;
            }

            //  Perlin turbulence (frame-staggered) 
            // PERF: Calculate every TurbulenceInterval frames, multiply effect by interval
            // Reduces Perlin calls by ~66% with minimal visual difference
            if (Time.frameCount % TurbulenceInterval == _frameSlot)
            {
                float time = Time.time * _turbulenceScale;
                float px = Mathf.PerlinNoise(time + _perlinSeedX, _perlinSeedY) - 0.5f;
                float py = Mathf.PerlinNoise(_perlinSeedX, time + _perlinSeedY) - 0.5f;

                float turbMult = _turbulenceStrength * 2f * dt * TurbulenceInterval;
                _velocity.x += px * turbMult;
                _velocity.y += py * turbMult * 0.5f;
            }

            //  Apply movement 
            Vector3 pos = _transform.position;
            pos.x += _velocity.x * dt;
            pos.y += _velocity.y * dt;
            _transform.position = pos;

            //  Visual rotation 
            _transform.Rotate(0f, 0f, _rotationSpeed * dt);

            //  Fade out (smoothstep) 
            float alpha = t * t * (3f - 2f * t);
            _sr.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, _baseColor.a * alpha);
        }
    }
}