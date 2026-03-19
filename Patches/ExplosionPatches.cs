using System;
using System.Collections.Generic;
using HarmonyLib;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;
using UnityEngine;

namespace ScavShrapnelMod.Patches
{
    // ═══════════════════════════════════════════════════════════════
    //  SHARED: Explosion tracking to prevent double-spawning
    //
    //  WHY: Multiple detection layers can catch the same explosion.
    //  E.g., CreateExplosionPatch fires, then DestroyBackup also fires
    //  for the same object. HashSet of position hashes prevents duplication.
    //
    //  Hash uses rounded position (×10) to absorb float drift.
    //  Queue evicts old entries to prevent unbounded growth.
    // ═══════════════════════════════════════════════════════════════

    public static class ExplosionTracker
    {
        private static readonly HashSet<int> _recent = new HashSet<int>();
        private static readonly Queue<int> _cleanup = new Queue<int>();
        private const int MaxTracked = 100;

        /// <summary>
        /// Generates position hash with 0.1 unit precision.
        /// Two explosions within 0.1 units produce the same hash.
        /// </summary>
        public static int GetHash(Vector2 pos)
        {
            int px = Mathf.RoundToInt(pos.x * 10f);
            int py = Mathf.RoundToInt(pos.y * 10f);
            return unchecked(px * 397 ^ py);
        }

        public static bool WasHandled(Vector2 pos)
            => _recent.Contains(GetHash(pos));

        public static void Track(Vector2 pos)
        {
            int hash = GetHash(pos);
            _recent.Add(hash);
            _cleanup.Enqueue(hash);

            while (_cleanup.Count > MaxTracked)
            {
                int old = _cleanup.Dequeue();
                _recent.Remove(old);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 1: Direct replacement of WorldGeneration.CreateExplosion
    //
    //  Prefix returns false — completely replaces the original method.
    //  Contains inlined vanilla explosion logic so we can wrap shrapnel
    //  phases around the block destruction call.
    //
    //  Execution order:
    //    1. PreExplosion (shrapnel, sparks, ash — needs intact blocks)
    //    2. RunVanillaExplosion (sound, damage, block destruction)
    //    3. PostExplosion (ground debris — needs exposed crater surfaces)
    //
    //  IMPORTANT: This patch may NOT fire if JIT inlines CreateExplosion.
    //  In that case, Layer 2 (DestroyBackup) catches the explosion.
    // ═══════════════════════════════════════════════════════════════

    public static class CreateExplosionPatch
    {
        /// <summary>
        /// Harmony Prefix for WorldGeneration.CreateExplosion.
        ///
        /// THREE MODES:
        ///
        /// 1. MP CLIENT:
        ///    - Block ALL local game logic calls (gravbag Update spam, etc.)
        ///    - Only process calls from MP mod's ForceCreateExplosionEffect
        ///      (detected by structuralDamage=0 AND disfigureChance=0)
        ///    - Spawn visual-only effects (no physics shrapnel)
        ///    - Return true to let vanilla explosion visuals play (sound, particles)
        ///
        /// 2. MP SERVER:
        ///    - Spawn full effects (physics + visuals)
        ///    - Return true to let vanilla CreateExplosion run + MP mod send to clients
        ///    - PostExplosion runs in Postfix after block destruction
        ///
        /// 3. SINGLEPLAYER:
        ///    - Full replacement (return false)
        ///    - Pre = RunVanillaExplosion = Post
        /// </summary>
        public static bool Prefix(ExplosionParams param)
        {
            try
            {
                bool isMP = MultiplayerHelper.IsNetworkRunning;
                bool isClient = MultiplayerHelper.IsClient;

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                // ═══════════════════════════════════════════════
                //  MP CLIENT
                // ═══════════════════════════════════════════════
                if (isClient)
                {
                    // MP mod's ForceCreateExplosionEffect zeroes ALL damage fields.
                    // Local game logic (gravbag.Update, mine trigger) keeps full damage.
                    // This is the ONLY reliable way to distinguish server packets.
                    bool isFromServer = param.structuralDamage <= 0.01f
                                     && param.disfigureChance <= 0.01f;

                    if (!isFromServer)
                        return false;  // Silently block local game logic

                    Plugin.Log.LogInfo($"[Explosion] CLIENT pos={param.position}" +
                        $" range={param.range:F1}");

                    // Spawn visual effects using standard mine profile.
                    // We can't know the exact explosion type from network params,
                    // but mine profile gives good visuals for all types.
                    // Client never spawns physics shrapnel (ShouldSpawnPhysicsShrapnel=false).
                    try
                    {
                        var clientParam = CreateClientVisualParams(param);
                        ShrapnelSpawnLogic.PreExplosion(clientParam);
                        ShrapnelSpawnLogic.PostExplosion(clientParam, preScan: true);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[Explosion] Client effects: {e.Message}");
                    }

                    // Let ForceCreateExplosionEffect's zero-damage call through
                    // so vanilla explosion visuals (sound, particle, blastmark) play
                    return true;
                }

                // ═══════════════════════════════════════════════
                //  MP SERVER
                // ═══════════════════════════════════════════════
                if (isMP)
                {
                    bool alreadyHandled = ExplosionTracker.WasHandled(param.position);
                    ExplosionTracker.Track(param.position);

                    if (!alreadyHandled)
                    {
                        Plugin.Log.LogInfo($"[Explosion] SERVER pos={param.position}" +
                            $" range={param.range:F1} dmg={param.structuralDamage:F0}");

                        try { ShrapnelSpawnLogic.PreExplosion(param); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Server Pre: {e.Message}");
                        }
                    }

                    // Let vanilla + MP mod run (sends CreateExplosionEffect to clients)
                    // Postfix handles PostExplosion after block destruction
                    return true;
                }

                // ═══════════════════════════════════════════════
                //  SINGLEPLAYER (full replacement)
                // ═══════════════════════════════════════════════
                {
                    bool alreadyHandled = ExplosionTracker.WasHandled(param.position);
                    ExplosionTracker.Track(param.position);

                    if (!alreadyHandled)
                    {
                        try { ShrapnelSpawnLogic.PreExplosion(param); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Pre: {e.Message}");
                        }
                    }

                    try { RunVanillaExplosion(param); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError(
                            $"[Explosion] Vanilla: {e.Message}\n{e.StackTrace}");
                    }

                    if (!alreadyHandled)
                    {
                        try { ShrapnelSpawnLogic.PostExplosion(param, preScan: false); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Explosion] Post: {e.Message}");
                        }
                    }

                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(
                    $"[Explosion] CRITICAL: {e.Message}\n{e.StackTrace}");
                return true;
            }
        }

        /// <summary>
        /// Postfix: runs AFTER vanilla CreateExplosion.
        /// Only needed for MP server where Prefix returns true.
        /// Spawns ground debris from newly-exposed crater surfaces.
        /// </summary>
        public static void Postfix(ExplosionParams param)
        {
            if (!MultiplayerHelper.IsNetworkRunning || MultiplayerHelper.IsClient)
                return;

            // Server only: PostExplosion after block destruction
            try
            {
                if (!ExplosionTracker.WasHandled(param.position))
                    return; // Not our explosion

                ShrapnelSpawnLogic.PostExplosion(param, preScan: false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Explosion] Postfix: {e.Message}");
            }
        }

        /// <summary>
        /// Creates ExplosionParams for client visual effects.
        /// Uses server's position/range/velocity but fills in standard
        /// mine-profile damage values for classification.
        ///
        /// WHY MINE PROFILE:
        ///   MP mod only sends position, velocity, range in its network packet.
        ///   We can't distinguish mine from gravbag or other explosions.
        ///   Mine profile (the most common) gives good visual results for all types.
        ///   Client-side physics shrapnel is disabled anyway, so damage values
        ///   only affect visual particle counts and types.
        /// </summary>
        private static ExplosionParams CreateClientVisualParams(ExplosionParams serverParam)
        {
            return new ExplosionParams
            {
                position = serverParam.position,
                range = serverParam.range,
                velocity = serverParam.velocity,
                // Standard mine defaults for visual classification
                structuralDamage = 500f,
                disfigureChance = 0.34f,
                shrapnelChance = 0.4f,
                sound = "explosion"
            };
        }

        /// <summary>
        /// 1:1 copy of decompiled WorldGeneration.CreateExplosion.
        /// Used only in singleplayer mode where we fully replace the original.
        /// </summary>
        private static void RunVanillaExplosion(ExplosionParams param)
        {
            Sound.Play(param.sound, Vector2.zero, true, false, null, 1f, 1f, false, false);

            UnityEngine.Object.Instantiate(
                Resources.Load("Special/ExplosionParticle"),
                param.position,
                Quaternion.identity);

            GameObject blastmark = UnityEngine.Object.Instantiate(
                Resources.Load("Special/blastmark"),
                WorldGeneration.world.GetClosestChunk(
                    WorldGeneration.world.WorldToBlockPos(param.position)).transform
            ) as GameObject;
            blastmark.transform.position = param.position;
            blastmark.transform.eulerAngles = new Vector3(
                0f, 0f, UnityEngine.Random.value * 360f);

            PlayerCamera.main.shaker.Shake(param.range * 20f);

            if (Vector2.Distance(param.position,
                PlayerCamera.main.body.transform.position) < param.range * 2.5f)
            {
                Sound.Play("tinnitus", Vector2.zero, true, false, null,
                    1f, 1f, true, true);
                PlayerCamera.main.body.eyePanicTime = 1f;
                PlayerCamera.main.body.eyeCloseTime = 5f;
                PlayerCamera.main.body.eyeScareTime = 12f;
                PlayerCamera.main.body.consciousness = 31f;
                PlayerCamera.main.body.hearingLoss +=
                    UnityEngine.Random.Range(27f, 36.6f);
                PlayerCamera.main.body.talker.Talk(
                    Locale.GetCharacter("loud"), null, false, false);
                PlayerCamera.main.shaker.Shake(param.range * 20f);
            }

            Collider2D[] array = Physics2D.OverlapCircleAll(
                param.position, param.range);
            List<Limb> list = new List<Limb>();

            foreach (Collider2D col in array)
            {
                if (col.TryGetComponent(out BuildingEntity be))
                {
                    be.health -= param.structuralDamage *
                        UnityEngine.Random.Range(0f, 2f);
                    if (be.TryGetComponent(out Rigidbody2D rb))
                        rb.velocity = (be.transform.position -
                            (Vector3)param.position).normalized * param.velocity;
                }

                if (col.TryGetComponent(out Item item))
                {
                    item.SetCondition(item.condition - param.structuralDamage *
                        0.005f * UnityEngine.Random.Range(0f, 1.3f));
                    if (item.TryGetComponent(out Rigidbody2D rb))
                        rb.velocity = (item.transform.position -
                            (Vector3)param.position).normalized * param.velocity;
                }

                if (col.TryGetComponent(out Limb limb))
                    list.Add(limb);

                if (col.TryGetComponent(out Body body))
                    list.AddRange(body.limbs);
            }

            WorldGeneration.world.GenerateBlockCircle(
                param.position, (int)param.range, 0, 1f, 0.85f, true, false, true);

            foreach (Limb limb2 in list)
            {
                if (!Physics2D.Linecast(param.position, limb2.transform.position,
                    LayerMask.GetMask("Ground")))
                {
                    float armor = limb2.GetArmorReduction();

                    if (UnityEngine.Random.Range(0f, 1f) < param.skinDamageChance)
                        limb2.skinHealth -=
                            param.skinDamage.RandomFromRange() / armor;

                    limb2.muscleHealth -=
                        param.muscleDamage.RandomFromRange() / armor;
                    limb2.body.shock = 100f;
                    limb2.body.lastTimeStepVelocity =
                        (limb2.body.transform.position -
                            (Vector3)param.position).normalized * param.velocity;
                    limb2.body.Ragdoll();

                    if (!limb2.hasShrapnel)
                        limb2.shrapnel =
                            UnityEngine.Random.value < param.shrapnelChance ? 5 : 0;

                    limb2.DamageWearables(param.shrapnelChance);

                    if (limb2.isVital && UnityEngine.Random.value < 0.5f)
                        limb2.body.internalBleeding +=
                            param.muscleDamage.RandomFromRange() * 0.4f / armor;

                    if (UnityEngine.Random.Range(0f, 1f) < param.bleedChance)
                        limb2.bleedAmount +=
                            param.bleedAmount.RandomFromRange() / armor;

                    if (UnityEngine.Random.Range(0f, 1f) <
                        param.boneBreakChance / armor)
                        limb2.BreakBone();

                    if (UnityEngine.Random.Range(0f, 1f) <
                        param.dislocationChance / armor)
                        limb2.Dislocate();

                    if (limb2.isHead)
                    {
                        limb2.body.consciousness = 0f;

                        if (UnityEngine.Random.Range(0f, 1f) < 0.7f / armor)
                            limb2.body.brainHealth -=
                                param.muscleDamage.RandomFromRange() / armor * 0.5f;

                        if (UnityEngine.Random.Range(0f, 1f) <
                            param.disfigureChance / armor)
                            limb2.body.Disfigure();

                        if (UnityEngine.Random.Range(0f, 1f) <
                            param.disfigureChance / armor)
                            limb2.body.RemoveEye();
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 2: GameObject.Destroy backup
    //
    //  Catches explosions when:
    //    • CreateExplosion is JIT-inlined (Layer 1 doesn't fire)
    //    • Explosion source destroys itself (mine, gravbag, dynamite)
    //
    //  Detection via GetExplosionInfo:
    //    • MineScript component + exploded flag
    //    • TurretScript component + health ≤ 0
    //    • CustomItemBehaviour + item.id check (dynamite, grenade, gravbag)
    //    • BuildingEntity + name check (explosive barrels)
    //
    //  Calls BOTH PreExplosion and PostExplosion.
    //  PostExplosion uses preScan=true because blocks aren't destroyed yet.
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(UnityEngine.Object), "Destroy",
      new[] { typeof(UnityEngine.Object), typeof(float) })]
    public static class GameObjectDestroyPatch
    {
        private static readonly HashSet<int> _processedObjects = new HashSet<int>();

        [HarmonyPrefix]
        public static void Prefix(UnityEngine.Object obj, float t)
        {
            try
            {
                if (obj == null) return;
                if (!(obj is GameObject go)) return;

                int id = go.GetInstanceID();
                if (_processedObjects.Contains(id)) return;

                ExplosionInfo info = GetExplosionInfo(go);
                if (info == null) return;

                if (ExplosionTracker.WasHandled(info.Position))
                    return;

                _processedObjects.Add(id);
                if (_processedObjects.Count > 200)
                    _processedObjects.Clear();

                ExplosionTracker.Track(info.Position);

                // MULTIPLAYER CLIENT CHECK:
                // On MP clients, the MP mod's ForceCreateExplosionEffect already
                // triggers our CreateExplosionPatch.Prefix, which calls Pre+Post.
                // DestroyBackup would cause a SECOND Pre+Post = double effects.
                // Skip DestroyBackup entirely on MP clients.
                if (MultiplayerHelper.IsClient)
                {
                    if (ShrapnelConfig.DebugLogging.Value)
                        Plugin.Log.LogInfo(
                            $"[DestroyBackup] Skipped {info.Type} on MP client" +
                            $" (handled by ForceCreateExplosionEffect)");
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[DestroyBackup] {info.Type} at {info.Position:F1} — spawning");

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                var param = new ExplosionParams
                {
                    position = info.Position,
                    range = info.Range,
                    structuralDamage = info.Damage,
                    velocity = info.Velocity,
                    sound = "explosion",
                    shrapnelChance = info.ShrapnelChance,
                    disfigureChance = info.DisfigureChance
                };

                ShrapnelSpawnLogic.PreExplosion(param);
                ShrapnelSpawnLogic.PostExplosion(param, preScan: true);

                Plugin.Log.LogInfo($"[DestroyBackup] ✓ {info.Type} complete");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[DestroyBackup] {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Inspects a GameObject being destroyed to determine if it's an explosive.
        ///
        /// Detection order:
        ///   1. MineScript — check 'exploded' field or health
        ///   2. TurretScript — check BuildingEntity.health ≤ 0
        ///   3. CustomItemBehaviour — check Item.id for specific explosive types:
        ///      • "gravbag" — battery overload pop (disfigureChance=0.15)
        ///      • "dynamite"/"tnt"/"explosive" — full bomb
        ///      • "grenade" — smaller bomb
        ///   4. BuildingEntity — name check for explosive barrels
        ///
        /// Returns null if not an explosive, preventing false positives.
        /// </summary>
        private static ExplosionInfo GetExplosionInfo(GameObject go)
        {
            // ── Mine ──
            var mine = go.GetComponent<MineScript>();
            if (mine != null)
            {
                try
                {
                    var explodedField = AccessTools.Field(typeof(MineScript), "exploded");
                    bool exploded = (bool)explodedField.GetValue(mine);
                    if (!exploded && (mine.build == null || mine.build.health >= 0.5f))
                        return null;
                }
                catch { }

                return new ExplosionInfo
                {
                    Type = "Mine",
                    Position = go.transform.position + Vector3.up,
                    Range = 12f,
                    Damage = 500f,
                    Velocity = 60f,
                    ShrapnelChance = 0.4f,
                    DisfigureChance = 0.34f // Default
                };
            }

            // ── Turret ──
            var turret = go.GetComponent<TurretScript>();
            if (turret != null)
            {
                var be = go.GetComponent<BuildingEntity>();
                if (be != null && be.health <= 0f)
                {
                    return new ExplosionInfo
                    {
                        Type = "Turret",
                        Position = go.transform.position,
                        Range = 9f,
                        Damage = 200f,
                        Velocity = 15f,
                        ShrapnelChance = 0.4f,
                        DisfigureChance = 0.34f
                    };
                }
                return null;
            }

            // ── CustomItemBehaviour (dynamite, grenades, GRAVBAG) ──
            var customItem = go.GetComponent<CustomItemBehaviour>();
            if (customItem != null)
            {
                // Check Item.id for precise identification
                Item item = go.GetComponent<Item>();
                string itemId = item != null ? item.id : "";
                string goName = go.name.ToLower();

                // GRAVBAG: battery overload pop
                // Vanilla creates ExplosionParams with only position + disfigureChance=0.15
                // All other fields use class defaults (range=12, damage=500, vel=60)
                // We must pass disfigureChance=0.15 so ClassifyExplosion detects Gravbag profile
                if (itemId == "gravbag")
                {
                    // Only trigger if condition depleted and has battery (matches vanilla logic)
                    if (item != null && item.condition <= 0.005f && item.battery != null && item.battery.hasBattery)
                    {
                        return new ExplosionInfo
                        {
                            Type = "Gravbag",
                            Position = go.transform.position,
                            Range = 12f,        // ExplosionParams default
                            Damage = 500f,       // ExplosionParams default
                            Velocity = 60f,      // ExplosionParams default
                            ShrapnelChance = 0.4f,  // ExplosionParams default
                            DisfigureChance = 0.15f  // ← CRITICAL: gravbag marker
                        };
                    }
                    return null;
                }

                // Dynamite / TNT
                if (itemId == "dynamite" ||
                    goName.Contains("dynamite") || goName.Contains("tnt") ||
                    goName.Contains("explosive"))
                {
                    return new ExplosionInfo
                    {
                        Type = "Dynamite",
                        Position = go.transform.position,
                        Range = 18f,
                        Damage = 2000f,
                        Velocity = 80f,
                        ShrapnelChance = 0.4f,
                        DisfigureChance = 0.34f
                    };
                }

                // Grenades
                if (goName.Contains("grenade"))
                {
                    return new ExplosionInfo
                    {
                        Type = "Grenade",
                        Position = go.transform.position,
                        Range = 10f,
                        Damage = 800f,
                        Velocity = 50f,
                        ShrapnelChance = 0.4f,
                        DisfigureChance = 0.34f
                    };
                }
            }

            // ── Explosive barrels ──
            var buildEnt = go.GetComponent<BuildingEntity>();
            if (buildEnt != null)
            {
                string beName = go.name.ToLower();
                if (beName.Contains("barrel") &&
                    (beName.Contains("explo") || beName.Contains("fuel")))
                {
                    return new ExplosionInfo
                    {
                        Type = "ExplosiveBarrel",
                        Position = go.transform.position,
                        Range = 15f,
                        Damage = 1500f,
                        Velocity = 70f,
                        ShrapnelChance = 0.4f,
                        DisfigureChance = 0.34f
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Explosion info extracted from a dying GameObject.
        /// DisfigureChance is critical for gravbag identification.
        /// </summary>
        private sealed class ExplosionInfo
        {
            public string Type;
            public Vector2 Position;
            public float Range;
            public float Damage;
            public float Velocity;
            public float ShrapnelChance;
            public float DisfigureChance;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 3: TurretScript.Update — turret death detection
    //
    //  Monitors turret health every frame. When health drops to 0,
    //  spawns shrapnel immediately.
    //  Calls both PreExplosion and PostExplosion (preScan=true).
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(TurretScript), "Update")]
    public static class TurretUpdatePatch
    {
        private static readonly HashSet<int> _explodedTurrets = new HashSet<int>();

        [HarmonyPostfix]
        public static void Postfix(TurretScript __instance)
        {
            try
            {
                var buildField = AccessTools.Field(typeof(TurretScript), "build");
                if (buildField == null) return;

                var build = (BuildingEntity)buildField.GetValue(__instance);
                if (build == null || build.health > 0f) return;

                int id = __instance.GetInstanceID();
                if (_explodedTurrets.Contains(id)) return;
                _explodedTurrets.Add(id);

                if (_explodedTurrets.Count > 100)
                    _explodedTurrets.Clear();

                Vector2 position = __instance.transform.position;
                if (ExplosionTracker.WasHandled(position)) return;
                ExplosionTracker.Track(position);

                if (!Plugin.VisualsWarmed)
                    Plugin.WarmVisuals();

                Plugin.Log.LogInfo("[TurretUpdate] Turret exploded — spawning shrapnel + debris");

                var param = new ExplosionParams
                {
                    position = position,
                    range = 9f,
                    structuralDamage = 200f,
                    velocity = 15f,
                    sound = "explosion",
                    shrapnelChance = 0.4f
                };

                ShrapnelSpawnLogic.PreExplosion(param);
                ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[TurretUpdate] {e.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LAYER 4: TurretScript.Shoot — bullet impact shrapnel
    // ═══════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
    public static class TurretShootPatch
    {
        [HarmonyPostfix]
        public static void Postfix(FireInfo info)
        {
            try
            {
                BulletShrapnelLogic.TrySpawnFromBullet(info);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[TurretShoot] {e.Message}");
            }
        }
    }
}