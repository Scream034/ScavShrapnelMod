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
    /// In-game console commands and logging facade.
    ///
    /// Commands:
    ///   shrapnel_explode  — spawn explosion (mine|dynamite|turret|gravbag, player|cursor, -e, -net)
    ///   shrapnel_debris   — spawn debris fragments ([count] [force] type, -v, -net, all)
    ///   shrapnel_clear    — destroy all active shrapnel and pool particles
    ///   shrapnel_status   — brief mod status with pool counts and network info
    ///   shrapnel_net      — detailed network sync diagnostics
    ///   shrapnel_testmat  — material/shader corruption check
    /// </summary>
    public static class Console
    {
        /// <summary>Logs a message to Unity console with mod prefix.</summary>
        public static void Log(string msg)
        {
            Debug.Log($"[{Plugin.Name}] {msg}");
        }

        /// <summary>Logs an error to both BepInEx logger and Unity console.</summary>
        public static void Error(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";
            Plugin.Log?.LogError(formatted);
            Debug.LogError(formatted);
        }

        /// <summary>Checks if args (starting at index 1) contain a specific value (case-insensitive).</summary>
        private static bool ArgsContain(string[] args, string value)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Checks if args contain any of the specified values (case-insensitive).</summary>
        private static bool ArgsContainAny(string[] args, params string[] values)
        {
            for (int i = 1; i < args.Length; i++)
                for (int j = 0; j < values.Length; j++)
                    if (string.Equals(args[i], values[j], StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        /// <summary>Finds the first integer argument (starting at index 1), or returns default.</summary>
        private static int FindInt(string[] args, int defaultValue)
        {
            for (int i = 1; i < args.Length; i++)
                if (int.TryParse(args[i], out int val))
                    return val;
            return defaultValue;
        }

        /// <summary>
        /// Finds the first float argument that isn't also a pure integer.
        /// Skips the first int-parseable token (treated as count) so that
        /// "shrapnel_debris 10 25.5" correctly returns 25.5 for force.
        /// </summary>
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

        /// <summary>
        /// Registers all shrapnel_ console commands with the game's ConsoleScript.
        /// Called once from <see cref="Plugin.RegisterCommands"/>.
        /// </summary>
        public static void Register()
        {
            // shrapnel_explode

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Explosion at cursor. Args: mine|dynamite|turret|gravbag, player, -e (effects only), -net (diagnostics)",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    bool effectsOnly = ArgsContainAny(args, "-e", "effects", "effectsonly");
                    bool netVerbose = ArgsContainAny(args, "-net", "net");

                    Vector2 pos = ArgsContain(args, "player")
                        ? (Vector2)PlayerCamera.main.body.transform.position
                        : (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    string explosionType = "mine";
                    if (ArgsContainAny(args, "dynamite", "tnt")) explosionType = "dynamite";
                    else if (ArgsContain(args, "turret")) explosionType = "turret";
                    else if (ArgsContainAny(args, "gravbag", "grav")) explosionType = "gravbag";

                    ExplosionParams param = new() { position = pos };
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

                    // WHY: MP client has no physics authority — force effects-only.
                    // Host-triggered explosions reach clients via Krokosha's replication.
                    bool isClient = MultiplayerHelper.IsNetworkRunning
                        && !MultiplayerHelper.IsServer;

                    if (isClient)
                    {
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
                        Log($"{explosionType.ToUpper()} CLIENT-EFFECTS at {pos:F1}" +
                            " (no physics authority)");
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
                        Log($"POST-EXPLODE NET:\n{ShrapnelNetSync.GetDiagnostics()}");
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

            // shrapnel_testmat

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

            // shrapnel_clear

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

            // shrapnel_debris

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Debris at cursor. [count] [force] [metal|stone|heavy|wood|electronic|all] [-v] [-net]",
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

                    // WHY: Fixed seed in verbose mode for reproducible debug runs
                    System.Random rng = verbose ? new System.Random(42) : new System.Random();

                    ShrapnelFactory.EnsureWoundSprites();

                    ShrapnelWeight[] weightCycle = {
                        ShrapnelWeight.Micro, ShrapnelWeight.Hot,
                        ShrapnelWeight.Medium, ShrapnelWeight.Heavy,
                        ShrapnelWeight.Massive };

                    if (verbose)
                        Log($"PRE  → {ShrapnelNetSync.GetBriefStatus()}");

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
                                    $" trail={proj.HasTrail} heat={proj.Heat:F2}" +
                                    $" scale={proj.transform.localScale.x:F3}");
                            }
                            else
                            {
                                Log($"  #{i} type={spawnType} weight={weight}" +
                                    " (visual-only, no physics)");
                            }
                        }
                        else if (proj != null)
                        {
                            spawned++;
                        }
                    }

                    Log($"Spawned {spawned}/{count}× " +
                        $"{(allTypes ? "ALL" : type.ToString())}" +
                        $" (force={force:F0}) at {pos:F1}");

                    if (verbose || netDiag)
                        Log($"POST → {ShrapnelNetSync.GetBriefStatus()}");

                    if (netDiag && MultiplayerHelper.IsNetworkRunning)
                        Log("Client should run 'shrapnel_net' to verify mirrors.");
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string>
                        { "metal", "stone", "heavy", "wood", "electronic",
                          "all", "-v", "verbose", "-net", "net" } }
                },
                new (string, string)[]
                {
                    ("string args", "[count] [force] metal|stone|heavy|wood|electronic|all -v -net")
                }
            ));

            // shrapnel_status

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

            // shrapnel_net

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

                    string[] lines = ShrapnelNetSync.GetDiagnostics().Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length > 0) Log(line);
                    }
                },
                null,
                new (string, string)[] { }
            ));
        }
    }
}