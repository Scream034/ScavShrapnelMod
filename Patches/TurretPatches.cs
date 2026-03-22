using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Patches
{
    /// <summary>
    /// Turret DEATH detection only. Shot detection handled by TurretUpdateTranspiler.
    /// Polls turret health → 0 for explosion effects.
    /// </summary>
    public static class TurretPatches
    {
        private static readonly HashSet<int> _explodedTurrets = new();

        /// <summary>
        /// Checks turret death. Called from TurretDeathMonitor polling.
        /// </summary>
        internal static void CheckTurretDeath(TurretScript turret)
        {
            if (!MultiplayerHelper.ShouldSpawnPhysicsShrapnel) return;

            try
            {
                var build = turret.GetComponent<BuildingEntity>();
                if (build == null || build.health > 0f) return;

                int id = turret.GetInstanceID();
                if (_explodedTurrets.Contains(id)) return;
                _explodedTurrets.Add(id);
                if (_explodedTurrets.Count > 100) _explodedTurrets.Clear();

                Vector2 position = turret.transform.position;
                if (ExplosionTracker.WasHandled(position)) return;
                ExplosionTracker.Track(position);

                if (!Plugin.VisualsWarmed) Plugin.WarmVisuals();

                Plugin.Log.LogInfo("[TurretDeath] Turret exploded");

                var param = new ExplosionParams
                {
                    position = position,
                    range = 9f,
                    structuralDamage = 200f,
                    velocity = 15f,
                    sound = "explosion",
                    shrapnelChance = 0.4f
                };

                ShrapnelSpawnLogic.PreExplosion(param);
                ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[TurretDeath] {e.Message}");
            }
        }
    }

    /// <summary>
    /// Polls turrets for death (health ≤ 0).
    /// Lightweight — only checks health, no shot detection.
    /// </summary>
    internal sealed class TurretDeathMonitor : MonoBehaviour
    {
        private TurretScript[] _turrets = Array.Empty<TurretScript>();
        private float _scanTimer;

        private void Update()
        {
            _scanTimer -= Time.deltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 0.5f;
                _turrets = FindObjectsOfType<TurretScript>();
            }

            for (int i = 0; i < _turrets.Length; i++)
            {
                TurretScript turret = _turrets[i];
                if (turret == null) continue;
                TurretPatches.CheckTurretDeath(turret);
            }
        }
    }
}