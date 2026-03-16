using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Расширения для детерминированного генератора случайных чисел.
    /// 
    /// Необходим для полной синхронизации осколков в мультиплеере:
    /// все клиенты используют одинаковый seed → одинаковые результаты.
    /// 
    /// UnityEngine.Random НЕ детерминированный между клиентами!
    /// </summary>
    public static class RandomExtensions
    {
        /// <summary>Случайное float в диапазоне [0, 1).</summary>
        public static float NextFloat(this System.Random rng) => (float)rng.NextDouble();

        /// <summary>Случайное float в диапазоне [min, max).</summary>
        public static float Range(this System.Random rng, float min, float max)
            => min + (max - min) * rng.NextFloat();

        /// <summary>Случайное int в диапазоне [min, max).</summary>
        public static int Range(this System.Random rng, int min, int max)
            => rng.Next(min, max);

        /// <summary>
        /// Случайная точка внутри единичного круга.
        /// Использует равномерное распределение (Sqrt для радиуса).
        /// </summary>
        public static Vector2 InsideUnitCircle(this System.Random rng)
        {
            float angle = rng.NextFloat() * Mathf.PI * 2f;
            float radius = Mathf.Sqrt(rng.NextFloat());
            return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }

        /// <summary>
        /// Случайное направление (точка на окружности).
        /// Нормализованный вектор.
        /// </summary>
        public static Vector2 InsideUnitCircleNormalized(this System.Random rng)
        {
            float angle = rng.NextFloat() * Mathf.PI * 2f;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
    }
}