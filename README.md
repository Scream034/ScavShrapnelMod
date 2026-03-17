# Shrapnel Overhaul Mod

![Version](https://img.shields.io/badge/version-0.7.0-blue)

[English](#-english) | [Русский](#-русский)

---

<a name="english"></a>
## 🇬🇧 English

### About the Mod
**Shrapnel Overhaul** completely changes how explosions work in the game. Instead of just a simple "bang" and some smoke, explosions now produce hundreds of real, flying fragments. They bounce off metal, get stuck in walls, and can seriously injure your character. With the latest update, explosions feature highly realistic shockwave physics, visible air pressure waves, and true 2D lighting interaction!

### ⚠️ Important Compatibility
This mod works with **Scav prototype version 5.1 and newer**. 
You can download the latest version of the game here:  
👉[https://orsonik.itch.io/scav-prototype](https://orsonik.itch.io/scav-prototype)

**Multiplayer Support:**  
Fully compatible with the [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop) (version **2.1.2** and above). Shrapnel generation, physics, and visual effects are 100% synchronized between all players!

**Universal Language Support:**  
Works flawlessly no matter what language the game is set to (English, Russian, etc.) and seamlessly identifies materials even from custom modded blocks!

### 🎥 Showcase

**Multiplayer Synchronization**  
*A friend steps on a mine, and the flying shrapnel hits you perfectly in sync!*  
![Multiplayer Mine Explosion](showcase/scav_1.gif)

**Biome Effects & Footwear Protection**  
*A turret explodes in the desert kicking up sand. You take a hit, fall onto scattered debris, but your boots save your feet from cuts.*  
![Desert Turret Explosion & Boots Protection](showcase/scav_2.gif)

**Watch Your Step!**  
*Stepping on that same debris barefoot instantly causes severe bleeding and triggers the injury minigame.*  
![Barefoot Damage](showcase/scav_3.gif)

### Main Features
*   **Physical Shrapnel:** Every explosion sends shards of metal, stone, or wood flying in all directions with perfect center-outward symmetry. Ground-placed mines correctly form an upward cone of destruction.
*   **True Lighting & Shadows:** Inert particles (rocks, dust, wood splinters, and smoke) now correctly respect the game's 2D lighting and will be pitch black in dark areas. Only genuinely hot materials (fire embers, sparks, pyrotechnics) glow in the dark.
*   **Multi-Directional Shockwaves & Airwaves:** Debris correctly blasts outward from cave ceilings, vertical walls, and overhangs. You can even see the subtle, expanding dust ring of the pressure wave traveling through open air!
*   **Atmospheric Aftermath:** Massive explosions leave behind rising smoke columns, glowing embers that rain down, and lingering dust clouds around the crater.
*   **Watch Your Step:** Fallen fragments stay on the ground. Walking over them barefoot will hurt your feet and cause bleeding! Wear shoes to stay safe.
*   **Ricochets & Embedding:** Shards bounce off metallic surfaces or get stuck in limbs and walls.
*   **Heat & Biomes:** Hot metal fragments sizzle in water. Explosions in cold biomes create massive steam clouds, while desert explosions kick up extra sand.
*   **Performance Optimized:** Zero micro-stutters during massive explosions! Use the new `BlockDebrisCountMultiplier` in the auto-generated config to fine-tune particle density to match your PC's performance.

### How to Install
1.  Requires **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** installed in your game directory.
2.  Place the `ScavShrapnelMod.dll` file into the following folder:  
    `CasualtiesUnknownDemo/BepInEx/plugins/`
3.  Launch the game and enjoy the chaos! (The configuration file will auto-generate on the first run).

---

<a name="русский"></a>
## 🇷🇺 Русский

### О моде
**Shrapnel Overhaul** полностью меняет механику взрывов. Теперь это не просто вспышка и дым, а сотни настоящих летящих осколков. Они рикошетят от металла, застревают в стенах и представляют серьезную угрозу для здоровья персонажа. В последнем обновлении добавлена реалистичная физика ударных волн, видимые перепады давления в воздухе и честное взаимодействие с 2D освещением!

### ⚠️ Важное примечание
Этот мод работает **на версии Scav prototype 5.1 и выше**.  
Скачать актуальную версию игры можно здесь:  
👉[https://orsonik.itch.io/scav-prototype](https://orsonik.itch.io/scav-prototype)

**Поддержка мультиплеера:**  
Мод полностью совместим с [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop) (начиная с версии **2.1.2**). Разлет, физика осколков и визуальные эффекты на 100% синхронизированы между всеми игроками!

**Мультиязычность и моды:**  
Больше никаких багов из-за русской локализации! Мод безошибочно определяет материалы блоков (дерево, камень, металл, песок) на любом языке игры, а также автоматически поддерживает блоки из других модов.

### 🎥 Демонстрация

**Синхронизация в мультиплеере**  
*Друг наступает на мину, и разлетающийся осколок честно прилетает прямо в вас!*  
![Взрыв мины в мультиплеере](showcase/scav_1.gif)

**Реакция биома и защита ботинок**  
*Взрыв турели в пустыне поднимает песок. Осколок сбивает вас с ног прямо на мелкий мусор, но плотные ботинки полностью спасают ноги от порезов.*  
![Взрыв турели и защита ботинок](showcase/scav_2.gif)

**Смотрите под ноги!**  
*Специально наступаем на те же осколки, но уже босиком — получаем глубокий порез, кровотечение и мини-игру с лечением.*  
![Урон босиком](showcase/scav_3.gif)

### Основные возможности
*   **Физические осколки:** Каждый взрыв создает разлетающиеся частицы с идеальной симметрией. Мины, лежащие на полу, теперь корректно формируют конус осколков, направленный вверх.
*   **Честное освещение и тени:** Обычный мусор (камни, пыль, щепки и дым) реагирует на игровое освещение и скрывается во тьме. Светятся в темноте только по-настоящему раскаленные элементы (искры, угли).
*   **Многонаправленные ударные волны:** Осколки отлетают от потолков пещер, вертикальных стен и выступов, а не только от ровного пола. Теперь можно увидеть даже расширяющееся кольцо пыли от ударной волны прямо в воздухе!
*   **Атмосферные последствия:** Мощные взрывы оставляют после себя поднимающиеся столбы дыма, осыпающиеся горящие угли и густую пыль вокруг воронки.
*   **Смотри под ноги:** Осколки остаются лежать на полу. Если наступить на них босиком, персонаж поранит ноги и получит кровотечение. Носите обувь!
*   **Рикошеты и застревание:** Осколки могут отскакивать от металлических поверхностей или застревать в стенах и теле.
*   **Температура и биомы:** Горячие осколки шипят в воде. Взрывы на холоде создают густой пар, а в пустыне поднимают облака песка.
*   **Глубокая оптимизация:** Никаких микрофризов во время масштабных взрывов! Настройте плотность частиц под мощность своего ПК с помощью нового параметра `BlockDebrisCountMultiplier` в конфигурационном файле.

### Инструкция по установке
1.  Убедитесь, что у вас установлен **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** в папке с игрой.
2.  Поместите файл `ScavShrapnelMod.dll` в папку:  
    `CasualtiesUnknownDemo/BepInEx/plugins/`
3.  Запускайте игру и наслаждайтесь! (Файл настроек сгенерируется автоматически при первом запуске).