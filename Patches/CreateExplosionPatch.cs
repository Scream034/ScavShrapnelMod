using System;
using System.Collections.Generic;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Patches
{
    /// <summary>
    /// LAYER 1: Full replacement of WorldGeneration.CreateExplosion.
    ///
    /// Execution order:
    ///   1. PreExplosion (shrapnel, sparks, ash — needs intact blocks)
    ///   2. RunVanillaExplosion (sound, damage, block destruction)
    ///   3. PostExplosion (ground debris — needs exposed crater surfaces)
    ///
    /// THREE MODES: Singleplayer (full replace), MP Server (passthrough), MP Client (effects only).
    /// </summary>
    public static class CreateExplosionPatch
    {
        /// <summary>Harmony Prefix for WorldGeneration.CreateExplosion.</summary>
        public static bool Prefix(ExplosionParams param)
        {
            try
            {
                bool isMP = MultiplayerHelper.IsNetworkRunning;
                bool isClient = MultiplayerHelper.IsClient;

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                // MP CLIENT
                if (isMP && isClient)
                {
                    bool isFromServer = param.structuralDamage <= 0.01f
                                     && param.disfigureChance <= 0.01f;

                    if (!isFromServer)
                        return false;

                    Plugin.Log.LogInfo($"[Explosion] CLIENT pos={param.position}" +
                        $" range={param.range:F1}");

                    try { RunVanillaExplosionVisuals(param); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[Explosion] Client visuals: {e.Message}");
                    }

                    try
                    {
                        var clientParam = CreateClientVisualParams(param);
                        ShrapnelSpawnLogic.PreExplosion(clientParam);
                        ShrapnelSpawnLogic.PostExplosion(clientParam, preScan: true);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[Explosion] Client effects: {e.Message}");
                    }

                    return false;
                }

                // MP SERVER
                if (isMP)
                {
                    bool alreadyHandled = ExplosionTracker.WasHandled(param.position);
                    ExplosionTracker.Track(param.position);

                    if (!alreadyHandled)
                    {
                        Plugin.Log.LogInfo($"[Explosion] SERVER pos={param.position}" +
                            $" range={param.range:F1} dmg={param.structuralDamage:F0}");

                        try { ShrapnelSpawnLogic.PreExplosion(param); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Server Pre: {e.Message}");
                        }
                    }

                    return true;
                }

                // SINGLEPLAYER
                {
                    bool alreadyHandled = ExplosionTracker.WasHandled(param.position);
                    ExplosionTracker.Track(param.position);

                    if (!alreadyHandled)
                    {
                        try { ShrapnelSpawnLogic.PreExplosion(param); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Pre: {e.Message}");
                        }
                    }

                    try { RunVanillaExplosion(param); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError(
                            $"[Explosion] Vanilla: {e.Message}\n{e.StackTrace}");
                    }

                    if (!alreadyHandled)
                    {
                        try { ShrapnelSpawnLogic.PostExplosion(param, preScan: false); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Post: {e.Message}");
                        }
                    }

                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[Explosion] CRITICAL: {e.Message}\n{e.StackTrace}");
                return true;
            }
        }

        /// <summary>Postfix: runs AFTER vanilla for MP server path.</summary>
        public static void Postfix(ExplosionParams param)
        {
            if (!MultiplayerHelper.IsNetworkRunning || MultiplayerHelper.IsClient)
                return;

            try
            {
                if (!ExplosionTracker.WasHandled(param.position))
                    return;

                ShrapnelSpawnLogic.PostExplosion(param, preScan: false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Explosion] Postfix: {e.Message}");
            }
        }

        private static ExplosionParams CreateClientVisualParams(ExplosionParams serverParam)
        {
            return new ExplosionParams
            {
                position = serverParam.position,
                range = serverParam.range,
                velocity = serverParam.velocity,
                structuralDamage = 500f,
                disfigureChance = 0.34f,
                shrapnelChance = 0.4f,
                sound = "explosion"
            };
        }

        /// <summary>1:1 copy of vanilla WorldGeneration.CreateExplosion.</summary>
        internal static void RunVanillaExplosion(ExplosionParams param)
        {
            Sound.Play(param.sound, Vector2.zero, true, false, null, 1f, 1f, false, false);

            UnityEngine.Object.Instantiate(
                Resources.Load("Special/ExplosionParticle"),
                param.position,
                Quaternion.identity);

            GameObject blastmark = UnityEngine.Object.Instantiate(
                Resources.Load("Special/blastmark"),
                WorldGeneration.world.GetClosestChunk(
                    WorldGeneration.world.WorldToBlockPos(param.position)).transform
            ) as GameObject;
            blastmark.transform.position = param.position;
            blastmark.transform.eulerAngles = new Vector3(
                0f, 0f, UnityEngine.Random.value * 360f);

            PlayerCamera.main.shaker.Shake(param.range * 20f);

            if (Vector2.Distance(param.position,
                PlayerCamera.main.body.transform.position) < param.range * 2.5f)
            {
                ApplyPlayerProximityEffects(param);
            }

            ApplyExplosionDamage(param);
        }

        /// <summary>Plays only visual/audio portion for MP clients.</summary>
        internal static void RunVanillaExplosionVisuals(ExplosionParams param)
        {
            Sound.Play(param.sound, Vector2.zero, true, false, null, 1f, 1f, false, false);

            UnityEngine.Object.Instantiate(
                Resources.Load("Special/ExplosionParticle"),
                param.position,
                Quaternion.identity);

            try
            {
                GameObject blastmark = UnityEngine.Object.Instantiate(
                    Resources.Load("Special/blastmark"),
                    WorldGeneration.world.GetClosestChunk(
                        WorldGeneration.world.WorldToBlockPos(param.position)).transform
                ) as GameObject;
                if (blastmark != null)
                {
                    blastmark.transform.position = param.position;
                    blastmark.transform.eulerAngles = new Vector3(
                        0f, 0f, UnityEngine.Random.value * 360f);
                }
            }
            catch { }

            if (PlayerCamera.main != null)
            {
                PlayerCamera.main.shaker.Shake(param.range * 20f);

                if (PlayerCamera.main.body != null &&
                    Vector2.Distance(param.position,
                        PlayerCamera.main.body.transform.position) < param.range * 2.5f)
                {
                    ApplyPlayerProximityEffects(param);
                }
            }
        }

        /// <summary>Tinnitus, eye effects, hearing loss when player is near explosion.</summary>
        private static void ApplyPlayerProximityEffects(ExplosionParams param)
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

        /// <summary>Entity/limb damage + block destruction from explosion.</summary>
        private static void ApplyExplosionDamage(ExplosionParams param)
        {
            Collider2D[] array = Physics2D.OverlapCircleAll(
                param.position, param.range);
            List<Limb> list = new();

            foreach (Collider2D col in array)
            {
                if (col.TryGetComponent(out BuildingEntity be))
                {
                    be.health -= param.structuralDamage *
                        UnityEngine.Random.Range(0f, 2f);
                    if (be.TryGetComponent(out Rigidbody2D rb))
                        rb.velocity = (be.transform.position -
                            (Vector3)param.position).normalized * param.velocity;
                }

                if (col.TryGetComponent(out Item item))
                {
                    item.SetCondition(item.condition - param.structuralDamage *
                        0.005f * UnityEngine.Random.Range(0f, 1.3f));
                    if (item.TryGetComponent(out Rigidbody2D rb))
                        rb.velocity = (item.transform.position -
                            (Vector3)param.position).normalized * param.velocity;
                }

                if (col.TryGetComponent(out Limb limb))
                    list.Add(limb);

                if (col.TryGetComponent(out Body body))
                    list.AddRange(body.limbs);
            }

            WorldGeneration.world.GenerateBlockCircle(
                param.position, (int)param.range, 0, 1f, 0.85f, true, false, true);

            foreach (Limb limb2 in list)
            {
                if (!Physics2D.Linecast(param.position, limb2.transform.position,
                    LayerMask.GetMask("Ground")))
                {
                    ApplyExplosionLimbDamage(limb2, param);
                }
            }
        }

        /// <summary>Per-limb explosion damage, identical to vanilla.</summary>
        private static void ApplyExplosionLimbDamage(Limb limb, ExplosionParams param)
        {
            float armor = limb.GetArmorReduction();

            if (UnityEngine.Random.Range(0f, 1f) < param.skinDamageChance)
                limb.skinHealth -= param.skinDamage.RandomFromRange() / armor;

            limb.muscleHealth -= param.muscleDamage.RandomFromRange() / armor;
            limb.body.shock = 100f;
            limb.body.lastTimeStepVelocity =
                (limb.body.transform.position - (Vector3)param.position).normalized * param.velocity;
            limb.body.Ragdoll();

            if (!limb.hasShrapnel)
                limb.shrapnel = UnityEngine.Random.value < param.shrapnelChance ? 5 : 0;

            limb.DamageWearables(param.shrapnelChance);

            if (limb.isVital && UnityEngine.Random.value < 0.5f)
                limb.body.internalBleeding +=
                    param.muscleDamage.RandomFromRange() * 0.4f / armor;

            if (UnityEngine.Random.Range(0f, 1f) < param.bleedChance)
                limb.bleedAmount += param.bleedAmount.RandomFromRange() / armor;

            if (UnityEngine.Random.Range(0f, 1f) < param.boneBreakChance / armor)
                limb.BreakBone();

            if (UnityEngine.Random.Range(0f, 1f) < param.dislocationChance / armor)
                limb.Dislocate();

            if (limb.isHead)
            {
                limb.body.consciousness = 0f;

                if (UnityEngine.Random.Range(0f, 1f) < 0.7f / armor)
                    limb.body.brainHealth -=
                        param.muscleDamage.RandomFromRange() / armor * 0.5f;

                if (UnityEngine.Random.Range(0f, 1f) < param.disfigureChance / armor)
                    limb.body.Disfigure();

                if (UnityEngine.Random.Range(0f, 1f) < param.disfigureChance / armor)
                    limb.body.RemoveEye();
            }
        }
    }
}