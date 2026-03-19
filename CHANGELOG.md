# Changelog

All notable changes to the Shrapnel Overhaul Mod will be documented here.

## [0.8.2] — 2026-03-20
### Fixed
- Fixed a major bug where singleplayer explosions would not trigger correctly when the multiplayer mod was installed but inactive.
- Improved network synchronization interpolation to significantly reduce "jerking" and stuttering of shrapnel on clients.
- Minor performance tweaks to the new Zero-GC object pooling system.
### Added
- More info about project: BUILD.md, CHANGELOG.md
- Added `-net` and `-v` (verbose) flags to `shrapnel_explode` and `shrapnel_debris` console commands to easily debug and simulate the full Server-to-Client explosion pipeline locally.

## [0.8.1] — 2026-03-19
### Fixed
- Fixed a critical "Ghost Explosion" bug where Grav Bags would trigger a lethal explosion when inserting/removing batteries (causing fake fall damage).
- Fixed a multiplayer desync bug where clients would encounter "phantom shards" that were not visible but still caused damage.
- Fixed various material shader corruptions caused by vanilla chunk unloading.
### Added
- **Shrapnel now correctly damages enemies (spiders), turrets, and other BuildingEntities!**
- Advanced console command argument parsing (commands are now order-independent and support shorthand flags).
- Added `shrapnel_net` command for in-depth network diagnostics.
### Changed
- Reduced default ground debris particle counts by 50% for cleaner environments based on player feedback.

## [0.8.0] — 2026-03-18
### Added
- **Complete Multiplayer Synchronization:** Physics shrapnel is now 100% server-authoritative.
- **Custom Network Protocol:** Implemented a lightweight network sync via Unity NGO CustomMessagingManager.
- **Zero Compile-Time Dependency:** All Netcode operations are resolved via advanced reflection. The mod works flawlessly even if the Multiplayer Mod is not installed.
- **Client-Side Prediction:** Added `ClientMirrorShrapnel` with parabolic extrapolation and gravity-corrected velocity back-computation for buttery smooth visual sync on clients.
- **Zero-GC Object Pooling:** Replaced Unity's instantiation overhead with a custom `AshParticlePoolManager` to completely eliminate micro-stutters during massive explosions.
- **GPU-Batched Sparks:** Migrated small sparks to Unity's `ParticleSystem` (`ParticlePoolManager`) to handle thousands of trailing sparks without CPU bottlenecking.

## [0.7.0] — 2026-03-17
### Added
- True Lighting & Shadows: Inert particles now correctly respect the game's 2D lighting and will be pitch black in dark areas.
- Universal Language Support: Fixed a major bug where the mod failed to recognize blocks if the game wasn't in English. 
- True Multi-Directional Shockwaves: Explosions now scan every single block face in the blast radius.
- Visible Pressure Waves: Added expanding dust rings traveling through open air.
- Perfect Explosion Symmetry: Fixed logic bugs that caused debris to favor one side of the screen.

## [0.6.0] — 2026-03-16
### Added
- Advanced Explosion Effects: Towering smoke columns, glowing fire embers, and lingering crater dust.
- Biome Reactions: Steam clouds in freezing temperatures and thick sand dust in deserts.
- Block Debris: Destroying blocks with explosives now generates physical debris particles.
- Enhanced Gunplay Feedback: Bright impact flashes, dynamic spark showers, and scattering metal chips when shooting metal.
- Visual Decay: Shrapnel elegantly shrinks and fades away at the end of its lifetime.
