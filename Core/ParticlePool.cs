using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// GPU-batched particle pool using Unity ParticleSystem.
    /// Replaces hundreds of individual GameObjects (AshParticle/VisualShrapnel)
    /// with a single ParticleSystem that handles rendering via GPU instancing.
    ///
    /// Two pool types:
    ///   Additive (sparks, embers, fire) — SrcAlpha+One blending, glow effect
    ///   Debris (dirt, dust, smoke) — SrcAlpha+OneMinusSrcAlpha, no glow
    ///
    /// CRITICAL RULES:
    /// 1. Shape module DISABLED — positions set via EmitParams
    /// 2. EmitParams.position is LOCAL to ParticleSystem transform
    ///    even when simulationSpace=World. Calculate: worldPos - ps.transform.position
    /// 3. Uses particle-compatible shaders, NEVER sprite shaders
    /// </summary>
    public sealed class ParticlePool
    {
        private readonly GameObject _root;
        private readonly ParticleSystem _ps;
        private readonly Transform _transform;

        /// <summary>Underlying ParticleSystem for advanced access.</summary>
        public ParticleSystem System => _ps;

        /// <summary>Current alive particle count.</summary>
        public int AliveCount => _ps.particleCount;

        /// <summary>
        /// Creates a particle pool with specified material and max capacity.
        /// </summary>
        /// <param name="name">GameObject name for debugging.</param>
        /// <param name="material">Must use particle-compatible shader. NOT sprite shader.</param>
        /// <param name="maxParticles">Maximum concurrent particles.</param>
        /// <param name="sortingOrder">Renderer sorting order.</param>
        public ParticlePool(string name, Material material, int maxParticles,
            int sortingOrder = 10)
        {
            _root = new GameObject($"ParticlePool_{name}");
            Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _transform = _root.transform;

            _ps = _root.AddComponent<ParticleSystem>();

            //  Main module 
            var main = _ps.main;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = false;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.startLifetime = 5f;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.simulationSpeed = 1f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            //  Emission module — DISABLED (we emit manually) 
            var emission = _ps.emission;
            emission.enabled = false;

            //  Shape module — DISABLED (critical: prevents random offset) 
            var shape = _ps.shape;
            shape.enabled = false;

            //  Renderer 
            var renderer = _root.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = material;
            renderer.sortingOrder = sortingOrder;
            renderer.minParticleSize = 0f;
            renderer.maxParticleSize = 10f;

            // Start the system (needed for Emit to work)
            _ps.Play();
        }

        private int _debugCount; // temp field

        /// <summary>
        /// Emits a single particle at world position with full control.
        /// </summary>
        /// <param name="worldPos">World-space position.</param>
        /// <param name="velocity">World-space velocity.</param>
        /// <param name="size">Particle size in world units.</param>
        /// <param name="lifetime">Seconds before particle dies.</param>
        /// <param name="color">Start color (RGBA).</param>
        public void Emit(Vector2 worldPos, Vector2 velocity, float size,
            float lifetime, Color32 color)
        {
            if (_debugCount++ < 5)
                Debug.Log($"[{_root.name}] vel=({velocity.x:F1},{velocity.y:F1}) " +
                          $"size={size:F3} life={lifetime:F1} color={color}");

            var ep = new ParticleSystem.EmitParams();

            // CRITICAL: EmitParams.position is LOCAL to ParticleSystem transform,
            // even when simulationSpace = World. World simulation only means
            // particles don't move with parent after emission.
            Vector3 psPos = _transform.position;
            ep.position = new Vector3(worldPos.x - psPos.x, worldPos.y - psPos.y, 0f);

            ep.velocity = new Vector3(velocity.x, velocity.y, 0f);
            ep.startSize = size;
            ep.startLifetime = lifetime;
            ep.startColor = color;

            // Random rotation for visual variety
            ep.rotation = Random.Range(0f, 360f);
            ep.angularVelocity = Random.Range(-180f, 180f);

            _ps.Emit(ep, 1);
        }

        public void Emit(Vector2 worldPos, Vector2 velocity, float size,
            float lifetime, Color32 color, System.Random rng)
        {
            var ep = new ParticleSystem.EmitParams();
            Vector3 psPos = _transform.position;
            ep.position = new Vector3(worldPos.x - psPos.x, worldPos.y - psPos.y, 0f);
            ep.velocity = new Vector3(velocity.x, velocity.y, 0f);
            ep.startSize = size;
            ep.startLifetime = lifetime;
            ep.startColor = color;

            if (rng != null)
            {
                ep.rotation = (float)(rng.NextDouble() * 360.0);
                ep.angularVelocity = (float)(rng.NextDouble() * 360.0 - 180.0);
            }
            else
            {
                ep.rotation = Random.Range(0f, 360f);
                ep.angularVelocity = Random.Range(-180f, 180f);
            }

            _ps.Emit(ep, 1);
        }

        public void EmitDelayed(Vector2 worldPos, Vector2 velocity, float size,
            float lifetime, Color32 color, float delay, System.Random rng)
        {
            // Extend lifetime to include delay. Color-over-lifetime handles fade.
            Emit(worldPos, velocity, size, lifetime + delay, color, rng);
        }

        /// <summary>Clears all alive particles.</summary>
        public void Clear()
        {
            _ps.Clear();
        }

        /// <summary>Destroys the pool GameObject.</summary>
        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}