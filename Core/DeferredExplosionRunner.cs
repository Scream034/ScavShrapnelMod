using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Defers shrapnel spawning to the next frame.
    /// WHY: When spawning shrapnel inside GameObject.Destroy Prefix,
    /// Unity is mid-destruction and rendering/physics may be unstable.
    /// </summary>
    public sealed class DeferredExplosionRunner : MonoBehaviour
    {
        private static DeferredExplosionRunner _instance;
        private static readonly Queue<PendingExplosion> _queue = new Queue<PendingExplosion>();

        public static void EnsureExists()
        {
            if (_instance != null) return;

            var go = new GameObject("ShrapnelMod_DeferredRunner")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DeferredExplosionRunner>();
        }

        /// <summary>
        /// Queues a shrapnel explosion for next frame.
        /// Does NOT call ExplosionTracker — the caller handles tracking.
        /// </summary>
        public static void Enqueue(Vector2 position, float range,
            float damage, float velocity, string source)
        {
            EnsureExists();

            _queue.Enqueue(new PendingExplosion
            {
                Position = position,
                Range = range,
                Damage = damage,
                Velocity = velocity,
                Source = source,
                Frame = Time.frameCount
            });

            Plugin.Log.LogInfo($"[Deferred] Queued {source} at {position:F1} (frame {Time.frameCount})");
        }

        private void Update()
        {
            if (_queue.Count == 0) return;

            int currentFrame = Time.frameCount;

            // Process explosions from PREVIOUS frames only
            while (_queue.Count > 0 && _queue.Peek().Frame < currentFrame)
            {
                var pending = _queue.Dequeue();

                Plugin.Log.LogInfo($"[Deferred] Processing {pending.Source} " +
                    $"at {pending.Position:F1} (queued frame {pending.Frame}, now {currentFrame})");

                try
                {
                    if (!Plugin.VisualsWarmed)
                        Plugin.WarmVisuals();

                    var param = new ExplosionParams
                    {
                        position = pending.Position,
                        range = pending.Range,
                        structuralDamage = pending.Damage,
                        velocity = pending.Velocity,
                        sound = "explosion",
                        shrapnelChance = 0.4f
                    };

                    // Apply defaults
                    if (param.range <= 0.01f) param.range = 12f;
                    if (param.structuralDamage <= 0.01f) param.structuralDamage = 500f;
                    if (param.velocity <= 0.01f) param.velocity = 60f;

                    ShrapnelSpawnLogic.PreExplosion(param);
                    ShrapnelSpawnLogic.PostExplosion(param, preScan: false);

                    Plugin.Log.LogInfo($"[Deferred] ✓ Shrapnel spawned for {pending.Source}!");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Deferred] Failed for {pending.Source}: {e.Message}\n{e.StackTrace}");
                }
            }
        }

        private struct PendingExplosion
        {
            public Vector2 Position;
            public float Range;
            public float Damage;
            public float Velocity;
            public string Source;
            public int Frame;
        }
    }
}