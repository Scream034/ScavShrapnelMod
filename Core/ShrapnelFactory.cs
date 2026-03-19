using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Factory for shrapnel objects: physics projectiles, visuals, ash, break fragments.
    /// </summary>
    public static class ShrapnelFactory
    {
        private static PhysicsMaterial2D _physMat;
        private static PhysicsMaterial2D PhysMat =>
            _physMat ?? (_physMat = new PhysicsMaterial2D("ShrapnelMat")
            {
                bounciness = 0.15f,
                friction = 0.6f
            });

        private static bool _woundCached;
        private static Sprite _woundSprite;
        private static Sprite _woundPanel;

        private static int _dmgFrame;
        private static int _dmgCount;

        private const float HotThreshold = 0.5f;

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

        internal static bool TryDamageSlot()
        {
            int f = Time.frameCount;
            if (f != _dmgFrame) { _dmgFrame = f; _dmgCount = 0; }
            if (_dmgCount >= ShrapnelConfig.MaxDamagePerFrame.Value) return false;
            _dmgCount++;
            return true;
        }

        //  PHYSICS SHRAPNEL — primary damage dealers (GameObjects)

        public static void Spawn(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng)
        {
            Vector2 direction = GetLaunchDirection(type, rng);
            SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        public static void SpawnDirectional(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            direction = MathHelper.RotateDirection(direction, rng.Range(-0.26f, 0.26f));
            SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        /// <summary>
        /// Core shrapnel spawning. Creates physics GameObject with ShrapnelProjectile.
        ///
        /// MULTIPLAYER FIX: Generates deterministic Seed from the explosion's rng
        /// and passes it to ShrapnelProjectile. Previously, projectiles seeded their
        /// _rng from GetInstanceID() ^ Time.frameCount, which differs between peers.
        /// Now the seed chain is: explosion position = MakeSeed = System.Random =
        /// per-shrapnel Seed, ensuring identical DeterministicRoll sequences.
        /// </summary>
        private static void SpawnCore(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            // Micro shrapnel: spawn visual sparks only, no physics GameObject
            if (weight == ShrapnelWeight.Micro)
            {
                SpawnMicroVisual(epicenter, baseSpeed, type, rng);
                return;
            }

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return;

            EnsureWoundSprites();
            float heat = HeatForWeight(weight);

            Material mat = heat > HotThreshold
                ? ShrapnelVisuals.UnlitMaterial
                : (ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial);
            if (mat == null) return;

            GameObject obj = new GameObject($"Shr_{type}_{index}");
            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.3f;
            obj.layer = 0;

            float scale = ScaleForWeight(weight, rng);
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = mat;
            sr.color = Color.Lerp(ShrapnelVisuals.GetColdColor(type),
                ShrapnelVisuals.GetHotColor(), heat);

            if (heat > 0.3f)
                ParticleHelper.ApplyEmission(sr, ShrapnelVisuals.GetHotColor() * heat * 1.3f);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            ConfigureRigidbody(rb, weight);

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = weight == ShrapnelWeight.Massive ? 0.5f : 0.3f;
            col.sharedMaterial = PhysMat;
            col.enabled = false;

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = weight != ShrapnelWeight.Hot;

            // MULTIPLAYER FIX: Deterministic seed from explosion's RNG chain.
            // Previously used GetInstanceID() ^ Time.frameCount which desynchronizes.
            proj.Seed = ShrapnelSpawnLogic.MakeShrapnelSeed(rng);

            SetDamage(proj, type, weight, rng);

            LaunchWithDirection(rb, weight, baseSpeed, direction, rng);
            TryAddTrail(obj, proj, weight, rng);

            DebrisTracker.Register(obj);
        }

        /// <summary>
        /// Micro shrapnel: visual spark shower at SparkPool.
        /// Small, fast, glows, causes surface cuts but no embed/bone break.
        /// Size↔Speed: speed ∝ 1/√(size) — smaller = faster.
        /// </summary>
        private static void SpawnMicroVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            if (!ShrapnelConfig.EnableMicroShrapnel.Value) return;

            int sparks = ShrapnelConfig.MicroSparksPerPiece.Value;
            for (int i = 0; i < sparks; i++)
            {
                Vector2 offset = rng.InsideUnitCircle() * 0.15f;
                Vector2 pos = epicenter + offset;
                Vector2 dir = rng.OnUnitCircle();

                float size = rng.Range(0.02f, 0.05f);
                float sizeSpeedMult = 1f / Mathf.Sqrt(size / 0.03f);
                float speed = MathHelper.ClampSpeed(
                    baseSpeed * rng.Range(2f, 3.5f) * sizeSpeedMult,
                    ShrapnelSpawnLogic.GlobalMaxSpeed);
                float lifetime = rng.Range(0.08f, 0.2f);

                var visual = new VisualParticleParams(size,
                    new Color(1f, 0.8f, 0.3f, 1f), 15,
                    ShrapnelVisuals.TriangleShape.Needle);
                var spark = new SparkParams(dir, speed, lifetime);

                ParticleHelper.SpawnSparkUnlit("MicroSpark", pos, visual, spark,
                    new EmissionParams(new Color(3f, 2f, 0.5f)));
            }
        }

        private static void ConfigureRigidbody(Rigidbody2D rb, ShrapnelWeight weight)
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

        private static Vector2 GetLaunchDirection(ShrapnelProjectile.ShrapnelType type,
            System.Random rng)
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
            float targetSpeed = MathHelper.ClampSpeed(baseSpeed * speedMult,
                ShrapnelSpawnLogic.GlobalMaxSpeed);
            rb.AddForce(direction * targetSpeed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-500f, 500f));
        }

        private static float GetSpeedMultiplier(ShrapnelWeight weight, System.Random rng)
        {
            switch (weight)
            {
                case ShrapnelWeight.Micro:   return rng.Range(1.5f, 2.5f);
                case ShrapnelWeight.Hot:     return rng.Range(0.8f, 1.3f);
                case ShrapnelWeight.Medium:  return rng.Range(0.8f, 1.2f);
                case ShrapnelWeight.Heavy:   return rng.Range(0.4f, 0.8f);
                case ShrapnelWeight.Massive: return rng.Range(0.2f, 0.4f);
                default:                     return 1f;
            }
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
            float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(2f, 3.5f),
                ShrapnelSpawnLogic.GlobalMaxSpeed);
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
                Vector2 velocity = new Vector2(
                    Mathf.Cos(angle) * radialSpeed,
                    Mathf.Sin(angle) * Mathf.Abs(radialSpeed) * 0.5f + upSpeed);

                float gravity = rng.Range(0.8f, 2f);
                var visual = new VisualParticleParams(rng.Range(0.02f, 0.06f),
                    color, 8, ShrapnelVisuals.TriangleShape.Chunk);
                var physics = AshPhysicsParams.Ash(velocity, rng.Range(2f, 5f), gravity, rng);

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

        //  BREAK FRAGMENTS — material-adaptive

        internal static void SpawnBreakFragments(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight parentWeight, float impactSpeed, System.Random rng = null)
        {
            if (rng == null)
            {
                int seed = unchecked(
                    (int)(position.x * 10000f) * 397 ^
                    (int)(position.y * 10000f) ^
                    (int)(impactSpeed * 100f));
                rng = new System.Random(seed);
            }

            int count = rng.Range(2, 4);
            ShrapnelWeight childWeight = GetChildWeight(parentWeight);
            var matProps = GetMaterialProperties(type);

            Material litMat = ShrapnelVisuals.LitMaterial ?? ShrapnelVisuals.UnlitMaterial;
            if (litMat == null) return;

            for (int i = 0; i < count; i++)
            {
                SpawnBreakFragment(position, impactNormal, parentScale, type,
                    childWeight, impactSpeed, i, rng, litMat, matProps);
            }
        }

        private readonly struct MaterialProps
        {
            public readonly float SpeedMult;
            public readonly float DamageMult;
            public MaterialProps(float speedMult, float damageMult)
            { SpeedMult = speedMult; DamageMult = damageMult; }
        }

        private static MaterialProps GetMaterialProperties(ShrapnelProjectile.ShrapnelType type)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Metal:
                case ShrapnelProjectile.ShrapnelType.HeavyMetal:
                    return new MaterialProps(0.3f, 1.4f);
                case ShrapnelProjectile.ShrapnelType.Wood:
                    return new MaterialProps(0.7f, 0.6f);
                case ShrapnelProjectile.ShrapnelType.Stone:
                    return new MaterialProps(0.5f, 1f);
                default:
                    return new MaterialProps(0.5f, 1f);
            }
        }

        private static ShrapnelWeight GetChildWeight(ShrapnelWeight parent)
        {
            switch (parent)
            {
                case ShrapnelWeight.Massive: return ShrapnelWeight.Heavy;
                case ShrapnelWeight.Heavy:   return ShrapnelWeight.Medium;
                case ShrapnelWeight.Medium:  return ShrapnelWeight.Hot;
                default:                     return ShrapnelWeight.Micro;
            }
        }

        private static void SpawnBreakFragment(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight, float impactSpeed, int index,
            System.Random rng, Material mat, MaterialProps matProps)
        {
            GameObject obj = new GameObject($"ShrBrk_{index}");
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

            // MULTIPLAYER FIX: Deterministic seed for break fragments too
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
        }

        //  UTILITIES

        internal static float ScaleForWeight(ShrapnelWeight w, System.Random rng)
        {
            switch (w)
            {
                case ShrapnelWeight.Micro:   return rng.Range(0.02f, 0.05f);
                case ShrapnelWeight.Hot:     return rng.Range(0.08f, 0.14f);
                case ShrapnelWeight.Medium:  return rng.Range(0.14f, 0.25f);
                case ShrapnelWeight.Heavy:   return rng.Range(0.22f, 0.45f);
                case ShrapnelWeight.Massive: return rng.Range(0.5f, 0.8f);
                default:                     return 0.18f;
            }
        }

        internal static float HeatForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Micro:   return 1.0f;
                case ShrapnelWeight.Hot:     return 1.0f;
                case ShrapnelWeight.Medium:  return 0.4f;
                case ShrapnelWeight.Heavy:   return 0.15f;
                case ShrapnelWeight.Massive: return 0.08f;
                default:                     return 0f;
            }
        }

        internal static void SetDamage(ShrapnelProjectile proj,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight, System.Random rng)
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

        private static void ConfigureTrail(TrailRenderer tr, ShrapnelWeight weight, float scale)
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