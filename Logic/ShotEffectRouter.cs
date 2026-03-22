using System;
using System.Reflection;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Central router for all bullet/turret shot effects with power-based scaling.
    /// 
    /// <para><b>Bullet Power Formula:</b>
    /// <c>power = structureDamage × (1 + knockBack/10) × shotsPerFire</c></para>
    /// 
    /// <para><b>Power Examples:</b>
    /// Pistol ≈ 45, Rifle ≈ 189, Turret = 80, Shotgun ≈ 500.</para>
    /// 
    /// <para><b>Effects Pipeline (per shot):</b>
    /// 1. Muzzle flash (bright unlit particle)
    /// 2. Muzzle blast dust (GroundDebrisLogic on nearby surfaces)
    /// 3. Barrel smoke (propellant gas wisps)
    /// 4. Raycast → impact point
    /// 5. Spark shower / full impact (metal)
    /// 6. Block debris (BlockClassifier material)
    /// 7. Gunpowder smoke at impact
    /// 8. Physics fragments (metal only, wide scatter)</para>
    /// </summary>
    public static class ShotEffectRouter
    {
        /// <summary>Shot origin type for per-source customization.</summary>
        public enum ShotSource { Gun = 0, Turret = 1, Custom = 2 }

        #region Constants

        private const int DedupeSlots = 32;
        private const int GroundLayer = 6;
        private const float BaselinePower = 25f;
        private const float TurretBasePower = 80f;
        private const float TurretSparkBoost = 2.0f;
        private const float MaxBulletFragSpeed = 18f;
        private const int MaxBulletFragments = 5;
        private const float BulletFragDamageMult = 0.4f;
        private const int MaxImpactDust = 20;
        private const int MaxSmoke = 10;

        #endregion

        #region Static State

        private static readonly int[] _recentHashes = new int[DedupeSlots];
        private static readonly RaycastHit2D[] _hitBuffer = new RaycastHit2D[16];

        private static int _recentCount;
        private static int _dedupeFrame = -1;
        private static int _callCount;
        private static int _bulletMask = -1;

        private static bool _gunFieldsInit;
        private static FieldInfo _structureDamageField;
        private static FieldInfo _knockBackField;
        private static FieldInfo _shotsPerFireField;

        #endregion

        #region Properties

        private static int BulletMask => _bulletMask != -1
            ? _bulletMask
            : (_bulletMask = LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor"));

        #endregion

        #region Public API

        /// <summary>Entry point with gun reference. Reads stats for power scaling.</summary>
        public static void OnBulletFired(Vector2 barrelPos, Vector2 fireDir,
            ShotSource source, GunScript gun)
        {
            OnBulletFiredCore(barrelPos, fireDir, source, ComputeBulletPower(gun, source));
        }

        /// <summary>Entry point without gun reference. Uses baseline/turret power.</summary>
        public static void OnBulletFired(Vector2 barrelPos, Vector2 fireDir,
            ShotSource source = ShotSource.Gun)
        {
            float power = source == ShotSource.Turret ? TurretBasePower : BaselinePower;
            OnBulletFiredCore(barrelPos, fireDir, source, power);
        }

        /// <summary>
        /// Reads structureDamage from GunScript via reflection.
        /// Uses <see cref="Convert.ToSingle"/> for type-safe unboxing.
        /// </summary>
        public static float ReadGunDamage(GunScript gun)
        {
            if (gun == null) return 0f;
            EnsureGunFields();
            if (_structureDamageField == null) return 0f;
            try { return Convert.ToSingle(_structureDamageField.GetValue(gun)); }
            catch { return 0f; }
        }

        #endregion

        #region Core Pipeline

        private static void OnBulletFiredCore(Vector2 barrelPos, Vector2 fireDir,
            ShotSource source, float bulletPower)
        {
            _callCount++;

            try
            {
                bool impact = ShrapnelConfig.EnableBulletImpactEffects.Value;
                bool frags = ShrapnelConfig.EnableBulletFragments.Value;

#if DEBUG
                Plugin.Log.LogInfo(
                    $"[Router] #{_callCount} pos={barrelPos} src={source}" +
                    $" power={bulletPower:F1} impact={impact} frags={frags}");
#endif

                if (!impact && !frags) return;
                if (!TryRegisterShot(barrelPos)) return;
                if (!Plugin.VisualsWarmed) Plugin.WarmVisuals();

                fireDir = fireDir.normalized;
                float powerRatio = bulletPower / BaselinePower;
                float sourceSparkMult = source == ShotSource.Turret ? TurretSparkBoost : 1f;
                float sparkScale = (1f + (powerRatio - 1f)
                    * ShrapnelConfig.BulletDamageSparkMultiplier.Value) * sourceSparkMult;
                float fragScale = 1f + Mathf.Sqrt(powerRatio)
                    * ShrapnelConfig.BulletPowerFragmentMultiplier.Value;

#if DEBUG
                Plugin.Log.LogInfo($"[Router] powerRatio={powerRatio:F2}" +
                    $" sparkScale={sparkScale:F2} fragScale={fragScale:F2}" +
                    $" srcMult={sourceSparkMult:F1}");
#endif

                // ── Stage 1: Muzzle effects (at barrel) ──
                if (impact)
                {
                    try { BulletImpactEffects.SpawnMuzzleFlash(barrelPos, fireDir); }
                    catch (Exception e) { Plugin.Log.LogError($"[Router] MuzzleFlash: {e.Message}"); }

                    try { SpawnMuzzleBlastDust(barrelPos, bulletPower, source); }
                    catch (Exception e) { Plugin.Log.LogError($"[Router] MuzzleDust: {e.Message}"); }

                    try { SpawnBarrelSmoke(barrelPos, fireDir, bulletPower); }
                    catch (Exception e) { Plugin.Log.LogError($"[Router] BarrelSmoke: {e.Message}"); }
                }

                // ── Stage 2: Raycast to find impact point ──
                if (!FindBlockHit(barrelPos, fireDir, out Vector2 hitPt,
                    out Vector2 hitNorm, out BlockInfo block))
                {
#if DEBUG
                    Plugin.Log.LogInfo("[Router] No ground hit");
#endif
                    if (impact) SpawnGenericSparks(barrelPos, fireDir, bulletPower);
                    return;
                }

                bool metallic = block != null && block.metallic;
                var rng = CreateHitRng(hitPt);
                bool ricochet = Mathf.Abs(Vector2.Dot(fireDir, hitNorm)) < 0.5f;

                // ── Stage 3: Impact effects (at hit point) ──
                if (impact)
                {
                    try
                    {
                        if (metallic)
                            BulletImpactEffects.SpawnFullImpact(
                                hitPt, hitNorm, rng, ricochet, sparkScale);
                        else
                            BulletImpactEffects.SpawnSparkShower(
                                hitPt, hitNorm, rng, sparkScale * 0.3f);

                        SpawnBlockHitDebris(hitPt, hitNorm, block, rng, bulletPower);
                        SpawnGunpowderSmoke(hitPt, hitNorm, rng, bulletPower);

                        // WHY: Bullet impact block blast now uses bullet direction
                        // for kinetic energy transfer — dust flies in bullet's
                        // travel direction, not just outward from faces.
                        SpawnBulletImpactBlockDust(hitPt, bulletPower, fireDir);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[Router] Impact: {e.Message}"); }
                }

                // ── Stage 4: Physics fragments (metal only) ──
                if (frags && metallic && MultiplayerHelper.ShouldSpawnPhysicsShrapnel
                    && bulletPower >= ShrapnelConfig.MinBulletPowerForFragments.Value)
                {
                    try { SpawnFragments(hitPt, hitNorm, block, rng, source, fragScale); }
                    catch (Exception e) { Plugin.Log.LogError($"[Router] Frags: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Router] CRITICAL: {e.Message}");
            }
        }

        #endregion

        #region Stage 3: Bullet Impact Block Dust (kinetic transfer)

        /// <summary>
        /// Bullet impact block dust with kinetic energy transfer.
        /// WHY: Separated from SpawnBlockHitDebris because this affects NEARBY
        /// blocks (not just the hit block) and needs bullet direction for
        /// directional spray via <see cref="GroundDebrisLogic.SpawnFromBulletImpact(Vector2, float, Vector2)"/>.
        /// </summary>
        private static void SpawnBulletImpactBlockDust(Vector2 hitPoint,
            float bulletPower, Vector2 bulletDir)
        {
            float powerRatio = bulletPower / BaselinePower;
            GroundDebrisLogic.SpawnFromBulletImpact(hitPoint, powerRatio, bulletDir);
        }

        #endregion

        #region Bullet Power

#if DEBUG
        private static int _powerLogCount;
#endif

        /// <summary>
        /// Computes bullet power from GunScript fields via reflection.
        ///
        /// WHY Convert.ToSingle: Direct <c>(float)(object)intValue</c> throws
        /// <see cref="InvalidCastException"/> when the boxed value is int.
        /// <see cref="Convert.ToSingle"/> handles int/float/double/short.
        ///
        /// WHY clamp shotsPerFire≥1: Some weapons store 0 for single-shot mode,
        /// which zeroes the entire formula: <c>135 × 1.5 × 0 = 0 → clamped to 10</c>.
        /// This caused power=10 on every shot, preventing fragments from spawning.
        /// </summary>
        private static float ComputeBulletPower(GunScript gun, ShotSource source)
        {
            if (source == ShotSource.Turret || gun == null)
                return source == ShotSource.Turret ? TurretBasePower : BaselinePower;

            EnsureGunFields();
            if (_structureDamageField == null) return BaselinePower;

            try
            {
                // WHY: Convert.ToSingle handles boxed int/float/double safely.
                float structDmg = Convert.ToSingle(_structureDamageField.GetValue(gun));

                float knockBack = 5f;
                if (_knockBackField != null)
                {
                    try { knockBack = Convert.ToSingle(_knockBackField.GetValue(gun)); }
                    catch { knockBack = 5f; }
                }
                // WHY: Negative knockBack makes multiplier < 1, can zero power.
                // KnockBack represents push-back force — always non-negative.
                knockBack = Mathf.Max(knockBack, 0f);

                int shotsPerFire = 1;
                if (_shotsPerFireField != null)
                {
                    try { shotsPerFire = Convert.ToInt32(_shotsPerFireField.GetValue(gun)); }
                    catch { shotsPerFire = 1; }
                }
                // WHY: shotsPerFire=0 zeroes the entire power formula.
                // Some guns store 0 for single-shot mode. Treat as 1.
                shotsPerFire = Mathf.Max(shotsPerFire, 1);

                float power = Mathf.Clamp(
                    structDmg * (1f + knockBack / 10f) * shotsPerFire,
                    BaselinePower, 500f);

#if DEBUG
                if (_powerLogCount < 3)
                {
                    _powerLogCount++;
                    Plugin.Log.LogInfo(
                        $"[Router] Power #{_powerLogCount}:" +
                        $" structDmg={structDmg:F1} knockBack={knockBack:F1}" +
                        $" shots={shotsPerFire} → power={power:F1}");
                }
#endif

                return power;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Router] ComputePower FAILED: {e.Message}");
                return BaselinePower;
            }
        }

        /// <summary>Describes a FieldInfo for diagnostic logging.</summary>
        private static string FieldDesc(FieldInfo fi)
            => fi != null ? $"✓({fi.FieldType.Name})" : "✗(null)";

        private static void EnsureGunFields()
        {
            if (_gunFieldsInit) return;
            _gunFieldsInit = true;
            try
            {
                // WHY: Include NonPublic to catch protected/private fields.
                // Game v5.1 may declare these with different access modifiers.
                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = typeof(GunScript);
                _structureDamageField = t.GetField("structureDamage", flags);
                _knockBackField = t.GetField("knockBack", flags);
                _shotsPerFireField = t.GetField("shotsPerFire", flags);

                // WHY: Log field types once for remote diagnosis.
                // Shows exactly what reflection found — critical for bug reports.
                Plugin.Log.LogInfo(
                    $"[Router] GunFields:" +
                    $" structDmg={FieldDesc(_structureDamageField)}" +
                    $" knockBack={FieldDesc(_knockBackField)}" +
                    $" shotsPerFire={FieldDesc(_shotsPerFireField)}");
            }
            catch (Exception e) { Plugin.Log.LogError($"[Router] GunFields: {e.Message}"); }
        }

        #endregion

        #region Deduplication

        private static bool TryRegisterShot(Vector2 pos)
        {
            int frame = Time.frameCount;
            if (frame != _dedupeFrame) { _dedupeFrame = frame; _recentCount = 0; }

            int hash = unchecked(
                Mathf.RoundToInt(pos.x * 10f) * 397 ^
                Mathf.RoundToInt(pos.y * 10f));

            for (int i = 0; i < _recentCount; i++)
                if (_recentHashes[i] == hash) return false;

            if (_recentCount < DedupeSlots)
                _recentHashes[_recentCount++] = hash;
            return true;
        }

        #endregion

        #region Raycast

        private static bool FindBlockHit(Vector2 origin, Vector2 dir,
            out Vector2 hitPt, out Vector2 hitNorm, out BlockInfo block)
        {
            hitPt = default; hitNorm = default; block = null;

            int hits = Physics2D.RaycastNonAlloc(origin, dir, _hitBuffer, 200f, BulletMask);
            for (int i = 0; i < hits; i++)
            {
                if (_hitBuffer[i].collider == null) continue;
                if (_hitBuffer[i].collider.gameObject.layer != GroundLayer) continue;

                Vector2 pt = _hitBuffer[i].point;
                try
                {
                    Vector2Int bp = WorldGeneration.world.WorldToBlockPos(pt + dir * 0.5f);
                    ushort id = WorldGeneration.world.GetBlock(bp);
                    if (id == 0) continue;

                    hitPt = pt;
                    hitNorm = _hitBuffer[i].normal;
                    block = WorldGeneration.world.GetBlockInfo(id);
                    return true;
                }
                catch { }
            }
            return false;
        }

        #endregion

        #region Stage 1: Muzzle Blast Dust (reuses GroundDebrisLogic)

        /// <summary>
        /// Muzzle gas blast dust from nearby block surfaces.
        /// 
        /// WHY: Delegates to <see cref="GroundDebrisLogic.SpawnFromMuzzleBlast"/>
        /// which uses column-scan for efficient surface detection and directional
        /// particle emission.
        /// </summary>
        private static void SpawnMuzzleBlastDust(Vector2 barrelPos, float bulletPower, ShotSource source)
        {
            float powerRatio = bulletPower / BaselinePower;
            bool isTurret = source == ShotSource.Turret;
            GroundDebrisLogic.SpawnFromMuzzleBlast(barrelPos, powerRatio, isTurret);
        }

        /// <summary>
        /// Propellant gas smoke at the barrel. 1-3 dark wisps.
        /// WHY: After the bright flash, residual propellant gas lingers
        /// as a small wisp rising from the barrel. Subtle, not a smoke screen.
        /// </summary>
        private static void SpawnBarrelSmoke(Vector2 barrelPos, Vector2 fireDir,
            float bulletPower)
        {
            if (!AshParticlePoolManager.EnsureReady()) return;

            int count = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Sqrt(bulletPower / BaselinePower)), 1, 3);
            var rng = CreateHitRng(barrelPos);

            for (int i = 0; i < count; i++)
            {
                Vector2 pos = barrelPos + fireDir * rng.Range(0.05f, 0.2f)
                            + rng.InsideUnitCircle() * 0.05f;

                float gray = rng.Range(0.2f, 0.35f);
                Color col = new(gray, gray, gray, rng.Range(0.25f, 0.45f));

                var vis = new VisualParticleParams(
                    rng.Range(0.04f, 0.08f), col, 6,
                    ShrapnelVisuals.TriangleShape.Chunk);

                var phy = AshPhysicsParams.Smoke(
                    fireDir * rng.Range(0.3f, 1f) + Vector2.up * rng.Range(0.1f, 0.4f)
                        + rng.InsideUnitCircle() * 0.15f,
                    rng.Range(0.8f, 2f),
                    gravity: -0.02f, drag: 0.4f,
                    turbulence: rng.Range(0.3f, 0.6f),
                    wind: new Vector2(rng.Range(-0.05f, 0.05f), 0.02f),
                    thermalLift: 0.1f);

                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }
        }

        #endregion

        #region Stage 3: Block Hit Debris & Gunpowder Smoke

        /// <summary>
        /// Material-appropriate debris when bullet hits any block.
        /// Metal = sparks. Soft blocks = more dust/chunks.
        /// Uses bulletPower for count scaling via √(powerRatio).
        /// </summary>
        private static void SpawnBlockHitDebris(Vector2 hitPoint, Vector2 hitNormal,
            BlockInfo block, System.Random rng, float bulletPower)
        {
            if (!AshParticlePoolManager.EnsureReady()) return;

            MaterialCategory cat = BlockClassifier.Classify(block);
            float dustMult = BlockClassifier.GetDustMultiplier(cat);
            bool isMetal = cat == MaterialCategory.Metal
                        || (block != null && block.metallic);

            float powerScale = Mathf.Sqrt(bulletPower / BaselinePower);
            float softBonus = isMetal ? 0.5f : dustMult;
            float scaled = Mathf.Min(powerScale * softBonus, 6f);

            int chunkCount = Mathf.Clamp(Mathf.RoundToInt(2 * scaled), 1, 8);
            int dustCount = Mathf.Clamp(Mathf.RoundToInt(3 * scaled), 1, MaxImpactDust);

            // Chunks
            for (int i = 0; i < chunkCount; i++)
            {
                Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.15f;
                Color col = BlockClassifier.GetColorWithAlpha(cat, rng, 0.9f);
                Vector2 tangent = new(-hitNormal.y, hitNormal.x);
                Vector2 vel = hitNormal * rng.Range(1.5f, 4f)
                            + tangent * rng.Range(-1.5f, 1.5f);

                var vis = new VisualParticleParams(
                    rng.Range(0.04f, 0.10f), col, 11,
                    (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
                var phy = AshPhysicsParams.Chunk(vel, rng.Range(1.5f, 4f), rng);
                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }

            // Dust
            for (int i = 0; i < dustCount; i++)
            {
                Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.2f;
                Color baseCol = BlockClassifier.GetColor(cat, rng);
                Color dustCol = Color.Lerp(baseCol, new Color(0.5f, 0.5f, 0.5f), 0.3f);
                dustCol.a = 0.4f;
                Vector2 tangent = new(-hitNormal.y, hitNormal.x);
                Vector2 vel = hitNormal * rng.Range(0.4f, 2f)
                            + tangent * rng.Range(-0.8f, 0.8f);

                var vis = new VisualParticleParams(
                    rng.Range(0.02f, 0.06f), dustCol, 10,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.Dust(vel, rng.Range(0.8f, 2.5f), rng);
                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }

            // Metal sparks
            if (isMetal)
            {
                int sparks = Mathf.Clamp(Mathf.RoundToInt(2 * powerScale), 1, 10);
                for (int i = 0; i < sparks; i++)
                {
                    Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.06f;
                    float heat = rng.Range(0.6f, 1f);
                    Color col = new(1f, Mathf.Lerp(0.5f, 0.9f, heat),
                        Mathf.Lerp(0.1f, 0.4f, heat));

                    var vis = new VisualParticleParams(
                        rng.Range(0.015f, 0.035f), col, 13,
                        ShrapnelVisuals.TriangleShape.Needle);
                    float angle = rng.Range(-60f, 60f) * Mathf.Deg2Rad;
                    Vector2 dir = MathHelper.RotateDirection(hitNormal, angle);
                    ParticleHelper.SpawnSpark(pos, vis,
                        new SparkParams(dir, rng.Range(5f, 12f), rng.Range(0.06f, 0.15f)));
                }
            }

            // Soft material: floating puffs
            if (!isMetal && dustMult > 1.05f)
            {
                int puffs = Mathf.Clamp(
                    Mathf.RoundToInt(2 * (dustMult - 1f) * powerScale), 1, 6);
                for (int i = 0; i < puffs; i++)
                {
                    Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.25f;
                    Color c = BlockClassifier.GetColor(cat, rng);
                    c.a = 0.25f;

                    var vis = new VisualParticleParams(
                        rng.Range(0.06f, 0.12f), c, 9,
                        ShrapnelVisuals.TriangleShape.Chunk);
                    var phy = AshPhysicsParams.Dust(
                        new Vector2(rng.Range(-0.3f, 0.3f), rng.Range(0.2f, 1f)),
                        rng.Range(2f, 5f), rng);
                    ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
                }
            }
        }

        /// <summary>
        /// Lingering gunpowder smoke at bullet impact point.
        /// Dark gray Lit wisps — dark in shadows, realistic.
        /// Scaled by √(powerRatio): pistol=2, rifle=4, shotgun=8.
        /// </summary>
        private static void SpawnGunpowderSmoke(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, float bulletPower)
        {
            if (!AshParticlePoolManager.EnsureReady()) return;

            float powerScale = Mathf.Sqrt(bulletPower / BaselinePower);
            int count = Mathf.Clamp(Mathf.RoundToInt(2f * powerScale), 2, MaxSmoke);

            for (int i = 0; i < count; i++)
            {
                Vector2 pos = hitPoint + rng.InsideUnitCircle() * 0.1f;
                float gray = rng.Range(0.12f, 0.28f);
                Color col = new(gray, gray, gray, rng.Range(0.2f, 0.4f));
                float scale = rng.Range(0.04f, 0.08f) * (0.7f + powerScale * 0.15f);

                Vector2 vel = hitNormal * rng.Range(0.2f, 0.8f)
                            + Vector2.up * rng.Range(0.15f, 0.6f)
                            + new Vector2(rng.Range(-0.2f, 0.2f), 0f);

                var vis = new VisualParticleParams(scale, col, 8,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var phy = AshPhysicsParams.Smoke(
                    vel, rng.Range(1.5f, 3.5f),
                    gravity: rng.Range(-0.03f, 0.02f), drag: 0.5f,
                    turbulence: rng.Range(0.4f, 0.8f),
                    wind: new Vector2(rng.Range(-0.08f, 0.08f), 0.03f),
                    thermalLift: rng.Range(0.05f, 0.2f));
                ParticleHelper.SpawnLit(pos, vis, phy, rng.Range(0f, 100f));
            }
        }

        #endregion

        #region Stage 4: Physics Fragments

        /// <summary>
        /// Spawns physics fragments with wide scatter. Metal blocks only.
        /// 60% hemisphere away from wall, 40% full random (can go backward).
        /// Speed/damage capped to prevent cross-map travel.
        /// </summary>
        private static void SpawnFragments(Vector2 hitPt, Vector2 hitNorm,
            BlockInfo block, System.Random rng, ShotSource source, float fragScale)
        {
            int count = rng.Range(
                ShrapnelConfig.BulletFragmentsMin.Value,
                ShrapnelConfig.BulletFragmentsMax.Value);

            count = Mathf.CeilToInt(count * fragScale);
            if (source == ShotSource.Turret)
                count = Mathf.CeilToInt(count * ShrapnelConfig.TurretFragmentMultiplier.Value);
            count = Mathf.Min(count, MaxBulletFragments);
            if (count <= 0) return;

            var type = block != null && block.metallic
                ? ShrapnelProjectile.ShrapnelType.HeavyMetal
                : ShrapnelProjectile.ShrapnelType.Metal;

            float baseSpeed = Mathf.Min(
                ShrapnelConfig.BulletBaseSpeed.Value * Mathf.Sqrt(fragScale),
                MaxBulletFragSpeed);

            for (int i = 0; i < count; i++)
            {
                var weight = RollBulletWeight(rng);
                Vector2 dir = ComputeWideScatter(hitNorm, rng);

                var proj = ShrapnelFactory.SpawnDirectional(
                    hitPt + hitNorm * 0.15f + rng.InsideUnitCircle() * 0.1f,
                    baseSpeed, type, weight, i, rng, dir);

                if (proj != null)
                {
                    proj.transform.localScale *= ShrapnelConfig.BulletScaleMultiplier.Value;
                    proj.Heat *= ShrapnelConfig.BulletHeatMultiplier.Value;
                    proj.Damage *= BulletFragDamageMult;
                    proj.BleedAmount *= BulletFragDamageMult;
                }
            }
        }

        /// <summary>60% hemisphere ±90° from normal, 40% full random.</summary>
        private static Vector2 ComputeWideScatter(Vector2 hitNormal, System.Random rng)
        {
            if (rng.NextFloat() < 0.6f)
            {
                float spread = rng.Range(-90f, 90f) * Mathf.Deg2Rad;
                return MathHelper.RotateDirection(hitNormal, spread).normalized;
            }
            return rng.OnUnitCircle();
        }

        /// <summary>Bullet fragment weight: capped at Medium.</summary>
        private static ShrapnelWeight RollBulletWeight(System.Random rng)
        {
            float r = rng.NextFloat();
            if (r < 0.10f) return ShrapnelWeight.Micro;
            if (r < 0.60f) return ShrapnelWeight.Hot;
            return ShrapnelWeight.Medium;
        }

        #endregion

        #region Helpers

        private static System.Random CreateHitRng(Vector2 pt)
        {
            return new System.Random(unchecked(
                Mathf.RoundToInt(pt.x * 100f) * 397 ^
                Mathf.RoundToInt(pt.y * 100f) ^
                Time.frameCount));
        }

        private static void SpawnGenericSparks(Vector2 barrelPos, Vector2 fireDir,
            float bulletPower)
        {
            try
            {
                Vector2 estimatedHit = barrelPos + fireDir * 10f;
                var rng = CreateHitRng(barrelPos);
                float sparkScale = 1f + (bulletPower / BaselinePower - 1f)
                    * ShrapnelConfig.BulletDamageSparkMultiplier.Value;
                BulletImpactEffects.SpawnSparkShower(estimatedHit, -fireDir, rng, sparkScale);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Router] GenericSparks: {e.Message}"); }
        }

        #endregion
    }
}