using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// GPU-batched particle pool using Unity ParticleSystem.
    /// Used ONLY for sparks (short-lived, simple trajectory).
    ///
    /// CRITICAL RULES:
    /// 1. Shape module DISABLED — positions set via EmitParams
    /// 2. EmitParams.position is LOCAL to ParticleSystem transform
    ///    even when simulationSpace=World
    /// 3. FollowCamera() must be called every frame to prevent culling
    /// 4. Uses particle-compatible shaders, NEVER sprite shaders
    /// </summary>
    public sealed class ParticlePool
    {
        private readonly GameObject _root;
        private readonly ParticleSystem _ps;
        private readonly ParticleSystemRenderer _renderer;
        private readonly Transform _transform;

        public ParticleSystem System => _ps;
        public int AliveCount => _ps.particleCount;

        public ParticlePool(string name, Material material, int maxParticles,
            int sortingOrder = 10)
        {
            _root = new GameObject($"ParticlePool_{name}");
            Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _transform = _root.transform;

            _ps = _root.AddComponent<ParticleSystem>();

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

            var emission = _ps.emission;
            emission.enabled = false;

            var shape = _ps.shape;
            shape.enabled = false;

            _renderer = _root.GetComponent<ParticleSystemRenderer>();
            _renderer.renderMode = ParticleSystemRenderMode.Billboard;
            _renderer.material = material;
            _renderer.sortingOrder = sortingOrder;
            _renderer.minParticleSize = 0f;
            _renderer.maxParticleSize = 10f;

            // Prevent frustum culling — particles spawn at world positions
            // but culling is based on renderer bounds at GameObject position
            _renderer.bounds = new Bounds(Vector3.zero, Vector3.one * 20000f);

            _ps.Play();
        }

        /// <summary>
        /// Moves pool to camera position to prevent frustum culling.
        /// simulationSpace=World means existing particles stay in place.
        /// Must be called every frame from SparkPoolUpdater.LateUpdate.
        /// </summary>
        public void FollowCamera()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 camPos = cam.transform.position;
                _transform.position = new Vector3(camPos.x, camPos.y, 0f);
            }
        }

        /// <summary>
        /// Emits a single particle at world position.
        /// Position is converted to local space relative to pool transform.
        /// </summary>
        public void Emit(Vector2 worldPos, Vector2 velocity, float size,
            float lifetime, Color32 color)
        {
            var ep = new ParticleSystem.EmitParams();

            Vector3 psPos = _transform.position;
            ep.position = new Vector3(worldPos.x - psPos.x, worldPos.y - psPos.y, 0f);
            ep.velocity = new Vector3(velocity.x, velocity.y, 0f);
            ep.startSize = size;
            ep.startLifetime = lifetime;
            ep.startColor = color;
            ep.rotation = Random.Range(0f, 360f);
            ep.angularVelocity = Random.Range(-180f, 180f);

            _ps.Emit(ep, 1);
        }

        public void Clear() => _ps.Clear();

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}