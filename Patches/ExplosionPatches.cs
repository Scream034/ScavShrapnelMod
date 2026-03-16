using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ScavShrapnelMod.Logic;

namespace ScavShrapnelMod.Patches
{
    //  ПАТЧИ ВЗРЫВОВ — чистый Harmony, никакой бизнес-логики

    /// <summary>
    /// Intercepts ALL calls to WorldGeneration.CreateExplosion.
    /// Spawns shrapnel BEFORE original explosion (Prefix).
    ///
    /// WHY: Using Prefix ensures shrapnel spawn uses the block layout
    /// BEFORE the explosion destroys blocks. This prevents shrapnel
    /// from spawning inside newly-created holes.
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion))]
    public static class CreateExplosionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ExplosionParams param)
        {
            // WHY: CustomCreateExplosion already called TrySpawnFromExplosion.
            // The throttle in TryRegisterSpawn will block the duplicate call.
            // But we still call it here for explosions NOT routed through Custom
            // (e.g. other mods or vanilla code calling CreateExplosion directly).
            ShrapnelSpawnLogic.TrySpawnFromExplosion(param);
            return true;
        }
    }

    /// <summary>
    /// Transpiler-утилита: заменяет вызовы WorldGeneration.CreateExplosion
    /// на ShrapnelSpawnLogic.CustomCreateExplosion в методах,
    /// которые вызывают CreateExplosion напрямую (минуя наш Prefix).
    /// </summary>
    public static class ExplosionCallReplacer
    {
        private static readonly MethodInfo OriginalMethod =
            AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion));

        private static readonly MethodInfo CustomMethod =
            AccessTools.Method(typeof(ShrapnelSpawnLogic), nameof(ShrapnelSpawnLogic.CustomCreateExplosion));

        public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Call &&
                    inst.operand is MethodInfo method &&
                    method == OriginalMethod)
                {
                    inst.operand = CustomMethod;
                }
                yield return inst;
            }
        }
    }

    /// <summary>Перехват MineScript.OnDestroy — мина взрывается при уничтожении.</summary>
    [HarmonyPatch(typeof(MineScript), "OnDestroy")]
    public static class MineOnDestroyPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> i)
            => ExplosionCallReplacer.Replace(i);
    }

    /// <summary>Перехват MineScript.Update — мина взрывается по триггеру.</summary>
    [HarmonyPatch(typeof(MineScript), "Update")]
    public static class MineUpdatePatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> i)
            => ExplosionCallReplacer.Replace(i);
    }

    //  ПАТЧ ПУЛЬ — осколки при попадании в металлический блок

    /// <summary>
    /// Осколки от пуль.
    /// Перехватывает TurretScript.Shoot (общий метод стрельбы для всего оружия).
    /// После выстрела проверяет: если пуля попала в металлический блок,
    /// спавнит 1-3 мелких осколка в сторону от точки попадания.
    /// </summary>
    [HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
    public static class BulletImpactPatch
    {
        /// <summary>
        /// Postfix: после каждого выстрела проверяем попадание в металл.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(FireInfo info)
        {
            BulletShrapnelLogic.TrySpawnFromBullet(info);
        }
    }
}