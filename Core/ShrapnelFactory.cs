using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Net;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Factory for shrapnel objects: physics projectiles, visuals, ash, break fragments.
    ///
    /// MULTIPLAYER:
    ///   SpawnCore / SpawnBreakFragment — call ServerRegister after DebrisTracker.Register.
    ///   Server creates real physics GameObjects; clients receive ClientMirrorShrapnel via net.
    ///   Both methods guard against double-registration (register only on server).
    ///
    /// REFACTORED: Weight-specific data now sourced from ShrapnelWeightData table.
    /// Trail config uses TrailConfig (single source of truth for server + client).
    /// Material selection via SelectMaterial helper.
    /// </summary>
    public static class ShrapnelFactory
    {
        //  PHYSICS MATERIAL (shared across all fragments)

        private static PhysicsMaterial2D _physMat;
        internal static PhysicsMaterial2D PhysMat => _physMat ??= new PhysicsMaterial2D("ShrapnelMat")
            {
                bounciness = 0.15f,
                friction = 0.6f
            };

        //  WOUND SPRITES (lazy-loaded)

        private static bool _woundCached;
        private static Sprite _woundSprite;
        private static Sprite _woundPanel;

        internal static void EnsureWoundSprites()
        {
            if (_woundCached) return;
            _woundCached = true;
            try
            {
                _woundSprite = Resources.Load<Sprite>("Special/footglass");
                _woundPanel = Resources.Load<Sprite>("Special/footglasshealthpanel");
            }
            catch { }
        }

        internal static Sprite WoundSprite => _woundSprite;
        internal static Sprite WoundPanel => _woundPanel;

        //  DAMAGE THROTTLE

        private static int _dmgFrame;
        private static int _dmgCount;

        internal static bool TryDamageSlot()
        {
            int f = Time.frameCount;
            if (f != _dmgFrame) { _dmgFrame = f; _dmgCount = 0; }
            if (_dmgCount >= ShrapnelConfig.MaxDamagePerFrame.Value) return false;
            _dmgCount++;
            return true;
        }

        //  MATERIAL SELECTION (centralized — was duplicated in 4 places)

        /// <summary>
        /// Selects appropriate material based on heat level.
        /// Hot fragments use Unlit (self-luminous), cold use Lit (reacts to lighting).
        /// Returns null if no materials available.
        /// </summary>
        internal static Material SelectMaterial(float heat)
        {
            return heat > ShrapnelVisuals.HotThreshold
                ? ShrapnelVisuals.UnlitMaterial
                : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);
        }

        //  PHYSICS SHRAPNEL — primary damage dealers

        /// <summary>
        /// Spawns a physics shrapnel fragment in a random direction.
        /// Returns the ShrapnelProjectile component, or null for Micro weight (visual-only).
        /// </summary>
        public static ShrapnelProjectile Spawn(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng)
        {
            Vector2 direction = GetLaunchDirection(type, rng);
            return SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        /// <summary>
        /// Spawns a physics shrapnel fragment in a specific direction ± spread.
        /// Returns the ShrapnelProjectile component, or null for Micro weight (visual-only).
        /// </summary>
        public static ShrapnelProjectile SpawnDirectional(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            direction = MathHelper.RotateDirection(direction,
                rng.Range(-0.26f, 0.26f));
            return SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        /// <summary>
        /// Core shrapnel spawner. Creates physics GameObject with ShrapnelProjectile.
        /// Registers with DebrisTracker and (if server in MP) with ShrapnelNetSync.
        ///
        /// REFACTORED: Uses ShrapnelWeightData table instead of per-field switches.
        /// Trail setup delegated to TrailConfig.
        /// </summary>
        private static ShrapnelProjectile SpawnCore(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            // Micro: visual sparks only, no physics GO
            if (weight == ShrapnelWeight.Micro)
            {
                SpawnMicroVisual(epicenter, baseSpeed, type, rng);
                return null;
            }

            ref readonly var wd = ref ShrapnelWeightData.Get(weight);

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return null;

            EnsureWoundSprites();

            Material mat = SelectMaterial(wd.InitialHeat);
            if (mat == null) return null;

            // PERF: Conditional string interpolation — avoid alloc in release
            GameObject obj = ShrapnelConfig.DebugLogging.Value
                ? new GameObject($"Shr_{type}_{index}")
                : new GameObject("Shr");

            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.3f;
            obj.layer = 0;

            float scale = rng.Range(wd.ScaleMin, wd.ScaleMax);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;
            sr.color = Color.Lerp(
                ShrapnelVisuals.GetColdColor(type),
                ShrapnelVisuals.GetHotColor(), wd.InitialHeat);

            if (wd.InitialHeat > 0.3f)
                ParticleHelper.ApplyEmission(sr,
                    ShrapnelVisuals.GetHotColor() * wd.InitialHeat * 1.3f);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            ShrapnelWeightData.ConfigureRigidbody(rb, weight);

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = wd.ColliderRadius;
            col.sharedMaterial = PhysMat;
            col.enabled = false; // Enabled after physics delay in ShrapnelProjectile

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = wd.InitialHeat;
            proj.CanBreak = wd.CanBreak;
            proj.Seed = ShrapnelSpawnLogic.MakeShrapnelSeed(rng);

            proj.Damage = rng.Range(wd.DamageMin, wd.DamageMax);
            proj.BleedAmount = rng.Range(wd.BleedMin, wd.BleedMax);
            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                proj.Damage *= 1.3f;

            LaunchWithDirection(rb, weight, baseSpeed, direction, rng);
            proj.HasTrail = TrailConfig.TryAdd(obj, weight, scale, rng);

            DebrisTracker.Register(obj);
            ShrapnelNetSync.ServerRegister(proj);

            return proj;
        }

        /// <summary>
        /// Creates a client-side physics shard with all damage gated.
        /// Shares setup logic with SpawnCore to eliminate duplication.
        /// Called by ShrapnelNetSync.OnReceiveSpawn.
        /// </summary>
        internal static ShrapnelProjectile SpawnClientShard(
            ushort netId, Vector2 position,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            float heat, ShrapnelVisuals.TriangleShape shape, float scale,
            bool hasTrail, bool atRest, Vector2 velocity, float rotationZ)
        {
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return null;

            Material mat = SelectMaterial(heat);
            if (mat == null) return null;

            ref readonly var wd = ref ShrapnelWeightData.Get(weight);

            var obj = new GameObject($"ShrC_{netId}");
            obj.transform.position = position;
            obj.transform.localScale = Vector3.one * scale;
            obj.transform.rotation = Quaternion.Euler(0f, 0f, rotationZ);
            obj.layer = 0;

            var sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;
            sr.color = Color.Lerp(
                ShrapnelVisuals.GetColdColor(type),
                ShrapnelVisuals.GetHotColor(), heat);

            if (heat > 0.3f)
                ParticleHelper.ApplyEmission(sr,
                    ShrapnelVisuals.GetHotColor() * heat * 1.3f);

            var rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            ShrapnelWeightData.ConfigureRigidbody(rb, weight);

            var col = obj.AddComponent<CircleCollider2D>();
            col.radius = wd.ColliderRadius;
            col.sharedMaterial = PhysMat;
            col.enabled = true; // WHY: No physics delay for client shards

            var proj = obj.AddComponent<ShrapnelProjectile>();
            proj.IsServerAuthoritative = false;
            proj.NetSyncId = netId;
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = false;
            proj.HasTrail = false;
            proj.Seed = unchecked(netId * 397 + 42);

            if (atRest)
            {
                rb.velocity = Vector2.zero;
                rb.isKinematic = true;
                proj.ForceToState(ShrapnelProjectile.ExternalState.Stuck, position);
            }
            else
            {
                rb.velocity = velocity;
                rb.AddTorque(
                    new System.Random(unchecked(netId * 17)).Range(-500f, 500f));
            }

            if (hasTrail && !atRest)
            {
                Material trailMat = ShrapnelVisuals.TrailMaterial;
                if (trailMat != null)
                {
                    var tr = obj.AddComponent<TrailRenderer>();
                    tr.sharedMaterial = trailMat;
                    TrailConfig.Apply(tr, weight, scale);
                    proj.HasTrail = true;
                }
            }

            return proj;
        }

        //  MICRO VISUAL (sparks only, no physics GO)

        private static void SpawnMicroVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            if (!ShrapnelConfig.EnableMicroShrapnel.Value) return;

            int sparks = ShrapnelConfig.MicroSparksPerPiece.Value;
            for (int i = 0; i < sparks; i++)
            {
                Vector2 pos = epicenter + rng.InsideUnitCircle() * 0.15f;
                Vector2 dir = rng.OnUnitCircle();

                float size = rng.Range(0.02f, 0.05f);
                float sizeSpeedMult = 1f / Mathf.Sqrt(size / 0.03f);
                float speed = MathHelper.ClampSpeed(
                    baseSpeed * rng.Range(2f, 3.5f) * sizeSpeedMult,
                    ShrapnelSpawnLogic.GlobalMaxSpeed);

                var visual = new VisualParticleParams(
                    size, new Color(1f, 0.8f, 0.3f, 1f), 15,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir, speed, rng.Range(0.08f, 0.2f));

                ParticleHelper.SpawnSparkUnlit("MicroSpark", pos, visual, spark,
                    new EmissionParams(new Color(3f, 2f, 0.5f)));
            }
        }

        //  RIGIDBODY CONFIGURATION — delegates to ShrapnelWeightData

        /// <summary>
        /// Configures Rigidbody2D for a weight class.
        /// Delegates to ShrapnelWeightData.ConfigureRigidbody.
        /// Kept as forwarding method for backward compatibility with existing call sites.
        /// </summary>
        internal static void ConfigureRigidbody(Rigidbody2D rb, ShrapnelWeight weight)
            => ShrapnelWeightData.ConfigureRigidbody(rb, weight);

        //  LAUNCH

        private static Vector2 GetLaunchDirection(
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            if (type == ShrapnelProjectile.ShrapnelType.Metal)
            {
                float angleDeg = rng.Range(-22.5f, 202.5f);
                return MathHelper.AngleToDirectionDeg(angleDeg);
            }
            return rng.OnUnitCircle();
        }

        private static void LaunchWithDirection(Rigidbody2D rb, ShrapnelWeight weight,
            float baseSpeed, Vector2 direction, System.Random rng)
        {
            ref readonly var wd = ref ShrapnelWeightData.Get(weight);
            float speedMult = rng.Range(wd.SpeedMultMin, wd.SpeedMultMax);
            float targetSpeed = MathHelper.ClampSpeed(
                baseSpeed * speedMult, ShrapnelSpawnLogic.GlobalMaxSpeed);
            rb.AddForce(direction * targetSpeed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-500f, 500f));
        }

        //  VISUAL SHRAPNEL — spark shower (ParticlePool)

        public static void SpawnVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            Vector2 direction = GetLaunchDirection(type, rng);
            SpawnVisualCore(epicenter, baseSpeed, type, rng, direction);
        }

        public static void SpawnVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng, float angleRad)
        {
            float spread = rng.Range(-0.2f, 0.2f);
            Vector2 direction = MathHelper.AngleToDirection(angleRad + spread);
            SpawnVisualCore(epicenter, baseSpeed, type, rng, direction);
        }

        private static void SpawnVisualCore(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng, Vector2 direction)
        {
            float speed = MathHelper.ClampSpeed(
                baseSpeed * rng.Range(2f, 3.5f), ShrapnelSpawnLogic.GlobalMaxSpeed);
            float lifetime = rng.Range(0.15f, 0.3f);
            Color hotCol = ShrapnelVisuals.GetHotColor();

            var visual = new VisualParticleParams(rng.Range(0.04f, 0.1f), hotCol, 9,
                ShrapnelVisuals.TriangleShape.Needle);
            var spark = new SparkParams(direction, speed, lifetime);

            ParticleHelper.SpawnSparkUnlit("VisualShr",
                epicenter + rng.InsideUnitCircle() * 0.2f,
                visual, spark,
                new EmissionParams(hotCol * rng.Range(1f, 2f)));
        }

        //  ASH CLOUD

        public static void SpawnAshCloud(Vector2 epicenter, int count,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            float ambientTemp = 20f;
            try { ambientTemp = WorldGeneration.world.ambientTemperature; } catch { }

            bool isCold = ambientTemp < 5f;
            bool isHot = ambientTemp > 25f;

            for (int i = 0; i < count; i++)
            {
                Vector2 position = epicenter + rng.InsideUnitCircle() * 0.5f;
                Color color = GetAshColor(isCold, isHot, rng);

                float angle = rng.NextAngle();
                float radialSpeed = rng.Range(0.5f, 3f);
                float upSpeed = rng.Range(0.5f, 4f);
                Vector2 velocity = new(
                    Mathf.Cos(angle) * radialSpeed,
                    Mathf.Sin(angle) * Mathf.Abs(radialSpeed) * 0.5f + upSpeed);

                float gravity = rng.Range(0.8f, 2f);
                var visual = new VisualParticleParams(
                    rng.Range(0.02f, 0.06f), color, 8,
                    ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.Ash(
                    velocity, rng.Range(2f, 5f), gravity, rng);

                ParticleHelper.SpawnLit("Ash", position, visual, physics,
                    rng.Range(0f, 100f));
            }
        }

        private static Color GetAshColor(bool isCold, bool isHot, System.Random rng)
        {
            if (isCold && rng.NextFloat() < 0.4f)
            {
                float gray = rng.Range(0.7f, 0.9f);
                return new Color(gray, gray, gray, 0.6f);
            }
            if (isHot || rng.NextFloat() < 0.3f)
                return new Color(rng.Range(0.8f, 1f), rng.Range(0.2f, 0.4f),
                    rng.Range(0f, 0.1f), 0.7f);
            float g = rng.Range(0.15f, 0.35f);
            return new Color(g, g, g, 0.5f);
        }

        //  BREAK FRAGMENTS

        /// <summary>
        /// Spawns child break fragments when a heavy/massive projectile shatters.
        /// Each fragment is registered with ShrapnelNetSync if on server.
        /// </summary>
        internal static void SpawnBreakFragments(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight parentWeight, float impactSpeed, System.Random rng = null)
        {
            if (rng == null)
            {
                int seed = unchecked(
                    (int)(position.x * 10000f) * 397
                    ^ (int)(position.y * 10000f)
                    ^ (int)(impactSpeed * 100f));
                rng = new System.Random(seed);
            }

            int count = rng.Range(2, 4);
            ShrapnelWeight childWeight = GetChildWeight(parentWeight);

            Material litMat = ShrapnelVisuals.LitMaterial
                ?? ShrapnelVisuals.UnlitMaterial;
            if (litMat == null) return;

            for (int i = 0; i < count; i++)
                SpawnBreakFragment(position, impactNormal, parentScale, type,
                    childWeight, impactSpeed, i, rng, litMat);
        }

        private static ShrapnelWeight GetChildWeight(ShrapnelWeight parent)
        {
            return parent switch
            {
                ShrapnelWeight.Massive => ShrapnelWeight.Heavy,
                ShrapnelWeight.Heavy => ShrapnelWeight.Medium,
                ShrapnelWeight.Medium => ShrapnelWeight.Hot,
                _ => ShrapnelWeight.Micro,
            };
        }

        private static void SpawnBreakFragment(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight, float impactSpeed, int index,
            System.Random rng, Material mat)
        {
            ref readonly var wd = ref ShrapnelWeightData.Get(weight);

            // WHY: HeavyMetal damage multiplier from material properties
            float damageMult = (type == ShrapnelProjectile.ShrapnelType.Metal ||
                               type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                ? 1.4f : (type == ShrapnelProjectile.ShrapnelType.Wood ? 0.6f : 1f);
            float speedMult = (type == ShrapnelProjectile.ShrapnelType.Metal ||
                              type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                ? 0.3f : (type == ShrapnelProjectile.ShrapnelType.Wood ? 0.7f : 0.5f);

            GameObject obj = new($"ShrBrk_{index}");
            float childScale = Mathf.Max(parentScale * rng.Range(0.4f, 0.6f), 0.05f);

            obj.transform.position = position + rng.InsideUnitCircle() * 0.15f;
            obj.transform.localScale = Vector3.one * childScale;
            obj.layer = 0;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(
                (ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;
            sr.color = ShrapnelVisuals.GetColdColor(type);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            rb.drag = 0.3f;
            ShrapnelWeightData.ConfigureRigidbody(rb, weight);

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = 0.25f;
            col.sharedMaterial = PhysMat;

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = 0.1f;
            proj.CanBreak = false;
            proj.Damage = rng.Range(2f, 6f) * damageMult;
            proj.BleedAmount = rng.Range(0.3f, 1.5f);
            proj.Seed = rng.Next();

            Vector2 spread = rng.InsideUnitCircle() * 0.8f;
            Vector2 dir = (impactNormal + spread).normalized;
            dir.y = Mathf.Abs(dir.y) * 0.5f + 0.1f;

            float childSpeed = MathHelper.ClampSpeed(
                impactSpeed * rng.Range(0.2f, 0.5f) * speedMult,
                ShrapnelSpawnLogic.GlobalMaxSpeed);

            rb.AddForce(dir * childSpeed * rb.mass * 5f, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-300f, 300f));

            DebrisTracker.Register(obj);
            ShrapnelNetSync.ServerRegister(proj);
        }

        //  UTILITIES — kept for backward compatibility

        /// <summary>Kept for call sites that use it directly.</summary>
        internal static float ScaleForWeight(ShrapnelWeight w, System.Random rng)
        {
            ref readonly var d = ref ShrapnelWeightData.Get(w);
            return rng.Range(d.ScaleMin, d.ScaleMax);
        }

        internal static float HeatForWeight(ShrapnelWeight w)
            => ShrapnelWeightData.Get(w).InitialHeat;

        internal static void SetDamage(ShrapnelProjectile proj,
            ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight, System.Random rng)
        {
            ref readonly var d = ref ShrapnelWeightData.Get(weight);
            proj.Damage = rng.Range(d.DamageMin, d.DamageMax);
            proj.BleedAmount = rng.Range(d.BleedMin, d.BleedMax);
            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                proj.Damage *= 1.3f;
        }

        /// <summary>Kept for backward compatibility. Delegates to TrailConfig.</summary>
        internal static void TryAddTrail(GameObject obj, ShrapnelProjectile proj,
            ShrapnelWeight weight, System.Random rng)
        {
            proj.HasTrail = TrailConfig.TryAdd(obj, weight,
                obj.transform.localScale.x, rng);
        }
    }
}