using System.Collections.Generic;
using UnityEngine;

namespace ScavShrapnelMod
{
    /// <summary>
    /// Статический кэш спрайтов, материалов и цветов для осколков.
    /// 
    /// Спрайты генерируются процедурно при первом запросе и кэшируются навсегда.
    /// Материалы создаются один раз (3 штуки: lit, unlit, trail).
    /// 
    /// Производительность: один DrawCall на батч одинаковых материалов.
    /// </summary>
    public static class ShrapnelVisuals
    {
        //  Кэш спрайтов 
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        //  Кэш материалов ─
        private static Material _litMaterial;
        private static Material _unlitMaterial;
        private static Material _trailMaterial;

        //  MATERIALS

        public static Material LitMaterial
        {
            get
            {
                if (_litMaterial == null)
                {
                    string[] litShaders =
                    {
                        "Universal Render Pipeline/2D/Sprite-Lit-Default",
                        "Sprites/Lit",
                        "Sprites/Default"
                    };

                    foreach (string name in litShaders)
                    {
                        Shader s = Shader.Find(name);
                        if (s != null)
                        {
                            _litMaterial = new Material(s);
                            Plugin.Log.LogInfo($"[Visuals] LitMaterial using: {name}");
                            break;
                        }
                    }

                    if (_litMaterial == null)
                    {
                        _litMaterial = new Material(Shader.Find("Sprites/Default"));
                        Plugin.Log.LogWarning("[Visuals] LitMaterial fallback to Sprites/Default");
                    }
                }
                return _litMaterial;
            }
        }

        public static Material UnlitMaterial
        {
            get
            {
                if (_unlitMaterial == null)
                {
                    string[] unlitShaders =
                    {
                        "Universal Render Pipeline/2D/Sprite-Unlit-Default",
                        "Sprites/Default"
                    };

                    foreach (string name in unlitShaders)
                    {
                        Shader s = Shader.Find(name);
                        if (s != null)
                        {
                            _unlitMaterial = new Material(s);
                            Plugin.Log.LogInfo($"[Visuals] UnlitMaterial using: {name}");
                            break;
                        }
                    }

                    if (_unlitMaterial == null)
                    {
                        _unlitMaterial = new Material(Shader.Find("Sprites/Default"));
                        Plugin.Log.LogWarning("[Visuals] UnlitMaterial fallback to Sprites/Default");
                    }
                }
                return _unlitMaterial;
            }
        }

        public static Material TrailMaterial
        {
            get
            {
                if (_trailMaterial == null)
                {
                    Shader s = Shader.Find("Sprites/Default");
                    if (s == null) s = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                    _trailMaterial = new Material(s);
                    Plugin.Log.LogInfo("[Visuals] TrailMaterial initialized");
                }
                return _trailMaterial;
            }
        }

        //  TRIANGLE SPRITES

        public enum TriangleShape { Acute, Right, Obtuse, Shard, Needle, Chunk }

        /// <summary>
        /// Процедурно сгенерированный спрайт осколка.
        /// Кэшируется навсегда. 32×32, FilterMode.Point.
        /// 
        /// Оптимизация: batch SetPixels вместо попиксельного SetPixel.
        /// Снижает время генерации спрайта ~32× (1 вызов вместо 1024).
        /// </summary>
        public static Sprite GetTriangleSprite(TriangleShape shape)
        {
            string key = shape.ToString();
            if (_spriteCache.TryGetValue(key, out Sprite cached))
                return cached;

            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            // Batch: заполняем массив целиком, один вызов SetPixels
            Color[] pixels = new Color[size * size];
            Vector2[] verts = GetShapeVertices(shape);

            Vector2[] scaled = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                scaled[i] = verts[i] * size;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), scaled))
                        pixels[y * size + x] = Color.white;
                    // else остаётся default Color (0,0,0,0) — прозрачный
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
            sprite.name = $"Shrapnel_{shape}";
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Вершины полигона для каждой формы (нормализованные 0–1).
        /// </summary>
        private static Vector2[] GetShapeVertices(TriangleShape shape)
        {
            switch (shape)
            {
                case TriangleShape.Acute:
                    return new[] {
                        new Vector2(0.45f, 1f),   new Vector2(0.7f, 0.6f),
                        new Vector2(0.9f, 0.15f),  new Vector2(0.1f, 0f),
                        new Vector2(0.2f, 0.4f)
                    };
                case TriangleShape.Right:
                    return new[] {
                        new Vector2(0f, 0.95f),    new Vector2(0.15f, 0.5f),
                        new Vector2(0f, 0.05f),    new Vector2(0.75f, 0f),
                        new Vector2(0.6f, 0.3f)
                    };
                case TriangleShape.Obtuse:
                    return new[] {
                        new Vector2(0.25f, 0.85f), new Vector2(0.55f, 0.7f),
                        new Vector2(1f, 0.1f),     new Vector2(0.6f, 0f),
                        new Vector2(0f, 0.05f),    new Vector2(0.1f, 0.5f)
                    };
                case TriangleShape.Shard:
                    return new[] {
                        new Vector2(0.35f, 1f),    new Vector2(0.55f, 0.65f),
                        new Vector2(0.85f, 0.05f), new Vector2(0.4f, 0.15f),
                        new Vector2(0f, 0.2f),     new Vector2(0.15f, 0.55f)
                    };
                case TriangleShape.Needle:
                    return new[] {
                        new Vector2(0.5f, 1f),     new Vector2(0.6f, 0.5f),
                        new Vector2(0.55f, 0f),    new Vector2(0.4f, 0.3f),
                        new Vector2(0.45f, 0.7f)
                    };
                case TriangleShape.Chunk:
                    return new[] {
                        new Vector2(0.1f, 0.9f),   new Vector2(0.5f, 0.95f),
                        new Vector2(0.9f, 0.7f),   new Vector2(0.95f, 0.15f),
                        new Vector2(0.4f, 0f),     new Vector2(0f, 0.1f),
                        new Vector2(0.15f, 0.5f)
                    };
                default:
                    return new[] {
                        new Vector2(0.5f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f)
                    };
            }
        }

        /// <summary>Ray-casting point-in-polygon. Работает для любых полигонов.</summary>
        private static bool PointInPolygon(Vector2 p, Vector2[] polygon)
        {
            bool inside = false;
            int j = polygon.Length - 1;
            for (int i = 0; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
                    p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y)
                           / (polygon[j].y - polygon[i].y) + polygon[i].x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        //  COLORS

        /// <summary>Холодный цвет (Heat = 0) — зависит от материала.</summary>
        public static Color GetColdColor(ShrapnelProjectile.ShrapnelType type)
        {
            switch (type)
            {
                case ShrapnelProjectile.ShrapnelType.Metal: return new Color(0.30f, 0.30f, 0.35f);
                case ShrapnelProjectile.ShrapnelType.HeavyMetal: return new Color(0.15f, 0.15f, 0.20f);
                case ShrapnelProjectile.ShrapnelType.Stone: return new Color(0.50f, 0.45f, 0.40f);
                case ShrapnelProjectile.ShrapnelType.Wood: return new Color(0.55f, 0.35f, 0.15f);
                case ShrapnelProjectile.ShrapnelType.Electronic: return new Color(0.10f, 0.60f, 0.30f);
                default: return Color.gray;
            }
        }

        /// <summary>Горячий цвет (Heat = 1) — раскалённый оранжевый.</summary>
        public static Color GetHotColor() => new Color(1f, 0.55f, 0.1f);
    }

    //  WEIGHT ENUM

    /// <summary>
    /// Весовые категории осколков.
    /// 
    /// Hot = лёгкий раскалённый, летит средне, слабый урон.
    /// Medium = средний, баланс урона и дальности.
    /// Heavy = тяжёлый, близко но больно. Может сломать кость.
    /// Massive = огромный кусок, редкий, летальный. Гарантированный перелом.
    /// </summary>
    public enum ShrapnelWeight { Hot, Medium, Heavy, Massive }
}