using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Patches;

namespace ScavShrapnelMod
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ScavShrapnelMod";
        public const string Name = "ScavShrapnelMod";
        public const string Version = "0.8.0";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static bool CommandsRegistered;
        internal static bool VisualsWarmed;
        private Harmony _harmony;

        private static bool _resetNotificationShown;

        private void Awake()
        {
            Log = Logger;

            Logger.LogInfo("╔═══════════════════════════════════════╗");
            Logger.LogInfo("║     Shrapnel Overhaul Mod v" + Version + "      ║");
            Logger.LogInfo("║     Realistic Explosion Fragments     ║");
            Logger.LogInfo("╚═══════════════════════════════════════╝");

            ShrapnelConfig.Bind(Config);
            Helpers.CustomResourceManager.Logger = Logger;

            _harmony = new Harmony(Guid);
            ApplyPatches();
            CreateCommandRegistrar();
        }

        private void ApplyPatches()
        {
            // LAYER 1: Manual patch on CreateExplosion (may not fire if inlined)
            try
            {
                var targetMethod = AccessTools.Method(
                    typeof(WorldGeneration),
                    nameof(WorldGeneration.CreateExplosion),
                    new[] { typeof(ExplosionParams) });

                if (targetMethod != null)
                {
                    var prefixMethod = AccessTools.Method(
                        typeof(CreateExplosionPatch),
                        nameof(CreateExplosionPatch.Prefix));
                    
                    var postfixMethod = AccessTools.Method(
                        typeof(CreateExplosionPatch),
                        nameof(CreateExplosionPatch.Postfix));

                    _harmony.Patch(
                        targetMethod,
                        prefix: new HarmonyMethod(prefixMethod)
                        {
                            priority = Priority.Low
                        },
                        postfix: new HarmonyMethod(postfixMethod)
                        {
                            priority = Priority.Last
                        });

                    Logger.LogInfo("[Patch] ✓ Layer 1: WorldGeneration.CreateExplosion");
                }
                else
                {
                    Logger.LogError("[Patch] ✗ Layer 1: CreateExplosion not found!");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[Patch] ✗ Layer 1: {e.Message}");
            }

            // LAYERS 2-4: Via PatchAll (Destroy hook, TurretUpdate, TurretShoot)
            try
            {
                _harmony.PatchAll();
                Logger.LogInfo("[Patch] ✓ Layers 2-4: PatchAll complete");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"[Patch] Layers 2-4 warning: {e.Message}");
            }
        }

        private void CreateCommandRegistrar()
        {
            try
            {
                GameObject registrar = new GameObject("ShrapnelMod_Registrar")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(registrar);
                registrar.AddComponent<ConsoleCommandRegistrar>();
            }
            catch (Exception e)
            {
                Logger.LogError($"[Init] Failed to create registrar: {e}");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static void RegisterCommands()
        {
            if (CommandsRegistered) return;

            try
            {
                Console.Register();
                CommandsRegistered = true;
                Log.LogInfo("[Console] Commands registered successfully");
                Console.Log($"v{Version} loaded! Type 'help' to see shrapnel_ commands.");
            }
            catch (Exception e)
            {
                Log.LogError($"[Console] Registration failed: {e}");
            }
        }

        internal static void WarmVisuals()
        {
            if (VisualsWarmed) return;
            try
            {
                ShrapnelVisuals.PreWarm();
                AshParticlePoolManager.Initialize();
                ParticlePoolManager.Initialize();
                ShrapnelSpawnLogic.ResetThrottle();
                VisualsWarmed = true;

                Log.LogInfo($"[Init] Warmed." +
                    $" AshPools={AshParticlePoolManager.Initialized}" +
                    $" SparkPool={ParticlePoolManager.Initialized}");
            }
            catch (Exception e)
            {
                Log.LogError($"[Init] PreWarm FAILED: {e.Message}\n{e.StackTrace}");
            }
        }

        internal static void OnWorldLoad()
        {
            VisualsWarmed = false;
            ShrapnelVisuals.ResetMaterials();
            AshParticlePoolManager.Shutdown();
            ParticlePoolManager.Shutdown();
            ShrapnelSpawnLogic.ResetThrottle();
            WarmVisuals();
            ShowConfigResetNotification();
        }

        private static void ShowConfigResetNotification()
        {
            if (_resetNotificationShown) return;
            _resetNotificationShown = true;

            string notification = ShrapnelConfig.GetResetNotification();
            if (notification == null) return;

            Log.LogWarning(notification);
            Console.Log(notification);
        }
    }

    internal sealed class ConsoleCommandRegistrar : MonoBehaviour
    {
        private void Update()
        {
            if (Plugin.CommandsRegistered)
            {
                Destroy(gameObject);
                return;
            }

            if (ConsoleScript.instance != null &&
                ConsoleScript.Commands != null &&
                ConsoleScript.Commands.Count > 0)
            {
                Plugin.RegisterCommands();
                Destroy(gameObject);
            }
        }
    }

    [HarmonyPatch(typeof(ConsoleScript), "Start")]
    internal static class ConsoleStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (ConsoleScript.Commands?.Count > 0 && !Plugin.CommandsRegistered)
            {
                Plugin.RegisterCommands();
            }
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), "UpdateChunkVisibility")]
    internal static class WorldLoadPatch
    {
        private static bool _warmedThisSession = false;

        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (WorldGeneration.world != null &&
                    !WorldGeneration.world.generatingWorld &&
                    !_warmedThisSession)
                {
                    _warmedThisSession = true;
                    Plugin.OnWorldLoad();
                    Plugin.Log.LogInfo("[WorldLoad] Visuals pre-warmed on world load");
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), "Start")]
    internal static class PlayerCameraStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (!Plugin.VisualsWarmed)
            {
                Plugin.WarmVisuals();
                Plugin.Log.LogInfo("[PlayerCamera] Visuals pre-warmed on camera start");
            }
        }
    }
}