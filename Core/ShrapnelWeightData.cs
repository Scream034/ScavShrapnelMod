using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Centralized per-weight physics and damage data table.
    /// Eliminates scattered switch blocks across ShrapnelProjectile, ShrapnelFactory, ShrapnelNetSync.
    /// 
    /// <para><b>PERF:</b> Array lookup by (int)weight — O(1), zero-alloc, cache-friendly.</para>
    /// <para><b>BALANCE:</b> KnockoutMultiplier and HeadOnlyDamage control fragment lethality.</para>
    /// </summary>
    public readonly struct ShrapnelWeightData
    {
        #region Physics Properties

        /// <summary>Rigidbody2D mass in kg.</summary>
        public readonly float Mass;

        /// <summary>Rigidbody2D gravity scale (1.0 = normal gravity).</summary>
        public readonly float GravityScale;

        /// <summary>Rigidbody2D linear drag coefficient.</summary>
        public readonly float Drag;

        /// <summary>CircleCollider2D radius in world units.</summary>
        public readonly float ColliderRadius;

        #endregion

        #region Visual Properties

        /// <summary>Minimum local scale on spawn.</summary>
        public readonly float ScaleMin;

        /// <summary>Maximum local scale on spawn.</summary>
        public readonly float ScaleMax;

        /// <summary>Minimum speed multiplier applied to base launch speed.</summary>
        public readonly float SpeedMultMin;

        /// <summary>Maximum speed multiplier applied to base launch speed.</summary>
        public readonly float SpeedMultMax;

        /// <summary>Initial heat value (0-1). Controls orange glow and material selection.</summary>
        public readonly float InitialHeat;

        /// <summary>Chance (0-1) that this weight class receives a trail renderer.</summary>
        public readonly float TrailChance;

        #endregion

        #region Damage Properties

        /// <summary>Chance (0-1) to embed in limb on hit.</summary>
        public readonly float EmbedChance;

        /// <summary>Armor/clothing durability damage on hit.</summary>
        public readonly float ArmorWear;

        /// <summary>Chance (0-1) to break bone on limb hit.</summary>
        public readonly float BoneBreakChance;

        /// <summary>Minimum base damage dealt to limbs.</summary>
        public readonly float DamageMin;

        /// <summary>Maximum base damage dealt to limbs.</summary>
        public readonly float DamageMax;

        /// <summary>Minimum bleed amount added on limb hit.</summary>
        public readonly float BleedMin;

        /// <summary>Maximum bleed amount added on limb hit.</summary>
        public readonly float BleedMax;

        #endregion

        #region Break Properties

        /// <summary>Minimum impact speed required to shatter this fragment.</summary>
        public readonly float BreakThreshold;

        /// <summary>Chance (0-1) to shatter when threshold is exceeded.</summary>
        public readonly float BreakChance;

        /// <summary>Whether this weight class can shatter into smaller pieces.</summary>
        public readonly bool CanBreak;

        #endregion

        #region Knockout Properties

        /// <summary>
        /// Consciousness reduction multiplier on head hit.
        /// Formula: consciousnessLoss = damage × KnockoutMultiplier.
        /// Higher values = longer unconsciousness.
        /// </summary>
        public readonly float KnockoutMultiplier;

        /// <summary>
        /// If true, this weight class only damages head limbs.
        /// Light fragments (Hot, Micro) bounce off body armor and thick clothing.
        /// </summary>
        public readonly bool HeadOnlyDamage;

        #endregion

        /// <summary>
        /// Creates a new weight data entry with all parameters.
        /// </summary>
        public ShrapnelWeightData(
            float mass, float gravityScale, float drag, float colliderRadius,
            float scaleMin, float scaleMax,
            float speedMultMin, float speedMultMax,
            float initialHeat,
            float embedChance, float armorWear, float boneBreakChance,
            float breakThreshold, float breakChance,
            float damageMin, float damageMax, float bleedMin, float bleedMax,
            float trailChance, bool canBreak,
            float knockoutMultiplier, bool headOnlyDamage)
        {
            Mass = mass;
            GravityScale = gravityScale;
            Drag = drag;
            ColliderRadius = colliderRadius;
            ScaleMin = scaleMin;
            ScaleMax = scaleMax;
            SpeedMultMin = speedMultMin;
            SpeedMultMax = speedMultMax;
            InitialHeat = initialHeat;
            EmbedChance = embedChance;
            ArmorWear = armorWear;
            BoneBreakChance = boneBreakChance;
            BreakThreshold = breakThreshold;
            BreakChance = breakChance;
            DamageMin = damageMin;
            DamageMax = damageMax;
            BleedMin = bleedMin;
            BleedMax = bleedMax;
            TrailChance = trailChance;
            CanBreak = canBreak;
            KnockoutMultiplier = knockoutMultiplier;
            HeadOnlyDamage = headOnlyDamage;
        }

        /// <summary>
        /// Weight data lookup table indexed by (int)ShrapnelWeight.
        /// Order must match enum: Hot=0, Medium=1, Heavy=2, Massive=3, Micro=4.
        /// </summary>
        private static readonly ShrapnelWeightData[] Table =
        {
            // Hot (0) — small glowing fragments, head-only damage
            new(
                mass: 0.02f, gravityScale: 0.3f, drag: 0.4f, colliderRadius: 0.3f,
                scaleMin: 0.08f, scaleMax: 0.14f,
                speedMultMin: 0.8f, speedMultMax: 1.3f,
                initialHeat: 1.0f,
                embedChance: 0.15f, armorWear: 0.005f, boneBreakChance: 0f,
                breakThreshold: float.MaxValue, breakChance: 0f,
                damageMin: 3f, damageMax: 8f, bleedMin: 0.5f, bleedMax: 2f,
                trailChance: 1f, canBreak: false,
                knockoutMultiplier: 1.0f, headOnlyDamage: true),

            // Medium (1) — standard fragments, damages all limbs
            new(
                mass: 0.08f, gravityScale: 0.15f, drag: 0.2f, colliderRadius: 0.3f,
                scaleMin: 0.14f, scaleMax: 0.25f,
                speedMultMin: 0.8f, speedMultMax: 1.2f,
                initialHeat: 0.4f,
                embedChance: 0.40f, armorWear: 0.01f, boneBreakChance: 0f,
                breakThreshold: 20f, breakChance: 0.2f,
                damageMin: 6f, damageMax: 15f, bleedMin: 1f, bleedMax: 4f,
                trailChance: 0.25f, canBreak: true,
                knockoutMultiplier: 1.5f, headOnlyDamage: false),

            // Heavy (2) — large chunks, full damage, moderate knockout
            new(
                mass: 0.25f, gravityScale: 0.35f, drag: 0.2f, colliderRadius: 0.3f,
                scaleMin: 0.22f, scaleMax: 0.45f,
                speedMultMin: 0.4f, speedMultMax: 0.8f,
                initialHeat: 0.15f,
                embedChance: 0.70f, armorWear: 0.02f, boneBreakChance: 0.08f,
                breakThreshold: 15f, breakChance: 0.35f,
                damageMin: 12f, damageMax: 25f, bleedMin: 2f, bleedMax: 6f,
                trailChance: 0f, canBreak: true,
                knockoutMultiplier: 2.5f, headOnlyDamage: false),

            // Massive (3) — devastating chunks, instant knockout
            new(
                mass: 0.8f, gravityScale: 0.5f, drag: 0.1f, colliderRadius: 0.5f,
                scaleMin: 0.5f, scaleMax: 0.8f,
                speedMultMin: 0.2f, speedMultMax: 0.4f,
                initialHeat: 0.08f,
                embedChance: 0.90f, armorWear: 0.05f, boneBreakChance: 1f,
                breakThreshold: 8f, breakChance: 0.6f,
                damageMin: 25f, damageMax: 50f, bleedMin: 5f, bleedMax: 12f,
                trailChance: 1f, canBreak: true,
                knockoutMultiplier: 5.0f, headOnlyDamage: false),

            // Micro (4) — visual sparks, head-only, minimal effect
            new(
                mass: 0.005f, gravityScale: 0.1f, drag: 0.5f, colliderRadius: 0.3f,
                scaleMin: 0.02f, scaleMax: 0.05f,
                speedMultMin: 1.5f, speedMultMax: 2.5f,
                initialHeat: 1.0f,
                embedChance: 0f, armorWear: 0.001f, boneBreakChance: 0f,
                breakThreshold: float.MaxValue, breakChance: 0f,
                damageMin: 1f, damageMax: 3f, bleedMin: 0.2f, bleedMax: 0.8f,
                trailChance: 0f, canBreak: false,
                knockoutMultiplier: 0.5f, headOnlyDamage: true),
        };

        /// <summary>
        /// Gets weight data by enum value. O(1) array lookup.
        /// </summary>
        /// <param name="weight">Weight class to look up.</param>
        /// <returns>Reference to readonly weight data.</returns>
        public static ref readonly ShrapnelWeightData Get(ShrapnelWeight weight)
            => ref Table[(int)weight];

        /// <summary>
        /// Configures a Rigidbody2D with physics values for a weight class.
        /// </summary>
        /// <param name="rb">Rigidbody to configure.</param>
        /// <param name="weight">Weight class determining physics properties.</param>
        public static void ConfigureRigidbody(Rigidbody2D rb, ShrapnelWeight weight)
        {
            ref readonly var d = ref Get(weight);
            rb.mass = d.Mass;
            rb.gravityScale = d.GravityScale;
            rb.drag = d.Drag;
        }
    }
}