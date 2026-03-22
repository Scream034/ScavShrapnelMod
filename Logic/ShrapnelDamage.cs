using System;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Projectiles;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Static damage logic for shrapnel impacts.
    /// 
    /// <para><b>Damage Gating:</b> Light fragments (Hot, Micro) deal reduced damage
    /// to non-head limbs. Full effects only on head hits.</para>
    /// 
    /// <para><b>Knockout Scaling:</b> Consciousness loss scales with weight.
    /// Massive = 5× knockout, Micro = 0.5× knockout.</para>
    /// 
    /// <para><b>Ragdoll:</b> Only Heavy and Massive fragments ragdoll.
    /// Lighter fragments cause pain/bleed without knockdown.</para>
    /// </summary>
    public static class ShrapnelDamage
    {
        #region Damage Application

        /// <summary>
        /// Applies shrapnel damage to a limb with weight-based gating.
        /// </summary>
        /// <param name="limb">Target limb to damage.</param>
        /// <param name="weight">Shrapnel weight class.</param>
        /// <param name="type">Material type (affects bone break).</param>
        /// <param name="baseDamage">Base damage before modifiers.</param>
        /// <param name="baseBleed">Base bleed before modifiers.</param>
        /// <param name="decayMult">Lifetime decay (1.0 = fresh).</param>
        /// <param name="rng">Deterministic random generator.</param>
        /// <param name="isFlying">True if in flying state.</param>
        /// <param name="rb">Fragment rigidbody for knockback direction.</param>
        public static void ApplyToLimb(
            Limb limb,
            ShrapnelWeight weight,
            ShrapnelProjectile.ShrapnelType type,
            float baseDamage,
            float baseBleed,
            float decayMult,
            System.Random rng,
            bool isFlying,
            Rigidbody2D rb)
        {
            ref readonly var wd = ref ShrapnelWeightData.Get(weight);

            // WHY: Light fragments deal reduced damage to non-head limbs.
            // They scratch skin but can't penetrate clothing/muscle effectively.
            // Head is vulnerable (eyes, ears, exposed skin).
            if (!limb.isHead && wd.HeadOnlyDamage)
            {
                ApplyReducedBodyDamage(limb, baseDamage, baseBleed, decayMult);
                return;
            }

            float armor = limb.GetArmorReduction();
            float dmg = baseDamage * decayMult / armor;
            float bleed = baseBleed * decayMult / armor;

            limb.skinHealth -= dmg * 0.7f;
            limb.muscleHealth -= dmg;
            limb.bleedAmount += bleed;
            limb.DamageWearables(wd.ArmorWear);

            ApplyEmbedChance(limb, wd.EmbedChance, decayMult, armor, rng);
            ApplyBoneBreak(limb, weight, type, wd.BoneBreakChance, armor, rng);
            ApplyInternalBleeding(limb, weight, dmg, decayMult, armor, rng);

            if (limb.isHead)
                ApplyHeadDamage(limb, weight, dmg, decayMult, armor,
                    wd.KnockoutMultiplier, rng);

            ApplyShockAndFeedback(limb, baseDamage, decayMult);

            if (weight == ShrapnelWeight.Heavy || weight == ShrapnelWeight.Massive)
                ApplyRagdoll(limb, weight, decayMult, isFlying, rb);
        }

        /// <summary>
        /// Applies micro shrapnel damage. Reduced on body, full on head.
        /// </summary>
        /// <param name="limb">Target limb.</param>
        /// <param name="decayMult">Lifetime decay multiplier.</param>
        /// <param name="rng">Deterministic random generator.</param>
        public static void ApplyMicroToLimb(Limb limb, float decayMult, System.Random rng)
        {
            float armor = limb.GetArmorReduction();
            float dmg = rng.Range(
                ShrapnelConfig.MicroDamageMin.Value,
                ShrapnelConfig.MicroDamageMax.Value) * decayMult / armor;
            float bleed = rng.Range(
                ShrapnelConfig.MicroBleedMin.Value,
                ShrapnelConfig.MicroBleedMax.Value) * decayMult / armor;

            if (!limb.isHead)
            {
                // WHY: Micro on body = reduced scratch, minor bleed.
                // Not zero — tiny hot metal still burns skin.
                limb.skinHealth -= dmg * 0.3f;
                limb.bleedAmount += bleed * 0.2f;
                limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 5f);
                return;
            }

            // Full damage on head
            limb.skinHealth -= dmg;
            limb.bleedAmount += bleed;
            limb.DamageWearables(0.001f);

            ref readonly var wd = ref ShrapnelWeightData.Get(ShrapnelWeight.Micro);
            limb.body.consciousness = Mathf.Max(0f,
                limb.body.consciousness - dmg * wd.KnockoutMultiplier);

            limb.body.shock = Mathf.Max(limb.body.shock,
                dmg * ShrapnelConfig.MicroShockMultiplier.Value);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline,
                ShrapnelConfig.MicroAdrenalineBase.Value + dmg * 0.3f);
            limb.body.DoGoreSound();
        }

        /// <summary>
        /// Applies damage to a BuildingEntity from shrapnel impact.
        /// </summary>
        /// <param name="entity">Target building entity.</param>
        /// <param name="damage">Base damage value.</param>
        /// <param name="decayMult">Lifetime decay multiplier.</param>
        /// <returns>True if damage was applied.</returns>
        public static bool ApplyToBuildingEntity(BuildingEntity entity,
            float damage, float decayMult)
        {
            try
            {
                if (entity.cantHit) return false;

                float dmg = damage * decayMult;
                entity.health -= dmg;

                if (entity.animal)
                {
                    entity.gameObject.SendMessage("AnimalHit", dmg,
                        SendMessageOptions.DontRequireReceiver);
                    if (entity.health <= 0f)
                        entity.gameObject.SendMessage("AnimalDeath",
                            SendMessageOptions.DontRequireReceiver);
                }

                TryCreateHitFlash(entity);
                return true;
            }
            catch (Exception e)
            {
                Console.Error($"[Shrapnel] BuildingEntity: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Applies step-on debris damage.
        /// </summary>
        /// <param name="target">Limb that stepped on debris.</param>
        /// <param name="decayMult">Lifetime decay multiplier.</param>
        /// <param name="armor">Pre-computed armor reduction.</param>
        /// <param name="rng">Deterministic random generator.</param>
        public static void ApplyStepOnDamage(Limb target, float decayMult,
            float armor, System.Random rng)
        {
            target.skinHealth -= rng.Range(15f, 35f) * decayMult / armor;
            target.muscleHealth -= rng.Range(5f, 15f) * decayMult / armor;
            target.bleedAmount += rng.Range(3f, 12f) * decayMult / armor;
            target.pain += 50f * decayMult / armor;
            target.shrapnel++;
            target.DamageWearables(0.01f);
        }

        #endregion

        #region Wound Visuals

        /// <summary>
        /// Applies wound visuals (temporary sprite + health panel).
        /// </summary>
        /// <param name="limb">Limb to apply visuals to.</param>
        public static void ApplyWoundVisuals(Limb limb)
        {
            try
            {
                if (ShrapnelFactory.WoundSprite != null)
                    limb.CreateTemporarySprite(ShrapnelFactory.WoundSprite,
                        0f, null, false, 600f, (Limb x) => !x.hasShrapnel);
                if (ShrapnelFactory.WoundPanel != null)
                    WoundView.view.AddImageToLimb(limb, ShrapnelFactory.WoundPanel,
                        false, (Limb x) => !x.hasShrapnel || x.dismembered);
            }
            catch (Exception e)
            {
                Console.Error($"[Shrapnel] Wound: {e.Message}");
            }
        }

        #endregion

        #region Break Parameters

        /// <summary>
        /// Gets break parameters for a weight class.
        /// </summary>
        /// <param name="weight">Weight class to query.</param>
        /// <param name="threshold">Minimum impact speed to break.</param>
        /// <param name="chance">Break probability when exceeded.</param>
        /// <returns>True if this weight can break.</returns>
        public static bool GetBreakParams(ShrapnelWeight weight,
            out float threshold, out float chance)
        {
            ref readonly var wd = ref ShrapnelWeightData.Get(weight);
            threshold = wd.BreakThreshold;
            chance = wd.BreakChance;
            return wd.CanBreak && chance > 0f && threshold < float.MaxValue;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Reduced damage for light fragments hitting non-head limbs.
        /// WHY NOT ZERO: Tiny hot metal still scratches skin and causes minor bleeding.
        /// Just can't penetrate deep enough for serious injury.
        /// </summary>
        private static void ApplyReducedBodyDamage(Limb limb,
            float baseDamage, float baseBleed, float decayMult)
        {
            float armor = limb.GetArmorReduction();
            limb.skinHealth -= baseDamage * 0.15f * decayMult / armor;
            limb.bleedAmount += baseBleed * 0.1f * decayMult / armor;
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 10f);
            limb.body.DoGoreSound();
        }

        private static void ApplyEmbedChance(Limb limb, float baseChance,
            float decayMult, float armor, System.Random rng)
        {
            float chance = baseChance * decayMult;
            if (chance > 0f
                && rng.NextDouble() < chance / (armor * armor)
                && rng.NextDouble() > 0.2f)
            {
                limb.shrapnel++;
            }
        }

        private static void ApplyBoneBreak(Limb limb, ShrapnelWeight weight,
            ShrapnelProjectile.ShrapnelType type, float baseChance,
            float armor, System.Random rng)
        {
            if (weight == ShrapnelWeight.Massive)
            {
                limb.BreakBone();
                return;
            }

            if (baseChance <= 0f) return;

            float adjusted = type == ShrapnelProjectile.ShrapnelType.HeavyMetal
                ? baseChance * 1.875f
                : baseChance;

            if (rng.NextDouble() < adjusted / armor)
                limb.BreakBone();
        }

        private static void ApplyInternalBleeding(Limb limb, ShrapnelWeight weight,
            float damage, float decayMult, float armor, System.Random rng)
        {
            if (!limb.isVital) return;
            if (weight == ShrapnelWeight.Hot || weight == ShrapnelWeight.Micro) return;

            float chance = weight switch
            {
                ShrapnelWeight.Massive => 0.6f,
                ShrapnelWeight.Heavy => 0.3f,
                _ => 0.15f
            } * decayMult;

            if (rng.NextDouble() < chance / armor)
                limb.body.internalBleeding += damage * 0.3f;
        }

        private static void ApplyHeadDamage(Limb limb, ShrapnelWeight weight,
            float damage, float decayMult, float armor,
            float knockoutMult, System.Random rng)
        {
            float loss = damage * knockoutMult;
            limb.body.consciousness = Mathf.Max(0f, limb.body.consciousness - loss);

            if ((weight == ShrapnelWeight.Heavy || weight == ShrapnelWeight.Massive)
                && rng.NextDouble() < 0.2f * decayMult / armor)
            {
                limb.body.brainHealth -= damage * 0.5f;
            }

            if (weight == ShrapnelWeight.Massive
                && rng.NextDouble() < 0.3f * decayMult / armor)
            {
                limb.body.Disfigure();
            }
        }

        private static void ApplyShockAndFeedback(Limb limb,
            float baseDamage, float decayMult)
        {
            limb.body.shock = Mathf.Max(limb.body.shock, baseDamage * decayMult * 2f);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline,
                20f + baseDamage * decayMult);
            limb.body.DoGoreSound();
            limb.body.talker.Talk(Locale.GetCharacter("loud"), null, false, false);
        }

        /// <summary>
        /// Ragdoll for Heavy and Massive fragments only.
        /// </summary>
        private static void ApplyRagdoll(Limb limb, ShrapnelWeight weight,
            float decayMult, bool isFlying, Rigidbody2D rb)
        {
            Vector2 knockDir = (isFlying && rb != null && !rb.isKinematic)
                ? rb.velocity.normalized
                : Vector2.up;

            float force = weight == ShrapnelWeight.Massive ? 10f : 5f;
            limb.body.lastTimeStepVelocity = knockDir * force * decayMult;
            limb.body.Ragdoll();
        }

        private static void TryCreateHitFlash(BuildingEntity entity)
        {
            try
            {
                var sr = entity.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null)
                {
                    WorldGeneration.world.CreateHitFlash(
                        sr.sprite, entity.transform.position,
                        entity.transform.rotation, Color.red,
                        entity.transform);
                }
            }
            catch (Exception e)
            {
                Console.Error($"[Shrapnel] HitFlash: {e.Message}");
            }
        }

        #endregion
    }
}