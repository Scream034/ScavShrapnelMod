using System;
using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Manages AshParticle object pools. Singleton lifetime tied to world.
    ///
    /// Two pools:
    ///   Lit   — Sprite-Lit-Default: debris, dust, smoke, ash, steam
    ///   Unlit — Sprite-Unlit-Default: embers, fire, glow
    ///
    /// Initialization is lazy — EnsureReady() called before first use.
    /// Material heal triggered by SparkPoolUpdater every 60 frames.
    /// </summary>
    public static class AshParticlePoolManager
    {
        private static AshParticlePool _litPool;
        private static AshParticlePool _unlitPool;
        private static bool _initialized;

        public static AshParticlePool Lit => _litPool;
        public static AshParticlePool Unlit => _unlitPool;
        public static bool Initialized => _initialized;

        /// <summary>
        /// Lazy initializer — guarantees pools are ready before use.
        /// Safe to call every frame — returns immediately if already initialized.
        /// </summary>
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
                // Warm materials first
                ShrapnelVisuals.PreWarm();

                Material litMat = ShrapnelVisuals.LitMaterial
                               ?? ShrapnelVisuals.UnlitMaterial;
                Material unlitMat = ShrapnelVisuals.UnlitMaterial;

                if (litMat == null)
                {
                    Console.Error("[AshPool] No materials available");
                    return;
                }
                if (unlitMat == null)
                    unlitMat = litMat;

                int litCap = ShrapnelConfig.PoolDebrisMaxParticles.Value;
                int unlitCap = ShrapnelConfig.PoolGlowMaxParticles.Value;

                _litPool = new AshParticlePool("Lit", litMat, litCap);
                _unlitPool = new AshParticlePool("Unlit", unlitMat, unlitCap);

                _initialized = true;
                Console.Log($"[AshPool] Ready: Lit={litCap} Unlit={unlitCap}");
            }
            catch (Exception e)
            {
                Console.Error($"[AshPool] Init failed: {e.Message}");
                Shutdown();
            }
        }

        public static void Shutdown()
        {
            _litPool?.Destroy();
            _unlitPool?.Destroy();
            _litPool = null;
            _unlitPool = null;
            _initialized = false;
        }

        public static void ClearAll()
        {
            _litPool?.Clear();
            _unlitPool?.Clear();
        }

        public static int TotalActive =>
            (_litPool?.ActiveCount ?? 0) + (_unlitPool?.ActiveCount ?? 0);

        public static string GetStats()
        {
            if (!_initialized) return "AshPools:OFF";
            return $"Lit:{_litPool.ActiveCount}/{_litPool.Capacity}" +
                   $" Unlit:{_unlitPool.ActiveCount}/{_unlitPool.Capacity}";
        }

        /// <summary>
        /// Periodic material heal — called from SparkPoolUpdater every 60 frames.
        /// 
        /// WHY: Vanilla chunk unloading can destroy shader references on materials
        /// created via Shader.Find(). This re-fetches materials from ShrapnelVisuals
        /// (which re-creates them if shader is null) and propagates to pools.
        /// 
        /// IMPORTANT: ShrapnelVisuals.LitMaterial/UnlitMaterial getters internally
        /// check if shader is null and re-create the material. So calling them here
        /// forces fresh material creation when corruption is detected.
        /// </summary>
        public static void HealMaterials()
        {
            if (!_initialized) return;

            // WHY: Accessing the properties triggers ShrapnelVisuals to re-create
            // materials if their shaders were corrupted. This is the fix —
            // previously HealMaterials() was defined but never called from anywhere.
            Material freshLit = ShrapnelVisuals.LitMaterial
                             ?? ShrapnelVisuals.UnlitMaterial;
            Material freshUnlit = ShrapnelVisuals.UnlitMaterial;

            _litPool?.HealMaterial(freshLit);
            _unlitPool?.HealMaterial(freshUnlit);
        }
    }
}