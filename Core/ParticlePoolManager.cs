using System;
using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Manages ONLY the Spark pool (Unity ParticleSystem).
    /// Debris/Glow handled by AshParticlePoolManager.
    ///
    /// CRITICAL: Update() must be called every frame to keep
    /// ParticleSystem near camera (prevents frustum culling).
    /// </summary>
    public static class ParticlePoolManager
    {
        private static ParticlePool _sparkPool;
        private static bool _initialized;
        private static GameObject _updater;

        public static ParticlePool Spark => _sparkPool;
        public static bool Initialized => _initialized;

        public static bool EnsureReady()
        {
            if (_initialized) return true;
            Initialize();
            return _initialized;
        }

        public static void Initialize()
        {
            Shutdown();

            try
            {
                ShrapnelVisuals.PreWarm();

                Material additiveMat = ShrapnelVisuals.AdditiveParticleMaterial;
                if (additiveMat == null)
                {
                    Console.Error("[SparkPool] Additive material not ready");
                    return;
                }

                int sparkMax = ShrapnelConfig.PoolSparkMaxParticles.Value;
                _sparkPool = new ParticlePool("Spark", additiveMat, sparkMax, 14);
                ConfigureSparkPool(_sparkPool.System);

                // Create updater MonoBehaviour for FollowCamera
                _updater = new GameObject("SparkPoolUpdater");
                UnityEngine.Object.DontDestroyOnLoad(_updater);
                _updater.hideFlags = HideFlags.HideAndDontSave;
                _updater.AddComponent<SparkPoolUpdater>();

                _initialized = true;
                Console.Log($"[SparkPool] Ready: capacity={sparkMax}");
            }
            catch (Exception e)
            {
                Console.Error($"[SparkPool] Init failed: {e.Message}");
                Shutdown();
            }
        }

        public static void Shutdown()
        {
            _sparkPool?.Destroy();
            _sparkPool = null;
            if (_updater != null)
                UnityEngine.Object.Destroy(_updater);
            _updater = null;
            _initialized = false;
        }

        public static void ClearAll()
        {
            _sparkPool?.Clear();
        }

        /// <summary>
        /// Called every frame by SparkPoolUpdater.
        /// Keeps ParticleSystem positioned at camera to prevent culling.
        /// </summary>
        internal static void Update()
        {
            _sparkPool?.FollowCamera();
        }

        private static void ConfigureSparkPool(ParticleSystem ps)
        {
            var main = ps.main;
            main.gravityModifier = 0.3f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.05f, 0.5f);

            var vel = ps.velocityOverLifetime;
            vel.enabled = false;

            var inherit = ps.inheritVelocity;
            inherit.enabled = false;

            var noise = ps.noise;
            noise.enabled = false;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = false;

            // Color fade
            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.8f, 0.3f),
                        new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);

            // Stretch renderer for spark trails
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2f;
            renderer.velocityScale = 0.05f;

            // Size shrink over lifetime
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(1f, 0.2f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);
        }
    }

    /// <summary>
    /// MonoBehaviour that calls ParticlePoolManager.Update() every frame.
    /// Ensures ParticleSystem follows camera for proper rendering.
    /// </summary>
    internal sealed class SparkPoolUpdater : MonoBehaviour
    {
        private void LateUpdate()
        {
            ParticlePoolManager.Update();
        }
    }
}