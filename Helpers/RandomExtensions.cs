using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Zero-allocation extension methods for System.Random.
    /// Provides Unity-style random utilities (Range, InsideUnitCircle, etc.)
    /// with deterministic seeding for multiplayer compatibility.
    ///
    /// WHY: UnityEngine.Random is non-deterministic (uses global state).
    /// System.Random with explicit seeding ensures reproducible behavior
    /// across clients in multiplayer scenarios.
    /// </summary>
    public static class RandomExtensions
    {
        /// <summary>
        /// Returns random int in range [minInclusive, maxExclusive).
        /// Equivalent to System.Random.Next(min, max).
        /// </summary>
        public static int Range(this System.Random rng, int minInclusive, int maxExclusive)
        {
            return rng.Next(minInclusive, maxExclusive);
        }

        /// <summary>
        /// Returns random float in range [minInclusive, maxInclusive).
        /// PERF: Uses NextDouble() * range + min pattern (zero-alloc).
        /// </summary>
        public static float Range(this System.Random rng, float minInclusive, float maxInclusive)
        {
            return (float)(rng.NextDouble() * (maxInclusive - minInclusive) + minInclusive);
        }

        /// <summary>
        /// Returns random float in [0, 1).
        /// Equivalent to UnityEngine.Random.value.
        /// </summary>
        public static float NextFloat(this System.Random rng)
        {
            return (float)rng.NextDouble();
        }

        /// <summary>
        /// Returns random angle in radians [0, 2π).
        /// </summary>
        public static float NextAngle(this System.Random rng)
        {
            return (float)(rng.NextDouble() * Mathf.PI * 2.0);
        }

        /// <summary>
        /// Returns random normalized Vector2 (point on unit circle).
        /// PERF: Uses angle-based generation, not rejection sampling.
        /// Guaranteed single-iteration (no while loop).
        /// </summary>
        public static Vector2 OnUnitCircle(this System.Random rng)
        {
            float angle = rng.NextAngle();
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        /// <summary>
        /// Returns random Vector2 inside unit circle (radius ≤ 1).
        /// PERF: Uses sqrt + angle method instead of rejection sampling.
        /// Unity's Random.insideUnitCircle uses rejection which can take
        /// multiple iterations. This is deterministic single-pass.
        /// </summary>
        public static Vector2 InsideUnitCircle(this System.Random rng)
        {
            float angle = rng.NextAngle();
            float radius = Mathf.Sqrt((float)rng.NextDouble()); // sqrt for uniform distribution
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }

        /// <summary>
        /// Returns random angle (radians) within a specific angular sector.
        /// Divides full circle into 'totalSectors' slices and returns
        /// random angle within the specified 'sectorIndex'.
        ///
        /// WHY: Used for stratified shrapnel distribution — ensures even
        /// coverage around explosion epicenter without clustering.
        /// </summary>
        /// <param name="sectorIndex">Sector index (0 to totalSectors-1).</param>
        /// <param name="totalSectors">Number of sectors to divide circle into.</param>
        /// <returns>Random angle in radians within the sector.</returns>
        public static float AngleInSector(this System.Random rng, int sectorIndex, int totalSectors)
        {
            float sectorSize = Mathf.PI * 2f / totalSectors;
            float sectorStart = sectorIndex * sectorSize;
            return sectorStart + (float)rng.NextDouble() * sectorSize;
        }
    }
}