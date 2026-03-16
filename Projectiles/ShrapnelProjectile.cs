using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;

namespace ScavShrapnelMod.Projectiles
{
    /// <summary>
    /// Main shrapnel component. Finite State Machine: Flying -> Stuck/Debris.
    ///
    /// Manages:
    /// - Physics (speed, gravity, collisions)
    /// - Heat cooldown (Hot -> cold color)
    /// - Damage (on Limb/Body hit)
    /// - Visual effects (outline, sparks, steam)
    /// - Ricochet from metal with many sparks
    /// - Breaking on impact
    /// - Logarithmic damage decay over lifetime
    /// - Visual decay (shrink + spin) in last 30% of lifetime
    /// </summary>
    public sealed class ShrapnelProjectile : MonoBehaviour
    {
        // ── ENUMS ──

        public enum ShrapnelType { Metal, Stone, Wood, Electronic, HeavyMetal }
        private enum State { Flying, Stuck, Debris }

        // ── CONSTANTS ──

        private const int GroundLayer = 6;
        private const float MaxVelocity = 100f;
        private const float PhysicsDelaySeconds = 0.05f;
        private const int MaxBlocksToDestroy = 3;
        private const int MaxRicochets = 3;
        private const float RicochetMaxAngleDeg = 30f;
        private const float RicochetSpeedRetention = 0.7f;
        private const float RicochetMinSpeed = 5f;
        private const float HeatCoolRate = 0.42f;
        private const float OutlineScaleMultiplier = 1.4f;
        private const float OutlineAlphaBase = 0.35f;
        private const float MinFlySqrSpeed = 0.5f;
        private const float MinFlyTimeBeforeDebris = 0.3f;
        private const float MinBlockImpactSpeed = 3f;
        private const float SparkImpactSpeed = 9f;

        // ── PUBLIC FIELDS ──

        public ShrapnelType Type;
        public ShrapnelWeight Weight;
        public float Damage;
        public float BleedAmount;
        public float Heat = 1f;
        public bool HasTrail;
        public bool CanBreak = true;

        // ── PRIVATE FIELDS ──

        private Rigidbody2D rb;
        private SpriteRenderer sr;
        private TrailRenderer trail;
        private Collider2D _col;
        private Transform _transform;

        private State state = State.Flying;
        private float lifeTimer;
        private float maxLifetime = 5f;
        private float _physicsDelay = PhysicsDelaySeconds;

        private float stuckTimer;
        private float debrisTimer;
        private float debrisLifetime;

        private int _blocksDestroyed;
        private int _ricochetCount;

        private bool _submerged;

        private Color coldColor;
        private bool cooledInWater;
        private float lastEmissionHeat = -1f;

        private int frameSlot;

        private GameObject _outlineObj;
        private SpriteRenderer _outlineSr;
        private bool _outlineApplied;

        private Vector3 _originalScale;

        /// <summary>
        /// WHY: Deterministic RNG seeded per-instance for consistent spark/debris behavior.
        /// Previous code used UnityEngine.Random which broke multiplayer determinism.
        /// </summary>
        private System.Random _rng;

        // ── PROPERTIES ──

        private float NormalizedLifetime
        {
            get
            {
                if (state == State.Stuck)
                    return 1f - Mathf.Clamp01(stuckTimer / ShrapnelConfig.StuckLifetime.Value);
                if (state == State.Debris)
                    return 1f - Mathf.Clamp01(debrisTimer / debrisLifetime);
                return 1f;
            }
        }

        private float DamageDecayMultiplier
        {
            get
            {
                float t = NormalizedLifetime;
                const float e = 2.71828f;
                return Mathf.Log(1f + t * e) / Mathf.Log(1f + e);
            }
        }

        // ── UNITY LIFECYCLE ──

        private void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            sr = GetComponent<SpriteRenderer>();
            trail = GetComponent<TrailRenderer>();
            _col = GetComponent<Collider2D>();
            _transform = transform;

            int id = GetInstanceID();
            frameSlot = Mathf.Abs(id) % 10;

            // WHY: Seed RNG from instance ID + frame for deterministic per-object randomness
            _rng = new System.Random(unchecked(id * 397 ^ Time.frameCount));
        }

        private void Start()
        {
            coldColor = ShrapnelVisuals.GetColdColor(Type);
            _originalScale = _transform.localScale;
        }

        private void Update()
        {
            int frame = Time.frameCount;
            switch (state)
            {
                case State.Flying: UpdateFlying(frame); break;
                case State.Stuck: UpdateStuck(frame); break;
                case State.Debris: UpdateDebris(frame); break;
            }
        }

        // ── STATE: FLYING ──

        private void UpdateFlying(int frame)
        {
            if (_physicsDelay > 0f)
            {
                _physicsDelay -= Time.deltaTime;
                if (_physicsDelay <= 0f && _col != null)
                    _col.enabled = true;
            }

            lifeTimer += Time.deltaTime;
            if (lifeTimer > maxLifetime) { BecomeDebris(); return; }

            float sqrSpeed = rb.velocity.sqrMagnitude;
            if (sqrSpeed > MaxVelocity * MaxVelocity)
                rb.velocity = rb.velocity.normalized * MaxVelocity;

            if (sqrSpeed < MinFlySqrSpeed && lifeTimer > MinFlyTimeBeforeDebris)
            { BecomeDebris(); return; }

            if (sqrSpeed > 4f)
            {
                float angle = Mathf.Atan2(rb.velocity.y, rb.velocity.x) * Mathf.Rad2Deg;
                _transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
            }

            if (frame % 3 == frameSlot % 3)
                TickHeat(Time.deltaTime * 3f);
        }

        // ── STATE: STUCK ──

        private void UpdateStuck(int frame)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer > ShrapnelConfig.StuckLifetime.Value)
            {
                Destroy(gameObject);
                return;
            }

            if (!_outlineApplied)
            {
                CreateOutline();
                _outlineApplied = true;
            }

            ApplyVisualDecay();

            if (frame % 5 == frameSlot % 5)
                PulseOutline();

            if (frame % 10 == frameSlot)
                CheckSupportAndFall();
        }

        // ── STATE: DEBRIS ──

        private void UpdateDebris(int frame)
        {
            debrisTimer += Time.deltaTime;
            if (debrisTimer > debrisLifetime)
            {
                Destroy(gameObject);
                return;
            }

            if (!_outlineApplied)
            {
                CreateOutline();
                _outlineApplied = true;
            }

            ApplyVisualDecay();

            if (frame % 5 == frameSlot % 5)
                PulseOutline();

            if (frame % 30 == frameSlot)
                CheckSubmerged();

            if (frame % 10 == frameSlot)
                CheckSupportAndFall();
        }

        // ── VISUAL DECAY ──

        private void ApplyVisualDecay()
        {
            float t = NormalizedLifetime;

            // Only decay in last 30% of lifetime
            if (t > 0.3f) return;

            float decayT = t / 0.3f;
            float decayFactor = decayT * decayT * (3f - 2f * decayT);

            float shrinkFactor = 0.2f + 0.8f * decayFactor;
            _transform.localScale = _originalScale * shrinkFactor;

            float spinSpeed = (1f - decayFactor) * 360f;
            _transform.Rotate(0f, 0f, spinSpeed * Time.deltaTime);

            if (sr != null)
            {
                Color c = sr.color;
                c.a = Mathf.Lerp(0.1f, 1f, decayFactor);
                sr.color = c;
            }
        }

        // ── OUTLINE ──

        private void CreateOutline()
        {
            if (_outlineObj != null) return;
            if (_submerged) return;

            _outlineObj = new GameObject("Outline");
            _outlineObj.transform.SetParent(_transform, false);
            _outlineObj.transform.localPosition = Vector3.zero;
            _outlineObj.transform.localRotation = Quaternion.identity;
            _outlineObj.transform.localScale = Vector3.one * OutlineScaleMultiplier;

            _outlineSr = _outlineObj.AddComponent<SpriteRenderer>();
            _outlineSr.sprite = sr.sprite;
            _outlineSr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
            _outlineSr.sortingOrder = sr.sortingOrder - 1;
            _outlineSr.color = new Color(0.9f, 0.1f, 0.05f, OutlineAlphaBase);
        }

        private void DestroyOutline()
        {
            if (_outlineObj != null)
            {
                Destroy(_outlineObj);
                _outlineObj = null;
                _outlineSr = null;
            }
        }

        private void PulseOutline()
        {
            if (_outlineSr == null) return;

            if (_submerged)
            {
                DestroyOutline();
                _outlineApplied = false;
                return;
            }

            float phase = Time.time * 3.14f + frameSlot * 0.628f;
            float sinVal = Mathf.Sin(phase);
            float alpha = OutlineAlphaBase + sinVal * 0.15f;
            _outlineSr.color = new Color(0.9f, 0.1f, 0.05f, alpha);
        }

        // ── WATER CHECK ──

        private void CheckSubmerged()
        {
            try
            {
                Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(_transform.position);
                float liquidLevel = FluidManager.main.WaterInfo(wPos).Item1;
                _submerged = liquidLevel > 0f;
            }
            catch
            {
                _submerged = false;
            }
        }

        // ── SUPPORT CHECK ──

        /// <summary>
        /// Checks if there's support under the shrapnel.
        /// Only active in Stuck/Debris states (not Flying).
        ///
        /// WHY: Previous version destroyed shrapnel on IndexOutOfRangeException
        /// which killed many shrapnel that briefly flew outside world bounds.
        /// Now only destroys if CONFIRMED no support, not on exceptions.
        /// </summary>
        private void CheckSupportAndFall()
        {
            // WHY: Only check support for Stuck/Debris.
            // Flying shrapnel handles its own out-of-bounds via lifeTimer.
            if (state == State.Flying) return;

            try
            {
                Vector2Int currentPos = WorldGeneration.world.WorldToBlockPos((Vector2)_transform.position);
                Vector2Int belowPos = currentPos + Vector2Int.down;

                if (WorldGeneration.world.GetBlock(currentPos) == 0 &&
                    WorldGeneration.world.GetBlock(belowPos) == 0)
                {
                    RestorePhysicsAndFly();
                }
            }
            catch (IndexOutOfRangeException)
            {
                // WHY: Shrapnel is outside world bounds.
                // For Debris/Stuck this means the block was destroyed and
                // shrapnel is falling into void. Safe to destroy.
                // But DON'T destroy Flying shrapnel here — let lifeTimer handle it.
                if (state == State.Stuck || state == State.Debris)
                    Destroy(gameObject);
            }
        }

        private void RestorePhysicsAndFly()
        {
            state = State.Flying;
            _outlineApplied = false;
            lifeTimer = 0f;
            maxLifetime = 3f;
            _originalScale = _transform.localScale;
            rb.isKinematic = false;

            switch (Weight)
            {
                case ShrapnelWeight.Hot: rb.gravityScale = 0.3f; break;
                case ShrapnelWeight.Medium: rb.gravityScale = 0.15f; break;
                case ShrapnelWeight.Heavy: rb.gravityScale = 0.35f; break;
                case ShrapnelWeight.Massive: rb.gravityScale = 0.5f; break;
                default: rb.gravityScale = 0.5f; break;
            }

            if (_col != null) _col.isTrigger = false;
            DestroyOutline();
        }

        // ── COLLISION ──

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (state == State.Debris || state == State.Stuck)
            {
                if (collision.relativeVelocity.magnitude > 5f)
                    BreakShard();
                return;
            }

            if (state != State.Flying) return;

            if (collision.collider.TryGetComponent(out Limb limb))
            {
                HitLimb(limb);
                return;
            }

            if (collision.collider.TryGetComponent(out Body body) &&
                body.limbs != null && body.limbs.Length > 0)
            {
                Limb target = FindBestLimbByTrajectory(body, collision);
                HitLimb(target);
                return;
            }

            if (collision.gameObject.layer == GroundLayer)
                HitBlock(collision);
        }

        private Limb FindBestLimbByTrajectory(Body body, Collision2D collision)
        {
            if (rb == null || rb.isKinematic || rb.bodyType == RigidbodyType2D.Static)
                return FindClosestLimb(body, collision);

            Vector2 shrapnelPos = (Vector2)_transform.position;
            Vector2 vel = rb.velocity;

            if (vel.sqrMagnitude < 1f)
                return FindClosestLimb(body, collision);

            Vector2 velDir = vel.normalized;

            Limb bestLimb = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;

                Vector2 limbPos = (Vector2)l.transform.position;
                Vector2 toL = limbPos - shrapnelPos;

                float forward = Vector2.Dot(toL, velDir);
                Vector2 closestOnRay = shrapnelPos + velDir * forward;
                float perpDistSqr = (limbPos - closestOnRay).sqrMagnitude;

                float behindPenalty = forward < 0f ? 4f : 0f;
                float score = perpDistSqr + behindPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestLimb = l;
                }
            }

            return bestLimb ?? FindClosestLimb(body, collision);
        }

        private Limb FindClosestLimb(Body body, Collision2D collision)
        {
            Vector2 hitPos = collision.contactCount > 0
                ? collision.GetContact(0).point
                : (Vector2)_transform.position;

            Limb closest = body.limbs[0];
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;

                float dist = ((Vector2)l.transform.position - hitPos).sqrMagnitude;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = l;
                }
            }

            return closest;
        }

        private void HitBlock(Collision2D collision)
        {
            if (collision.contactCount == 0) return;
            float impactSpeed = collision.relativeVelocity.magnitude;
            if (impactSpeed < MinBlockImpactSpeed) { BecomeDebris(); return; }

            Vector2 hitPoint = collision.GetContact(0).point;
            Vector2 hitNormal = collision.GetContact(0).normal;

            if (TryRicochet(impactSpeed, hitPoint, hitNormal))
                return;

            if (CanBreak && TryBreak(impactSpeed, hitPoint, hitNormal))
                return;

            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) { BecomeDebris(); return; }

                BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                if (info == null) { BecomeDebris(); return; }

                float kineticDamage = impactSpeed * rb.mass * 10f;

                if (kineticDamage > info.health && _blocksDestroyed < MaxBlocksToDestroy)
                {
                    if (ShrapnelFactory.TryDamageSlot())
                    {
                        WorldGeneration.world.DamageBlock(hitPoint - hitNormal * 0.1f, kineticDamage, true, false);
                        _blocksDestroyed++;
                    }
                    rb.velocity = -hitNormal * impactSpeed * 0.4f;
                    return;
                }

                if (_blocksDestroyed >= MaxBlocksToDestroy)
                {
                    BecomeStuck(blockPos, hitPoint);
                    return;
                }

                bool isSoft = !info.metallic && info.health <= 300f;
                if ((isSoft || impactSpeed > 30f) && impactSpeed > 5f)
                {
                    BecomeStuck(blockPos, hitPoint);
                    return;
                }

                if (info.metallic && impactSpeed > SparkImpactSpeed)
                    SpawnSparks(hitPoint, hitNormal, false);

                rb.velocity *= 0.4f;
            }
            catch { BecomeDebris(); }
        }

        // ── RICOCHET ──

        private bool TryRicochet(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            if (_ricochetCount >= MaxRicochets) return false;
            if (impactSpeed < RicochetMinSpeed) return false;

            try
            {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(hitPoint - hitNormal * 0.1f);
                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) return false;

                BlockInfo info = WorldGeneration.world.GetBlockInfo(blockId);
                if (info == null || !info.metallic) return false;
            }
            catch { return false; }

            Vector2 velDir = rb.velocity.normalized;
            float dotNormal = Mathf.Abs(Vector2.Dot(velDir, hitNormal));
            float angleFromSurface = Mathf.Asin(dotNormal) * Mathf.Rad2Deg;

            if (angleFromSurface > RicochetMaxAngleDeg) return false;

            Vector2 reflected = Vector2.Reflect(rb.velocity, hitNormal);
            rb.velocity = reflected * RicochetSpeedRetention;
            _ricochetCount++;

            // Spawn many sparks and debris on ricochet
            SpawnSparks(hitPoint, hitNormal, true);

            return true;
        }

        private bool TryBreak(float impactSpeed, Vector2 hitPoint, Vector2 hitNormal)
        {
            float breakThreshold, breakChance;
            switch (Weight)
            {
                case ShrapnelWeight.Massive: breakThreshold = 8f; breakChance = 0.6f; break;
                case ShrapnelWeight.Heavy: breakThreshold = 15f; breakChance = 0.35f; break;
                case ShrapnelWeight.Medium: breakThreshold = 20f; breakChance = 0.2f; break;
                default: return false;
            }

            if (impactSpeed < breakThreshold) return false;

            float roll = DeterministicRoll(hitPoint);
            if (roll > breakChance) return false;

            ShrapnelFactory.SpawnBreakFragments(
                hitPoint, hitNormal, _transform.localScale.x,
                Type, Weight, impactSpeed);
            BreakShard();
            return true;
        }

        private float DeterministicRoll(Vector2 pos)
        {
            int hash = unchecked(
                (int)(pos.x * 73856093f) ^
                (int)(pos.y * 19349663f) ^
                Time.frameCount * 83492791);
            return (float)((uint)hash % 10000) / 10000f;
        }

        // ── SPARKS ──

        /// <summary>
        /// Spawns sparks on metal impact or ricochet.
        /// WHY: Now uses instance System.Random instead of UnityEngine.Random
        /// for multiplayer determinism. All sparks registered in DebrisTracker.
        /// </summary>
        private void SpawnSparks(Vector2 pos, Vector2 normal, bool isRicochet)
        {
            int minSparks, maxSparks;
            if (isRicochet)
            {
                minSparks = ShrapnelConfig.RicochetSparksMin.Value;
                maxSparks = ShrapnelConfig.RicochetSparksMax.Value;
            }
            else
            {
                minSparks = ShrapnelConfig.MetalImpactSparksMin.Value;
                maxSparks = ShrapnelConfig.MetalImpactSparksMax.Value;
            }

            int sparkCount = _rng.Range(minSparks, maxSparks);

            for (int i = 0; i < sparkCount; i++)
            {
                GameObject spark = new GameObject("Spark");
                spark.transform.position = pos + _rng.InsideUnitCircle() * 0.1f;
                spark.transform.localScale = Vector3.one * _rng.Range(0.02f, 0.06f);

                SpriteRenderer ssr = spark.AddComponent<SpriteRenderer>();
                ssr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Needle);
                ssr.sharedMaterial = ShrapnelVisuals.LitMaterial;

                float colorVar = _rng.NextFloat();
                ssr.color = new Color(
                    1f,
                    Mathf.Lerp(0.5f, 0.95f, colorVar),
                    Mathf.Lerp(0.1f, 0.5f, colorVar));
                ssr.sortingOrder = 11;

                ShrapnelFactory.MPB.Clear();
                ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                    new Color(3f, Mathf.Lerp(1.5f, 2.5f, colorVar), 0.5f));
                ssr.SetPropertyBlock(ShrapnelFactory.MPB);

                float spreadAngle = _rng.Range(-60f, 60f) * Mathf.Deg2Rad;
                Vector2 baseDir = isRicochet ? Vector2.Reflect(-rb.velocity.normalized, normal) : normal;
                Vector2 sparkDir = MathHelper.RotateDirection(baseDir, spreadAngle);

                float sparkSpeed = _rng.Range(3f, 10f);
                float sparkLife = _rng.Range(0.08f, 0.25f);

                var visual = spark.AddComponent<VisualShrapnel>();
                visual.Initialize(sparkDir, sparkSpeed, sparkLife);

                // WHY: Previously untracked — sparks were invisible to DebrisTracker,
                // bypassing eviction cap and causing potential memory leaks.
                DebrisTracker.RegisterVisual(spark);
            }

            if (isRicochet)
            {
                SpawnRicochetDebris(pos, normal);
            }
        }

        /// <summary>
        /// Spawns metal debris particles on ricochet.
        /// WHY: Now uses instance System.Random for determinism.
        /// </summary>
        private void SpawnRicochetDebris(Vector2 pos, Vector2 normal)
        {
            int minDebris = ShrapnelConfig.RicochetDebrisMin.Value;
            int maxDebris = ShrapnelConfig.RicochetDebrisMax.Value;
            int debrisCount = _rng.Range(minDebris, maxDebris);

            Material unlitMat = ShrapnelVisuals.UnlitMaterial;
            if (unlitMat == null) return;

            for (int i = 0; i < debrisCount; i++)
            {
                GameObject debris = new GameObject("RicochetDebris");
                debris.transform.position = pos + _rng.InsideUnitCircle() * 0.08f;
                debris.transform.localScale = Vector3.one * _rng.Range(0.03f, 0.08f);

                SpriteRenderer dsr = debris.AddComponent<SpriteRenderer>();
                dsr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
                dsr.sharedMaterial = unlitMat;
                dsr.sortingOrder = 10;

                float gray = _rng.Range(0.3f, 0.5f);
                Color debrisColor = new Color(gray, gray, gray, 0.9f);

                float angle = _rng.Range(-70f, 70f) * Mathf.Deg2Rad;
                Vector2 debrisDir = MathHelper.RotateDirection(normal, angle);

                Vector2 velocity = debrisDir * _rng.Range(2f, 6f);

                AshParticle ash = debris.AddComponent<AshParticle>();
                ash.Initialize(velocity, _rng.Range(0.3f, 0.8f), debrisColor,
                    _rng.Range(0f, 6.28f), 1.2f);

                DebrisTracker.RegisterVisual(debris);
            }
        }

        // ── LIMB DAMAGE ──

        private void HitLimb(Limb limb)
        {
            if (limb.dismembered) { Destroy(gameObject); return; }

            float armor = limb.GetArmorReduction();

            float decayMult = DamageDecayMultiplier;
            float dmg = Damage * decayMult / armor;
            float bleed = BleedAmount * decayMult / armor;

            limb.skinHealth -= dmg * 0.7f;
            limb.muscleHealth -= dmg;
            limb.bleedAmount += bleed;

            float armorWearAmount;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: armorWearAmount = 0.005f; break;
                case ShrapnelWeight.Medium: armorWearAmount = 0.01f; break;
                case ShrapnelWeight.Heavy: armorWearAmount = 0.02f; break;
                case ShrapnelWeight.Massive: armorWearAmount = 0.05f; break;
                default: armorWearAmount = 0.01f; break;
            }
            limb.DamageWearables(armorWearAmount);

            float embedChance;
            switch (Weight)
            {
                case ShrapnelWeight.Hot: embedChance = 0.15f; break;
                case ShrapnelWeight.Medium: embedChance = 0.4f; break;
                case ShrapnelWeight.Heavy: embedChance = 0.7f; break;
                case ShrapnelWeight.Massive: embedChance = 0.9f; break;
                default: embedChance = 0.3f; break;
            }
            embedChance *= decayMult;

            Vector2 limbPos = (Vector2)limb.transform.position;
            float roll1 = DeterministicRoll(limbPos);
            float roll2 = DeterministicRoll(limbPos + Vector2.right);
            if (roll1 < embedChance / (armor * armor) && roll2 > 0.2f)
                limb.shrapnel++;

            if (Weight == ShrapnelWeight.Massive)
            {
                limb.BreakBone();
            }
            else if (Weight == ShrapnelWeight.Heavy)
            {
                float boneChance = Type == ShrapnelType.HeavyMetal ? 0.15f : 0.08f;
                if (DeterministicRoll(limbPos + Vector2.left) < boneChance / armor)
                    limb.BreakBone();
            }

            if (limb.isVital && Weight != ShrapnelWeight.Hot)
            {
                float intChance = Weight == ShrapnelWeight.Massive ? 0.6f
                                : (Weight == ShrapnelWeight.Heavy ? 0.3f : 0.15f);
                intChance *= decayMult;
                if (DeterministicRoll(limbPos + Vector2.down) < intChance / armor)
                    limb.body.internalBleeding += dmg * 0.3f;
            }

            if (limb.isHead)
            {
                limb.body.consciousness -= dmg * 2f;

                if ((Weight == ShrapnelWeight.Heavy || Weight == ShrapnelWeight.Massive) &&
                    DeterministicRoll(limbPos * 2f) < 0.2f * decayMult / armor)
                    limb.body.brainHealth -= dmg * 0.5f;

                if (Weight == ShrapnelWeight.Massive &&
                    DeterministicRoll(limbPos * 3f) < 0.3f * decayMult / armor)
                    limb.body.Disfigure();
            }

            limb.body.shock = Mathf.Max(limb.body.shock, Damage * decayMult * 2f);
            limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 20f + Damage * decayMult);
            limb.body.DoGoreSound();
            limb.body.talker.Talk(Locale.GetCharacter("loud"), null, false, false);

            float ragdollThreshold = state == State.Flying ? 15f : 25f;
            if (Weight == ShrapnelWeight.Heavy || Weight == ShrapnelWeight.Massive ||
                Damage * decayMult > ragdollThreshold)
            {
                Vector2 knockbackDir = (state == State.Flying && rb != null && !rb.isKinematic)
                    ? rb.velocity.normalized
                    : Vector2.up;
                limb.body.lastTimeStepVelocity = knockbackDir *
                    (Weight == ShrapnelWeight.Massive ? 10f : 5f) * decayMult;
                limb.body.Ragdoll();
            }

            ApplyWoundVisuals(limb);
            Destroy(gameObject);
        }

        private void ApplyWoundVisuals(Limb limb)
        {
            try
            {
                if (ShrapnelFactory.WoundSprite != null)
                    limb.CreateTemporarySprite(ShrapnelFactory.WoundSprite, 0f, null, false, 600f,
                        (Limb x) => !x.hasShrapnel);
                if (ShrapnelFactory.WoundPanel != null)
                    WoundView.view.AddImageToLimb(limb, ShrapnelFactory.WoundPanel, false,
                        (Limb x) => !x.hasShrapnel || x.dismembered);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Shrapnel] Wound: {e.Message}");
            }
        }

        // ── STATE TRANSITIONS ──

        /// <summary>
        /// Transitions shrapnel to Stuck state (embedded in block).
        /// Stops physics, positions at hit point.
        /// </summary>
        private void BecomeStuck(Vector2Int blockPos, Vector2 hitPoint)
        {
            state = State.Stuck;
            stuckTimer = 0f;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            // WHY: Check body type before velocity access
            if (rb != null && !rb.isKinematic && rb.bodyType != RigidbodyType2D.Static)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            rb.isKinematic = true;

            _transform.position = (Vector2)hitPoint;
            if (trail != null) trail.enabled = false;
            ClearHeatAndEmission();
        }

        /// <summary>
        /// Transitions shrapnel to Debris state (lying on surface).
        /// Lifetime from config based on material type.
        /// </summary>
        private void BecomeDebris()
        {
            state = State.Debris;
            _outlineApplied = false;
            _originalScale = _transform.localScale;

            // WHY: Check isKinematic before accessing velocity to prevent
            // "Cannot use velocity on static body" error
            if (rb != null && !rb.isKinematic && rb.bodyType != RigidbodyType2D.Static)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            rb.isKinematic = true;

            if (trail != null) trail.enabled = false;
            if (_col != null) _col.isTrigger = true;
            ClearHeatAndEmission();

            debrisTimer = 0f;

            switch (Type)
            {
                case ShrapnelType.Metal: debrisLifetime = ShrapnelConfig.DebrisLifetimeMetal.Value; break;
                case ShrapnelType.HeavyMetal: debrisLifetime = ShrapnelConfig.DebrisLifetimeHeavyMetal.Value; break;
                case ShrapnelType.Stone: debrisLifetime = ShrapnelConfig.DebrisLifetimeStone.Value; break;
                case ShrapnelType.Wood: debrisLifetime = ShrapnelConfig.DebrisLifetimeWood.Value; break;
                case ShrapnelType.Electronic: debrisLifetime = ShrapnelConfig.DebrisLifetimeElectronic.Value; break;
                default: debrisLifetime = 300f; break;
            }
        }

        // ── TRIGGER ──

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (state != State.Debris && state != State.Stuck) return;

            if (!other.TryGetComponent(out Body body))
            {
                BreakShard();
                return;
            }

            if (state != State.Debris) return;

            if (_submerged) return;

            bool isStanding = body.transform.position.y > _transform.position.y;
            if (isStanding)
            {
                Item footwear = body.GetWearableBySlotID("feet");
                if (footwear != null)
                {
                    footwear.SetCondition(footwear.condition -
                        0.05f * footwear.Stats.wearableHitDurabilityLossMultiplier);
                    BreakShard();
                    return;
                }
            }

            Limb target = FindClosestUndamagedLimb(body);
            if (target == null) { BreakShard(); return; }

            float armor = target.GetArmorReduction();
            float decayMult = DamageDecayMultiplier;

            // WHY: Use instance _rng instead of allocating new System.Random per trigger.
            // Previous code created new System.Random on every OnTriggerEnter2D.
            target.skinHealth -= _rng.Range(15f, 35f) * decayMult / armor;
            target.muscleHealth -= _rng.Range(5f, 15f) * decayMult / armor;
            target.bleedAmount += _rng.Range(3f, 12f) * decayMult / armor;
            target.pain += 50f * decayMult / armor;
            target.shrapnel++;

            target.DamageWearables(0.01f);

            body.adrenaline = Mathf.Max(body.adrenaline, 40f);
            body.DoGoreSound();
            body.talker.Talk(Locale.GetCharacter("steponglass"), null, false, false);
            ApplyWoundVisuals(target);
            BreakShard();
        }

        private Limb FindClosestUndamagedLimb(Body body)
        {
            if (body.limbs == null || body.limbs.Length == 0) return null;

            Vector2 myPos = (Vector2)_transform.position;
            Limb closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < body.limbs.Length; i++)
            {
                Limb l = body.limbs[i];
                if (l == null || l.dismembered) continue;

                float dist = ((Vector2)l.transform.position - myPos).sqrMagnitude;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = l;
                }
            }

            return closest;
        }

        private void BreakShard()
        {
            try
            {
                Sound.Play("glassshard", _transform.position, false, true, null, 1f, 1f, false, false);
            }
            catch { }
            Destroy(gameObject);
        }

        // ── HEAT SYSTEM ──

        private void TickHeat(float dt)
        {
            if (Heat <= 0f) return;

            float cool = HeatCoolRate * dt;
            if (WorldGeneration.world != null && WorldGeneration.world.ambientTemperature < 5f)
                cool *= 2f;

            Heat = Mathf.Max(Heat - cool, 0f);
            sr.color = Color.Lerp(coldColor, ShrapnelVisuals.GetHotColor(), Heat);

            if (Mathf.Abs(Heat - lastEmissionHeat) > 0.05f)
            {
                lastEmissionHeat = Heat;
                UpdateEmission();
            }

            if (Heat <= 0f) ClearHeatAndEmission();

            if (!cooledInWater && Heat > 0.1f)
            {
                try
                {
                    Vector2Int wPos = WorldGeneration.world.WorldToBlockPos(_transform.position);
                    if (FluidManager.main.WaterInfo(wPos).Item1 > 0f)
                    {
                        cooledInWater = true;
                        _submerged = true;
                        ClearHeatAndEmission();
                        Sound.Play("fizz", _transform.position, false, true, null, 1f, 1f, false, false);
                        SpawnSteamPuff();
                    }
                }
                catch { }
            }
        }

        private void SpawnSteamPuff()
        {
            // WHY: Use instance _rng instead of allocating new System.Random
            int count = _rng.Range(3, 6);
            for (int i = 0; i < count; i++)
            {
                GameObject steam = new GameObject("Steam");
                steam.transform.position = _transform.position;
                steam.transform.localScale = Vector3.one * _rng.Range(0.03f, 0.07f);

                SpriteRenderer ssr = steam.AddComponent<SpriteRenderer>();
                ssr.sprite = ShrapnelVisuals.GetTriangleSprite(ShrapnelVisuals.TriangleShape.Chunk);
                ssr.sharedMaterial = ShrapnelVisuals.UnlitMaterial;
                ssr.sortingOrder = 12;

                float gray = _rng.Range(0.8f, 1f);
                Color steamColor = new Color(gray, gray, gray, 0.5f);

                Vector2 velocity = new Vector2(_rng.Range(-0.3f, 0.3f), _rng.Range(1f, 2.5f));

                AshParticle ash = steam.AddComponent<AshParticle>();
                ash.Initialize(velocity, _rng.Range(0.5f, 1.2f), steamColor, _rng.Range(0f, 6.28f));

                DebrisTracker.RegisterVisual(steam);
            }
        }

        private void UpdateEmission()
        {
            if (Heat > 0.01f)
            {
                ShrapnelFactory.MPB.Clear();
                ShrapnelFactory.MPB.SetColor(ShrapnelFactory.EmissionColorId,
                    ShrapnelVisuals.GetHotColor() * Heat * 1.3f);
                sr.SetPropertyBlock(ShrapnelFactory.MPB);
            }
            else
            {
                ClearEmission();
            }
        }

        private void ClearHeatAndEmission()
        {
            Heat = 0f;
            sr.color = coldColor;
            ClearEmission();
        }

        private void ClearEmission()
        {
            sr.SetPropertyBlock(null);
            lastEmissionHeat = 0f;
        }
    }
}