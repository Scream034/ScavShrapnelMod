using System;
using System.Globalization;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Projectiles;
using ScavShrapnelMod.Patches;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Console commands for mod testing and in-game logging facade.
    ///
    /// Logging strategy:
    ///   Console.Log()   → In-game console via Debug.Log (user-facing)
    ///   Console.Error() → Both console + BepInEx LogError
    ///
    /// Command syntax — order-independent, short arguments:
    ///   shrapnel_explode [-e] [mine|dynamite|turret|gravbag] [player|cursor]
    ///   shrapnel_debris [count] [force] [metal|stone|heavy|wood|electronic]
    ///   shrapnel_clear
    ///   shrapnel_status
    ///   shrapnel_testmat
    ///
    /// Examples:
    ///   shrapnel_explode -e              = mine effects-only at cursor
    ///   shrapnel_explode dynamite player  = full dynamite at player
    ///   shrapnel_explode gravbag          = gravbag battery pop at cursor
    ///   shrapnel_explode turret -e        = turret effects-only at cursor
    ///   shrapnel_debris 10 wood           = 10 wood fragments at cursor
    ///   shrapnel_debris 5 30 heavy        = 5 heavy fragments at force 30
    /// </summary>
    public static class Console
    {
        /// <summary>
        /// Logs a message to in-game console (user-facing).
        /// Routed via Debug.Log which BepInEx picks up for Player.log.
        /// </summary>
        public static void Log(string msg)
        {
            Debug.Log($"[{Plugin.Name}] {msg}");
        }

        /// <summary>
        /// Logs an error to both Unity log and BepInEx log.
        /// </summary>
        public static void Error(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";
            Plugin.Log?.LogError(formatted);
            Debug.LogError(formatted);
        }

        /// <summary>Case-insensitive argument search, skips args[0].</summary>
        private static bool ArgsContain(string[] args, string value)
        {
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Case-insensitive multi-value argument search.</summary>
        private static bool ArgsContainAny(string[] args, params string[] values)
        {
            for (int i = 1; i < args.Length; i++)
                for (int j = 0; j < values.Length; j++)
                    if (string.Equals(args[i], values[j], StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        /// <summary>Finds first parseable int in args, skips args[0].</summary>
        private static int FindInt(string[] args, int defaultValue)
        {
            for (int i = 1; i < args.Length; i++)
                if (int.TryParse(args[i], out int val))
                    return val;
            return defaultValue;
        }

        /// <summary>
        /// Finds first float that isn't also a valid int, or second numeric.
        /// Used to distinguish count (int) from force (float) in mixed args.
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

        /// <summary>Registers all shrapnel console commands.</summary>
        public static void Register()
        {
            //  COMMAND: shrapnel_explode

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Explosion. Args: mine|dynamite|turret|gravbag, player|cursor, -e (effects only)",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    bool effectsOnly = ArgsContainAny(args, "-e", "effects", "effectsonly");

                    // Position: player or cursor (default)
                    Vector2 pos;
                    if (ArgsContain(args, "player"))
                        pos = PlayerCamera.main.body.transform.position;
                    else
                        pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    // Explosion type detection — check ALL types
                    string explosionType = "mine"; // default
                    if (ArgsContainAny(args, "dynamite", "tnt")) explosionType = "dynamite";
                    else if (ArgsContain(args, "turret")) explosionType = "turret";
                    else if (ArgsContainAny(args, "gravbag", "grav")) explosionType = "gravbag";

                    // Build ExplosionParams matching vanilla values for each type.
                    // CRITICAL: These must match what ClassifyExplosion expects,
                    // otherwise the wrong profile is selected.
                    ExplosionParams param = new ExplosionParams { position = pos };

                    switch (explosionType)
                    {
                        case "mine":
                            // Mine uses all ExplosionParams defaults:
                            // range=12, damage=500, velocity=60, disfigureChance=0.34
                            break;

                        case "dynamite":
                            // Matches CustomItemBehaviour.DynamiteExplode()
                            param.range = 18f;
                            param.structuralDamage = 2000f;
                            // velocity stays default 60
                            break;

                        case "turret":
                            // Matches ShrapnelConfig turret detection values
                            param.range = 9f;
                            param.velocity = 15f;
                            break;

                        case "gravbag":
                            // Matches CustomItemBehaviour.Update() gravbag section:
                            //   new ExplosionParams { position=..., disfigureChance=0.15f }
                            // All other fields stay at class defaults.
                            param.disfigureChance = 0.15f;
                            break;
                    }

                    if (effectsOnly)
                    {
                        // Effects-only: no terrain damage
                        // Call Pre + Post directly, skip CreateExplosion
                        ExplosionTracker.Track(pos);
                        ShrapnelSpawnLogic.PreExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: true);

                        Log($"{explosionType.ToUpper()} EFFECTS at {pos:F1}");
                    }
                    else
                    {
                        // Full explosion: Pre + CreateExplosion + Post
                        //
                        // DOUBLE-SPAWN PREVENTION:
                        //   1. Track(pos) prevents DestroyBackup from also firing
                        //   2. TryRegisterSpawn inside PreExplosion prevents
                        //      double-Pre if Prefix also fires

                        ExplosionTracker.Track(pos);

                        ShrapnelSpawnLogic.PreExplosion(param);
                        WorldGeneration.CreateExplosion(param);
                        ShrapnelSpawnLogic.PostExplosion(param, preScan: false);

                        Log($"{explosionType.ToUpper()} FULL at {pos:F1}");
                    }
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string>
                        { "mine", "dynamite", "turret", "gravbag", "grav",
                          "-e", "effects", "player" } }
                },
                new (string, string)[]
                {
                    ("string args", "Any order: mine|dynamite|turret|gravbag, player, -e")
                }
            ));

            //  COMMAND: shrapnel_testmat

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

            //  COMMAND: shrapnel_clear

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

            //  COMMAND: shrapnel_debris

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Debris at cursor. Args: [count] [force] [metal|stone|heavy|wood|electronic]",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;
                    if (ArgsContain(args, "stone")) type = ShrapnelProjectile.ShrapnelType.Stone;
                    else if (ArgsContainAny(args, "heavy", "heavymetal"))
                        type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                    else if (ArgsContain(args, "wood")) type = ShrapnelProjectile.ShrapnelType.Wood;
                    else if (ArgsContainAny(args, "electronic", "elec"))
                        type = ShrapnelProjectile.ShrapnelType.Electronic;

                    int count = Mathf.Clamp(FindInt(args, 5), 1, 100);
                    float force = FindFloat(args, 0f);
                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

                    ShrapnelFactory.EnsureWoundSprites();
                    System.Random rng = new System.Random();

                    for (int i = 0; i < count; i++)
                    {
                        float roll = (float)rng.NextDouble();
                        ShrapnelWeight weight;
                        if (roll < 0.08f) weight = ShrapnelWeight.Micro;
                        else if (roll < 0.23f) weight = ShrapnelWeight.Hot;
                        else if (roll < 0.53f) weight = ShrapnelWeight.Medium;
                        else if (roll < 0.88f) weight = ShrapnelWeight.Heavy;
                        else weight = ShrapnelWeight.Massive;

                        ShrapnelFactory.Spawn(pos, force, type, weight, i, rng);
                    }

                    Log($"Spawned {count}× {type} (force={force:F0})");
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string>
                        { "metal", "stone", "heavy", "wood", "electronic" } }
                },
                new (string, string)[]
                {
                    ("string args", "Any order: [count] [force] metal|stone|heavy|wood|electronic")
                }
            ));

            //  COMMAND: shrapnel_status

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status with pool particle counts",
                (args) =>
                {
                    Log($"v{Plugin.Version} | {DebrisTracker.GetStats()}" +
                        $" | Total: {DebrisTracker.TotalAliveParticles}");
                },
                null,
                new (string, string)[] { }
            ));
        }
    }
}