using System;
using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Консольные команды для тестирования мода.
    /// 
    /// Команды:
    /// - shrapnel_explode [type] [position] [effectsonly] — создать взрыв
    /// - shrapnel_clear — удалить все осколки
    /// - shrapnel_debris [count] [force] [type] — спавнить debris
    /// - shrapnel_status — статус мода
    /// </summary>
    public static class ConsoleCommands
    {
        private static void LogToConsole(string message)
        {
            Debug.Log("[ShrapnelMod] " + message);
        }

        public static void Register()
        {
            //  КОМАНДА 1: Универсальный взрыв ──
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_explode",
                "Creates explosion. Args: type (mine/dynamite/turret), position (cursor/player), mode (full/effectsonly)",
                (args) =>
                {
                    if (!PlayerCamera.main)
                        throw new Exception("No world loaded!");

                    //  Позиция 
                    Vector2 pos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                    if (args.Length > 2 && args[2].ToLower() == "player")
                    {
                        pos = PlayerCamera.main.body.transform.position;
                    }

                    //  Тип 
                    string explosionType = "mine";
                    if (args.Length > 1)
                    {
                        explosionType = args[1].ToLower();
                    }

                    //  Режим: full (взрыв + эффекты) или effectsonly (только эффекты) 
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
                        // Только визуальные эффекты: осколки, пепел, ground debris
                        // БЕЗ реального взрыва (без урона, без разрушения блоков)
                        ShrapnelSpawnLogic.TrySpawnFromExplosion(param);
                        LogToConsole($"{explosionType.ToUpper()} EFFECTS ONLY at {pos}");
                    }
                    else
                    {
                        // Полный взрыв: эффекты + реальный взрыв
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

            //  КОМАНДА 2: Очистка ──
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_clear",
                "Destroys all active shrapnel objects.",
                (args) =>
                {
                    ShrapnelProjectile[] all = UnityEngine.Object.FindObjectsOfType<ShrapnelProjectile>();
                    int count = all.Length;
                    for (int i = 0; i < all.Length; i++)
                        UnityEngine.Object.Destroy(all[i].gameObject);

                    // Также очищаем визуальные и пепел
                    VisualShrapnel[] visuals = UnityEngine.Object.FindObjectsOfType<VisualShrapnel>();
                    for (int i = 0; i < visuals.Length; i++)
                        UnityEngine.Object.Destroy(visuals[i].gameObject);

                    AshParticle[] ashes = UnityEngine.Object.FindObjectsOfType<AshParticle>();
                    for (int i = 0; i < ashes.Length; i++)
                        UnityEngine.Object.Destroy(ashes[i].gameObject);

                    LogToConsole($"Cleared {count} shrapnel + {visuals.Length} visual + {ashes.Length} particles.");
                },
                null,
                new (string, string)[] { }
            ));

            //  КОМАНДА 3: Спавн Debris 
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

            //  КОМАНДА 4: Статус ─
            ConsoleScript.Commands.Add(new Command(
                "shrapnel_status",
                "Mod status.",
                (args) =>
                {
                    int debrisCount = UnityEngine.Object.FindObjectsOfType<ShrapnelProjectile>().Length;
                    int visualCount = UnityEngine.Object.FindObjectsOfType<VisualShrapnel>().Length;
                    int ashCount = UnityEngine.Object.FindObjectsOfType<AshParticle>().Length;
                    LogToConsole($"v{Plugin.Version} | shrapnel:{debrisCount} visual:{visualCount} particles:{ashCount}");
                },
                null,
                new (string, string)[] { }
            ));
        }
    }
}