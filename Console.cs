using System;
using System.Globalization;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Helpers;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Net;
using ScavShrapnelMod.Patches;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Console commands for mod testing and in-game logging facade.
    ///
    /// Command list:
    ///   shrapnel_explode  — spawn explosion with shrapnel
    ///   shrapnel_debris   — spawn debris fragments
    ///   shrapnel_clear    — destroy all active shrapnel
    ///   shrapnel_status   — brief mod status with pool counts + network
    ///   shrapnel_net      — detailed network sync diagnostics
    ///   shrapnel_testmat  — material/shader corruption check
    ///   shrapnel_nettest  — simulate full server→client explosion pipeline with verbose logging
    /// </summary>
    public static class Console
    {
        public static void Log(string msg)
        {
            Debug.Log($"[{Plugin.Name}] {msg}");
        }

        public static void Error(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";
            Plugin.Log?.LogError(formatted);
            Debug.LogError(formatted);
        }

        private static bool ArgsContain(string[] args, string value)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool ArgsContainAny(string[] args, params string[] values)
        {
            for (int i = 1; i < args.Length; i++)
                for (int j = 0; j < values.Length; j++)
                    if (string.Equals(args[i], values[j], StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        private static int FindInt(string[] args, int defaultValue)
        {
            for (int i = 1; i < args.Length; i++)
                if (int.TryParse(args[i], out int val))
                    return val;
            return defaultValue;
        }

        private static float FindFloat(string[] args, float defaultValue)
        {
            bool foundInt = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (float.TryParse(args[i], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float val))
                {
                    if (int.TryParse(args[i], out _))
                    {
                        if (foundInt) return val;
                        foundInt = true;
                        continue;
                    }
                    return val;
                }
            }
            return defaultValue;
        }

        public static void Register()
        {
            // ─── COMMAND: shrapnel_explode ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Explosion. Args: mine|dynamite|turret|gravbag, player|cursor, -e (effects only), -net (post-explode diagnostics)",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    bool effectsOnly = ArgsContainAny(args, "-e", "effects", "effectsonly");
                    bool netVerbose = ArgsContainAny(args, "-net", "net");

                    Vector2 pos;
                    if (ArgsContain(args, "player"))
                        pos = PlayerCamera.main.body.transform.position;
                    else
                        pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    string explosionType = "mine";
                    if (ArgsContainAny(args, "dynamite", "tnt")) explosionType = "dynamite";
                    else if (ArgsContain(args, "turret")) explosionType = "turret";
                    else if (ArgsContainAny(args, "gravbag", "grav")) explosionType = "gravbag";

                    ExplosionParams param = new ExplosionParams { position = pos };

                    switch (explosionType)
                    {
                        case "dynamite":
                            param.range = 18f;
                            param.structuralDamage = 2000f;
                            break;
                        case "turret":
                            param.range = 9f;
                            param.velocity = 15f;
                            break;
                        case "gravbag":
                            param.disfigureChance = 0.15f;
                            break;
                    }

                    // WHY: On client in MP, host-triggered explosions arrive via Krokosha's
                    // CreateExplosion replication. Client console commands in MP are local-only
                    // effects (no physics shrapnel). On host, full pipeline runs normally.
                    bool isClient = MultiplayerHelper.IsNetworkRunning && !MultiplayerHelper.IsServer;

                    if (isClient)
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
                        Log($"{explosionType.ToUpper()} CLIENT-EFFECTS at {pos:F1}" +
                            " (client has no physics authority)");
                        return;
                    }

                    if (effectsOnly)
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
                        Log($"{explosionType.ToUpper()} EFFECTS at {pos:F1}");
                    }
                    else
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        WorldGeneration.CreateExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: false);
                        Log($"{explosionType.ToUpper()} FULL at {pos:F1}");
                    }

                    if (netVerbose)
                    {
                        string diag = ShrapnelNetSync.GetDiagnostics();
                        Log($"POST-EXPLODE NET:\n{diag}");
                    }
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string>
                        { "mine", "dynamite", "turret", "gravbag", "grav",
                          "-e", "effects", "player", "-net", "net" } }
                },
                new (string, string)[]
                {
                    ("string args", "Any order: mine|dynamite|turret|gravbag, player, -e, -net")
                }
            ));

            // ─── COMMAND: shrapnel_testmat ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_testmat",
                "Test material state and detect shader corruption",
                (args) =>
                {
                    Log("=== MATERIAL STATE ===");

                    var unlit = ShrapnelVisuals.UnlitMaterial;
                    var lit = ShrapnelVisuals.LitMaterial;
                    var trail = ShrapnelVisuals.TrailMaterial;

                    Log(unlit != null
                        ? $"Unlit: shader={unlit.shader?.name ?? "NULL"} renderQueue={unlit.renderQueue}"
                        : "Unlit: NULL!");

                    Log(lit != null
                        ? $"Lit: shader={lit.shader?.name ?? "NULL"} renderQueue={lit.renderQueue}"
                        : "Lit: NULL!");

                    Log(trail != null
                        ? $"Trail: shader={trail.shader?.name ?? "NULL"}"
                        : "Trail: NULL!");

                    var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                    int corruptCount = 0;
                    foreach (var sr in renderers)
                        if (sr.sharedMaterial != null && sr.sharedMaterial.shader == null)
                            corruptCount++;
                    Log($"Corrupt SpriteRenderers: {corruptCount}/{renderers.Length}");
                },
                null,
                new (string, string)[] { }
            ));

            // ─── COMMAND: shrapnel_clear ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_clear",
                "Destroys all active shrapnel and pool particles",
                (args) =>
                {
                    string stats = DebrisTracker.GetStats();
                    DebrisTracker.Clear();
                    Log($"Cleared: {stats}");
                },
                null,
                new (string, string)[] { }
            ));

            // ─── COMMAND: shrapnel_debris ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Debris at cursor. Args: [count] [force] [metal|stone|heavy|wood|electronic|all] [-v verbose] [-net post-spawn diagnostics]",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    bool allTypes = ArgsContain(args, "all");
                    bool verbose = ArgsContainAny(args, "-v", "verbose");
                    bool netDiag = ArgsContainAny(args, "-net", "net");

                    ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;
                    if (!allTypes)
                    {
                        if (ArgsContain(args, "stone")) type = ShrapnelProjectile.ShrapnelType.Stone;
                        else if (ArgsContainAny(args, "heavy", "heavymetal"))
                            type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                        else if (ArgsContain(args, "wood")) type = ShrapnelProjectile.ShrapnelType.Wood;
                        else if (ArgsContainAny(args, "electronic", "elec"))
                            type = ShrapnelProjectile.ShrapnelType.Electronic;
                    }

                    int count = Mathf.Clamp(FindInt(args, 5), 1, 100);
                    float force = FindFloat(args, 0f);
                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    // WHY: Fixed seed when verbose — reproducible results for debugging
                    System.Random rng = verbose
                        ? new System.Random(42)
                        : new System.Random();

                    ShrapnelFactory.EnsureWoundSprites();

                    // WHY: Cycle all weight classes when "all" to verify every trail/visual combo
                    ShrapnelWeight[] weightCycle = {
                        ShrapnelWeight.Micro, ShrapnelWeight.Hot,
                        ShrapnelWeight.Medium, ShrapnelWeight.Heavy,
                        ShrapnelWeight.Massive };

                    if (verbose)
                    {
                        string preDiag = ShrapnelNetSync.GetBriefStatus();
                        Log($"PRE  → {preDiag}");
                    }

                    int spawned = 0;
                    for (int i = 0; i < count; i++)
                    {
                        ShrapnelWeight weight;
                        ShrapnelProjectile.ShrapnelType spawnType;

                        if (allTypes)
                        {
                            weight = weightCycle[i % weightCycle.Length];
                            spawnType = (ShrapnelProjectile.ShrapnelType)(i % 5);
                        }
                        else
                        {
                            float roll = (float)rng.NextDouble();
                            if (roll < 0.08f) weight = ShrapnelWeight.Micro;
                            else if (roll < 0.23f) weight = ShrapnelWeight.Hot;
                            else if (roll < 0.53f) weight = ShrapnelWeight.Medium;
                            else if (roll < 0.88f) weight = ShrapnelWeight.Heavy;
                            else weight = ShrapnelWeight.Massive;
                            spawnType = type;
                        }

                        ShrapnelProjectile proj = ShrapnelFactory.Spawn(
                            pos, force, spawnType, weight, i, rng);

                        if (verbose)
                        {
                            if (proj != null)
                            {
                                spawned++;
                                Log($"  #{spawned} netId={proj.NetSyncId}" +
                                    $" type={spawnType} weight={weight}" +
                                    $" trail={proj.HasTrail}" +
                                    $" heat={proj.Heat:F2}" +
                                    $" scale={proj.transform.localScale.x:F3}");
                            }
                            else
                            {
                                // WHY: Micro weight returns null (visual sparks only, no physics GO)
                                Log($"  #{i} type={spawnType} weight={weight} (visual-only, no physics)");
                            }
                        }
                        else
                        {
                            if (proj != null) spawned++;
                        }
                    }

                    Log($"Spawned {spawned}/{count}× {(allTypes ? "ALL" : type.ToString())}" +
                        $" (force={force:F0}) at {pos:F1}");

                    if (verbose || netDiag)
                    {
                        string postDiag = ShrapnelNetSync.GetBriefStatus();
                        Log($"POST → {postDiag}");
                    }

                    if (netDiag && MultiplayerHelper.IsNetworkRunning)
                    {
                        Log("Client should run 'shrapnel_net' to verify mirrors.");
                    }
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string>
                        { "metal", "stone", "heavy", "wood", "electronic",
                          "all", "-v", "verbose", "-net", "net" } }
                },
                new (string, string)[]
                {
                    ("string args", "Any order: [count] [force] metal|stone|heavy|wood|electronic|all -v -net")
                }
            ));

            // ─── COMMAND: shrapnel_status (enhanced with network) ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status: pool counts, physics objects, network sync",
                (args) =>
                {
                    string poolStats = DebrisTracker.GetStats();
                    string netStatus = ShrapnelNetSync.GetBriefStatus();
                    string mpInfo = MultiplayerHelper.IsNetworkRunning
                        ? (MultiplayerHelper.IsServer ? "MP:HOST" : "MP:CLIENT")
                        : "MP:off";

                    Log($"v{Plugin.Version} | {poolStats}" +
                        $" | Total: {DebrisTracker.TotalAliveParticles}" +
                        $" | {mpInfo} | {netStatus}");
                },
                null,
                new (string, string)[] { }
            ));

            // ─── COMMAND: shrapnel_net (detailed network diagnostics) ───

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_net",
                "Detailed network sync diagnostics",
                (args) =>
                {
                    Log($"MP mod present: {MultiplayerHelper.IsMultiplayerModPresent}");
                    Log($"Network running: {MultiplayerHelper.IsNetworkRunning}");

                    if (MultiplayerHelper.IsNetworkRunning)
                    {
                        Log($"Role: {(MultiplayerHelper.IsServer ? "SERVER (host)" : "CLIENT")}");
                        Log($"Should spawn physics: {MultiplayerHelper.ShouldSpawnPhysicsShrapnel}");
                    }

                    string diag = ShrapnelNetSync.GetDiagnostics();
                    string[] lines = diag.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length > 0)
                            Log(line);
                    }
                },
                null,
                new (string, string)[] { }
            ));
        }

        /// <summary>
        /// Runs server-side net test: spawns controlled fragments at cursor,
        /// logs each spawn with netId, type, weight, trail flag.
        /// Clients should see mirrors appear with matching properties.
        /// </summary>
        private static void RunServerNetTest(string[] args)
        {
            Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            int count = Mathf.Clamp(FindInt(args, 10), 1, 50);
            bool allTypes = ArgsContain(args, "all");

            ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;
            if (!allTypes)
            {
                if (ArgsContain(args, "stone")) type = ShrapnelProjectile.ShrapnelType.Stone;
                else if (ArgsContainAny(args, "heavy", "heavymetal"))
                    type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                else if (ArgsContain(args, "wood")) type = ShrapnelProjectile.ShrapnelType.Wood;
                else if (ArgsContainAny(args, "electronic", "elec"))
                    type = ShrapnelProjectile.ShrapnelType.Electronic;
            }

            // Pre-test snapshot
            string preDiag = ShrapnelNetSync.GetBriefStatus();
            Log($"NET TEST: PRE  → {preDiag}");
            Log($"NET TEST: Spawning {count} fragments at {pos:F1}" +
                $" type={(allTypes ? "ALL" : type.ToString())}");

            ShrapnelFactory.EnsureWoundSprites();
            System.Random rng = new System.Random(42); // WHY: Fixed seed for reproducibility

            // WHY: Spawn one of each weight class to verify all trail/visual combos
            ShrapnelWeight[] weights = allTypes
                ? new[] {
                    ShrapnelWeight.Micro, ShrapnelWeight.Hot,
                    ShrapnelWeight.Medium, ShrapnelWeight.Heavy,
                    ShrapnelWeight.Massive }
                : null;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                ShrapnelWeight weight;
                ShrapnelProjectile.ShrapnelType spawnType;

                if (allTypes)
                {
                    weight = weights[i % weights.Length];
                    spawnType = (ShrapnelProjectile.ShrapnelType)(i % 5);
                }
                else
                {
                    float roll = (float)rng.NextDouble();
                    if (roll < 0.08f) weight = ShrapnelWeight.Micro;
                    else if (roll < 0.23f) weight = ShrapnelWeight.Hot;
                    else if (roll < 0.53f) weight = ShrapnelWeight.Medium;
                    else if (roll < 0.88f) weight = ShrapnelWeight.Heavy;
                    else weight = ShrapnelWeight.Massive;
                    spawnType = type;
                }

                // WHY: Offset each fragment slightly so they don't stack
                Vector2 spawnPos = pos + new Vector2(
                    (float)rng.NextDouble() * 2f - 1f,
                    (float)rng.NextDouble() * 2f - 1f) * 0.5f;

                float force = 20f + (float)rng.NextDouble() * 40f;

                ShrapnelProjectile proj = ShrapnelFactory.Spawn(
                    spawnPos, force, spawnType, weight, i, rng);

                if (proj != null)
                {
                    spawned++;
                    Log($"  #{spawned} netId={proj.NetSyncId}" +
                        $" type={spawnType} weight={weight}" +
                        $" trail={proj.HasTrail}" +
                        $" heat={proj.Heat:F2}" +
                        $" scale={proj.transform.localScale.x:F3}");
                }
                else
                {
                    Log($"  #{i} FAILED to spawn (null returned)");
                }
            }

            // Post-test snapshot
            string postDiag = ShrapnelNetSync.GetBriefStatus();
            Log($"NET TEST: POST → {postDiag}");
            Log($"NET TEST: Done. {spawned}/{count} spawned." +
                " Client should run 'shrapnel_net' to verify mirrors.");
        }

        /// <summary>
        /// Runs local (singleplayer) test: spawns fragments with verbose logging.
        /// Useful for testing visuals without network.
        /// </summary>
        private static void RunLocalNetTest(string[] args)
        {
            Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            int count = Mathf.Clamp(FindInt(args, 5), 1, 50);

            ShrapnelFactory.EnsureWoundSprites();
            System.Random rng = new System.Random(42);

            ShrapnelWeight[] weights = {
                ShrapnelWeight.Micro, ShrapnelWeight.Hot,
                ShrapnelWeight.Medium, ShrapnelWeight.Heavy,
                ShrapnelWeight.Massive };

            Log($"LOCAL TEST: Spawning {count} fragments at {pos:F1}");

            for (int i = 0; i < count; i++)
            {
                var weight = weights[i % weights.Length];
                var spawnType = (ShrapnelProjectile.ShrapnelType)(i % 5);

                Vector2 spawnPos = pos + new Vector2(
                    (float)rng.NextDouble() * 2f - 1f,
                    (float)rng.NextDouble() * 2f - 1f) * 0.5f;

                float force = 20f + (float)rng.NextDouble() * 40f;

                ShrapnelProjectile proj = ShrapnelFactory.Spawn(
                    spawnPos, force, spawnType, weight, i, rng);

                if (proj != null)
                {
                    Log($"  #{i} type={spawnType} weight={weight}" +
                        $" trail={proj.HasTrail}" +
                        $" heat={proj.Heat:F2}" +
                        $" scale={proj.transform.localScale.x:F3}");
                }
            }

            Log($"LOCAL TEST: Done.");
        }
    }
}