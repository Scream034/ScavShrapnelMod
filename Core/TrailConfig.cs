using UnityEngine;

namespace ScavShrapnelMod.Core
{
    /// <summary>
    /// Trail configuration per weight class. Single source of truth.
    /// Eliminates 3× duplicated trail setup in ShrapnelFactory,
    /// ShrapnelNetSync.CreateClientShard, and ShrapnelProjectile.
    /// </summary>
    public static class TrailConfig
    {
        /// <summary>
        /// Returns true if this weight class should receive a trail.
        /// </summary>
        public static bool ShouldHaveTrail(ShrapnelWeight weight, System.Random rng)
        {
            ref readonly var d = ref ShrapnelWeightData.Get(weight);
            // PERF: trailChance is 0f or 1f for most weights — branch predicted
            return d.TrailChance >= 1f || (d.TrailChance > 0f && rng.NextDouble() < d.TrailChance);
        }

        /// <summary>
        /// Configures a TrailRenderer with weight-appropriate visual settings.
        /// Requires TrailMaterial to be non-null (caller checks).
        /// </summary>
        /// <param name="tr">TrailRenderer to configure.</param>
        /// <param name="weight">Shrapnel weight class.</param>
        /// <param name="scale">Local scale of the shrapnel GameObject.</param>
        public static void Apply(TrailRenderer tr, ShrapnelWeight weight, float scale)
        {
            tr.sortingOrder = 9;
            tr.numCapVertices = 1;
            tr.autodestruct = false;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows = false;
            tr.endWidth = 0f;

            switch (weight)
            {
                case ShrapnelWeight.Massive:
                    tr.time = 0.4f;
                    tr.startWidth = 0.12f * scale * 5f;
                    tr.startColor = new Color(0.3f, 0.25f, 0.2f, 0.8f);
                    tr.endColor = new Color(0.2f, 0.2f, 0.2f, 0f);
                    break;

                case ShrapnelWeight.Hot:
                    tr.time = 0.25f;
                    tr.startWidth = 0.06f * scale * 10f;
                    tr.startColor = new Color(1f, 0.5f, 0.1f, 0.9f);
                    tr.endColor = new Color(1f, 0.2f, 0f, 0f);
                    break;

                default:
                    tr.time = 0.15f;
                    tr.startWidth = 0.04f * scale * 10f;
                    tr.startColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                    tr.endColor = new Color(0.4f, 0.4f, 0.4f, 0f);
                    break;
            }
        }

        /// <summary>
        /// Adds and configures a TrailRenderer if appropriate for this weight.
        /// Returns true if trail was added.
        /// </summary>
        public static bool TryAdd(GameObject obj, ShrapnelWeight weight,
            float scale, System.Random rng)
        {
            if (!ShouldHaveTrail(weight, rng)) return false;

            Material mat = ShrapnelVisuals.TrailMaterial;
            if (mat == null) return false;

            TrailRenderer tr = obj.AddComponent<TrailRenderer>();
            tr.sharedMaterial = mat;
            Apply(tr, weight, scale);
            return true;
        }
    }
}