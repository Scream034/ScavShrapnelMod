using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Math utilities for shrapnel calculations.
    /// Zero-alloc static methods for hot paths.
    /// </summary>
    public static class MathHelper
    {
        /// <summary>
        /// Converts angle in radians to normalized direction vector.
        /// </summary>
        /// <param name="radians">Angle in radians. 0 = right, π/2 = up.</param>
        /// <returns>Normalized direction vector.</returns>
        public static Vector2 AngleToDirection(float radians)
        {
            return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        }

        /// <summary>
        /// Converts angle in degrees to normalized direction vector.
        /// </summary>
        public static Vector2 AngleToDirectionDeg(float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }

        /// <summary>
        /// Rotates a direction vector by the given angle in radians.
        /// </summary>
        public static Vector2 RotateDirection(Vector2 dir, float radians)
        {
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(
                dir.x * cos - dir.y * sin,
                dir.x * sin + dir.y * cos);
        }

        /// <summary>
        /// Clamps speed to maximum, returns clamped value.
        /// </summary>
        public static float ClampSpeed(float speed, float max)
        {
            return speed > max ? max : speed;
        }
    }
}