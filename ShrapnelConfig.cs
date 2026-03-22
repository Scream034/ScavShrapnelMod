using System;
using System.IO;
using BepInEx.Configuration;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Centralized mod configuration via BepInEx ConfigFile.
    ///
    /// All parameters available in BepInEx/config/ScavShrapnelMod.cfg.
    /// Changes apply on game restart.
    ///
    /// VERSION MIGRATION:
    /// When mod version changes, the old config is backed up to
    /// ScavShrapnelMod.cfg.backup.{version} and a fresh config with
    /// new defaults is created. User is notified in game console.
    ///
    /// Sections:
    /// - Performance: limits, throttle, lifetime
    /// - Performance.Pools: ParticleSystem pool sizes
    /// - Bullets: bullet shrapnel and impact effects
    /// - Explosions: explosion classification and profiles
    /// - Explosions.Mine/Dynamite/Turret: per-profile parameters
    /// - Effects.Explosion: smoke, embers, crater dust
    /// - Effects.BulletImpact: sparks, flash, metal chips
    /// - GroundDebris: surface debris particles
    /// - Sparks: shrapnel projectile impact sparks
    /// - Sparks.Diversity: spark sub-type ratios
    /// - Micro: micro-shrapnel player interaction
    /// - Lifetime: debris/stuck duration
    /// - Interact: player interaction distance
    /// </summary>
    public static class ShrapnelConfig
    {
        //  VERSION TRACKING

        /// <summary>
        /// Stored mod version from config file.
        /// Used to detect version changes and trigger config reset.
        /// </summary>
        private static ConfigEntry<string> _modVersion;

        /// <summary>True if config was reset due to version mismatch this session.</summary>
        public static bool WasReset { get; private set; }

        /// <summary>Previous version before reset, or null if no reset occurred.</summary>
        public static string PreviousVersion { get; private set; }

        /// <summary>Path to backup file if config was reset, or null.</summary>
        public static string BackupPath { get; private set; }

        //  PERFORMANCE

        /// <summary>Max alive physical shrapnel (ShrapnelProjectile) objects.</summary>
        public static ConfigEntry<int> MaxAliveDebris;

        /// <summary>
        /// Max alive visual particles in fallback GameObject mode.
        /// When ParticleSystem pools are active, this only limits fallback particles.
        /// Pool particles are limited by their own MaxParticles setting.
        /// </summary>
        public static ConfigEntry<int> MaxAliveVisualParticles;

        /// <summary>Global spawn count multiplier (0.1 = 10%, 2.0 = 200%).</summary>
        public static ConfigEntry<float> SpawnCountMultiplier;

        /// <summary>Max speed of any shrapnel (m/s).</summary>
        public static ConfigEntry<float> GlobalMaxSpeed;

        /// <summary>Speed multiplier for all shrapnel.</summary>
        public static ConfigEntry<float> GlobalSpeedBoost;

        /// <summary>Min distance between spawns in same frame (m).</summary>
        public static ConfigEntry<float> MinDistanceBetweenSpawns;

        /// <summary>Max DamageBlock calls per frame to prevent lag spikes.</summary>
        public static ConfigEntry<int> MaxDamagePerFrame;

        /// <summary>Enable Debug.Log for each explosion (verbose).</summary>
        public static ConfigEntry<bool> DebugLogging;

        //  PERFORMANCE — PARTICLE POOLS

        /// <summary>
        /// Max concurrent particles in DebrisPool (alpha-blended).
        /// Handles: ground chunks, dust, smoke, ash, metal chips, steam.
        /// </summary>
        public static ConfigEntry<int> PoolDebrisMaxParticles;

        /// <summary>
        /// Max concurrent particles in GlowPool (additive).
        /// Handles: embers, fire, burning chunks, muzzle flash.
        /// </summary>
        public static ConfigEntry<int> PoolGlowMaxParticles;

        /// <summary>
        /// Max concurrent particles in SparkPool (additive + stretch).
        /// Handles: streak sparks, core sparks, micro shrapnel visuals.
        /// </summary>
        public static ConfigEntry<int> PoolSparkMaxParticles;

        //  BULLETS

        /// <summary>Enable physical shrapnel fragments from bullet impacts on metal.</summary>
        public static ConfigEntry<bool> EnableBulletFragments;

        /// <summary>Enable visual impact effects (flash, sparks, metal chips) on metal.</summary>
        public static ConfigEntry<bool> EnableBulletImpactEffects;

        /// <summary>Min frames between bullet shrapnel spawns.</summary>
        public static ConfigEntry<int> BulletMinFramesBetweenSpawns;

        /// <summary>Min fragments per bullet impact.</summary>
        public static ConfigEntry<int> BulletFragmentsMin;

        /// <summary>Max fragments per bullet impact (exclusive).</summary>
        public static ConfigEntry<int> BulletFragmentsMax;

        /// <summary>Base speed for bullet fragments (m/s).</summary>
        public static ConfigEntry<float> BulletBaseSpeed;

        /// <summary>Legacy: min sparks from bullet.</summary>
        public static ConfigEntry<int> BulletSparksMin;

        /// <summary>Legacy: max sparks from bullet.</summary>
        public static ConfigEntry<int> BulletSparksMax;

        /// <summary>Scale multiplier for bullet shrapnel size.</summary>
        public static ConfigEntry<float> BulletScaleMultiplier;

        /// <summary>Heat multiplier for bullet shrapnel glow.</summary>
        public static ConfigEntry<float> BulletHeatMultiplier;

        /// <summary>Turret fragment count multiplier. Turrets fire larger rounds → more fragments.</summary>
        public static ConfigEntry<float> TurretFragmentMultiplier;

        /// <summary>
        /// Spark count multiplier based on bullet power.
        /// Formula: sparks × (1 + (powerRatio - 1) × mult)
        /// </summary>
        public static ConfigEntry<float> BulletDamageSparkMultiplier;

        /// <summary>
        /// Fragment count/speed multiplier based on bullet power.
        /// Formula: frags × (1 + √(powerRatio) × mult)
        /// </summary>
        public static ConfigEntry<float> BulletPowerFragmentMultiplier;

        /// <summary>
        /// Minimum bullet power to spawn physics fragments.
        /// Baseline: pistol=25, rifle=80, shotgun=100+
        /// </summary>
        public static ConfigEntry<float> MinBulletPowerForFragments;

        //  MUZZLE BLAST DUST

        /// <summary>Base scan radius (blocks) for muzzle blast dust. Guns use this value directly.</summary>
        public static ConfigEntry<int> MuzzleBlastRadius;

        /// <summary>Radius multiplier for turrets. Turret radius = MuzzleBlastRadius × this value.</summary>
        public static ConfigEntry<float> MuzzleBlastRadiusTurretMult;

        /// <summary>Base particle count multiplier. Actual count = this × √(powerRatio).</summary>
        public static ConfigEntry<float> MuzzleBlastCountMult;

        /// <summary>Max particles per shot for guns.</summary>
        public static ConfigEntry<int> MuzzleBlastMaxParticles;

        /// <summary>Max particles per shot for turrets.</summary>
        public static ConfigEntry<int> MuzzleBlastMaxParticlesTurret;

        /// <summary>Minimum energy floor (0-1). Higher = more visible particles at max range.</summary>
        public static ConfigEntry<float> MuzzleBlastMinEnergy;

        /// <summary>Enable muzzle blast dust effect.</summary>
        public static ConfigEntry<bool> EnableMuzzleBlastDust;

        //  BULLET IMPACT BLOCK BLAST

        /// <summary>Enable dust from nearby blocks when a bullet hits a block.</summary>
        public static ConfigEntry<bool> EnableBulletImpactBlockBlast;

        /// <summary>Scan radius (blocks) around bullet impact point for block blast dust.</summary>
        public static ConfigEntry<int> BulletImpactBlastRadius;

        /// <summary>
        /// Base particle count multiplier for bullet impact block blast.
        /// Actual count per surface = this × √(powerRatio) × distanceFalloff.
        /// </summary>
        public static ConfigEntry<float> BulletImpactBlastCountMult;

        /// <summary>Max particles total per bullet impact block blast.</summary>
        public static ConfigEntry<int> BulletImpactBlastMaxParticles;

        /// <summary>
        /// Minimum energy floor (0-1) for impact blast particles at max range.
        /// Lower than muzzle blast (0.2 vs 0.5) — impact is more localized.
        /// </summary>
        public static ConfigEntry<float> BulletImpactBlastMinEnergy;

        //  BULLET IMPACT — KINETIC TRANSFER

        /// <summary>
        /// Directional energy multiplier for bullet impact dust plume (0–2).
        /// Controls how many bonus particles spawn along bullet travel axis.
        /// 0 = no kinetic plume. 1 = balanced. 2 = very directional spray.
        /// </summary>
        public static ConfigEntry<float> BulletImpactKineticTransfer;

        /// <summary>
        /// Scan radius multiplier when bullet hits metal blocks.
        /// WHY: Metal conducts kinetic energy further — shockwave travels
        /// through connected metal structure, disturbing distant surfaces.
        /// 1.0 = no bonus. 1.5 = 50% larger radius for metal.
        /// </summary>
        public static ConfigEntry<float> BulletImpactMetalConductivity;

        //  EXPLOSIONS — CLASSIFICATION

        /// <summary>Tolerance for explosion parameter comparison (+/- epsilon).</summary>
        public static ConfigEntry<float> ClassifyEpsilon;

        //  Dynamite 
        public static ConfigEntry<float> DynamiteRange;
        public static ConfigEntry<float> DynamiteStructuralDamage;
        public static ConfigEntry<int> DynamitePrimaryMin;
        public static ConfigEntry<int> DynamitePrimaryMax;
        public static ConfigEntry<float> DynamiteSpeed;
        public static ConfigEntry<int> DynamiteVisualMin;
        public static ConfigEntry<int> DynamiteVisualMax;

        //  Turret 
        public static ConfigEntry<float> TurretRange;
        public static ConfigEntry<float> TurretVelocity;
        public static ConfigEntry<int> TurretPrimaryMin;
        public static ConfigEntry<int> TurretPrimaryMax;
        public static ConfigEntry<float> TurretSpeed;
        public static ConfigEntry<int> TurretVisualMin;
        public static ConfigEntry<int> TurretVisualMax;

        //  Mine 
        public static ConfigEntry<int> MinePrimaryMin;
        public static ConfigEntry<int> MinePrimaryMax;
        public static ConfigEntry<float> MineSpeed;
        public static ConfigEntry<int> MineVisualMin;
        public static ConfigEntry<int> MineVisualMax;

        //  GROUND DEBRIS

        /// <summary>Scan radius multiplier relative to explosion range.</summary>
        public static ConfigEntry<float> GroundDebrisRangeMultiplier;

        /// <summary>Particle count multiplier for ground debris.</summary>
        public static ConfigEntry<float> GroundDebrisCountMultiplier;

        /// <summary>Shockwave propagation speed (world units/sec).</summary>
        public static ConfigEntry<float> GroundDebrisShockwaveSpeed;

        /// <summary>Base particle budget per exposed block face.</summary>
        public static ConfigEntry<int> GroundDebrisBudgetPerBlock;

        /// <summary>Max total ground debris particles per explosion.</summary>
        public static ConfigEntry<int> GroundDebrisMaxTotal;

        /// <summary>Multiplier for block debris particles from destroyed blocks.</summary>
        public static ConfigEntry<float> BlockDebrisCountMultiplier;

        //  SPARKS — SHRAPNEL PROJECTILE IMPACTS

        /// <summary>Min sparks when shrapnel hits metal block.</summary>
        public static ConfigEntry<int> MetalImpactSparksMin;

        /// <summary>Max sparks when shrapnel hits metal block (exclusive).</summary>
        public static ConfigEntry<int> MetalImpactSparksMax;

        /// <summary>Min sparks on ricochet off metal.</summary>
        public static ConfigEntry<int> RicochetSparksMin;

        /// <summary>Max sparks on ricochet off metal (exclusive).</summary>
        public static ConfigEntry<int> RicochetSparksMax;

        /// <summary>Min debris particles from ricochet scatter.</summary>
        public static ConfigEntry<int> RicochetDebrisMin;

        /// <summary>Max debris particles from ricochet scatter (exclusive).</summary>
        public static ConfigEntry<int> RicochetDebrisMax;

        //  SPARK DIVERSITY

        /// <summary>Fraction of sparks that are thin/fast needles (0.0-1.0).</summary>
        public static ConfigEntry<float> SparkNeedleFraction;

        /// <summary>Fraction of sparks that are medium trailing (0.0-1.0).</summary>
        public static ConfigEntry<float> SparkMediumFraction;

        //  ADVANCED EXPLOSION EFFECTS

        /// <summary>Enable rising smoke column after explosions.</summary>
        public static ConfigEntry<bool> EnableSmokeColumn;

        /// <summary>Enable glowing fire embers that scatter and land.</summary>
        public static ConfigEntry<bool> EnableFireEmbers;

        /// <summary>Enable lingering dust cloud at crater.</summary>
        public static ConfigEntry<bool> EnableCraterDust;

        /// <summary>Smoke column particle count multiplier.</summary>
        public static ConfigEntry<float> SmokeColumnCountMultiplier;

        /// <summary>Smoke column lifetime multiplier.</summary>
        public static ConfigEntry<float> SmokeColumnLifetimeMultiplier;

        /// <summary>Fire embers count multiplier.</summary>
        public static ConfigEntry<float> FireEmbersCountMultiplier;

        /// <summary>Crater dust particle count multiplier.</summary>
        public static ConfigEntry<float> CraterDustCountMultiplier;

        /// <summary>Crater dust lifetime multiplier.</summary>
        public static ConfigEntry<float> CraterDustLifetimeMultiplier;

        //  ENHANCED BULLET IMPACT EFFECTS

        /// <summary>Min fast streak sparks on regular metal impact.</summary>
        public static ConfigEntry<int> ImpactStreakSparksMin;

        /// <summary>Max fast streak sparks on regular metal impact.</summary>
        public static ConfigEntry<int> ImpactStreakSparksMax;

        /// <summary>Min streak sparks on ricochet.</summary>
        public static ConfigEntry<int> RicochetStreakSparksMin;

        /// <summary>Max streak sparks on ricochet.</summary>
        public static ConfigEntry<int> RicochetStreakSparksMax;

        /// <summary>Min slow floating sparks on impact.</summary>
        public static ConfigEntry<int> ImpactFloatSparksMin;

        /// <summary>Max slow floating sparks on impact.</summary>
        public static ConfigEntry<int> ImpactFloatSparksMax;

        /// <summary>Min metal chip debris on impact.</summary>
        public static ConfigEntry<int> ImpactMetalChipsMin;

        /// <summary>Max metal chip debris on impact.</summary>
        public static ConfigEntry<int> ImpactMetalChipsMax;

        //  MICRO SHRAPNEL

        /// <summary>Enable micro shrapnel spawning during explosions.</summary>
        public static ConfigEntry<bool> EnableMicroShrapnel;

        /// <summary>Min skin damage from micro shrapnel.</summary>
        public static ConfigEntry<float> MicroDamageMin;

        /// <summary>Max skin damage from micro shrapnel.</summary>
        public static ConfigEntry<float> MicroDamageMax;

        /// <summary>Min bleed from micro shrapnel hit.</summary>
        public static ConfigEntry<float> MicroBleedMin;

        /// <summary>Max bleed from micro shrapnel hit.</summary>
        public static ConfigEntry<float> MicroBleedMax;

        /// <summary>Shock multiplier for micro shrapnel.</summary>
        public static ConfigEntry<float> MicroShockMultiplier;

        /// <summary>Adrenaline base from micro shrapnel hit.</summary>
        public static ConfigEntry<float> MicroAdrenalineBase;

        /// <summary>Visual sparks per micro shrapnel piece.</summary>
        public static ConfigEntry<int> MicroSparksPerPiece;

        //  DEBRIS LIFETIME

        /// <summary>Metal debris lifetime (seconds).</summary>
        public static ConfigEntry<float> DebrisLifetimeMetal;

        /// <summary>Heavy metal debris lifetime (seconds).</summary>
        public static ConfigEntry<float> DebrisLifetimeHeavyMetal;

        /// <summary>Stone debris lifetime (seconds).</summary>
        public static ConfigEntry<float> DebrisLifetimeStone;

        /// <summary>Wood debris lifetime (seconds).</summary>
        public static ConfigEntry<float> DebrisLifetimeWood;

        /// <summary>Electronic debris lifetime (seconds).</summary>
        public static ConfigEntry<float> DebrisLifetimeElectronic;

        /// <summary>Shrapnel stuck in wall lifetime (seconds).</summary>
        public static ConfigEntry<float> StuckLifetime;

        //  INTERACT

        /// <summary>Max distance to destroy debris by clicking (tiles).</summary>
        public static ConfigEntry<float> MaxInteractDistance;

        //  BIND

        /// <summary>
        /// Initializes all config entries from BepInEx ConfigFile.
        /// Call from Plugin.Awake() before any other mod code runs.
        ///
        /// VERSION HANDLING:
        /// 1. Config doesn't exist = create fresh with current version
        /// 2. Version matches = load normally
        /// 3. Version differs = backup old, delete, create fresh, notify user
        /// </summary>
        public static void Bind(ConfigFile cfg)
        {
            string currentVersion = Plugin.Version;

            bool needsReset = CheckVersionMismatch(cfg, currentVersion);

            if (needsReset)
            {
                BackupAndResetConfig(cfg, currentVersion);
            }

            //  Internal version stamp 
            _modVersion = cfg.Bind("Internal", "_ModVersion", currentVersion,
                new ConfigDescription(
                    "Mod version that created this config. " +
                    "When mod updates, old config is backed up and reset. " +
                    "Do NOT edit manually.",
                    null,
                    new object[] { "HideInUI" }));

            if (needsReset)
            {
                _modVersion.Value = currentVersion;
            }

            //  Performance 
            MaxAliveDebris = cfg.Bind("Performance", "MaxAliveDebris", 500,
                new ConfigDescription("Max alive physical shrapnel objects. Oldest removed first.",
                    new AcceptableValueRange<int>(50, 2000)));

            MaxAliveVisualParticles = cfg.Bind("Performance", "MaxAliveVisualParticles", 5000,
                new ConfigDescription(
                    "Max alive visual particles in fallback mode. " +
                    "When ParticleSystem pools are active, this only limits fallback GameObjects.",
                    new AcceptableValueRange<int>(500, 15000)));

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

            //  Particle Pools 
            PoolDebrisMaxParticles = cfg.Bind("Performance.Pools", "DebrisMaxParticles", 6000,
                new ConfigDescription(
                    "Max concurrent particles in DebrisPool (alpha-blended). " +
                    "Handles ground chunks, dust, smoke, ash, metal chips, steam.",
                    new AcceptableValueRange<int>(1000, 20000)));

            PoolGlowMaxParticles = cfg.Bind("Performance.Pools", "GlowMaxParticles", 2500,
                new ConfigDescription(
                    "Max concurrent particles in GlowPool (additive). " +
                    "Handles embers, fire, burning chunks.",
                    new AcceptableValueRange<int>(500, 10000)));

            PoolSparkMaxParticles = cfg.Bind("Performance.Pools", "SparkMaxParticles", 2000,
                new ConfigDescription(
                    "Max concurrent particles in SparkPool (additive + stretch). " +
                    "Handles streak sparks, core sparks, micro shrapnel visuals.",
                    new AcceptableValueRange<int>(500, 8000)));

            //  Bullets 
            EnableBulletFragments = cfg.Bind("Bullets", "EnableFragments", true,
                "Enable physical shrapnel fragments from bullet impacts on metal.");

            EnableBulletImpactEffects = cfg.Bind("Bullets", "EnableImpactEffects", true,
                "Enable visual impact effects (flash, sparks, metal chips) on metal.");

            BulletMinFramesBetweenSpawns = cfg.Bind("Bullets", "MinFramesBetweenSpawns", 1,
                new ConfigDescription("Min frames between bullet shrapnel spawns.",
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

            TurretFragmentMultiplier = cfg.Bind("Bullets", "TurretFragmentMultiplier", 2.5f,
                new ConfigDescription(
                    "Fragment count multiplier for turret shots. " +
                    "Turrets fire larger caliber rounds producing more fragments.",
                    new AcceptableValueRange<float>(1f, 5f)));

            BulletDamageSparkMultiplier = cfg.Bind("Bullets", "DamageSparkMultiplier", 1.0f,
                new ConfigDescription(
                    "Spark count scaling from bullet power. " +
                    "1.0 = linear, 2.0 = double effect.",
                    new AcceptableValueRange<float>(0f, 5f)));

            BulletPowerFragmentMultiplier = cfg.Bind("Bullets", "PowerFragmentMultiplier", 0.5f,
                new ConfigDescription(
                    "Fragment count/speed scaling from bullet power. " +
                    "Uses √(power) for realistic energy distribution.",
                    new AcceptableValueRange<float>(0f, 3f)));

            MinBulletPowerForFragments = cfg.Bind("Bullets", "MinPowerForFragments", 20f,
                new ConfigDescription(
                    "Minimum bullet power to spawn physics fragments. " +
                    "Below this = visual sparks only.",
                    new AcceptableValueRange<float>(5f, 100f)));

            //  Muzzle Blast Dust
            EnableMuzzleBlastDust = cfg.Bind("Effects.MuzzleBlast", "Enable", true,
                "Enable dust/debris kicked up from nearby surfaces when firing.");

            MuzzleBlastRadius = cfg.Bind("Effects.MuzzleBlast", "Radius", 8,
                new ConfigDescription(
                    "Base scan radius (blocks) for muzzle blast dust. " +
                    "Guns use this value directly. Surfaces within this radius " +
                    "of the barrel will spawn debris particles.",
                    new AcceptableValueRange<int>(1, 20)));

            MuzzleBlastRadiusTurretMult = cfg.Bind("Effects.MuzzleBlast", "RadiusTurretMultiplier", 1.5f,
                new ConfigDescription(
                    "Radius multiplier for turrets. " +
                    "Turret scan radius = Radius × this value. " +
                    "Default 1.5: guns=8 blocks, turrets=12 blocks.",
                    new AcceptableValueRange<float>(1f, 4f)));

            MuzzleBlastCountMult = cfg.Bind("Effects.MuzzleBlast", "CountMultiplier", 3f,
                new ConfigDescription(
                    "Base particle count multiplier per surface. " +
                    "Actual count = this × √(bulletPower / 25). " +
                    "Higher = more particles per exposed block face.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            MuzzleBlastMaxParticles = cfg.Bind("Effects.MuzzleBlast", "MaxParticles", 150,
                new ConfigDescription(
                    "Maximum particles per shot for guns (pistol, rifle, shotgun). " +
                    "Caps total debris to prevent performance issues.",
                    new AcceptableValueRange<int>(20, 500)));

            MuzzleBlastMaxParticlesTurret = cfg.Bind("Effects.MuzzleBlast", "MaxParticlesTurret", 300,
                new ConfigDescription(
                    "Maximum particles per shot for turrets. " +
                    "Higher than guns due to larger scan radius.",
                    new AcceptableValueRange<int>(50, 800)));

            MuzzleBlastMinEnergy = cfg.Bind("Effects.MuzzleBlast", "MinEnergy", 0.5f,
                new ConfigDescription(
                    "Minimum energy floor (0-1) for particles at max range. " +
                    "Higher = more visible particles even at edge of blast radius. " +
                    "0.5 = half intensity at max range, 1.0 = full intensity everywhere.",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            //  Bullet Impact Block Blast
            EnableBulletImpactBlockBlast = cfg.Bind("Effects.BulletImpactBlast", "Enable", true,
                "Enable dust/debris emitted from nearby blocks when a bullet hits a block.");

            BulletImpactBlastRadius = cfg.Bind("Effects.BulletImpactBlast", "Radius", 4,
                new ConfigDescription(
                    "Scan radius (blocks) around bullet impact point. " +
                    "Metal blocks extend this via MetalConductivity multiplier. " +
                    "Range 2-6 recommended for visible directional dust plume.",
                    new AcceptableValueRange<int>(1, 8)));

            BulletImpactBlastCountMult = cfg.Bind("Effects.BulletImpactBlast", "CountMultiplier", 2.0f,
                new ConfigDescription(
                    "Base particle count per surface. " +
                    "Actual count = this × √(powerRatio) × energyFalloff. " +
                    "Higher than before (was 1.5) for visible dust plumes.",
                    new AcceptableValueRange<float>(0.5f, 6f)));

            BulletImpactBlastMaxParticles = cfg.Bind("Effects.BulletImpactBlast", "MaxParticles", 120,
                new ConfigDescription(
                    "Max total particles per bullet impact block blast. " +
                    "Doubled from 60 to support kinetic plume + conduction sparks.",
                    new AcceptableValueRange<int>(20, 400)));

            BulletImpactBlastMinEnergy = cfg.Bind("Effects.BulletImpactBlast", "MinEnergy", 0.15f,
                new ConfigDescription(
                    "Minimum energy floor (0-1) at max radius. " +
                    "Lower than muzzle blast — impact energy drops sharply.",
                    new AcceptableValueRange<float>(0.0f, 0.8f)));

            BulletImpactKineticTransfer = cfg.Bind("Effects.BulletImpactBlast", "KineticTransfer", 1.0f,
                new ConfigDescription(
                    "Directional energy multiplier for bullet impact dust plume. " +
                    "Controls bonus particles along bullet travel axis. " +
                    "0 = no kinetic plume, 1 = balanced, 2 = very directional spray.",
                    new AcceptableValueRange<float>(0f, 2f)));

            BulletImpactMetalConductivity = cfg.Bind("Effects.BulletImpactBlast", "MetalConductivity", 1.5f,
                new ConfigDescription(
                    "Scan radius multiplier when bullet hits metal blocks. " +
                    "Metal conducts kinetic energy further through structure. " +
                    "1.0 = no bonus, 1.5 = 50% larger scan radius for metal.",
                    new AcceptableValueRange<float>(1f, 3f)));

            //  Explosions Classification 
            ClassifyEpsilon = cfg.Bind("Explosions", "ClassifyEpsilon", 0.5f,
                "Tolerance for explosion parameter comparison.");

            //  Dynamite 
            DynamiteRange = cfg.Bind("Explosions.Dynamite", "Range", 18f,
                "Expected dynamite range for classification.");
            DynamiteStructuralDamage = cfg.Bind("Explosions.Dynamite", "StructuralDamage", 2000f,
                "Expected dynamite structuralDamage.");
            DynamitePrimaryMin = cfg.Bind("Explosions.Dynamite", "PrimaryMin", 15,
                new ConfigDescription("Min primary shrapnel count.",
                    new AcceptableValueRange<int>(5, 50)));
            DynamitePrimaryMax = cfg.Bind("Explosions.Dynamite", "PrimaryMax", 30,
                new ConfigDescription("Max primary shrapnel count (exclusive).",
                    new AcceptableValueRange<int>(10, 80)));
            DynamiteSpeed = cfg.Bind("Explosions.Dynamite", "Speed", 40f,
                "Base speed (m/s).");
            DynamiteVisualMin = cfg.Bind("Explosions.Dynamite", "VisualMin", 350,
                "Min visual shrapnel count.");
            DynamiteVisualMax = cfg.Bind("Explosions.Dynamite", "VisualMax", 500,
                "Max visual shrapnel count (exclusive).");

            //  Turret 
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

            //  Mine 
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

            //  Ground Debris 
            GroundDebrisRangeMultiplier = cfg.Bind("GroundDebris", "RangeMultiplier", 3.5f,
                new ConfigDescription("Scan radius multiplier relative to explosion range.",
                    new AcceptableValueRange<float>(1f, 8f)));

            GroundDebrisCountMultiplier = cfg.Bind("GroundDebris", "CountMultiplier", 2.0f,
                new ConfigDescription("Particle count multiplier for ground debris.",
                    new AcceptableValueRange<float>(0.5f, 6f)));

            GroundDebrisShockwaveSpeed = cfg.Bind("GroundDebris", "ShockwaveSpeed", 40f,
                new ConfigDescription(
                    "Shockwave propagation speed (world units/sec). " +
                    "Controls delay before distant debris appears.",
                    new AcceptableValueRange<float>(10f, 200f)));

            GroundDebrisBudgetPerBlock = cfg.Bind("GroundDebris", "BudgetPerBlock", 75,
                new ConfigDescription("Base particle budget per surface block.",
                    new AcceptableValueRange<int>(4, 400)));

            GroundDebrisMaxTotal = cfg.Bind("GroundDebris", "MaxTotal", 1500,
                new ConfigDescription("Max total ground debris particles per explosion.",
                    new AcceptableValueRange<int>(200, 12000)));

            BlockDebrisCountMultiplier = cfg.Bind("GroundDebris", "BlockDebrisCountMultiplier", 1f,
                new ConfigDescription("Multiplier for block debris from destroyed blocks.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            //  Sparks 
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

            //  Spark Diversity 
            SparkNeedleFraction = cfg.Bind("Sparks.Diversity", "NeedleFraction", 0.40f,
                new ConfigDescription(
                    "Fraction of sparks that are thin/fast needles.",
                    new AcceptableValueRange<float>(0.1f, 0.8f)));

            SparkMediumFraction = cfg.Bind("Sparks.Diversity", "MediumFraction", 0.35f,
                new ConfigDescription(
                    "Fraction of sparks that are medium trailing. " +
                    "Remainder (1 - needle - medium) are thick/hot globs.",
                    new AcceptableValueRange<float>(0.1f, 0.7f)));

            //  Advanced Explosion Effects 
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

            //  Enhanced Bullet Impact Effects 
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

            //  Micro Shrapnel 
            EnableMicroShrapnel = cfg.Bind("Micro", "Enable", true,
                "Enable micro shrapnel spawning during explosions.");

            MicroDamageMin = cfg.Bind("Micro", "DamageMin", 1f,
                new ConfigDescription("Min skin damage from micro shrapnel.",
                    new AcceptableValueRange<float>(0f, 10f)));

            MicroDamageMax = cfg.Bind("Micro", "DamageMax", 3f,
                new ConfigDescription("Max skin damage from micro shrapnel.",
                    new AcceptableValueRange<float>(0.5f, 15f)));

            MicroBleedMin = cfg.Bind("Micro", "BleedMin", 0.2f,
                new ConfigDescription("Min bleed from micro shrapnel.",
                    new AcceptableValueRange<float>(0f, 5f)));

            MicroBleedMax = cfg.Bind("Micro", "BleedMax", 0.8f,
                new ConfigDescription("Max bleed from micro shrapnel.",
                    new AcceptableValueRange<float>(0.1f, 8f)));

            MicroShockMultiplier = cfg.Bind("Micro", "ShockMultiplier", 0.5f,
                new ConfigDescription("Shock multiplier for micro hits.",
                    new AcceptableValueRange<float>(0f, 2f)));

            MicroAdrenalineBase = cfg.Bind("Micro", "AdrenalineBase", 5f,
                new ConfigDescription("Adrenaline base from micro hit.",
                    new AcceptableValueRange<float>(0f, 30f)));

            MicroSparksPerPiece = cfg.Bind("Micro", "SparksPerPiece", 3,
                new ConfigDescription("Visual sparks per micro shrapnel piece.",
                    new AcceptableValueRange<int>(1, 8)));

            //  Debris Lifetime 
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

            //  Interact 
            MaxInteractDistance = cfg.Bind("Interact", "MaxClickDistance", 3f,
                "Max distance to destroy debris by clicking (tiles).");

            //  Flush to disk 
            cfg.Save();
        }

        //  VERSION CHECK & BACKUP

        /// <summary>
        /// Checks config file on disk for version mismatch BEFORE BepInEx binds entries.
        ///
        /// WHY read raw file: Once Bind() is called, BepInEx caches the value.
        /// We need to detect mismatch before that to decide whether to delete
        /// the file and let Bind() create fresh defaults.
        /// </summary>
        /// <returns>true if config exists with different/missing version</returns>
        private static bool CheckVersionMismatch(ConfigFile cfg, string currentVersion)
        {
            string configPath = cfg.ConfigFilePath;

            // No config = fresh install, create normally
            if (!File.Exists(configPath))
            {
                return false;
            }

            try
            {
                string[] lines = File.ReadAllLines(configPath);
                string savedVersion = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();

                    // WHY: BepInEx config format is "Key = Value"
                    // We look for "_ModVersion = X.Y.Z"
                    if (trimmed.StartsWith("_ModVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIndex = trimmed.IndexOf('=');
                        if (eqIndex > 0 && eqIndex < trimmed.Length - 1)
                        {
                            savedVersion = trimmed.Substring(eqIndex + 1).Trim();
                            break;
                        }
                    }
                }

                // No version key = pre-versioning config, needs reset
                if (string.IsNullOrEmpty(savedVersion))
                {
                    PreviousVersion = "pre-0.8.0";
                    return true;
                }

                // Version matches = no reset
                if (string.Equals(savedVersion, currentVersion, StringComparison.Ordinal))
                {
                    return false;
                }

                // Version differs = needs reset
                PreviousVersion = savedVersion;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[Config] Version check failed: {e.Message}");
                // WHY: On read error, don't destroy user's config
                return false;
            }
        }

        /// <summary>
        /// Backs up old config and deletes original so BepInEx creates fresh defaults.
        ///
        /// Backup naming: ScavShrapnelMod.cfg.backup.{oldVersion}
        /// Collision handling: appends timestamp if backup already exists.
        /// </summary>
        private static void BackupAndResetConfig(ConfigFile cfg, string currentVersion)
        {
            string configPath = cfg.ConfigFilePath;
            string versionSuffix = PreviousVersion ?? "unknown";

            foreach (char c in Path.GetInvalidFileNameChars())
                versionSuffix = versionSuffix.Replace(c, '_');

            string backupName = $"{configPath}.backup.{versionSuffix}";

            if (File.Exists(backupName))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupName = $"{configPath}.backup.{versionSuffix}.{timestamp}";
            }

            try
            {
                File.Copy(configPath, backupName, overwrite: false);
                BackupPath = backupName;

                File.Delete(configPath);
                cfg.Reload();

                WasReset = true;

                Plugin.Log?.LogInfo(
                    $"[Config] Version changed: {PreviousVersion} → {currentVersion}");
                Plugin.Log?.LogInfo(
                    $"[Config] Old config backed up: {Path.GetFileName(backupName)}");
                Plugin.Log?.LogInfo(
                    $"[Config] Fresh config created with v{currentVersion} defaults.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[Config] Backup/reset failed: {e.Message}");
                WasReset = false;
            }
        }

        /// <summary>
        /// Returns user-friendly notification about config reset for the in-game console.
        /// Returns null if no reset occurred.
        /// </summary>
        public static string GetResetNotification()
        {
            if (!WasReset) return null;

            string backupFile = BackupPath != null
                ? Path.GetFileName(BackupPath)
                : "unknown";

            return $"[{Plugin.Name}] Config reset: v{PreviousVersion} → v{Plugin.Version}. " +
                   $"Old settings backed up to: {backupFile}";
        }
    }
}