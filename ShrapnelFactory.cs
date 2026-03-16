using UnityEngine;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Factory for shrapnel objects: physics projectiles, visuals, ash, and break fragments.
    ///
    /// All methods accept System.Random for deterministic behavior.
    /// Uses pooled materials/sprites via <see cref="ShrapnelVisuals"/>.
    /// All created objects registered in <see cref="DebrisTracker"/>.
    /// </summary>
    public static class ShrapnelFactory
    {
        //  Shared physics material 
        private static PhysicsMaterial2D _physMat;
        private static PhysicsMaterial2D PhysMat =>
            _physMat ?? (_physMat = new PhysicsMaterial2D("ShrapnelMat")
            {
                bounciness = 0.15f,
                friction = 0.6f
            });

        //  Wound sprite cache 
        private static bool _woundCached;
        private static Sprite _woundSprite;
        private static Sprite _woundPanel;

        //  DamageBlock throttle 
        private static int _dmgFrame;
        private static int _dmgCount;

        //  MaterialPropertyBlock (reused) 
        private static MaterialPropertyBlock _mpb;
        internal static MaterialPropertyBlock MPB => _mpb ?? (_mpb = new MaterialPropertyBlock());

        private static int _emissionId = -1;
        internal static int EmissionColorId =>
            _emissionId == -1
                ? (_emissionId = Shader.PropertyToID("_EmissionColor"))
                : _emissionId;

        //  WOUND SPRITES 

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

        //  THROTTLE 

        internal static bool TryDamageSlot()
        {
            int f = Time.frameCount;
            if (f != _dmgFrame) { _dmgFrame = f; _dmgCount = 0; }
            if (_dmgCount >= ShrapnelConfig.MaxDamagePerFrame.Value) return false;
            _dmgCount++;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        //  PRIMARY SHRAPNEL — physics projectile with damage
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns a physics shrapnel with random direction.
        /// Registers in <see cref="DebrisTracker"/>.
        /// </summary>
        public static void Spawn(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng)
        {
            Vector2 direction = GetLaunchDirection(type, rng);
            SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        /// <summary>
        /// Spawns a physics shrapnel with specified direction.
        /// Used for stratified angular distribution.
        /// Registers in <see cref="DebrisTracker"/>.
        /// </summary>
        public static void SpawnDirectional(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            // Apply slight spread (±15°) for natural variation
            direction = MathHelper.RotateDirection(direction, rng.Range(-0.26f, 0.26f));
            SpawnCore(epicenter, baseSpeed, type, weight, index, rng, direction);
        }

        /// <summary>
        /// Core shrapnel creation logic. Called by both Spawn and SpawnDirectional.
        /// </summary>
        private static void SpawnCore(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight,
            int index, System.Random rng, Vector2 direction)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return;

            EnsureWoundSprites();

            //  GameObject 
            GameObject obj = new GameObject($"Shr_{type}_{index}");
            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.3f;
            obj.layer = 0;

            float scale = ScaleForWeight(weight, rng);
            obj.transform.localScale = Vector3.one * scale;

            //  SpriteRenderer 
            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = litMat;

            float heat = HeatForWeight(weight);
            sr.color = Color.Lerp(ShrapnelVisuals.GetColdColor(type), ShrapnelVisuals.GetHotColor(), heat);

            if (heat > 0.3f)
            {
                MPB.Clear();
                MPB.SetColor(EmissionColorId, ShrapnelVisuals.GetHotColor() * heat * 1.3f);
                sr.SetPropertyBlock(MPB);
            }

            //  Rigidbody2D 
            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.sharedMaterial = PhysMat;
            ConfigureRigidbody(rb, weight);

            //  Collider (delayed enable) 
            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = weight == ShrapnelWeight.Massive ? 0.5f : 0.3f;
            col.sharedMaterial = PhysMat;
            col.enabled = false;

            //  ShrapnelProjectile 
            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = type;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = weight != ShrapnelWeight.Hot;
            SetDamage(proj, type, weight, rng);

            //  Launch 
            LaunchWithDirection(rb, weight, baseSpeed, direction, rng);
            TryAddTrail(obj, proj, weight, rng);

            DebrisTracker.Register(obj);
        }

        /// <summary>
        /// Configures Rigidbody2D parameters based on weight category.
        /// </summary>
        private static void ConfigureRigidbody(Rigidbody2D rb, ShrapnelWeight weight)
        {
            switch (weight)
            {
                case ShrapnelWeight.Hot:
                    rb.mass = 0.02f;
                    rb.gravityScale = 0.3f;
                    rb.drag = 0.4f;
                    break;
                case ShrapnelWeight.Medium:
                    rb.mass = 0.08f;
                    rb.gravityScale = 0.15f;
                    rb.drag = 0.2f;
                    break;
                case ShrapnelWeight.Heavy:
                    rb.mass = 0.25f;
                    rb.gravityScale = 0.35f;
                    rb.drag = 0.2f;
                    break;
                case ShrapnelWeight.Massive:
                    rb.mass = 0.8f;
                    rb.gravityScale = 0.5f;
                    rb.drag = 0.1f;
                    break;
            }
        }

        /// <summary>
        /// Gets launch direction based on explosion type.
        /// Mines use 240° upward cone (excludes ground).
        /// Others use full 360°.
        /// </summary>
        private static Vector2 GetLaunchDirection(ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            if (type == ShrapnelProjectile.ShrapnelType.Metal)
            {
                // Mine: 240° upward (-30° to 210°)
                float angleDeg = rng.Range(-30f, 210f);
                return MathHelper.AngleToDirectionDeg(angleDeg);
            }
            return rng.OnUnitCircle();
        }

        /// <summary>
        /// Launches shrapnel in specified direction with weight-based speed modifier.
        /// </summary>
        private static void LaunchWithDirection(Rigidbody2D rb, ShrapnelWeight weight,
            float baseSpeed, Vector2 direction, System.Random rng)
        {
            float speedMult = GetSpeedMultiplier(weight, rng);
            float targetSpeed = MathHelper.ClampSpeed(baseSpeed * speedMult, ShrapnelSpawnLogic.GlobalMaxSpeed);

            rb.AddForce(direction * targetSpeed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-500f, 500f));
        }

        private static float GetSpeedMultiplier(ShrapnelWeight weight, System.Random rng)
        {
            switch (weight)
            {
                case ShrapnelWeight.Hot: return rng.Range(0.8f, 1.3f);
                case ShrapnelWeight.Medium: return rng.Range(0.8f, 1.2f);
                case ShrapnelWeight.Heavy: return rng.Range(0.4f, 0.8f);
                case ShrapnelWeight.Massive: return rng.Range(0.2f, 0.4f);
                default: return 1f;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  VISUAL SHRAPNEL — no physics, transform-only
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns visual-only shrapnel with random direction.
        /// </summary>
        public static void SpawnVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            Vector2 direction = GetLaunchDirection(type, rng);
            SpawnVisualCore(epicenter, baseSpeed, type, rng, direction);
        }

        /// <summary>
        /// Spawns visual-only shrapnel at specified angle (radians).
        /// Used for pyrotechnic shrapnel in stratified distribution.
        /// </summary>
        public static void SpawnVisual(Vector2 epicenter, float baseSpeed,
            ShrapnelProjectile.ShrapnelType type, System.Random rng, float angleRad)
        {
            float spread = rng.Range(-0.2f, 0.2f);
            Vector2 direction = MathHelper.AngleToDirection(angleRad + spread);
            SpawnVisualCore(epicenter, baseSpeed, type, rng, direction);
        }

        /// <summary>
        /// Core visual shrapnel creation.
        /// </summary>
        private static void SpawnVisualCore(Vector2 epicenter, float baseSpeed,
           ShrapnelProjectile.ShrapnelType type, System.Random rng, Vector2 direction)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            GameObject obj = new GameObject("VisualShr");
            obj.transform.position = epicenter + rng.InsideUnitCircle() * 0.2f;
            obj.transform.localScale = Vector3.one * rng.Range(0.04f, 0.1f);

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
            sr.sharedMaterial = litMat;
            sr.sortingOrder = 9;

            Color hotCol = ShrapnelVisuals.GetHotColor();
            sr.color = hotCol;

            MPB.Clear();
            MPB.SetColor(EmissionColorId, hotCol * rng.Range(1f, 2f));
            sr.SetPropertyBlock(MPB);

            float speed = MathHelper.ClampSpeed(baseSpeed * rng.Range(2f, 3.5f), ShrapnelSpawnLogic.GlobalMaxSpeed);
            float lifetime = rng.Range(0.15f, 0.3f);

            var visual = obj.AddComponent<VisualShrapnel>();
            visual.Initialize(direction, speed, lifetime);

            DebrisTracker.RegisterVisual(obj); // WHY: Visual pool, not physical
        }

        // ═══════════════════════════════════════════════════════════════
        //  ASH CLOUD
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Spawns ash/ember cloud with realistic physics dispersion.
        /// </summary>
        public static void SpawnAshCloud(Vector2 epicenter, int count,
            ShrapnelProjectile.ShrapnelType type, System.Random rng)
        {
            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

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

                float gravity = rng.Range(0.8f, 2.0f);
                var visual = new VisualParticleParams(
                    rng.Range(0.02f, 0.06f),
                    color,
                    sortingOrder: 8,
                    ShrapnelVisuals.TriangleShape.Chunk);

                var physics = AshPhysicsParams.Ash(velocity, rng.Range(2f, 5f), gravity, rng);

                // WHY: Ash particles go to visual pool
                ParticleHelper.SpawnAshParticle("Ash", position, visual, physics, rng.Range(0f, 100f));
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
            {
                return new Color(
                    rng.Range(0.8f, 1f),
                    rng.Range(0.2f, 0.4f),
                    rng.Range(0f, 0.1f),
                    0.7f);
            }

            float g = rng.Range(0.15f, 0.35f);
            return new Color(g, g, g, 0.5f);
        }

        // ═══════════════════════════════════════════════════════════════
        //  BREAK FRAGMENTS
        // ═══════════════════════════════════════════════════════════════

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

            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            for (int i = 0; i < count; i++)
            {
                SpawnBreakFragment(position, impactNormal, parentScale, type,
                    childWeight, impactSpeed, i, rng, litMat);
            }
        }

        private static ShrapnelWeight GetChildWeight(ShrapnelWeight parent)
        {
            switch (parent)
            {
                case ShrapnelWeight.Massive: return ShrapnelWeight.Heavy;
                case ShrapnelWeight.Heavy: return ShrapnelWeight.Medium;
                default: return ShrapnelWeight.Hot;
            }
        }

        private static void SpawnBreakFragment(Vector2 position, Vector2 impactNormal,
            float parentScale, ShrapnelProjectile.ShrapnelType type,
            ShrapnelWeight weight, float impactSpeed, int index,
            System.Random rng, Material mat)
        {
            GameObject obj = new GameObject($"ShrBrk_{index}");
            float childScale = Mathf.Max(parentScale * rng.Range(0.4f, 0.6f), 0.05f);

            obj.transform.position = position + rng.InsideUnitCircle() * 0.15f;
            obj.transform.localScale = Vector3.one * childScale;
            obj.layer = 0;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = ShrapnelVisuals.GetTriangleSprite((ShrapnelVisuals.TriangleShape)rng.Next(0, 6));
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
            proj.Damage = rng.Range(2f, 6f);
            proj.BleedAmount = rng.Range(0.3f, 1.5f);

            Vector2 spread = rng.InsideUnitCircle() * 0.8f;
            Vector2 dir = (impactNormal + spread).normalized;
            dir.y = Mathf.Abs(dir.y) * 0.5f + 0.1f;

            float childSpeed = MathHelper.ClampSpeed(impactSpeed * rng.Range(0.2f, 0.5f), ShrapnelSpawnLogic.GlobalMaxSpeed);

            rb.AddForce(dir * childSpeed * rb.mass * 5f, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-300f, 300f));

            DebrisTracker.Register(obj);
        }

        // ═══════════════════════════════════════════════════════════════
        //  UTILITIES
        // ═══════════════════════════════════════════════════════════════

        internal static float ScaleForWeight(ShrapnelWeight w, System.Random rng)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot: return rng.Range(0.08f, 0.14f);
                case ShrapnelWeight.Medium: return rng.Range(0.14f, 0.25f);
                case ShrapnelWeight.Heavy: return rng.Range(0.22f, 0.45f);
                case ShrapnelWeight.Massive: return rng.Range(0.5f, 0.8f);
                default: return 0.18f;
            }
        }

        internal static float HeatForWeight(ShrapnelWeight w)
        {
            switch (w)
            {
                case ShrapnelWeight.Hot: return 1.0f;
                case ShrapnelWeight.Medium: return 0.4f;
                case ShrapnelWeight.Heavy: return 0.15f;
                case ShrapnelWeight.Massive: return 0.08f;
                default: return 0f;
            }
        }

        internal static void SetDamage(ShrapnelProjectile proj,
            ShrapnelProjectile.ShrapnelType type, ShrapnelWeight weight, System.Random rng)
        {
            switch (weight)
            {
                case ShrapnelWeight.Hot:
                    proj.Damage = rng.Range(3f, 8f);
                    proj.BleedAmount = rng.Range(0.5f, 2f);
                    break;
                case ShrapnelWeight.Medium:
                    proj.Damage = rng.Range(6f, 15f);
                    proj.BleedAmount = rng.Range(1f, 4f);
                    break;
                case ShrapnelWeight.Heavy:
                    proj.Damage = rng.Range(12f, 25f);
                    proj.BleedAmount = rng.Range(2f, 6f);
                    break;
                case ShrapnelWeight.Massive:
                    proj.Damage = rng.Range(25f, 50f);
                    proj.BleedAmount = rng.Range(5f, 12f);
                    break;
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
                    tr.startWidth = 0.12f * scale * 5f;
                    tr.endWidth = 0f;
                    tr.startColor = new Color(0.3f, 0.25f, 0.2f, 0.8f);
                    tr.endColor = new Color(0.2f, 0.2f, 0.2f, 0f);
                    break;
                case ShrapnelWeight.Hot:
                    tr.time = 0.25f;
                    tr.startWidth = 0.06f * scale * 10f;
                    tr.endWidth = 0f;
                    tr.startColor = new Color(1f, 0.5f, 0.1f, 0.9f);
                    tr.endColor = new Color(1f, 0.2f, 0f, 0f);
                    break;
                default:
                    tr.time = 0.15f;
                    tr.startWidth = 0.04f * scale * 10f;
                    tr.endWidth = 0f;
                    tr.startColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                    tr.endColor = new Color(0.4f, 0.4f, 0.4f, 0f);
                    break;
            }
        }
    }
}