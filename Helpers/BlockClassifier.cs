using UnityEngine;

namespace ScavShrapnelMod.Helpers
{
    /// <summary>
    /// Material category for blocks.
    /// Classified from internal game data (hitsound, stepsound, metallic flag).
    /// Never from block name (localized, unreliable).
    /// </summary>
    public enum MaterialCategory
    {
        Unknown,
        Sand,       // hitsound=sand, stepsound=Sand
        Snow,       // hitsound=sand, stepsound=Snow
        Dirt,       // hitsound=dirt
        Rock,       // hitsound=rock
        Concrete,   // hitsound=concrete
        Metal,      // metallic=true OR hitsound=steel/scrapmetal
        Wood,       // hitsound=wood
        Glass,      // hitsound=glass, stepsound=Glass
        Ice,        // hitsound=glass, stepsound=Ice
        Rubber,     // hitsound=rubber, stepsound=Rubber
        Plastic,    // hitsound=rubber, stepsound=Plastic
        Organic,    // hitsound=rustle or gore2
        Trash,      // hitsound=trash
        Ore         // hitsound=crystal
    }

    /// <summary>
    /// Centralized block material classification system.
    /// Uses internal game data only — never localized block names.
    /// 
    /// Shared by:
    ///   - GroundDebrisLogic (ground surface particles)
    ///   - ShrapnelSpawnLogic (block debris from destroyed blocks)
    ///   - Any future systems needing material-aware logic
    /// 
    /// Based on GetBlockInfo() switch statement (35 block types).
    /// 100% coverage via hitsound/stepsound mapping.
    /// </summary>
    public static class BlockClassifier
    {
        /// <summary>
        /// Classifies a block into a material category.
        /// Priority: metallic flag → hitsound → stepsound refinement.
        /// </summary>
        /// <param name="info">BlockInfo from WorldGeneration.GetBlockInfo(). Null-safe.</param>
        /// <returns>MaterialCategory enum value.</returns>
        public static MaterialCategory Classify(BlockInfo info)
        {
            if (info == null) return MaterialCategory.Unknown;
            if (info.metallic) return MaterialCategory.Metal;

            string hit  = info.hitsound ?? string.Empty;
            string step = info.stepsound ?? string.Empty;

            // WHY: hitsound is the primary material indicator.
            // stepsound refines ambiguous cases (sand vs snow, glass vs ice).

            if (hit == "steel" || hit == "scrapmetal") return MaterialCategory.Metal;
            if (hit == "rock")      return MaterialCategory.Rock;
            if (hit == "sand")      return (step == "Snow") ? MaterialCategory.Snow : MaterialCategory.Sand;
            if (hit == "dirt")      return MaterialCategory.Dirt;
            if (hit == "wood")      return MaterialCategory.Wood;
            if (hit == "concrete")  return MaterialCategory.Concrete;
            if (hit == "glass")     return (step == "Ice") ? MaterialCategory.Ice : MaterialCategory.Glass;
            if (hit == "rubber")    return (step == "Plastic") ? MaterialCategory.Plastic : MaterialCategory.Rubber;
            if (hit == "rustle")    return MaterialCategory.Organic;
            if (hit == "gore2")     return MaterialCategory.Organic;
            if (hit == "trash")     return MaterialCategory.Trash;
            if (hit == "crystal")   return MaterialCategory.Ore;

            return MaterialCategory.Unknown;
        }

        /// <summary>
        /// Returns dust count multiplier for a material category.
        /// Soft/loose materials produce more fine dust.
        /// Hard/dense materials produce fewer, larger chunks.
        /// Range: 0.8 – 1.3 (max 1.6× difference).
        /// </summary>
        public static float GetDustMultiplier(MaterialCategory cat)
        {
            switch (cat)
            {
                case MaterialCategory.Sand:     return 1.3f;
                case MaterialCategory.Snow:     return 1.25f;
                case MaterialCategory.Dirt:     return 1.2f;
                case MaterialCategory.Organic:  return 1.15f;
                case MaterialCategory.Trash:    return 1.1f;
                case MaterialCategory.Glass:    return 1.1f;
                case MaterialCategory.Ice:      return 1.1f;
                case MaterialCategory.Wood:     return 1.05f;
                case MaterialCategory.Plastic:  return 1.0f;
                case MaterialCategory.Rubber:   return 0.95f;
                case MaterialCategory.Rock:     return 0.9f;
                case MaterialCategory.Concrete: return 0.9f;
                case MaterialCategory.Ore:      return 0.85f;
                case MaterialCategory.Metal:    return 0.8f;
                default:                        return 1.0f;
            }
        }

        /// <summary>
        /// Returns base color for a material category.
        /// Randomized within each category's palette.
        /// Used for ground debris and block destruction particles.
        /// </summary>
        public static Color GetColor(MaterialCategory cat, System.Random rng)
        {
            switch (cat)
            {
                case MaterialCategory.Sand:
                    // Warm yellow-tan
                    return new Color(
                        rng.Range(0.72f, 0.86f),
                        rng.Range(0.62f, 0.76f),
                        rng.Range(0.32f, 0.46f));

                case MaterialCategory.Snow:
                    // White with slight blue tint
                {
                    float w = rng.Range(0.85f, 0.97f);
                    return new Color(w * 0.98f, w, w * 1.02f);
                }

                case MaterialCategory.Dirt:
                    // Dark earthy brown
                    return new Color(
                        rng.Range(0.38f, 0.52f),
                        rng.Range(0.25f, 0.38f),
                        rng.Range(0.12f, 0.22f));

                case MaterialCategory.Rock:
                    // Neutral gray with slight warm tint
                {
                    float g = rng.Range(0.38f, 0.56f);
                    return new Color(g, g * 0.97f, g * 0.93f);
                }

                case MaterialCategory.Concrete:
                    // Light gray, slightly warmer than rock
                {
                    float g = rng.Range(0.45f, 0.6f);
                    return new Color(g * 1.02f, g, g * 0.96f);
                }

                case MaterialCategory.Metal:
                    // Cool blue-gray (steel/alloy tint)
                {
                    float g = rng.Range(0.28f, 0.44f);
                    return new Color(g * 0.92f, g * 0.96f, g * 1.1f);
                }

                case MaterialCategory.Wood:
                    // Warm medium brown
                    return new Color(
                        rng.Range(0.42f, 0.56f),
                        rng.Range(0.26f, 0.38f),
                        rng.Range(0.12f, 0.2f));

                case MaterialCategory.Glass:
                    // Light blue-white (transparent debris)
                {
                    float g = rng.Range(0.6f, 0.78f);
                    return new Color(g * 0.92f, g * 0.97f, g * 1.05f);
                }

                case MaterialCategory.Ice:
                    // Pale blue (colder than glass)
                {
                    float g = rng.Range(0.65f, 0.82f);
                    return new Color(g * 0.88f, g * 0.95f, g * 1.08f);
                }

                case MaterialCategory.Rubber:
                    // Very dark gray (tire-black)
                {
                    float g = rng.Range(0.12f, 0.22f);
                    return new Color(g, g, g);
                }

                case MaterialCategory.Plastic:
                    // Light off-white gray
                {
                    float g = rng.Range(0.55f, 0.72f);
                    return new Color(g * 1.01f, g, g * 0.97f);
                }

                case MaterialCategory.Organic:
                    // Green-brown (plant/fungal)
                    return new Color(
                        rng.Range(0.22f, 0.38f),
                        rng.Range(0.32f, 0.48f),
                        rng.Range(0.12f, 0.25f));

                case MaterialCategory.Trash:
                    // Dark mixed gray-brown
                {
                    float g = rng.Range(0.2f, 0.35f);
                    return new Color(g * 1.1f, g * 0.95f, g * 0.85f);
                }

                case MaterialCategory.Ore:
                    // Copper-tinted (warm metallic)
                {
                    float g = rng.Range(0.35f, 0.5f);
                    return new Color(g * 1.25f, g * 0.85f, g * 0.55f);
                }

                default:
                    // Unknown: safe neutral gray
                {
                    float g = rng.Range(0.35f, 0.5f);
                    return new Color(g, g * 0.98f, g * 0.95f);
                }
            }
        }

        /// <summary>
        /// Returns color with specified alpha.
        /// Convenience wrapper for debris particles.
        /// </summary>
        public static Color GetColorWithAlpha(MaterialCategory cat, System.Random rng, float alpha)
        {
            Color c = GetColor(cat, rng);
            c.a = alpha;
            return c;
        }
    }
}