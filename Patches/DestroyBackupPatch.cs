using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Patches
{
    /// <summary>
    /// LAYER 2: Catches explosions via GameObject.Destroy when Layer 1 doesn't fire.
    /// Detects MineScript, TurretScript, CustomItemBehaviour, BuildingEntity.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Object), "Destroy",
      new[] { typeof(UnityEngine.Object), typeof(float) })]
    public static class DestroyBackupPatch
    {
        private static readonly HashSet<int> _processedObjects = new();

        [HarmonyPrefix]
        public static void Prefix(UnityEngine.Object obj, float t)
        {
            try
            {
                if (obj == null) return;
                if (obj is not GameObject go) return;

                int id = go.GetInstanceID();
                if (_processedObjects.Contains(id)) return;

                ExplosionInfo info = GetExplosionInfo(go);
                if (info == null) return;

                if (ExplosionTracker.WasHandled(info.Position))
                    return;

                _processedObjects.Add(id);
                if (_processedObjects.Count > 200)
                    _processedObjects.Clear();

                ExplosionTracker.Track(info.Position);

                if (MultiplayerHelper.IsNetworkRunning && MultiplayerHelper.IsClient)
                {
                    if (ShrapnelConfig.DebugLogging.Value)
                        Plugin.Log.LogInfo(
                            $"[DestroyBackup] Skipped {info.Type} on MP client");
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[DestroyBackup] {info.Type} at {info.Position:F1} — spawning");

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                var param = new ExplosionParams
                {
                    position = info.Position,
                    range = info.Range,
                    structuralDamage = info.Damage,
                    velocity = info.Velocity,
                    sound = "explosion",
                    shrapnelChance = info.ShrapnelChance,
                    disfigureChance = info.DisfigureChance
                };

                ShrapnelSpawnLogic.PreExplosion(param);
                ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[DestroyBackup] {e.Message}\n{e.StackTrace}");
            }
        }

        private static ExplosionInfo GetExplosionInfo(GameObject go)
        {
            var mine = go.GetComponent<MineScript>();
            if (mine != null)
            {
                try
                {
                    var explodedField = AccessTools.Field(typeof(MineScript), "exploded");
                    bool exploded = (bool)explodedField.GetValue(mine);
                    if (!exploded && (mine.build == null || mine.build.health >= 0.5f))
                        return null;
                }
                catch { }

                return new ExplosionInfo("Mine",
                    go.transform.position + Vector3.up, 12f, 500f, 60f, 0.4f, 0.34f);
            }

            var turret = go.GetComponent<TurretScript>();
            if (turret != null)
            {
                var be = go.GetComponent<BuildingEntity>();
                if (be != null && be.health <= 0f)
                    return new ExplosionInfo("Turret",
                        go.transform.position, 9f, 200f, 15f, 0.4f, 0.34f);
                return null;
            }

            var customItem = go.GetComponent<CustomItemBehaviour>();
            if (customItem != null)
            {
                Item item = go.GetComponent<Item>();
                string itemId = item != null ? item.id : "";
                string goName = go.name.ToLower();

                if (itemId == "gravbag")
                {
                    if (item != null && item.condition <= 0.005f
                        && item.battery != null && item.battery.hasBattery)
                        return new ExplosionInfo("Gravbag",
                            go.transform.position, 12f, 500f, 60f, 0.4f, 0.15f);
                    return null;
                }

                if (itemId == "dynamite" || goName.Contains("dynamite")
                    || goName.Contains("tnt") || goName.Contains("explosive"))
                    return new ExplosionInfo("Dynamite",
                        go.transform.position, 18f, 2000f, 80f, 0.4f, 0.34f);

                if (goName.Contains("grenade"))
                    return new ExplosionInfo("Grenade",
                        go.transform.position, 10f, 800f, 50f, 0.4f, 0.34f);
            }

            var buildEnt = go.GetComponent<BuildingEntity>();
            if (buildEnt != null)
            {
                string beName = go.name.ToLower();
                if (beName.Contains("barrel") &&
                    (beName.Contains("explo") || beName.Contains("fuel")))
                    return new ExplosionInfo("ExplosiveBarrel",
                        go.transform.position, 15f, 1500f, 70f, 0.4f, 0.34f);
            }

            return null;
        }

        /// <summary>Compact explosion info record.</summary>
        private sealed class ExplosionInfo
        {
            public readonly string Type;
            public readonly Vector2 Position;
            public readonly float Range, Damage, Velocity, ShrapnelChance, DisfigureChance;

            public ExplosionInfo(string type, Vector2 pos, float range,
                float damage, float velocity, float shrapnelChance, float disfigureChance)
            {
                Type = type;
                Position = pos;
                Range = range;
                Damage = damage;
                Velocity = velocity;
                ShrapnelChance = shrapnelChance;
                DisfigureChance = disfigureChance;
            }
        }
    }
}