using System;
using System.Collections.Generic;
using HarmonyLib;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using UnityEngine;

namespace ScavShrapnelMod.Patches
{
    // ═══════════════════════════════════════════════════════════════
    //  SHARED: Explosion tracking to prevent double-spawning
    // ═══════════════════════════════════════════════════════════════

    public static class ExplosionTracker
    {
        private static readonly HashSet<int> _recent = new HashSet<int>();
        private static readonly Queue<int> _cleanup = new Queue<int>();
        private const int MaxTracked = 100;

        public static int GetHash(Vector2 pos)
        {
            int px = Mathf.RoundToInt(pos.x * 10f);
            int py = Mathf.RoundToInt(pos.y * 10f);
            return unchecked(px * 397 ^ py);
        }

        public static bool WasHandled(Vector2 pos)
        {
            return _recent.Contains(GetHash(pos));
        }

        public static void Track(Vector2 pos)
        {
            int hash = GetHash(pos);
            _recent.Add(hash);
            _cleanup.Enqueue(hash);

            while (_cleanup.Count > MaxTracked)
            {
                int old = _cleanup.Dequeue();
                _recent.Remove(old);
            }
        }

        public static ExplosionParams ApplyDefaults(ExplosionParams param)
        {
            if (param.range <= 0.01f)
                param.range = 12f;
            if (param.structuralDamage <= 0.01f)
                param.structuralDamage = 500f;
            if (param.velocity <= 0.01f)
                param.velocity = 60f;
            return param;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  MAIN PATCH: Direct replacement of WorldGeneration.CreateExplosion
    //
    //  WHY Prefix returns false:
    //    We completely replace the original method. The Prefix contains
    //    the full vanilla explosion logic (sound, particles, blastmark,
    //    camera shake, limb damage, block destruction) plus our shrapnel
    //    spawning wrapped around the block destruction call.
    //
    //  WHY no Burst disable needed:
    //    CreateExplosion is a managed C# static method, not a Burst job.
    //    Burst only applies to IJob/IJobEntity structs. We verified this
    //    by checking the method has no [BurstCompile] attribute and is
    //    not called from any Burst-compiled job schedule chain.
    //
    //  Execution order:
    //    1. PreExplosion (shrapnel, sparks, ash — needs intact blocks)
    //    2. Vanilla explosion logic (destroys blocks, damages entities)
    //    3. PostExplosion (ground debris — needs exposed crater surfaces)
    // ═══════════════════════════════════════════════════════════════

    public static class CreateExplosionPatch
    {
        /// <summary>
        /// Complete replacement for WorldGeneration.CreateExplosion.
        /// Returns false to skip the original method entirely.
        /// </summary>
        public static bool Prefix(ExplosionParams param)
        {
            try
            {
                Plugin.Log.LogInfo($"[CreateExplosion] REPLACED! pos={param.position}" +
                    $" range={param.range:F1} dmg={param.structuralDamage:F0}");

                // ── Phase 1: Pre-explosion shrapnel (before block destruction) ──
                try
                {
                    if (!Plugin.VisualsWarmed)
                        Plugin.WarmVisuals();

                    ShrapnelSpawnLogic.PreExplosion(param);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[CreateExplosion] PreExplosion: {e.Message}");
                }

                // ── Phase 2: Full vanilla explosion logic (inlined) ──
                try
                {
                    RunVanillaExplosion(param);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[CreateExplosion] VanillaExplosion: {e.Message}\n{e.StackTrace}");
                }

                // ── Phase 3: Post-explosion ground debris (after block destruction) ──
                try
                {
                    ShrapnelSpawnLogic.PostExplosion(param, preScan: false);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[CreateExplosion] PostExplosion: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[CreateExplosion] CRITICAL: {e.Message}\n{e.StackTrace}");
            }

            // Return false: skip original method entirely
            return false;
        }

        /// <summary>
        /// Exact replica of the vanilla WorldGeneration.CreateExplosion logic.
        /// Inlined here so we can wrap shrapnel phases around the block destruction.
        ///
        /// WHY inline instead of calling original:
        ///   Prefix returns false, so the original never runs. We must reproduce
        ///   all vanilla behavior: sound, particles, blastmark, camera shake,
        ///   tinnitus, entity damage, limb damage, and block circle destruction.
        ///
        /// This is a 1:1 copy of the decompiled vanilla method.
        /// </summary>
        private static void RunVanillaExplosion(ExplosionParams param)
        {
            // Sound
            Sound.Play(param.sound, Vector2.zero, true, false, null, 1f, 1f, false, false);

            // Explosion particle
            UnityEngine.Object.Instantiate(
                Resources.Load("Special/ExplosionParticle"),
                param.position,
                Quaternion.identity);

            // Blast mark
            GameObject blastmark = UnityEngine.Object.Instantiate(
                Resources.Load("Special/blastmark"),
                WorldGeneration.world.GetClosestChunk(
                    WorldGeneration.world.WorldToBlockPos(param.position)).transform
            ) as GameObject;
            blastmark.transform.position = param.position;
            blastmark.transform.eulerAngles = new Vector3(0f, 0f, UnityEngine.Random.value * 360f);

            // Camera shake
            PlayerCamera.main.shaker.Shake(param.range * 20f);

            // Tinnitus / close-range effects
            if (Vector2.Distance(param.position, PlayerCamera.main.body.transform.position)
                < param.range * 2.5f)
            {
                Sound.Play("tinnitus", Vector2.zero, true, false, null, 1f, 1f, true, true);
                PlayerCamera.main.body.eyePanicTime = 1f;
                PlayerCamera.main.body.eyeCloseTime = 5f;
                PlayerCamera.main.body.eyeScareTime = 12f;
                PlayerCamera.main.body.consciousness = 31f;
                PlayerCamera.main.body.hearingLoss += UnityEngine.Random.Range(27f, 36.6f);
                PlayerCamera.main.body.talker.Talk(
                    Locale.GetCharacter("loud"), null, false, false);
                PlayerCamera.main.shaker.Shake(param.range * 20f);
            }

            // Physics overlap — collect affected entities and limbs
            Collider2D[] array = Physics2D.OverlapCircleAll(param.position, param.range);
            List<Limb> list = new List<Limb>();

            foreach (Collider2D collider2D in array)
            {
                BuildingEntity buildingEntity;
                if (collider2D.TryGetComponent<BuildingEntity>(out buildingEntity))
                {
                    buildingEntity.health -= param.structuralDamage
                        * UnityEngine.Random.Range(0f, 2f);
                    Rigidbody2D rigidbody2D;
                    if (buildingEntity.TryGetComponent<Rigidbody2D>(out rigidbody2D))
                    {
                        rigidbody2D.velocity =
                            (buildingEntity.transform.position - (Vector3)param.position)
                            .normalized * param.velocity;
                    }
                }

                Item item;
                if (collider2D.TryGetComponent<Item>(out item))
                {
                    item.SetCondition(item.condition - param.structuralDamage
                        * 0.005f * UnityEngine.Random.Range(0f, 1.3f));
                    Rigidbody2D rigidbody2D2;
                    if (item.TryGetComponent<Rigidbody2D>(out rigidbody2D2))
                    {
                        rigidbody2D2.velocity =
                            (item.transform.position - (Vector3)param.position)
                            .normalized * param.velocity;
                    }
                }

                Limb limb;
                if (collider2D.TryGetComponent<Limb>(out limb))
                {
                    list.Add(limb);
                }

                Body body;
                if (collider2D.TryGetComponent<Body>(out body))
                {
                    list.AddRange(body.limbs);
                }
            }

            // Block destruction (the core terrain modification)
            WorldGeneration.world.GenerateBlockCircle(
                param.position, (int)param.range, 0, 1f, 0.85f, true, false, true);

            // Limb damage
            foreach (Limb limb2 in list)
            {
                if (!Physics2D.Linecast(param.position, limb2.transform.position,
                    LayerMask.GetMask(new string[] { "Ground" })))
                {
                    float armorReduction = limb2.GetArmorReduction();

                    if (UnityEngine.Random.Range(0f, 1f) < param.skinDamageChance)
                    {
                        limb2.skinHealth -= param.skinDamage.RandomFromRange() / armorReduction;
                    }

                    limb2.muscleHealth -= param.muscleDamage.RandomFromRange() / armorReduction;
                    limb2.body.shock = 100f;
                    limb2.body.lastTimeStepVelocity =
                        (limb2.body.transform.position - (Vector3)param.position)
                        .normalized * param.velocity;
                    limb2.body.Ragdoll();

                    if (!limb2.hasShrapnel)
                    {
                        limb2.shrapnel =
                            ((UnityEngine.Random.value < param.shrapnelChance) ? 5 : 0);
                    }

                    limb2.DamageWearables(param.shrapnelChance);

                    if (limb2.isVital && UnityEngine.Random.value < 0.5f)
                    {
                        limb2.body.internalBleeding +=
                            param.muscleDamage.RandomFromRange() * 0.4f / armorReduction;
                    }

                    if (UnityEngine.Random.Range(0f, 1f) < param.bleedChance)
                    {
                        limb2.bleedAmount +=
                            param.bleedAmount.RandomFromRange() / armorReduction;
                    }

                    if (UnityEngine.Random.Range(0f, 1f)
                        < param.boneBreakChance / armorReduction)
                    {
                        limb2.BreakBone();
                    }

                    if (UnityEngine.Random.Range(0f, 1f)
                        < param.dislocationChance / armorReduction)
                    {
                        limb2.Dislocate();
                    }

                    if (limb2.isHead)
                    {
                        limb2.body.consciousness = 0f;

                        if (UnityEngine.Random.Range(0f, 1f) < 0.7f / armorReduction)
                        {
                            limb2.body.brainHealth -=
                                param.muscleDamage.RandomFromRange() / armorReduction * 0.5f;
                        }

                        if (UnityEngine.Random.Range(0f, 1f)
                            < param.disfigureChance / armorReduction)
                        {
                            limb2.body.Disfigure();
                        }

                        if (UnityEngine.Random.Range(0f, 1f)
                            < param.disfigureChance / armorReduction)
                        {
                            limb2.body.RemoveEye();
                        }
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 2: GameObject.Destroy backup — IMMEDIATE spawning
    //  WHY: Catches explosions from sources that bypass CreateExplosion
    //  (e.g., custom scripts that destroy explosive objects directly).
    //  Spawns immediately in the Prefix — no deferred runner needed
    //  because the scene state is still valid before Destroy executes.
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(UnityEngine.Object), "Destroy",
      new[] { typeof(UnityEngine.Object), typeof(float) })]
    public static class GameObjectDestroyPatch
    {
        private static readonly HashSet<int> _processedObjects = new HashSet<int>();

        [HarmonyPrefix]
        public static void Prefix(UnityEngine.Object obj, float t)
        {
            try
            {
                if (obj == null) return;
                if (!(obj is GameObject go)) return;

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

                Plugin.Log.LogInfo(
                    $"[DestroyBackup] {info.Type} at {info.Position:F1} — spawning immediately");

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                var param = new ExplosionParams
                {
                    position = info.Position,
                    range = info.Range,
                    structuralDamage = info.Damage,
                    velocity = info.Velocity,
                    sound = "explosion",
                    shrapnelChance = 0.4f
                };

                ShrapnelSpawnLogic.PreExplosion(param);

                Plugin.Log.LogInfo($"[DestroyBackup] ✓ Shrapnel spawned for {info.Type}!");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[DestroyBackup] {e.Message}\n{e.StackTrace}");
            }
        }

        private static ExplosionInfo GetExplosionInfo(GameObject go)
        {
            // Mine
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

                return new ExplosionInfo
                {
                    Type = "Mine",
                    Position = go.transform.position + Vector3.up,
                    Range = 12f,
                    Damage = 500f,
                    Velocity = 60f
                };
            }

            // Turret
            var turret = go.GetComponent<TurretScript>();
            if (turret != null)
            {
                var be = go.GetComponent<BuildingEntity>();
                if (be != null && be.health <= 0f)
                {
                    return new ExplosionInfo
                    {
                        Type = "Turret",
                        Position = go.transform.position,
                        Range = 9f,
                        Damage = 200f,
                        Velocity = 15f
                    };
                }
                return null;
            }

            // CustomItemBehaviour (dynamite, grenades)
            var customItem = go.GetComponent<CustomItemBehaviour>();
            if (customItem != null)
            {
                string name = go.name.ToLower();
                if (name.Contains("dynamite") || name.Contains("tnt") ||
                    name.Contains("explosive"))
                {
                    return new ExplosionInfo
                    {
                        Type = "Dynamite",
                        Position = go.transform.position,
                        Range = 18f,
                        Damage = 2000f,
                        Velocity = 80f
                    };
                }
                if (name.Contains("grenade"))
                {
                    return new ExplosionInfo
                    {
                        Type = "Grenade",
                        Position = go.transform.position,
                        Range = 10f,
                        Damage = 800f,
                        Velocity = 50f
                    };
                }
            }

            // Explosive barrels
            var buildEnt = go.GetComponent<BuildingEntity>();
            if (buildEnt != null)
            {
                string beName = go.name.ToLower();
                if (beName.Contains("barrel") &&
                    (beName.Contains("explo") || beName.Contains("fuel")))
                {
                    return new ExplosionInfo
                    {
                        Type = "ExplosiveBarrel",
                        Position = go.transform.position,
                        Range = 15f,
                        Damage = 1500f,
                        Velocity = 70f
                    };
                }
            }

            return null;
        }

        private sealed class ExplosionInfo
        {
            public string Type;
            public Vector2 Position;
            public float Range;
            public float Damage;
            public float Velocity;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 3: TurretScript.Update — dedicated turret hook
    //  Spawns immediately, no deferral.
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(TurretScript), "Update")]
    public static class TurretUpdatePatch
    {
        private static readonly HashSet<int> _explodedTurrets = new HashSet<int>();

        [HarmonyPostfix]
        public static void Postfix(TurretScript __instance)
        {
            try
            {
                var buildField = AccessTools.Field(typeof(TurretScript), "build");
                if (buildField == null) return;

                var build = (BuildingEntity)buildField.GetValue(__instance);
                if (build == null) return;

                if (build.health > 0f) return;

                int id = __instance.GetInstanceID();
                if (_explodedTurrets.Contains(id)) return;
                _explodedTurrets.Add(id);

                if (_explodedTurrets.Count > 100)
                    _explodedTurrets.Clear();

                Vector2 position = __instance.transform.position;

                if (ExplosionTracker.WasHandled(position))
                    return;

                ExplosionTracker.Track(position);

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                Plugin.Log.LogInfo("[TurretUpdate] Turret exploded, spawning shrapnel");

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
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[TurretUpdate] {e.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 4: TurretScript.Shoot — bullet impact shrapnel
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
    public static class TurretShootPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FireInfo info)
        {
            try
            {
                BulletShrapnelLogic.TrySpawnFromBullet(info);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[TurretShoot] {e.Message}");
            }
        }
    }
}