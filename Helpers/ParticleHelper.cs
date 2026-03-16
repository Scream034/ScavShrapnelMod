using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Parameters for visual particle spawning.
    /// Immutable struct to avoid allocations.
    /// </summary>
    public readonly struct VisualParticleParams
    {
        public readonly float Scale;
        public readonly Color Color;
        public readonly int SortingOrder;
        public readonly ShrapnelVisuals.TriangleShape Shape;

        public VisualParticleParams(float scale, Color color, int sortingOrder,
            ShrapnelVisuals.TriangleShape shape = ShrapnelVisuals.TriangleShape.Chunk)
        {
            Scale = scale;
            Color = color;
            SortingOrder = sortingOrder;
            Shape = shape;
        }
    }

    /// <summary>
    /// Parameters for AshParticle physics initialization.
    /// Immutable struct for clean parameter passing.
    /// </summary>
    public readonly struct AshPhysicsParams
    {
        public readonly Vector2 Velocity;
        public readonly float Lifetime;
        public readonly float Gravity;
        public readonly float Drag;
        public readonly float TurbulenceStrength;
        public readonly float TurbulenceScale;
        public readonly Vector2 Wind;
        public readonly float ThermalLift;

        public AshPhysicsParams(
            Vector2 velocity,
            float lifetime,
            float gravity = 1.5f,
            float drag = 0.5f,
            float turbulenceStrength = 0.5f,
            float turbulenceScale = 2f,
            Vector2 wind = default,
            float thermalLift = 0f)
        {
            Velocity = velocity;
            Lifetime = lifetime;
            Gravity = gravity;
            Drag = drag;
            TurbulenceStrength = turbulenceStrength;
            TurbulenceScale = turbulenceScale;
            Wind = wind;
            ThermalLift = thermalLift;
        }

        /// <summary>Creates params for ground chunks: low gravity, moderate drag.</summary>
        public static AshPhysicsParams Chunk(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: 0.3f,
                drag: 0.4f,
                turbulenceStrength: 0.3f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.1f, 0.1f), 0f),
                thermalLift: 0f);
        }

        /// <summary>Creates params for dust: very low gravity, high turbulence, lingers.</summary>
        public static AshPhysicsParams Dust(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: 0.15f,
                drag: 0.7f,
                turbulenceStrength: 0.9f,
                turbulenceScale: 2.0f,
                wind: new Vector2(rng.Range(-0.15f, 0.15f), 0f),
                thermalLift: 0.1f);
        }

        /// <summary>Creates params for streaks: fast, low drag, minimal turbulence.</summary>
        public static AshPhysicsParams Streak(Vector2 velocity, float lifetime)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: 0.8f,
                drag: 0.2f,
                turbulenceStrength: 0.15f,
                turbulenceScale: 1.0f,
                wind: Vector2.zero,
                thermalLift: 0f);
        }

        /// <summary>Creates params for desert dust: very low gravity, long-lasting.</summary>
        public static AshPhysicsParams DesertDust(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: 0.08f,
                drag: 0.6f,
                turbulenceStrength: 0.8f,
                turbulenceScale: 1.5f,
                wind: new Vector2(rng.Range(-0.3f, 0.3f), 0f),
                thermalLift: 0.3f);
        }

        /// <summary>Creates params for cold steam: negative gravity (rises), high turbulence.</summary>
        public static AshPhysicsParams ColdSteam(Vector2 velocity, float lifetime, System.Random rng)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: -0.15f,
                drag: 0.5f,
                turbulenceStrength: 1.0f,
                turbulenceScale: 2.5f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0.1f),
                thermalLift: 0.5f);
        }

        /// <summary>Creates params for standard ash with automatic derivation from gravity.</summary>
        public static AshPhysicsParams Ash(Vector2 velocity, float lifetime, float gravity, System.Random rng)
        {
            float normalizedGravity = Mathf.Clamp01(Mathf.Abs(gravity) / 2f);
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: gravity,
                drag: Mathf.Lerp(0.7f, 0.3f, normalizedGravity),
                turbulenceStrength: Mathf.Lerp(0.9f, 0.3f, normalizedGravity),
                turbulenceScale: 2f,
                wind: new Vector2(rng.Range(-0.2f, 0.2f), 0f),
                thermalLift: rng.Range(0f, 0.3f));
        }

        /// <summary>Creates params for smoke: negative gravity (rises), high drag.</summary>
        public static AshPhysicsParams Smoke(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind,
            float thermalLift)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: gravity,
                drag: drag,
                turbulenceStrength: turbulence,
                turbulenceScale: 2f,
                wind: wind,
                thermalLift: thermalLift);
        }

        /// <summary>Creates params for embers: moderate gravity, low drag.</summary>
        public static AshPhysicsParams Ember(Vector2 velocity, float lifetime,
            float gravity, float drag, float turbulence, Vector2 wind,
            float thermalLift)
        {
            return new AshPhysicsParams(
                velocity, lifetime,
                gravity: gravity,
                drag: drag,
                turbulenceStrength: turbulence,
                turbulenceScale: 1f,
                wind: wind,
                thermalLift: thermalLift);
        }
    }

    /// <summary>
    /// Parameters for VisualShrapnel (transform-driven sparks, no physics).
    /// Immutable struct for zero-alloc parameter passing.
    /// </summary>
    public readonly struct SparkParams
    {
        public readonly Vector2 Direction;
        public readonly float Speed;
        public readonly float Lifetime;

        public SparkParams(Vector2 direction, float speed, float lifetime)
        {
            Direction = direction;
            Speed = speed;
            Lifetime = lifetime;
        }
    }

    /// <summary>
    /// Parameters for emission glow on particles.
    /// Null-equivalent: use <see cref="None"/> to skip emission.
    /// </summary>
    public readonly struct EmissionParams
    {
        /// <summary>Emission color (HDR, values > 1 for bloom).</summary>
        public readonly Color Color;
        /// <summary>Whether to apply emission.</summary>
        public readonly bool Enabled;

        public EmissionParams(Color color)
        {
            Color = color;
            Enabled = true;
        }

        /// <summary>No emission — skip SetPropertyBlock call.</summary>
        public static readonly EmissionParams None = default;
    }

    /// <summary>
    /// Centralized factory for all visual particles.
    ///
    /// Material selection:
    ///   LitMaterial   — inert debris (rocks, dirt, ash, smoke, metal fragments).
    ///                   Dark in dark areas. Respects game lighting.
    ///   UnlitMaterial — self-luminous effects (fire, sparks, embers, tracers).
    ///                   Always visible regardless of scene lighting.
    ///
    /// Two particle types:
    ///   AshParticle     — physics-driven (gravity, drag, turbulence, wind).
    ///                     For: smoke, dust, debris, ash, embers, steam.
    ///   VisualShrapnel  — transform-driven (linear movement, no physics).
    ///                     For: sparks, streak sparks, tracer lines.
    ///
    /// All spawned particles are registered in <see cref="DebrisTracker"/>.
    /// </summary>
    public static class ParticleHelper
    {
        // PERF: Cached MaterialPropertyBlock — reused across all spawns.
        // Safe because Unity copies data during SetPropertyBlock.
        private static MaterialPropertyBlock _mpb;
        private static MaterialPropertyBlock MPB =>
            _mpb ?? (_mpb = new MaterialPropertyBlock());

        private static int _emissionId = -1;
        private static int EmissionColorId =>
            _emissionId == -1
                ? (_emissionId = Shader.PropertyToID("_EmissionColor"))
                : _emissionId;

        // ════════════════════════════════════════════════════════════
        //  ASH PARTICLE SPAWNERS (physics-driven)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a LIT AshParticle that respects the game's 2D lighting.
        /// Use for: ground debris, ash, smoke, dust, metal chips, wood splinters.
        /// Dark in dark areas. Falls back to UnlitMaterial if LitMaterial unavailable.
        /// </summary>
        public static GameObject SpawnLit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay = 0f)
        {
            // WHY: Fallback chain — LitMaterial → UnlitMaterial → null.
            // Better to see a glowing particle than no particle at all.
            Material mat = ShrapnelVisuals.LitMaterial
                        ?? ShrapnelVisuals.UnlitMaterial;
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, mat, EmissionParams.None);
        }

        /// <summary>
        /// Spawns a LIT AshParticle with emission glow.
        /// Use for: burning chunks, hot debris that glows but still receives lighting.
        /// </summary>
        public static GameObject SpawnLit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            in EmissionParams emission,
            float startDelay = 0f)
        {
            Material mat = ShrapnelVisuals.LitMaterial
                        ?? ShrapnelVisuals.UnlitMaterial;
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, mat, emission);
        }

        /// <summary>
        /// Spawns an UNLIT AshParticle that ignores scene lighting.
        /// Use for: fire, embers, muzzle flash — anything self-luminous.
        /// Always visible regardless of darkness.
        /// </summary>
        public static GameObject SpawnUnlit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay = 0f)
        {
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, ShrapnelVisuals.UnlitMaterial, EmissionParams.None);
        }

        /// <summary>
        /// Spawns an UNLIT AshParticle with emission glow.
        /// Use for: fire embers, hot sparks that need bloom effect.
        /// </summary>
        public static GameObject SpawnUnlit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            in EmissionParams emission,
            float startDelay = 0f)
        {
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, ShrapnelVisuals.UnlitMaterial, emission);
        }

        // ════════════════════════════════════════════════════════════
        //  VISUAL SHRAPNEL SPAWNERS (transform-driven sparks)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a LIT VisualShrapnel spark (linear movement, no physics).
        /// Use for: metal chips flying off impact, cold debris streaks.
        /// </summary>
        public static GameObject SpawnSparkLit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in SparkParams spark,
            in EmissionParams emission = default)
        {
            Material mat = ShrapnelVisuals.LitMaterial
                        ?? ShrapnelVisuals.UnlitMaterial;
            return SpawnVisualShrapnelCore(name, position, visual, spark, mat, emission);
        }

        /// <summary>
        /// Spawns an UNLIT VisualShrapnel spark (linear movement, no physics).
        /// Use for: hot sparks, streak sparks, tracer lines — self-luminous.
        /// </summary>
        public static GameObject SpawnSparkUnlit(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in SparkParams spark,
            in EmissionParams emission = default)
        {
            return SpawnVisualShrapnelCore(name, position, visual, spark,
                ShrapnelVisuals.UnlitMaterial, emission);
        }

        // ════════════════════════════════════════════════════════════
        //  LEGACY API (backward compatible)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns particle with UnlitMaterial (no delay). Legacy — backward compatible.
        /// Prefer SpawnLit() or SpawnUnlit() for new code.
        /// </summary>
        public static GameObject SpawnAshParticle(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed)
        {
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, 0f, ShrapnelVisuals.UnlitMaterial, EmissionParams.None);
        }

        /// <summary>
        /// Spawns particle with delay. Legacy — backward compatible.
        /// Uses UnlitMaterial.
        /// </summary>
        public static GameObject SpawnAshParticle(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay)
        {
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, ShrapnelVisuals.UnlitMaterial, EmissionParams.None);
        }

        /// <summary>
        /// Spawns particle with explicit material override.
        /// </summary>
        public static GameObject SpawnAshParticle(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay,
            Material overrideMaterial)
        {
            Material mat = overrideMaterial ?? ShrapnelVisuals.UnlitMaterial;
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, mat, EmissionParams.None);
        }

        // ════════════════════════════════════════════════════════════
        //  BURST SPAWN
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns multiple AshParticles in a burst pattern.
        /// Uses UnlitMaterial (legacy behavior for fire/sparks).
        /// </summary>
        public static void SpawnBurst(
            string namePrefix,
            Vector2 epicenter,
            int count,
            float spawnRadius,
            System.Func<int, System.Random, VisualParticleParams> visualFactory,
            System.Func<int, System.Random, AshPhysicsParams> physicsFactory,
            System.Random rng)
        {
            SpawnBurst(namePrefix, epicenter, count, spawnRadius,
                visualFactory, physicsFactory, rng, false);
        }

        /// <summary>
        /// Spawns multiple AshParticles in a burst pattern.
        /// </summary>
        /// <param name="useLit">True = LitMaterial (debris), False = UnlitMaterial (fire).</param>
        public static void SpawnBurst(
            string namePrefix,
            Vector2 epicenter,
            int count,
            float spawnRadius,
            System.Func<int, System.Random, VisualParticleParams> visualFactory,
            System.Func<int, System.Random, AshPhysicsParams> physicsFactory,
            System.Random rng,
            bool useLit)
        {
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
                Vector2 position = epicenter + offset;

                var visual = visualFactory(i, rng);
                var physics = physicsFactory(i, rng);
                float perlinSeed = rng.Range(0f, 100f);

                if (useLit)
                    SpawnLit(namePrefix, position, visual, physics, perlinSeed);
                else
                    SpawnUnlit(namePrefix, position, visual, physics, perlinSeed);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  CORE — AshParticle (all AshParticle paths converge here)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Core AshParticle creation. All public AshParticle methods route here.
        /// </summary>
        private static GameObject SpawnAshParticleCore(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay,
            Material mat)
        {
            return SpawnAshParticleCore(name, position, visual, physics,
                perlinSeed, startDelay, mat, EmissionParams.None);
        }

        /// <summary>
        /// Core AshParticle creation with emission support.
        /// </summary>
        private static GameObject SpawnAshParticleCore(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed,
            float startDelay,
            Material mat,
            in EmissionParams emission)
        {
            if (mat == null) return null;

            GameObject obj = new GameObject(name);
            obj.transform.position = position;
            obj.transform.localScale = Vector3.one * visual.Scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(visual.Shape);
            sr.sharedMaterial = mat;
            sr.sortingOrder = visual.SortingOrder;
            sr.color = visual.Color;

            if (emission.Enabled)
                ApplyEmission(sr, emission.Color);

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.InitializeFullDelayed(
                physics.Velocity,
                physics.Lifetime,
                visual.Color,
                physics.Gravity,
                physics.Drag,
                physics.TurbulenceStrength,
                physics.TurbulenceScale,
                physics.Wind,
                physics.ThermalLift,
                perlinSeed,
                startDelay);

            DebrisTracker.RegisterVisual(obj);
            return obj;
        }

        // ════════════════════════════════════════════════════════════
        //  CORE — VisualShrapnel (all spark paths converge here)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Core VisualShrapnel creation. Transform-driven linear movement.
        /// </summary>
        private static GameObject SpawnVisualShrapnelCore(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in SparkParams spark,
            Material mat,
            in EmissionParams emission)
        {
            if (mat == null) return null;

            GameObject obj = new GameObject(name);
            obj.transform.position = position;
            obj.transform.localScale = Vector3.one * visual.Scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(visual.Shape);
            sr.sharedMaterial = mat;
            sr.sortingOrder = visual.SortingOrder;
            sr.color = visual.Color;

            if (emission.Enabled)
                ApplyEmission(sr, emission.Color);

            VisualShrapnel vs = obj.AddComponent<VisualShrapnel>();
            vs.Initialize(spark.Direction, spark.Speed, spark.Lifetime);

            DebrisTracker.RegisterVisual(obj);
            return obj;
        }

        // ════════════════════════════════════════════════════════════
        //  EMISSION HELPER
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// <summary>
        /// Applies emission color via MaterialPropertyBlock.
        /// PERF: Reuses static MPB instance — Unity copies data in SetPropertyBlock.
        /// Shared by ParticleHelper, ShrapnelFactory, and ShrapnelProjectile.
        /// </summary>
        internal static void ApplyEmission(SpriteRenderer sr, Color emissionColor)
        {
            MPB.Clear();
            MPB.SetColor(EmissionColorId, emissionColor);
            sr.SetPropertyBlock(MPB);
        }

        /// <summary>
        /// Clears emission from a SpriteRenderer.
        /// </summary>
        internal static void ClearEmission(SpriteRenderer sr)
        {
            sr.SetPropertyBlock(null);
        }
    }
}