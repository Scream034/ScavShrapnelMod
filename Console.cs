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
    ///   Console.Log()   → In-game console (user-facing messages)
    ///   Console.Debug() → BepInEx log only (background diagnostics)
    ///   Console.Error() → Both console + BepInEx LogError
    ///
    /// Order-independent, short arguments.
    ///   shrapnel_explode [-e] [mine|dynamite|turret] [player|cursor]
    ///   shrapnel_debris [count] [force] [metal|stone|heavy|wood|electronic]
    ///   shrapnel_clear
    ///   shrapnel_status
    ///
    /// Examples:
    ///   shrapnel_explode -e              = mine effects-only at cursor
    ///   shrapnel_explode dynamite player  = full dynamite at player
    ///   shrapnel_explode turret -e        = turret effects-only at cursor
    ///   shrapnel_debris 10 wood           = 10 wood fragments at cursor
    ///   shrapnel_debris 5 30 heavy        = 5 heavy fragments at force 30
    /// </summary>
    public static class Console
    {
        /// <summary>
        /// Logs a message to the in-game console (user-facing).
        /// Falls back to Unity log if console unavailable.
        /// </summary>
        /// <param name="msg">Message to display</param>
        public static void Log(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";

            // WHY: Route to in-game console if available, else Unity log
            try
            {
                if (ConsoleScript.instance != null)
                {
                    // WHY: ConsoleScript uses static SendCommand or instance method
                    // to display text. Debug.Log is picked up by BepInEx LogListener
                    // and also appears in Player.log — sufficient for in-game visibility.
                    Debug.Log(formatted);
                }
                else
                {
                    Debug.Log(formatted);
                }
            }
            catch
            {
                Debug.Log(formatted);
            }
        }

        /// <summary>
        /// Logs debug info to BepInEx log only (background, not shown in-game).
        /// Use for diagnostics, performance info, patch status.
        /// </summary>
        /// <param name="msg">Debug message</param>
        public static void LogDebug(string msg)
        {
            Plugin.Log?.LogDebug($"[{Plugin.Name}] {msg}");
        }

        /// <summary>
        /// Logs an error to both Unity log and BepInEx log.
        /// </summary>
        /// <param name="msg">Error message</param>
        public static void Error(string msg)
        {
            string formatted = $"[{Plugin.Name}] {msg}";
            Plugin.Log?.LogError(formatted);
            Debug.LogError(formatted);
        }

        /// <summary>
        /// Checks if any argument in args[] matches the given value (case-insensitive).
        /// Skips args[0] (the command name itself).
        /// </summary>
        /// <param name="args">Command arguments array</param>
        /// <param name="value">Value to search for</param>
        /// <returns>True if found</returns>
        private static bool ArgsContain(string[] args, string value)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if any argument in args[] matches any of the given values.
        /// </summary>
        /// <param name="args">Command arguments array</param>
        /// <param name="values">Values to search for</param>
        /// <returns>True if any match found</returns>
        private static bool ArgsContainAny(string[] args, params string[] values)
        {
            for (int i = 1; i < args.Length; i++)
            {
                for (int j = 0; j < values.Length; j++)
                {
                    if (string.Equals(args[i], values[j], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Tries to find and parse the first numeric argument as int.
        /// Skips args[0]. Returns defaultValue if none found.
        /// </summary>
        /// <param name="args">Command arguments array</param>
        /// <param name="defaultValue">Default value if not found</param>
        /// <returns>Parsed integer or default</returns>
        private static int FindInt(string[] args, int defaultValue)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (int.TryParse(args[i], out int val))
                    return val;
            }
            return defaultValue;
        }

        /// <summary>
        /// Tries to find and parse a float argument.
        /// Returns the FIRST float that isn't also a valid int,
        /// or the second numeric value if the first was int.
        /// Skips args[0]. Returns defaultValue if none found.
        /// </summary>
        /// <param name="args">Command arguments array</param>
        /// <param name="defaultValue">Default value if not found</param>
        /// <returns>Parsed float or default</returns>
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
        /// Registers all shrapnel console commands.
        /// </summary>
        public static void Register()
        {
            //  COMMAND 1: shrapnel_explode

            ConsoleScript.Commands.Add(new Command(
       "shrapnel_explode",
       "Explosion (visual effects without terrain damage if -e used). Args: mine|dynamite|turret, player|cursor, -e",
       (args) =>
       {
           if (!PlayerCamera.main)
               throw new Exception("No world loaded!");

           bool effectsOnly = ArgsContainAny(args, "-e", "effects", "effectsonly");

           Vector2 pos;
           if (ArgsContain(args, "player"))
               pos = PlayerCamera.main.body.transform.position;
           else
               pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

           string explosionType = "mine";
           if (ArgsContain(args, "dynamite")) explosionType = "dynamite";
           else if (ArgsContain(args, "turret")) explosionType = "turret";

           ExplosionParams param = new ExplosionParams
           {
               position = pos,
               sound = "explosion",
               shrapnelChance = 0.4f
           };

           switch (explosionType)
           {
               case "mine":
                   param.range = 12f;
                   param.structuralDamage = 500f;
                   break;
               case "dynamite":
                   param.range = 18f;
                   param.structuralDamage = 2000f;
                   param.velocity = 80f;
                   break;
               case "turret":
                   param.range = 9f;
                   param.structuralDamage = 500f;
                   param.velocity = 15f;
                   param.disfigureChance = 0.2f;
                   break;
           }

           if (effectsOnly)
           {
               ShrapnelSpawnLogic.PreExplosion(param);
               ShrapnelSpawnLogic.PostExplosion(param, preScan: true);
               Log($"{explosionType.ToUpper()} EFFECTS (no terrain damage) at {pos:F1}");
           }
           else
           {
               // WHY: Call PreExplosion manually because CreateExplosion Prefix
               // may not fire due to JIT inlining.
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
            { "mine", "dynamite", "turret", "-e", "effects", "player" } }
       },
       new (string, string)[]
       {
        ("string args", "Any order: mine|dynamite|turret, player, -e (effects without damage)")
       }
   ));

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_testmat",
                "Test if materials are corrupted",
                (args) =>
                {
                    Log("=== MATERIAL STATE ===");

                    var unlit = ShrapnelVisuals.UnlitMaterial;
                    var lit = ShrapnelVisuals.LitMaterial;
                    var trail = ShrapnelVisuals.TrailMaterial;

                    if (unlit != null)
                    {
                        Log($"Unlit: shader={unlit.shader?.name} color={unlit.color} " +
                            $"renderQueue={unlit.renderQueue}");
                    }
                    else Log("Unlit: NULL!");

                    if (lit != null)
                    {
                        Log($"Lit: shader={lit.shader?.name} color={lit.color} " +
                            $"renderQueue={lit.renderQueue}");
                    }
                    else Log("Lit: NULL!");

                    if (trail != null)
                    {
                        Log($"Trail: shader={trail.shader?.name} color={trail.color}");
                    }
                    else Log("Trail: NULL!");

                    // Check if any SpriteRenderer in scene has corrupted material
                    var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                    int corruptCount = 0;
                    foreach (var sr in renderers)
                    {
                        if (sr.sharedMaterial != null && sr.sharedMaterial.shader == null)
                        {
                            corruptCount++;
                        }
                    }
                    Log($"Corrupt SpriteRenderers: {corruptCount}/{renderers.Length}");
                },
                null,
                new (string, string)[] { }
            ));

            //  COMMAND 2: shrapnel_clear

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_clear",
                "Destroys all active shrapnel and pool particles.",
                (args) =>
                {
                    string stats = DebrisTracker.GetStats();
                    DebrisTracker.Clear();
                    Log($"Cleared: {stats}");
                },
                null,
                new (string, string)[] { }
            ));

            //  COMMAND 3: shrapnel_debris

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Debris at cursor. Args (any order): [count] [force] [metal|stone|heavy|wood|electronic]",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;
                    if (ArgsContain(args, "stone")) type = ShrapnelProjectile.ShrapnelType.Stone;
                    else if (ArgsContainAny(args, "heavy", "heavymetal")) type = ShrapnelProjectile.ShrapnelType.HeavyMetal;
                    else if (ArgsContain(args, "wood")) type = ShrapnelProjectile.ShrapnelType.Wood;
                    else if (ArgsContainAny(args, "electronic", "elec")) type = ShrapnelProjectile.ShrapnelType.Electronic;

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

            //  COMMAND 4: shrapnel_status

            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status with pool particle counts.",
                (args) =>
                {
                    Log($"v{Plugin.Version} | {DebrisTracker.GetStats()}" +
                        $" | Total alive: {DebrisTracker.TotalAliveParticles}");
                },
                null,
                new (string, string)[] { }
            ));
        }
    }
}