# Shrapnel Overhaul Mod ![Version](https://img.shields.io/badge/version-0.9.2-blue)

[English](#english) | [Русский](#русский)

---

## English

### About the Mod
**Shrapnel Overhaul** completely transforms explosions and gunfights in Scav. Instead of a simple flash, every explosion sends hundreds of real, deadly fragments flying through the air. Bullets kick up massive dust clouds, rip chunks out of walls, and create blinding showers of sparks. 

Everything is fully synchronized in multiplayer and heavily optimized so your game won't lag, even during massive chain explosions.

### Compatibility
* **Game:** Works with **Scav prototype v5.1+**.
* **Multiplayer:** 100% compatible with the [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop). Every spark and shard is synced between players. (Works perfectly in singleplayer too!).
* **Languages:** Works with any game language and automatically supports custom blocks from other mods.

### 🎥 Showcase

> **Note:** The first three GIFs below are from an earlier version (0.7.0). An updated showcase is coming soon!

**Kinetic Bullet Impacts (New in v0.9!)**
*Watch how bullets transfer energy through walls, kicking up directional dust plumes and blinding metal sparks.*
![Kinetic Bullet Impacts and Muzzle Flashes](showcase/scav_090_impacts.gif)

**Multiplayer Synchronization**
*A friend steps on a mine and the flying shrapnel hits you — perfectly in sync.*
![Multiplayer Mine Explosion Sync](showcase/scav_070_1.gif)

**Biome Effects & Boot Protection**
*A turret explodes in the desert, kicking up sand. You fall onto scattered debris, but your boots save your feet from the glass.*
![Desert Turret Explosion Biome Effects](showcase/scav_070_2.gif)

**Watch Your Step!**
*Same debris, no boots — severe bleeding and the injury minigame.*
![Barefoot Glass Damage](showcase/scav_070_3.gif)

### 💥 Features

* **Explosions that Matter:** Real flying debris (metal, stone, wood) that can hurt players, enemies (spiders), and turrets.
* **Intense Gunfights:** Bullets now transfer kinetic energy through walls. Dust sprays backward from the impact, metal conducts energy creating spark showers, and guns emit a concussive muzzle blast.
* **Dynamic Environments:** Sand flies in the desert, steam hisses in the freezing cold. Hot metal glows in dark caves and sizzles if it lands in water.
* **Watch Your Step:** Shrapnel stays on the ground. Step on glass barefoot? You'll bleed. Wear boots to protect yourself!
* **Smooth Performance:** A custom built-in engine ensures that even with thousands of particles on screen, your game won't freeze or drop frames.

### 🛠️ Installation
1. Install **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** in your game folder.
2. Place `ScavShrapnelMod.dll` into `CasualtiesUnknownDemo/BepInEx/plugins/`
3. Launch the game! (Settings will generate automatically).

### ⌨️ Console Commands
Open the in-game console with `~`. 

| Command | Description | Example |
|---------|-------------|---------|
| `shrapnel_explode` | Spawns an explosion at cursor | `shrapnel_explode dynamite` |
| `shrapnel_debris` | Spawns physics fragments | `shrapnel_debris 20 50 metal` |
| `shrapnel_shot` | Tests bullet effects pipeline | `shrapnel_shot rifle R -metal` |
| `shrapnel_highlight`| Shows shards through walls | `shrapnel_highlight 15` |
| `shrapnel_clear` | Destroys all shrapnel | `shrapnel_clear` |
| `shrapnel_status` | Shows mod performance stats | `shrapnel_status` |

*(For detailed technical info, network diagnostics, and architecture, see `README_DEV.md`).*

---

## Русский

### О моде
**Shrapnel Overhaul** делает взрывы и перестрелки в Scav невероятно сочными. Вместо обычной вспышки, каждый взрыв раскидывает сотни смертоносных осколков. Попадания пуль поднимают густые облака пыли, вырывают куски из стен и создают фонтаны искр.

Всё это полностью синхронизировано в мультиплеере и жестко оптимизировано — игра не будет лагать даже при цепных взрывах.

### Совместимость
* **Игра:** Работает на **Scav prototype v5.1+**.
* **Мультиплеер:** 100% совместимость с [Co-op Multiplayer Mod](https://github.com/Krokosha666/cas-unk-krokosha-multiplayer-coop). Каждая искра и осколок синхронизируются между игроками. (В одиночной игре тоже работает идеально!).
* **Языки:** Работает на любом языке игры и автоматически понимает блоки из других модов.

### 🎥 Демонстрация

> **Примечание:** Первые три гифки записаны на старой версии (0.7.0). Скоро они будут обновлены!

**Кинетические попадания пуль (Новое в v0.9!)**
*Посмотрите, как пули передают энергию сквозь стены, поднимая направленную пыль и ослепительные искры от металла.*
![Кинетические попадания пуль и вспышки выстрелов](showcase/scav_090_impacts.gif)

**Синхронизация в мультиплеере**
*Друг наступает на мину — осколок прилетает прямо в вас, полностью синхронно.*
![Синхронизация взрыва мины в мультиплеере](showcase/scav_070_1.gif)

**Биомы и защита ботинок**
*Взрыв турели в пустыне. Вас сбивает на мусор, но ботинки спасают ноги.*
![Взрыв турели в пустыне и биомные эффекты](showcase/scav_070_2.gif)

**Смотрите под ноги!**
*Тот же мусор босиком — кровотечение и мини-игра лечения.*
![Урон от стекла босиком](showcase/scav_070_3.gif)

### 💥 Главные фишки

* **Опасные взрывы:** Настоящие летящие обломки (металл, камень, дерево), которые могут ранить игроков, врагов (пауков) и турели.
* **Сочные перестрелки:** Пули передают кинетическую энергию сквозь стены. Пыль вылетает в сторону стрелка, металл проводит энергию создавая снопы искр, а от ствола расходится газовая волна.
* **Живой мир:** В пустыне взрывы поднимают песок, на морозе — пар. Горячий металл светится в темных пещерах и шипит, падая в воду.
* **Смотри под ноги:** Осколки остаются лежать на земле. Наступишь на стекло босиком — пойдет кровь. Надевай ботинки!
* **Идеальная производительность:** Кастомный движок мода гарантирует, что даже тысячи частиц на экране не вызовут фризов или просадок FPS.

### 🛠️ Установка
1. Установите **[BepInEx 5.4.21+](https://github.com/BepInEx/BepInEx/releases)** в папку с игрой.
2. Поместите `ScavShrapnelMod.dll` в `CasualtiesUnknownDemo/BepInEx/plugins/`
3. Запустите игру! (Настройки создадутся автоматически).

### ⌨️ Консольные команды
Откройте консоль клавишей `~`.

| Команда | Описание | Пример |
|---------|----------|---------|
| `shrapnel_explode` | Взрыв на курсоре | `shrapnel_explode dynamite` |
| `shrapnel_debris` | Спавн осколков | `shrapnel_debris 20 50 metal` |
| `shrapnel_shot` | Тест эффектов выстрела | `shrapnel_shot rifle R -metal` |
| `shrapnel_highlight`| Подсветка осколков сквозь стены | `shrapnel_highlight 15` |
| `shrapnel_clear` | Удалить все осколки | `shrapnel_clear` |
| `shrapnel_status` | Статус производительности мода | `shrapnel_status` |

*(Для технической информации, сетевой диагностики и архитектуры смотрите `README_DEV.md`).*

[English](#english) | [Русский](#русский)