# Shrapnel Overhaul Mod ![Version](https://img.shields.io/badge/version-0.8.4-blue)

### [English](#-english) | [Русский](#-русский)

<a name="english"></a>
## 🇬🇧 English

### About the Mod
**Shrapnel Overhaul** completely changes how explosions work in the game. Instead of just a simple "bang" and some smoke, explosions now produce hundreds of real, flying fragments. They bounce off metal, get stuck in walls, and can seriously injure characters. Featuring highly realistic physics, true 2D lighting interaction, and an advanced, custom-built multiplayer synchronization engine!

### ⚠️ Important Compatibility
This mod works with **Scav prototype version 5.1 and newer**. 
You can download the latest version of the game here:  
👉[https://orsonik.itch.io/scav-prototype](https://orsonik.itch.io/scav-prototype)

**Multiplayer Support:**  
Fully compatible with the [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop) (version **2.1.2** and above). However, **it does not require it**. The mod uses advanced reflection to detect multiplayer sessions dynamically. If you play in co-op, shrapnel generation, physics, and visual effects are 100% synchronized between all players via a highly optimized, server-authoritative custom protocol!

**Universal Language Support:**  
Works flawlessly no matter what language the game is set to (English, Russian, etc.) and seamlessly identifies materials even from custom modded blocks!

### 🎥 Showcase

> **Note:** The showcase GIFs below are from an older version (0.7.0). The visuals are a bit outdated, and I will upload new ones soon to show off the brand new particle effects and buttery smooth multiplayer synchronization!

**Multiplayer Synchronization**  
*A friend steps on a mine, and the flying shrapnel hits you perfectly in sync!*  
![Multiplayer Mine Explosion](showcase/scav_070_1.gif)

**Biome Effects & Footwear Protection**  
*A turret explodes in the desert kicking up sand. You take a hit, fall onto scattered debris, but your boots save your feet from cuts.*  
![Desert Turret Explosion & Boots Protection](showcase/scav_070_2.gif)

**Watch Your Step!**  
*Stepping on that same debris barefoot instantly causes severe bleeding and triggers the injury minigame.*  
![Barefoot Damage](showcase/scav_070_3.gif)

### Main Features
* **Physical & Micro Shrapnel:** Every explosion sends shards of metal, stone, or wood flying in all directions with perfect center-outward symmetry.
* **Damage Interaction:** Shards damage players, **enemies (spiders), traps (turrets), and destroy blocks!**
* **True Lighting & Shadows:** Inert particles correctly respect the game's 2D lighting. Fragments glow in the dark while hot, and turn pitch black in caves as they cool.
* **Multi-Directional Physics:** Debris blasts outward from cave ceilings, vertical walls, and overhangs. Shockwaves correctly propagate through open air!
* **Multiplayer Synchronization:** Server-authoritative shrapnel with client-side parabolic extrapolation and interpolation ensures buttery smooth, 1:1 chaos with friends!
* **Atmospheric Aftermath:** Massive explosions leave behind rising smoke columns, lingering crater dust, and glowing embers that cool as they land.
* **Watch Your Step:** Shards stay on the ground. Walking over them barefoot causes injury! Wear boots to stay safe.
* **Zero-GC Performance:** Built from the ground up with custom memory pooling (`AshParticlePoolManager`) and GPU-batched sparks to ensure **zero micro-stutters** even during massive chain explosions.

### How to Install
1. Requires **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** installed in your game directory.
2. Place the `ScavShrapnelMod.dll` file into the following folder:  
   `CasualtiesUnknownDemo/BepInEx/plugins/`
3. Launch the game! (The configuration file will auto-generate and update safely on the first run).

---

<a name="русский"></a>
## 🇷🇺 Русский

### О моде
**Shrapnel Overhaul** полностью меняет механику взрывов. Теперь это не просто вспышка и дым, а сотни настоящих летящих осколков. Они рикошетят от металла, застревают в стенах и представляют серьезную угрозу для здоровья. Включает в себя реалистичную физику, честное взаимодействие с 2D освещением и продвинутый кастомный движок сетевой синхронизации для мультиплеера!

### ⚠️ Важное примечание
Этот мод работает **на версии Scav prototype 5.1 и выше**.  
Скачать актуальную версию игры можно здесь:  
👉[https://orsonik.itch.io/scav-prototype](https://orsonik.itch.io/scav-prototype)

**Поддержка мультиплеера:**  
Мод полностью совместим с [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop) (начиная с версии **2.1.2**). При этом **он не требует его для работы**. Мод использует продвинутую рефлексию для динамического обнаружения сетевой игры. В коопе физика и визуал осколков на 100% синхронизируются между игроками через оптимизированный серверный протокол!

**Мультиязычность и моды:**  
Больше никаких багов из-за русской локализации! Мод безошибочно определяет материалы блоков (дерево, камень, металл, песок) на любом языке игры, а также автоматически поддерживает блоки из других модов.

### 🎥 Демонстрация

> **Примечание:** Гифки ниже записаны на старой версии (0.7.0). Визуал в них немного устарел, скоро я загружу новые, чтобы показать все свежие эффекты частиц и идеальную сетевую синхронизацию!

**Синхронизация в мультиплеере**  
*Друг наступает на мину, и разлетающийся осколок честно прилетает прямо в вас!*  
![Взрыв мины в мультиплеере](showcase/scav_070_1.gif)

**Реакция биома и защита ботинок**  
*Взрыв турели в пустыне поднимает песок. Осколок сбивает вас с ног прямо на мелкий мусор, но плотные ботинки полностью спасают ноги от порезов.*  
![Взрыв турели и защита ботинок](showcase/scav_070_2.gif)

**Смотрите под ноги!**  
*Специально наступаем на те же осколки, но уже босиком — получаем глубокий порез, кровотечение и мини-игру с лечением.*  
![Урон босиком](showcase/scav_070_3.gif)

### Основные возможности
* **Физические и микро-осколки:** Взрывы и попадания пуль создают физические частицы с идеальной симметрией разлета.
* **Урон всему:** Осколки ранят игроков, **врагов (пауков), ловушки (турели) и разрушают блоки!**
* **Честное освещение и тени:** Обычный мусор реагирует на игровое освещение и скрывается во тьме. Раскаленные осколки светятся, но по мере остывания тоже сливаются с тенями.
* **Многонаправленные ударные волны:** Осколки отлетают от потолков, вертикальных стен и выступов. Видимые перепады давления в воздухе!
* **Сетевая синхронизация:** Сервер полностью управляет физикой, а клиенты используют параболическую экстраполяцию и интерполяцию. Никаких дерганых движений — идеальная плавность и синхронность с друзьями!
* **Атмосферные последствия:** Столбы дыма, густая пыль вокруг воронки и осыпающиеся угли.
* **Смотри под ноги:** Осколки остаются лежать на полу. Если наступить на них босиком, персонаж поранит ноги. Носите обувь!
* **Zero-GC Оптимизация:** Мод написан с использованием кастомных пулов памяти (`AshParticlePoolManager`) и GPU-батчинга искр. Никаких микрофризов даже при масштабных цепных взрывах!

### Инструкция по установке
1. Убедитесь, что у вас установлен **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** в папке с игрой.
2. Поместите файл `ScavShrapnelMod.dll` в папку:  
   `CasualtiesUnknownDemo/BepInEx/plugins/`
3. Запускайте игру и наслаждайтесь! (Файл настроек сгенерируется автоматически при первом запуске).