using System;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Projectiles;

namespace ScavShrapnelMod.Console
{
    /// <summary>
    /// Console commands for mod testing.
    /// 
    /// Commands:
    /// - shrapnel_explode [type] [position] [effectsonly] — create explosion
    /// - shrapnel_clear — destroy all shrapnel
    /// - shrapnel_debris [count] [force] [type] — spawn debris
    /// - shrapnel_status — mod status
    /// </summary>
    public static class ConsoleCommands
    {
        private static void LogToConsole(string message)
        {
            Debug.Log("[ShrapnelMod] " + message);
        }

        public static void Register()
        {
            //  COMMAND 1: Universal explosion 
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Creates explosion. Args: type (mine/dynamite/turret), position (cursor/player), mode (full/effectsonly)",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    //  Position 
                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                    if (args.Length > 2 && args[2].ToLower() == "player")
                    {
                        pos = PlayerCamera.main.body.transform.position;
                    }

                    //  Type 
                    string explosionType = "mine";
                    if (args.Length > 1)
                    {
                        explosionType = args[1].ToLower();
                    }

                    //  Mode: full (explosion + effects) or effectsonly (effects only) 
                    bool effectsOnly = false;
                    if (args.Length > 3 && args[3].ToLower() == "effectsonly")
                    {
                        effectsOnly = true;
                    }

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
                        default:
                            throw new Exception($"\"{explosionType}\" is not a valid explosion type!");
                    }

                    if (effectsOnly)
                    {
                        // Effects only: shrapnel, ash, ground debris
                        // WITHOUT real explosion (no damage, no block destruction)
                        ShrapnelSpawnLogic.TrySpawnFromExplosion(param);
                        LogToConsole($"{explosionType.ToUpper()} EFFECTS ONLY at {pos}");
                    }
                    else
                    {
                        // Full explosion: effects + real explosion
                        ShrapnelSpawnLogic.CustomCreateExplosion(param);
                        LogToConsole($"{explosionType.ToUpper()} explosion at {pos}");
                    }
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 1, new System.Collections.Generic.List<string> { "mine", "dynamite", "turret" } },
                    { 2, new System.Collections.Generic.List<string> { "cursor", "player" } },
                    { 3, new System.Collections.Generic.List<string> { "full", "effectsonly" } }
                },
                new (string, string)[]
                {
                    ("string type", "mine / dynamite / turret"),
                    ("string position", "cursor / player"),
                    ("string mode", "full / effectsonly")
                }
            ));

            //  COMMAND 2: Clear 
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_clear",
                "Destroys all active shrapnel objects.",
                (args) =>
                {
                    // WHY: Previous implementation used FindObjectsOfType (very expensive O(n) scene scan)
                    // three times. DebrisTracker.Clear() already tracks everything and is O(n) on tracked
                    // objects only, plus it properly clears the internal tracking lists.
                    int physCount = DebrisTracker.PhysicalCount;
                    int visCount = DebrisTracker.VisualCount;

                    DebrisTracker.Clear();

                    LogToConsole($"Cleared {physCount} physical + {visCount} visual objects via tracker.");
                },
                null,
                new (string, string)[] { }
            ));

            //  COMMAND 3: Spawn Debris 
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_debris",
                "Spawns debris at cursor. Args: count, force, type",
                (args) =>
                {
                    if (!PlayerCamera.main) throw new Exception("No world loaded!");

                    int count = 5;
                    float force = 0f;
                    ShrapnelProjectile.ShrapnelType type = ShrapnelProjectile.ShrapnelType.Metal;

                    if (args.Length > 1) count = Mathf.Clamp(int.Parse(args[1]), 1, 100);
                    if (args.Length > 2) force = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
                    if (args.Length > 3)
                    {
                        switch (args[3].ToLower())
                        {
                            case "metal": type = ShrapnelProjectile.ShrapnelType.Metal; break;
                            case "stone": type = ShrapnelProjectile.ShrapnelType.Stone; break;
                            case "heavy": type = ShrapnelProjectile.ShrapnelType.HeavyMetal; break;
                            case "wood": type = ShrapnelProjectile.ShrapnelType.Wood; break;
                            case "electronic": type = ShrapnelProjectile.ShrapnelType.Electronic; break;
                        }
                    }

                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                    ShrapnelFactory.EnsureWoundSprites();

                    System.Random rng = new System.Random();

                    for (int i = 0; i < count; i++)
                    {
                        float roll = (float)rng.NextDouble();
                        ShrapnelWeight weight;
                        if (roll < 0.15f) weight = ShrapnelWeight.Hot;
                        else if (roll < 0.45f) weight = ShrapnelWeight.Medium;
                        else if (roll < 0.85f) weight = ShrapnelWeight.Heavy;
                        else weight = ShrapnelWeight.Massive;

                        ShrapnelFactory.Spawn(pos, force, type, weight, i, rng);
                    }

                    LogToConsole($"Spawned {count}x {type} fragments.");
                },
                new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>
                {
                    { 3, new System.Collections.Generic.List<string> { "metal", "stone", "heavy", "wood", "electronic" } }
                },
                new (string, string)[]
                {
                    ("int count", "Count"),
                    ("float force", "Force"),
                    ("string type", "metal/stone/heavy/wood/electronic")
                }
            ));

            //  COMMAND 4: Status 
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status.",
                (args) =>
                {
                    // WHY: Use DebrisTracker counts instead of FindObjectsOfType.
                    // FindObjectsOfType scans entire scene — expensive for a debug command.
                    LogToConsole($"v{Plugin.Version}" +
                        $" | phys:{DebrisTracker.PhysicalCount} vis:{DebrisTracker.VisualCount}" +
                        $" | total:{DebrisTracker.Count}");
                },
                null,
                new (string, string)[] { }
            ));
        }
    }
}