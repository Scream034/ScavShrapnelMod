using BepInEx.Configuration;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Centralized mod configuration via BepInEx ConfigFile.
    ///
    /// All parameters available in BepInEx/config/ScavShrapnelMod.cfg.
    /// Changes apply on game restart.
    ///
    /// Sections:
    /// - Performance: limits, throttle, lifetime
    /// - Bullets: bullet shrapnel and impact effects
    /// - Explosions: explosion classification
    /// - Effects.Explosion: smoke, embers, crater dust
    /// - Effects.BulletImpact: sparks, flash, metal chips
    /// - Lifetime: debris/stuck duration
    /// - Interact: player interaction
    /// </summary>
    public static class ShrapnelConfig
    {
        // ══════════════════════════════════════════════════════════════════
        //  PERFORMANCE
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Max alive physical shrapnel (ShrapnelProjectile) objects.</summary>
        public static ConfigEntry<int> MaxAliveDebris;

        /// <summary>Max alive visual particles (AshParticle, VisualShrapnel, etc.).</summary>
        public static ConfigEntry<int> MaxAliveVisualParticles;

        /// <summary>Global spawn count multiplier (0.1 = 10%, 2.0 = 200%).</summary>
        public static ConfigEntry<float> SpawnCountMultiplier;

        /// <summary>Max speed of any shrapnel (m/s).</summary>
        public static ConfigEntry<float> GlobalMaxSpeed;

        /// <summary>Speed multiplier for all shrapnel.</summary>
        public static ConfigEntry<float> GlobalSpeedBoost;

        /// <summary>Min distance between spawns in same frame (m).</summary>
        public static ConfigEntry<float> MinDistanceBetweenSpawns;

        /// <summary>Max DamageBlock calls per frame.</summary>
        public static ConfigEntry<int> MaxDamagePerFrame;

        /// <summary>Enable Debug.Log for each explosion.</summary>
        public static ConfigEntry<bool> DebugLogging;

        // ══════════════════════════════════════════════════════════════════
        //  BULLETS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Enable physical shrapnel fragments from bullet impacts.</summary>
        public static ConfigEntry<bool> EnableBulletFragments;

        /// <summary>Enable visual impact effects (flash, sparks, chips).</summary>
        public static ConfigEntry<bool> EnableBulletImpactEffects;

        /// <summary>Min frames between bullet shrapnel spawns.</summary>
        public static ConfigEntry<int> BulletMinFramesBetweenSpawns;

        /// <summary>Min fragments from bullet.</summary>
        public static ConfigEntry<int> BulletFragmentsMin;

        /// <summary>Max fragments from bullet (exclusive).</summary>
        public static ConfigEntry<int> BulletFragmentsMax;

        /// <summary>Base speed for bullet fragments (m/s).</summary>
        public static ConfigEntry<float> BulletBaseSpeed;

        /// <summary>Min sparks from bullet (legacy, use ImpactStreakSparks instead).</summary>
        public static ConfigEntry<int> BulletSparksMin;

        /// <summary>Max sparks from bullet (legacy, use ImpactStreakSparks instead).</summary>
        public static ConfigEntry<int> BulletSparksMax;

        /// <summary>Scale multiplier for bullet shrapnel.</summary>
        public static ConfigEntry<float> BulletScaleMultiplier;

        /// <summary>Heat multiplier for bullet shrapnel.</summary>
        public static ConfigEntry<float> BulletHeatMultiplier;

        // ══════════════════════════════════════════════════════════════════
        //  EXPLOSIONS: CLASSIFICATION
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Tolerance for explosion parameter comparison (+/- epsilon).</summary>
        public static ConfigEntry<float> ClassifyEpsilon;

        // ── Dynamite ──
        public static ConfigEntry<float> DynamiteRange;
        public static ConfigEntry<float> DynamiteStructuralDamage;
        public static ConfigEntry<int> DynamitePrimaryMin;
        public static ConfigEntry<int> DynamitePrimaryMax;
        public static ConfigEntry<float> DynamiteSpeed;
        public static ConfigEntry<int> DynamiteVisualMin;
        public static ConfigEntry<int> DynamiteVisualMax;

        // ── Turret ──
        public static ConfigEntry<float> TurretRange;
        public static ConfigEntry<float> TurretVelocity;
        public static ConfigEntry<int> TurretPrimaryMin;
        public static ConfigEntry<int> TurretPrimaryMax;
        public static ConfigEntry<float> TurretSpeed;
        public static ConfigEntry<int> TurretVisualMin;
        public static ConfigEntry<int> TurretVisualMax;

        // ── Mine (fallback) ──
        public static ConfigEntry<int> MinePrimaryMin;
        public static ConfigEntry<int> MinePrimaryMax;
        public static ConfigEntry<float> MineSpeed;
        public static ConfigEntry<int> MineVisualMin;
        public static ConfigEntry<int> MineVisualMax;

        // ══════════════════════════════════════════════════════════════════
        //  GROUND DEBRIS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Multiplier for ground debris scan radius relative to explosion range.</summary>
        public static ConfigEntry<float> GroundDebrisRangeMultiplier;

        /// <summary>Multiplier for ground debris particle count.</summary>
        public static ConfigEntry<float> GroundDebrisCountMultiplier;

        // ══════════════════════════════════════════════════════════════════
        //  SPARKS (SHRAPNEL PROJECTILE IMPACTS)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Min sparks on metal impact.</summary>
        public static ConfigEntry<int> MetalImpactSparksMin;

        /// <summary>Max sparks on metal impact (exclusive).</summary>
        public static ConfigEntry<int> MetalImpactSparksMax;

        /// <summary>Min sparks on ricochet.</summary>
        public static ConfigEntry<int> RicochetSparksMin;

        /// <summary>Max sparks on ricochet (exclusive).</summary>
        public static ConfigEntry<int> RicochetSparksMax;

        /// <summary>Min debris particles on ricochet.</summary>
        public static ConfigEntry<int> RicochetDebrisMin;

        /// <summary>Max debris particles on ricochet (exclusive).</summary>
        public static ConfigEntry<int> RicochetDebrisMax;

        // ══════════════════════════════════════════════════════════════════
        //  ADVANCED EXPLOSION EFFECTS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Enable smoke column effect.</summary>
        public static ConfigEntry<bool> EnableSmokeColumn;

        /// <summary>Enable fire embers effect.</summary>
        public static ConfigEntry<bool> EnableFireEmbers;

        /// <summary>Enable crater dust effect.</summary>
        public static ConfigEntry<bool> EnableCraterDust;

        /// <summary>Smoke column particle count multiplier.</summary>
        public static ConfigEntry<float> SmokeColumnCountMultiplier;

        /// <summary>Smoke column lifetime multiplier.</summary>
        public static ConfigEntry<float> SmokeColumnLifetimeMultiplier;

        /// <summary>Fire embers count multiplier.</summary>
        public static ConfigEntry<float> FireEmbersCountMultiplier;

        /// <summary>Crater dust count multiplier.</summary>
        public static ConfigEntry<float> CraterDustCountMultiplier;

        /// <summary>Crater dust lifetime multiplier.</summary>
        public static ConfigEntry<float> CraterDustLifetimeMultiplier;

        // ══════════════════════════════════════════════════════════════════
        //  ENHANCED BULLET IMPACT EFFECTS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Min streak sparks on regular impact.</summary>
        public static ConfigEntry<int> ImpactStreakSparksMin;

        /// <summary>Max streak sparks on regular impact.</summary>
        public static ConfigEntry<int> ImpactStreakSparksMax;

        /// <summary>Min streak sparks on ricochet.</summary>
        public static ConfigEntry<int> RicochetStreakSparksMin;

        /// <summary>Max streak sparks on ricochet.</summary>
        public static ConfigEntry<int> RicochetStreakSparksMax;

        /// <summary>Min floating sparks on impact.</summary>
        public static ConfigEntry<int> ImpactFloatSparksMin;

        /// <summary>Max floating sparks on impact.</summary>
        public static ConfigEntry<int> ImpactFloatSparksMax;

        /// <summary>Min metal chips on impact.</summary>
        public static ConfigEntry<int> ImpactMetalChipsMin;

        /// <summary>Max metal chips on impact.</summary>
        public static ConfigEntry<int> ImpactMetalChipsMax;

        // ══════════════════════════════════════════════════════════════════
        //  DEBRIS LIFETIME
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Metal debris lifetime (sec).</summary>
        public static ConfigEntry<float> DebrisLifetimeMetal;

        /// <summary>HeavyMetal debris lifetime (sec).</summary>
        public static ConfigEntry<float> DebrisLifetimeHeavyMetal;

        /// <summary>Stone debris lifetime (sec).</summary>
        public static ConfigEntry<float> DebrisLifetimeStone;

        /// <summary>Wood debris lifetime (sec).</summary>
        public static ConfigEntry<float> DebrisLifetimeWood;

        /// <summary>Electronic debris lifetime (sec).</summary>
        public static ConfigEntry<float> DebrisLifetimeElectronic;

        /// <summary>Stuck shrapnel lifetime (sec).</summary>
        public static ConfigEntry<float> StuckLifetime;

        // ══════════════════════════════════════════════════════════════════
        //  INTERACT
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Max distance to destroy debris by clicking (tiles).</summary>
        public static ConfigEntry<float> MaxInteractDistance;

        // ══════════════════════════════════════════════════════════════════
        //  BIND METHOD
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initializes all config entries. Call from Plugin.Awake().
        /// </summary>
        public static void Bind(ConfigFile cfg)
        {
            // ── Performance ──
            MaxAliveDebris = cfg.Bind("Performance", "MaxAliveDebris", 500,
                new ConfigDescription("Max alive physical shrapnel objects. Oldest removed first.",
                    new AcceptableValueRange<int>(50, 2000)));

            MaxAliveVisualParticles = cfg.Bind("Performance", "MaxAliveVisualParticles", 3000,
                new ConfigDescription("Max alive visual particles (ash, dust, sparks). Oldest removed first.",
                    new AcceptableValueRange<int>(500, 10000)));

            SpawnCountMultiplier = cfg.Bind("Performance", "SpawnCountMultiplier", 1f,
                new ConfigDescription("Global spawn count multiplier (0.1-3.0).",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            GlobalMaxSpeed = cfg.Bind("Performance", "GlobalMaxSpeed", 140f,
                new ConfigDescription("Max shrapnel speed (m/s).",
                    new AcceptableValueRange<float>(30f, 500f)));

            GlobalSpeedBoost = cfg.Bind("Performance", "GlobalSpeedBoost", 1.8f,
                new ConfigDescription("Speed multiplier for all shrapnel.",
                    new AcceptableValueRange<float>(0.5f, 5f)));

            MinDistanceBetweenSpawns = cfg.Bind("Performance", "MinDistanceBetweenSpawns", 1.5f,
                "Min distance between spawns in same frame (m).");

            MaxDamagePerFrame = cfg.Bind("Performance", "MaxDamagePerFrame", 5,
                new ConfigDescription("Max DamageBlock calls per frame.",
                    new AcceptableValueRange<int>(1, 20)));

            DebugLogging = cfg.Bind("Performance", "DebugLogging", false,
                "Enable Debug.Log for each explosion.");

            // ── Bullets ──
            EnableBulletFragments = cfg.Bind("Bullets", "EnableFragments", true,
                "Enable physical shrapnel fragments from bullet impacts on metal.");

            EnableBulletImpactEffects = cfg.Bind("Bullets", "EnableImpactEffects", true,
                "Enable visual impact effects (flash, sparks, metal chips) on metal.");

            BulletMinFramesBetweenSpawns = cfg.Bind("Bullets", "MinFramesBetweenSpawns", 1,
                new ConfigDescription("Min frames between bullet shrapnel spawns. Lower = more responsive.",
                    new AcceptableValueRange<int>(0, 30)));

            BulletFragmentsMin = cfg.Bind("Bullets", "FragmentsMin", 1,
                new ConfigDescription("Min fragments from bullet impact.",
                    new AcceptableValueRange<int>(0, 10)));

            BulletFragmentsMax = cfg.Bind("Bullets", "FragmentsMax", 3,
                new ConfigDescription("Max fragments from bullet impact (exclusive).",
                    new AcceptableValueRange<int>(1, 15)));

            BulletBaseSpeed = cfg.Bind("Bullets", "BaseSpeed", 25f,
                "Base speed for bullet fragments (m/s).");

            BulletSparksMin = cfg.Bind("Bullets", "SparksMin", 8,
                new ConfigDescription("(Legacy) Min sparks from bullet impact.",
                    new AcceptableValueRange<int>(0, 30)));

            BulletSparksMax = cfg.Bind("Bullets", "SparksMax", 16,
                new ConfigDescription("(Legacy) Max sparks from bullet impact (exclusive).",
                    new AcceptableValueRange<int>(1, 40)));

            BulletScaleMultiplier = cfg.Bind("Bullets", "ScaleMultiplier", 0.72f,
                "Scale multiplier for bullet shrapnel.");

            BulletHeatMultiplier = cfg.Bind("Bullets", "HeatMultiplier", 0.5f,
                "Heat multiplier for bullet shrapnel.");

            // ── Explosions Classification ──
            ClassifyEpsilon = cfg.Bind("Explosions", "ClassifyEpsilon", 0.5f,
                "Tolerance for explosion parameter comparison.");

            // Dynamite
            DynamiteRange = cfg.Bind("Explosions.Dynamite", "Range", 18f,
                "Expected dynamite range for classification.");
            DynamiteStructuralDamage = cfg.Bind("Explosions.Dynamite", "StructuralDamage", 2000f,
                "Expected dynamite structuralDamage.");
            DynamitePrimaryMin = cfg.Bind("Explosions.Dynamite", "PrimaryMin", 35,
                "Min primary shrapnel count.");
            DynamitePrimaryMax = cfg.Bind("Explosions.Dynamite", "PrimaryMax", 60,
                "Max primary shrapnel count (exclusive).");
            DynamiteSpeed = cfg.Bind("Explosions.Dynamite", "Speed", 40f,
                "Base speed (m/s).");
            DynamiteVisualMin = cfg.Bind("Explosions.Dynamite", "VisualMin", 350,
                "Min visual shrapnel count.");
            DynamiteVisualMax = cfg.Bind("Explosions.Dynamite", "VisualMax", 500,
                "Max visual shrapnel count (exclusive).");

            // Turret
            TurretRange = cfg.Bind("Explosions.Turret", "Range", 9f,
                "Expected turret range for classification.");
            TurretVelocity = cfg.Bind("Explosions.Turret", "Velocity", 15f,
                "Expected turret velocity for classification.");
            TurretPrimaryMin = cfg.Bind("Explosions.Turret", "PrimaryMin", 18,
                "Min primary shrapnel count.");
            TurretPrimaryMax = cfg.Bind("Explosions.Turret", "PrimaryMax", 35,
                "Max primary shrapnel count (exclusive).");
            TurretSpeed = cfg.Bind("Explosions.Turret", "Speed", 40f,
                "Base speed (m/s).");
            TurretVisualMin = cfg.Bind("Explosions.Turret", "VisualMin", 120,
                "Min visual shrapnel count.");
            TurretVisualMax = cfg.Bind("Explosions.Turret", "VisualMax", 200,
                "Max visual shrapnel count (exclusive).");

            // Mine
            MinePrimaryMin = cfg.Bind("Explosions.Mine", "PrimaryMin", 50,
                "Min primary shrapnel count.");
            MinePrimaryMax = cfg.Bind("Explosions.Mine", "PrimaryMax", 85,
                "Max primary shrapnel count (exclusive).");
            MineSpeed = cfg.Bind("Explosions.Mine", "Speed", 50f,
                "Base speed (m/s).");
            MineVisualMin = cfg.Bind("Explosions.Mine", "VisualMin", 180,
                "Min visual shrapnel count.");
            MineVisualMax = cfg.Bind("Explosions.Mine", "VisualMax", 280,
                "Max visual shrapnel count (exclusive).");

            // ── Ground Debris ──
            GroundDebrisRangeMultiplier = cfg.Bind("GroundDebris", "RangeMultiplier", 3.5f,
                new ConfigDescription("Scan radius multiplier relative to explosion range.",
                    new AcceptableValueRange<float>(1f, 6f)));

            GroundDebrisCountMultiplier = cfg.Bind("GroundDebris", "CountMultiplier", 1.5f,
                new ConfigDescription("Particle count multiplier for ground debris.",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            // ── Sparks (Shrapnel Projectile Impacts) ──
            MetalImpactSparksMin = cfg.Bind("Sparks", "MetalImpactMin", 6,
                new ConfigDescription("Min sparks when shrapnel hits metal.",
                    new AcceptableValueRange<int>(1, 20)));

            MetalImpactSparksMax = cfg.Bind("Sparks", "MetalImpactMax", 14,
                new ConfigDescription("Max sparks when shrapnel hits metal (exclusive).",
                    new AcceptableValueRange<int>(2, 30)));

            RicochetSparksMin = cfg.Bind("Sparks", "RicochetSparksMin", 10,
                new ConfigDescription("Min sparks on ricochet.",
                    new AcceptableValueRange<int>(3, 25)));

            RicochetSparksMax = cfg.Bind("Sparks", "RicochetSparksMax", 20,
                new ConfigDescription("Max sparks on ricochet (exclusive).",
                    new AcceptableValueRange<int>(5, 40)));

            RicochetDebrisMin = cfg.Bind("Sparks", "RicochetDebrisMin", 3,
                new ConfigDescription("Min debris particles on ricochet.",
                    new AcceptableValueRange<int>(0, 10)));

            RicochetDebrisMax = cfg.Bind("Sparks", "RicochetDebrisMax", 7,
                new ConfigDescription("Max debris particles on ricochet (exclusive).",
                    new AcceptableValueRange<int>(1, 15)));

            // ── Advanced Explosion Effects ──
            EnableSmokeColumn = cfg.Bind("Effects.Explosion", "EnableSmokeColumn", true,
                "Enable rising smoke column after explosions.");

            EnableFireEmbers = cfg.Bind("Effects.Explosion", "EnableFireEmbers", true,
                "Enable glowing fire embers that scatter and land.");

            EnableCraterDust = cfg.Bind("Effects.Explosion", "EnableCraterDust", true,
                "Enable lingering dust cloud at crater.");

            SmokeColumnCountMultiplier = cfg.Bind("Effects.Explosion", "SmokeColumnCount", 1f,
                new ConfigDescription("Smoke column particle count multiplier.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            SmokeColumnLifetimeMultiplier = cfg.Bind("Effects.Explosion", "SmokeColumnLifetime", 1f,
                new ConfigDescription("Smoke column lifetime multiplier.",
                    new AcceptableValueRange<float>(0.5f, 3f)));

            FireEmbersCountMultiplier = cfg.Bind("Effects.Explosion", "FireEmbersCount", 1f,
                new ConfigDescription("Fire embers count multiplier.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            CraterDustCountMultiplier = cfg.Bind("Effects.Explosion", "CraterDustCount", 1f,
                new ConfigDescription("Crater dust particle count multiplier.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            CraterDustLifetimeMultiplier = cfg.Bind("Effects.Explosion", "CraterDustLifetime", 1f,
                new ConfigDescription("Crater dust lifetime multiplier.",
                    new AcceptableValueRange<float>(0.5f, 3f)));

            // ── Enhanced Bullet Impact Effects ──
            ImpactStreakSparksMin = cfg.Bind("Effects.BulletImpact", "StreakSparksMin", 12,
                new ConfigDescription("Min fast streak sparks on metal impact.",
                    new AcceptableValueRange<int>(3, 30)));

            ImpactStreakSparksMax = cfg.Bind("Effects.BulletImpact", "StreakSparksMax", 22,
                new ConfigDescription("Max fast streak sparks on metal impact.",
                    new AcceptableValueRange<int>(5, 50)));

            RicochetStreakSparksMin = cfg.Bind("Effects.BulletImpact", "RicochetStreakMin", 18,
                new ConfigDescription("Min streak sparks on ricochet.",
                    new AcceptableValueRange<int>(5, 40)));

            RicochetStreakSparksMax = cfg.Bind("Effects.BulletImpact", "RicochetStreakMax", 30,
                new ConfigDescription("Max streak sparks on ricochet.",
                    new AcceptableValueRange<int>(10, 60)));

            ImpactFloatSparksMin = cfg.Bind("Effects.BulletImpact", "FloatSparksMin", 6,
                new ConfigDescription("Min slow floating sparks.",
                    new AcceptableValueRange<int>(0, 20)));

            ImpactFloatSparksMax = cfg.Bind("Effects.BulletImpact", "FloatSparksMax", 12,
                new ConfigDescription("Max slow floating sparks.",
                    new AcceptableValueRange<int>(1, 30)));

            ImpactMetalChipsMin = cfg.Bind("Effects.BulletImpact", "MetalChipsMin", 3,
                new ConfigDescription("Min metal chip debris on impact.",
                    new AcceptableValueRange<int>(0, 15)));

            ImpactMetalChipsMax = cfg.Bind("Effects.BulletImpact", "MetalChipsMax", 8,
                new ConfigDescription("Max metal chip debris on impact.",
                    new AcceptableValueRange<int>(1, 20)));

            // ── Debris Lifetime ──
            DebrisLifetimeMetal = cfg.Bind("Lifetime", "Metal", 900f,
                "Metal debris lifetime (seconds). 900 = 15 minutes.");
            DebrisLifetimeHeavyMetal = cfg.Bind("Lifetime", "HeavyMetal", 1200f,
                "Heavy metal debris lifetime (seconds). 1200 = 20 minutes.");
            DebrisLifetimeStone = cfg.Bind("Lifetime", "Stone", 600f,
                "Stone debris lifetime (seconds). 600 = 10 minutes.");
            DebrisLifetimeWood = cfg.Bind("Lifetime", "Wood", 420f,
                "Wood debris lifetime (seconds). 420 = 7 minutes.");
            DebrisLifetimeElectronic = cfg.Bind("Lifetime", "Electronic", 720f,
                "Electronic debris lifetime (seconds). 720 = 12 minutes.");
            StuckLifetime = cfg.Bind("Lifetime", "Stuck", 60f,
                "Shrapnel stuck in wall lifetime (seconds).");

            // ── Interact ──
            MaxInteractDistance = cfg.Bind("Interact", "MaxClickDistance", 3f,
                "Max distance to destroy debris by clicking (tiles).");
        }
    }
}