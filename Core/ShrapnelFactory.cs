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
    /// SCALE RANGE NOTE:
    ///   Maximum scale is Massive: 0.8 (scalePacked = 800, well within ushort range).
    ///   scalePacked = scale × 1000 = max 800 of 65535. No overflow risk.
    /// </summary>
    public static class ShrapnelFactory
    {
        //  PHYSICS MATERIAL (shared across all fragments)

        private static PhysicsMaterial2D _physMat;
        private static PhysicsMaterial2D PhysMat =>
            _physMat ?? (_physMat = new PhysicsMaterial2D("ShrapnelMat")
            {
                bounciness = 0.15f,
                friction = 0.6f
            });

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
        /// Returns the ShrapnelProjectile component, or null for Micro weight.
        ///
        /// MULTIPLAYER FIX:
        ///   Seed is derived from explosion's RNG chain (deterministic).
        ///   Previously used GetInstanceID() ^ Time.frameCount which diverges host↔client.
        ///
        /// SCALE PACK SAFETY:
        ///   scalePacked = scale × 1000. Max scale (Massive) = 0.8 = 800.
        ///   Well within ushort max (65535). No overflow possible.
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

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return null;

            EnsureWoundSprites();
            float heat = HeatForWeight(weight);

            // HotThreshold sourced from ShrapnelVisuals — single source of truth
            Material mat = heat > ShrapnelVisuals.HotThreshold
                ? ShrapnelVisuals.UnlitMaterial
                : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);
            if (mat == null) return null;

            GameObject obj = new($"Shr_{type}_{index}");
            obj.transform.position =
                epicenter + rng.InsideUnitCircle() * 0.3f;
            obj.layer = 0;

            float scale = ScaleForWeight(weight, rng);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;
            sr.color = Color.Lerp(
                ShrapnelVisuals.GetColdColor(type),
                ShrapnelVisuals.GetHotColor(), heat);

            if (heat > 0.3f)
                ParticleHelper.ApplyEmission(sr,
                    ShrapnelVisuals.GetHotColor() * heat * 1.3f);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            ConfigureRigidbody(rb, weight);

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = weight == ShrapnelWeight.Massive ? 0.5f : 0.3f;
            col.sharedMaterial = PhysMat;
            col.enabled = false; // Enabled after physics delay in ShrapnelProjectile

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = weight != ShrapnelWeight.Hot;
            proj.Seed = ShrapnelSpawnLogic.MakeShrapnelSeed(rng);

            SetDamage(proj, type, weight, rng);
            LaunchWithDirection(rb, weight, baseSpeed, direction, rng);
            TryAddTrail(obj, proj, weight, rng);

            DebrisTracker.Register(obj);
            ShrapnelNetSync.ServerRegister(proj); // no-op in singleplayer/on client

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

        //  RIGIDBODY CONFIGURATION
        //  NOTE: gravityScale values here must match
        //  ClientMirrorShrapnel.GravityScaleForWeight exactly.

        internal static void ConfigureRigidbody(Rigidbody2D rb, ShrapnelWeight weight)
        {
            switch (weight)
            {
                case ShrapnelWeight.Hot:
                    rb.mass = 0.02f; rb.gravityScale = 0.3f; rb.drag = 0.4f; break;
                case ShrapnelWeight.Medium:
                    rb.mass = 0.08f; rb.gravityScale = 0.15f; rb.drag = 0.2f; break;
                case ShrapnelWeight.Heavy:
                    rb.mass = 0.25f; rb.gravityScale = 0.35f; rb.drag = 0.2f; break;
                case ShrapnelWeight.Massive:
                    rb.mass = 0.8f; rb.gravityScale = 0.5f; rb.drag = 0.1f; break;
                case ShrapnelWeight.Micro:
                    rb.mass = 0.005f; rb.gravityScale = 0.1f; rb.drag = 0.5f; break;
            }
        }

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
            float speedMult = GetSpeedMultiplier(weight, rng);
            float targetSpeed = MathHelper.ClampSpeed(
                baseSpeed * speedMult, ShrapnelSpawnLogic.GlobalMaxSpeed);
            rb.AddForce(direction * targetSpeed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-500f, 500f));
        }

        private static float GetSpeedMultiplier(ShrapnelWeight weight, System.Random rng)
        {
            return weight switch
            {
                ShrapnelWeight.Micro => rng.Range(1.5f, 2.5f),
                ShrapnelWeight.Hot => rng.Range(0.8f, 1.3f),
                ShrapnelWeight.Medium => rng.Range(0.8f, 1.2f),
                ShrapnelWeight.Heavy => rng.Range(0.4f, 0.8f),
                ShrapnelWeight.Massive => rng.Range(0.2f, 0.4f),
                _ => 1f,
            };
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
            var matProps = GetMaterialProperties(type);

            Material litMat = ShrapnelVisuals.LitMaterial
                ?? ShrapnelVisuals.UnlitMaterial;
            if (litMat == null) return;

            for (int i = 0; i < count; i++)
                SpawnBreakFragment(position, impactNormal, parentScale, type,
                    childWeight, impactSpeed, i, rng, litMat, matProps);
        }

        private readonly struct MaterialProps
        {
            public readonly float SpeedMult;
            public readonly float DamageMult;
            public MaterialProps(float s, float d) { SpeedMult = s; DamageMult = d; }
        }

        private static MaterialProps GetMaterialProperties(
            ShrapnelProjectile.ShrapnelType type)
        {
            return type switch
            {
                ShrapnelProjectile.ShrapnelType.Metal or ShrapnelProjectile.ShrapnelType.HeavyMetal => new MaterialProps(0.3f, 1.4f),
                ShrapnelProjectile.ShrapnelType.Wood => new MaterialProps(0.7f, 0.6f),
                ShrapnelProjectile.ShrapnelType.Stone => new MaterialProps(0.5f, 1f),
                _ => new MaterialProps(0.5f, 1f),
            };
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
            System.Random rng, Material mat, MaterialProps matProps)
        {
            GameObject obj = new($"ShrBrk_{index}");
            float childScale = Mathf.Max(parentScale * rng.Range(0.4f, 0.6f), 0.05f);

            obj.transform.position =
                position + rng.InsideUnitCircle() * 0.15f;
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
            ConfigureRigidbody(rb, weight);

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = 0.25f;
            col.sharedMaterial = PhysMat;

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = 0.1f;
            proj.CanBreak = false;
            proj.Damage = rng.Range(2f, 6f) * matProps.DamageMult;
            proj.BleedAmount = rng.Range(0.3f, 1.5f);
            proj.Seed = rng.Next();

            Vector2 spread = rng.InsideUnitCircle() * 0.8f;
            Vector2 dir = (impactNormal + spread).normalized;
            dir.y = Mathf.Abs(dir.y) * 0.5f + 0.1f;

            float childSpeed = MathHelper.ClampSpeed(
                impactSpeed * rng.Range(0.2f, 0.5f) * matProps.SpeedMult,
                ShrapnelSpawnLogic.GlobalMaxSpeed);

            rb.AddForce(dir * childSpeed * rb.mass * 5f, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-300f, 300f));

            DebrisTracker.Register(obj);
            ShrapnelNetSync.ServerRegister(proj); // no-op in singleplayer/on client
        }

        //  UTILITIES

        internal static float ScaleForWeight(ShrapnelWeight w, System.Random rng)
        {
            return w switch
            {
                ShrapnelWeight.Micro => rng.Range(0.02f, 0.05f),
                ShrapnelWeight.Hot => rng.Range(0.08f, 0.14f),
                ShrapnelWeight.Medium => rng.Range(0.14f, 0.25f),
                ShrapnelWeight.Heavy => rng.Range(0.22f, 0.45f),
                ShrapnelWeight.Massive => rng.Range(0.5f, 0.8f),
                _ => 0.18f,
            };
        }

        internal static float HeatForWeight(ShrapnelWeight w)
        {
            return w switch
            {
                ShrapnelWeight.Micro => 1.0f,
                ShrapnelWeight.Hot => 1.0f,
                ShrapnelWeight.Medium => 0.4f,
                ShrapnelWeight.Heavy => 0.15f,
                ShrapnelWeight.Massive => 0.08f,
                _ => 0f,
            };
        }

        internal static void SetDamage(ShrapnelProjectile proj,
            ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight, System.Random rng)
        {
            switch (weight)
            {
                case ShrapnelWeight.Micro:
                    proj.Damage = rng.Range(1f, 3f);
                    proj.BleedAmount = rng.Range(0.2f, 0.8f); break;
                case ShrapnelWeight.Hot:
                    proj.Damage = rng.Range(3f, 8f);
                    proj.BleedAmount = rng.Range(0.5f, 2f); break;
                case ShrapnelWeight.Medium:
                    proj.Damage = rng.Range(6f, 15f);
                    proj.BleedAmount = rng.Range(1f, 4f); break;
                case ShrapnelWeight.Heavy:
                    proj.Damage = rng.Range(12f, 25f);
                    proj.BleedAmount = rng.Range(2f, 6f); break;
                case ShrapnelWeight.Massive:
                    proj.Damage = rng.Range(25f, 50f);
                    proj.BleedAmount = rng.Range(5f, 12f); break;
            }
            if (type == ShrapnelProjectile.ShrapnelType.HeavyMetal)
                proj.Damage *= 1.3f;
        }

        internal static void TryAddTrail(GameObject obj, ShrapnelProjectile proj,
            ShrapnelWeight weight, System.Random rng)
        {
            bool give = weight == ShrapnelWeight.Hot
                || weight == ShrapnelWeight.Massive
                || (weight == ShrapnelWeight.Medium && rng.NextDouble() < 0.25);
            if (!give) return;

            Material mat = ShrapnelVisuals.TrailMaterial;
            if (mat == null) return;

            TrailRenderer tr = obj.AddComponent<TrailRenderer>();
            tr.sharedMaterial = mat;
            tr.sortingOrder = 9;
            tr.numCapVertices = 1;
            tr.autodestruct = false;

            float scale = obj.transform.localScale.x;
            ConfigureTrail(tr, weight, scale);
            proj.HasTrail = true;
        }

        private static void ConfigureTrail(TrailRenderer tr,
            ShrapnelWeight weight, float scale)
        {
            switch (weight)
            {
                case ShrapnelWeight.Massive:
                    tr.time = 0.4f;
                    tr.startWidth = 0.12f * scale * 5f; tr.endWidth = 0f;
                    tr.startColor = new Color(0.3f, 0.25f, 0.2f, 0.8f);
                    tr.endColor = new Color(0.2f, 0.2f, 0.2f, 0f); break;
                case ShrapnelWeight.Hot:
                    tr.time = 0.25f;
                    tr.startWidth = 0.06f * scale * 10f; tr.endWidth = 0f;
                    tr.startColor = new Color(1f, 0.5f, 0.1f, 0.9f);
                    tr.endColor = new Color(1f, 0.2f, 0f, 0f); break;
                default:
                    tr.time = 0.15f;
                    tr.startWidth = 0.04f * scale * 10f; tr.endWidth = 0f;
                    tr.startColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                    tr.endColor = new Color(0.4f, 0.4f, 0.4f, 0f); break;
            }
        }
    }
}