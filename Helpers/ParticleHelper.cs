using UnityEngine;

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
    }

    /// <summary>
    /// Helper for spawning visual particles with AshParticle component.
    /// Centralizes common particle creation pattern to eliminate duplication.
    /// All spawned particles are registered in <see cref="DebrisTracker"/>.
    /// </summary>
    public static class ParticleHelper
    {
        /// <summary>
        /// Spawns a visual particle with AshParticle physics.
        /// Single unified method replacing duplicated spawn code across files.
        /// </summary>
        /// <param name="name">GameObject name for debugging.</param>
        /// <param name="position">World position.</param>
        /// <param name="visual">Visual parameters (scale, color, sorting, shape).</param>
        /// <param name="physics">Physics parameters (velocity, gravity, drag, etc.).</param>
        /// <param name="perlinSeed">Unique seed for Perlin turbulence.</param>
        /// <returns>Created GameObject, or null if material unavailable.</returns>
        public static GameObject SpawnAshParticle(
            string name,
            Vector2 position,
            in VisualParticleParams visual,
            in AshPhysicsParams physics,
            float perlinSeed)
        {
            Material mat = ShrapnelVisuals.UnlitMaterial;
            if (mat == null) return null;

            GameObject obj = new GameObject(name);
            obj.transform.position = position;
            obj.transform.localScale = Vector3.one * visual.Scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(visual.Shape);
            sr.sharedMaterial = mat;
            sr.sortingOrder = visual.SortingOrder;
            sr.color = visual.Color;

            AshParticle ash = obj.AddComponent<AshParticle>();
            ash.InitializeFull(
                physics.Velocity,
                physics.Lifetime,
                visual.Color,
                physics.Gravity,
                physics.Drag,
                physics.TurbulenceStrength,
                physics.TurbulenceScale,
                physics.Wind,
                physics.ThermalLift,
                perlinSeed);

            // WHY: RegisterVisual instead of Register — separate pool
            DebrisTracker.RegisterVisual(obj);
            return obj;
        }

        /// <summary>
        /// Spawns multiple particles in a burst pattern.
        /// Optimized batch version to reduce per-call overhead.
        /// </summary>
        /// <param name="namePrefix">Prefix for GameObject names.</param>
        /// <param name="epicenter">Center position for spawn.</param>
        /// <param name="count">Number of particles to spawn.</param>
        /// <param name="spawnRadius">Random offset radius from epicenter.</param>
        /// <param name="visualFactory">Function to create visual params per particle.</param>
        /// <param name="physicsFactory">Function to create physics params per particle.</param>
        /// <param name="rng">Deterministic random generator.</param>
        public static void SpawnBurst(
            string namePrefix,
            Vector2 epicenter,
            int count,
            float spawnRadius,
            System.Func<int, System.Random, VisualParticleParams> visualFactory,
            System.Func<int, System.Random, AshPhysicsParams> physicsFactory,
            System.Random rng)
        {
            Material mat = ShrapnelVisuals.UnlitMaterial;
            if (mat == null) return;

            for (int i = 0; i < count; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * spawnRadius;
                Vector2 position = epicenter + offset;

                var visual = visualFactory(i, rng);
                var physics = physicsFactory(i, rng);
                float perlinSeed = rng.Range(0f, 100f);

                SpawnAshParticle(namePrefix, position, visual, physics, perlinSeed);
            }
        }
    }
}