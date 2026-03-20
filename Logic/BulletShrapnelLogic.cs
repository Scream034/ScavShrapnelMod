using System;
using UnityEngine;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Effects;
using ScavShrapnelMod.Projectiles;
using ScavShrapnelMod.Net;

namespace ScavShrapnelMod.Logic
{
    /// <summary>
    /// Shrapnel and effects from bullets hitting metal blocks.
    ///
    /// All numeric parameters from <see cref="ShrapnelConfig"/> (Bullets section).
    ///
    /// Mechanics:
    /// 1. Bullet hits block (TurretScript.Shoot -> Postfix)
    /// 2. Raycast along shot direction
    /// 3. If hit metallic block -> spawn fragments + full impact effects
    /// 4. Fragments fly away from hit point with random spread
    /// 5. Impact flash, spark shower, and metal chips for visceral feedback
    ///
    /// Performance limits:
    /// - Frame throttle (configurable)
    /// - Max fragments per spawn (configurable)
    /// - Only metallic blocks
    /// - Raycast max 200 units
    /// </summary>
    public static class BulletShrapnelLogic
    {
        /// <summary>Raycast distance. Matches TurretScript.Shoot (200f).</summary>
        private const float RaycastDistance = 200f;

        /// <summary>Ground layer name in Unity.</summary>
        private const string GroundLayerName = "Ground";

        /// <summary>Chance that fragment is Hot vs Medium.</summary>
        private const float HotWeightChance = 0.6f;

        /// <summary>Cached Ground layer mask.</summary>
        private static int _groundMask = -1;
        private static int GroundMask
        {
            get
            {
                if (_groundMask == -1)
                    _groundMask = LayerMask.GetMask(GroundLayerName);
                return _groundMask;
            }
        }

        private static int _lastSpawnFrame;

        /// <summary>
        /// Entry point. Called from Postfix patch of TurretScript.Shoot.
        /// Parameters from config.
        /// </summary>
        public static void TrySpawnFromBullet(FireInfo info)
        {
            // MULTIPLAYER: Only server spawns physics fragments.
            // Client receives visual mirrors via ShrapnelNetSync.
            if (!MultiplayerHelper.ShouldSpawnPhysicsShrapnel) return;

            try
            {
                int frame = Time.frameCount;
                if (frame - _lastSpawnFrame < ShrapnelConfig.BulletMinFramesBetweenSpawns.Value)
                    return;

                Vector2 origin = info.pos;
                Vector2 direction = info.dir;

                RaycastHit2D hit = Physics2D.Raycast(origin, direction, RaycastDistance, GroundMask);
                if (!hit.collider) return;

                Vector2 blockSamplePos = hit.point + direction * 0.1f;
                Vector2Int blockPos;
                try
                {
                    blockPos = WorldGeneration.world.WorldToBlockPos(blockSamplePos);
                }
                catch (IndexOutOfRangeException) { return; }

                ushort blockId = WorldGeneration.world.GetBlock(blockPos);
                if (blockId == 0) return;

                BlockInfo blockInfo = WorldGeneration.world.GetBlockInfo(blockId);
                if (blockInfo == null || !blockInfo.metallic) return;

                _lastSpawnFrame = frame;

                int seed = unchecked(
                    (int)(hit.point.x * 10000f) * 397 ^
                    (int)(hit.point.y * 10000f) ^
                    frame);
                System.Random rng = new(seed);

                // Spawn physical shrapnel fragments
                if (ShrapnelConfig.EnableBulletFragments.Value)
                {
                    int fragmentCount = rng.Range(
                        ShrapnelConfig.BulletFragmentsMin.Value,
                        ShrapnelConfig.BulletFragmentsMax.Value);

                    fragmentCount = Mathf.Max(1,
                        Mathf.RoundToInt(fragmentCount * ShrapnelConfig.SpawnCountMultiplier.Value));

                    for (int i = 0; i < fragmentCount; i++)
                        SpawnBulletFragment(hit.point, hit.normal, rng, i);
                }

                // Spawn full impact effects (flash, sparks, metal chips)
                if (ShrapnelConfig.EnableBulletImpactEffects.Value)
                {
                    BulletImpactEffects.SpawnFullImpact(hit.point, hit.normal, rng, false);
                }
            }
            catch (Exception e)
            {
                Console.Error($"BulletShrapnel: {e.Message}");
            }
        }

        /// <summary>
        /// Spawns one small physics fragment from bullet impact.
        /// Registers in <see cref="DebrisTracker"/>.
        /// </summary>
        private static void SpawnBulletFragment(Vector2 hitPoint, Vector2 hitNormal,
            System.Random rng, int index)
        {
            Material litMat = ShrapnelVisuals.LitMaterial;
            if (litMat == null) return;

            float scaleMultiplier = ShrapnelConfig.BulletScaleMultiplier.Value;
            float heatMultiplier = ShrapnelConfig.BulletHeatMultiplier.Value;
            float baseSpeed = ShrapnelConfig.BulletBaseSpeed.Value;
            float globalMaxSpeed = ShrapnelConfig.GlobalMaxSpeed.Value;

            ShrapnelWeight weight = rng.NextFloat() < HotWeightChance
                ? ShrapnelWeight.Hot
                : ShrapnelWeight.Medium;

            var shape = (ShrapnelVisuals.TriangleShape)rng.Next(0, 6);
            Sprite sprite = ShrapnelVisuals.GetTriangleSprite(shape);
            if (sprite == null) return;

            ShrapnelFactory.EnsureWoundSprites();

            GameObject obj = new($"BulletShr_{index}");
            obj.transform.position = hitPoint + rng.InsideUnitCircle() * 0.1f;
            obj.layer = 0;

            float scale = ShrapnelFactory.ScaleForWeight(weight, rng) * scaleMultiplier;
            obj.transform.localScale = Vector3.one * scale;

            SpriteRenderer sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 10;
            sr.sharedMaterial = litMat;

            float heat = ShrapnelFactory.HeatForWeight(weight) * heatMultiplier;
            sr.color = Color.Lerp(
                ShrapnelVisuals.GetColdColor(ShrapnelProjectile.ShrapnelType.Metal),
                ShrapnelVisuals.GetHotColor(), heat);

            Rigidbody2D rb = obj.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.mass = weight == ShrapnelWeight.Hot ? 0.01f : 0.04f;
            rb.gravityScale = 0.15f;
            rb.drag = 0.3f;

            CircleCollider2D col = obj.AddComponent<CircleCollider2D>();
            col.radius = 0.2f;

            ShrapnelProjectile proj = obj.AddComponent<ShrapnelProjectile>();
            proj.Type = ShrapnelProjectile.ShrapnelType.Metal;
            proj.Weight = weight;
            proj.Heat = heat;
            proj.CanBreak = false;
            proj.Damage = rng.Range(1f, 5f);
            proj.BleedAmount = rng.Range(0.3f, 1.5f);
            proj.Seed = rng.Next();

            Vector2 spread = rng.InsideUnitCircle() * 0.6f;
            Vector2 dir = (hitNormal + spread).normalized;
            dir.y = Mathf.Max(dir.y, 0.1f);
            dir.Normalize();

            float speed = Mathf.Min(
                baseSpeed * rng.Range(0.5f, 1.5f),
                globalMaxSpeed);

            rb.AddForce(dir * speed * rb.mass, ForceMode2D.Impulse);
            rb.AddTorque(rng.Range(-200f, 200f));

            DebrisTracker.Register(obj);
            ShrapnelNetSync.ServerRegister(proj);
        }
    }
}