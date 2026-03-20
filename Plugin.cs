using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ScavShrapnelMod.Core;
using ScavShrapnelMod.Logic;
using ScavShrapnelMod.Net;
using ScavShrapnelMod.Patches;
using ScavShrapnelMod.Helpers;

namespace ScavShrapnelMod
{
    /// <summary>
    /// BepInEx plugin entry point. Manages Harmony patches, visual warmup,
    /// console command registration, and world lifecycle.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ScavShrapnelMod";
        public const string Name = "ScavShrapnelMod";
        public const string Version = "0.8.3";

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
            GameVersionChecker.Check();

            _harmony = new Harmony(Guid);
            ApplyPatches();
            CreateCommandRegistrar();
        }

        private void ApplyPatches()
        {
            // LAYER 1: Manual Harmony patch on CreateExplosion
            try
            {
                var targetMethod = AccessTools.Method(
                    typeof(WorldGeneration),
                    nameof(WorldGeneration.CreateExplosion),
                    new[] { typeof(ExplosionParams) });

                if (targetMethod != null)
                {
                    _harmony.Patch(
                        targetMethod,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(CreateExplosionPatch),
                                nameof(CreateExplosionPatch.Prefix)))
                        { priority = Priority.Low },
                        postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CreateExplosionPatch),
                                nameof(CreateExplosionPatch.Postfix)))
                        { priority = Priority.Last });

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

            // LAYERS 2-4: Attribute-based (Destroy hook, TurretUpdate, TurretShoot)
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
                var registrar = new GameObject("ShrapnelMod_Registrar")
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

        /// <summary>
        /// Registers console commands. Called once when ConsoleScript is available.
        /// </summary>
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

        /// <summary>
        /// Initializes visual assets, particle pools, and network sync.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
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

                if (MultiplayerHelper.IsNetworkRunning)
                    ShrapnelNetSync.Initialize();

                Log.LogInfo($"[Init] Warmed." +
                    $" AshPools={AshParticlePoolManager.Initialized}" +
                    $" SparkPool={ParticlePoolManager.Initialized}");
            }
            catch (Exception e)
            {
                Log.LogError($"[Init] PreWarm FAILED: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Full cleanup and re-initialization for world transitions.
        /// Shuts down network sync, resets materials, restarts pools.
        /// </summary>
        internal static void OnWorldLoad()
        {
            ShrapnelNetSync.Shutdown();
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

    /// <summary>
    /// Polls for ConsoleScript availability during startup.
    /// Self-destructs after registering commands or when no longer needed.
    /// </summary>
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

    /// <summary>Fallback: registers commands when ConsoleScript.Start fires.</summary>
    [HarmonyPatch(typeof(ConsoleScript), "Start")]
    internal static class ConsoleStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (ConsoleScript.Commands?.Count > 0 && !Plugin.CommandsRegistered)
                Plugin.RegisterCommands();
        }
    }

    /// <summary>
    /// Detects world load completion via chunk visibility updates.
    /// Triggers <see cref="Plugin.OnWorldLoad"/> once per world.
    ///
    /// Tracks the current world instance to correctly detect world transitions
    /// (exit to menu → new world) within the same game session.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), "UpdateChunkVisibility")]
    internal static class WorldLoadPatch
    {
        /// <summary>
        /// Cached reference to the world instance we already warmed for.
        /// When WorldGeneration.world changes (new world), this mismatch
        /// triggers a fresh OnWorldLoad call.
        /// </summary>
        private static WorldGeneration _lastWarmedWorld;

        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                var world = WorldGeneration.world;
                if (world == null || world.generatingWorld)
                    return;

                // WHY: Comparing instance reference, not bool flag.
                // On second world load, world is a new instance → mismatch → re-warm.
                // Prevents the bug where _warmedThisSession stayed true forever.
                if (world == _lastWarmedWorld)
                    return;

                _lastWarmedWorld = world;
                Plugin.OnWorldLoad();
                Plugin.Log.LogInfo("[WorldLoad] Visuals pre-warmed on world load");
            }
            catch { /* guard — runs every frame in postfix */ }
        }
    }

    /// <summary>Ensures visuals are warmed when player camera initializes.</summary>
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