# Changelog

All notable changes to the Shrapnel Overhaul Mod will be documented here.

## [0.9.2] — 2026-03-22

### Fixed
- **Critical: Bullet power always calculated as 10** — `shotsPerFire=0` for single-shot
  weapons zeroed the entire power formula (`135 × 1.5 × 0 = 0`, clamped to 10). This
  prevented physics fragments from ever spawning on bullet impacts (`power 10 < min 20`),
  made all spark showers nearly invisible (`sparkScale=0.4`), and caused `recv=0/0/0` on
  clients since the server never created any shards to sync.
  - Fix: `shotsPerFire` now clamped to minimum 1, `knockBack` clamped to minimum 0.
  - Fix: `Convert.ToSingle()` replaces direct `(float)` cast for type-safe unboxing of
    reflected fields (handles boxed `int`, `float`, `double`).
  - Fix: Reflection flags now include `NonPublic` to catch fields with any access modifier.
  - First 3 shots log full power breakdown in Release builds for remote diagnosis.

- **Particles flying through solid blocks** — Muzzle blast dust and bullet impact dust
  could spray directly into terrain when the blast/bullet direction opposed the surface
  normal. `SafeBlastDirection()` now reflects the direction vector when it points into
  the block, ensuring all particles spray into open air.

- **Kinetic dust plume spraying into walls** — `SpawnKineticPlume` was emitting particles
  along `bulletDir`, which by definition points INTO the block that was hit. Reversed to
  `-bulletDir` — dust now sprays backward toward the shooter, which is physically correct
  (debris exits the entry side of the impact).

- **Unicode crash in game console** — Characters like `✓` caused font fallback warnings
  flooding the log. All console output now uses ASCII only (`[+]`, `[-]`, `*`, `--`).

- **Old config defaults persisting after code changes** — Muzzle blast radius stayed at 8
  instead of new default 12, impact radius at 2 instead of 4, etc. Version bump to 0.9.2
  triggers automatic config backup and reset with correct defaults.

### Added
- **Kinetic Energy Transfer system (bullet impacts)** — Bullets now transfer momentum
  directionally through blocks. Dust sprays backward from impact, metal conducts energy
  further (1.5× scan radius), soft materials absorb energy but create denser local clouds.
  Three-phase system: surface dust, kinetic plume, and metal conduction sparks.

- **Column-scan surface detection (muzzle blast)** — Replaced brute-force grid scan with
  column-scan algorithm that finds actual air→solid transitions. Scans downward for ground,
  upward for ceilings, laterally for walls. Finds 3× more surfaces with less iteration.

- **Directional particle emission** — Muzzle blast and impact dust now blend face normal
  with blast/bullet direction for realistic spray patterns. Configurable blend factor
  (40% blast for muzzle, 65% bullet for impacts). Dust rises with thermal convection.

- **Material conductivity model** — Metal blocks extend impact scan radius by 1.5×
  (configurable). Rock/concrete get slight bonus (1.1×). Sand/organic absorb energy
  (0.7× radius but 1.4× particle density). Creates material-appropriate dust behavior.

- **Metal conduction sparks** — Bright needle sparks travel perpendicular to bullet
  direction along metal surfaces, simulating energy conducted through the block structure.
  Only spawns on metal impacts.

- **Console: `shrapnel_shot`** — Full bullet effect pipeline test at cursor position.
  Supports weapon presets (`pistol`, `rifle`, `shotgun`, `turret`), custom power values,
  directional control (`L`/`R`/`U`/`D`), force metal sparks (`-metal`), and individual
  effect isolation (`muzzle`, `impact`, `sparks`).

- **Console: `shrapnel_guninfo`** — Dumps all GunScript fields for power calculation
  diagnosis. Shows priority fields (damage, knockback, shotsPerFire) first, computed
  power result, and optional full field dump with `all` argument.

- **Console: `shrapnel_highlight`** — Temporarily makes all physics shards visible
  through walls with bright orange glow and top sorting order. Configurable duration
  (default 10s), toggle off with `0`. Self-reverts cleanly.

- **Config: `BulletImpactKineticTransfer`** (default 1.0) — Controls how many bonus
  particles spawn along bullet travel axis. 0 = disabled, 2 = very directional.

- **Config: `BulletImpactMetalConductivity`** (default 1.5) — Scan radius multiplier
  for metal block impacts.

### Changed
- **`MuzzleBlastRadius` default: 8 → 12** — Column-scan finds surfaces efficiently at
  larger radius. Guns now disturb dust across a 12-block area, turrets across 18 blocks.
- **`BulletImpactBlastRadius` default: 2 → 4** — Kinetic transfer needs larger scan area
  to create visible directional plumes.
- **`BulletImpactBlastMaxParticles` default: 60 → 120** — Supports kinetic plume +
  conduction sparks + surface dust without hitting cap.
- **`BulletImpactBlastCountMult` default: 1.5 → 2.0** — More particles per surface for
  visible dust clouds.
- **`BulletImpactBlastMinEnergy` default: 0.2 → 0.15** — Sharper falloff for more
  localized impact feel.
- **`MuzzleBlastMaxParticles` default: 150 → 200** — Covers larger scan radius.
- **`MuzzleBlastMaxParticlesTurret` default: 300 → 350** — Turret coverage.
- **Console commands consolidated** — Removed `shrapnel_testmat` (merged into
  `shrapnel_status mat`). All commands use consistent argument parsing.

## [0.9.1] — 2026-03-21

### Added
- **Muzzle Blast Dust** — Firing a gun creates a concussive gas blast that disturbs
  nearby surfaces. Reuses the same chunk/dust/streak particles as explosions for visual
  consistency. Configurable scan radius (guns 8 blocks, turrets 12 via ×1.5 multiplier),
  power-scaled count via √(bulletPower/25) with diminishing returns. Full config section
  `[Effects.MuzzleBlast]`.

- **Bullet Impact Block Blast** — Bullets hitting blocks emit dust from nearby exposed
  surfaces. Scans 1–3 block radius around hit point, finds solid blocks with exposed faces,
  emits particles toward air. Linear energy falloff with distance. Config section
  `[Effects.BulletImpactBlast]`.

- **Gunpowder Smoke** — Dark gray Lit wisps at bullet impact point. Count scales as
  2 × √(powerRatio): pistol≈2, rifle≈5, shotgun≈9. Slow upward drift with turbulence,
  marks where the bullet hit. Uses Lit material — dark in shadows, realistic.

- **Block Hit Debris (Shrapnel→Block)** — `ShrapnelProjectile.HitBlock()` now spawns
  material-appropriate debris particles. Uses `BlockClassifier`: metal produces sparks,
  soft materials produce more dust and chunks. Energy scales from fragment impact speed.

- **Fragment Scatter Improvement** — 60% hemisphere (±90° from surface normal) + 40%
  full random direction. Fragments can now fly back toward the shooter for realistic
  ricochet physics. Max speed capped at 18 m/s, damage multiplied by 0.4 (chips, not
  kills). Max 5 fragments per bullet impact.

- **Barrel Smoke** — 1–3 dark propellant gas wisps rising from barrel after each shot.
  Count scales with √(powerRatio). Uses Lit material for shadow interaction.

- **Turret Spark Boost** — Turret shots get ×2.0 spark scale (`TurretSparkBoost = 2.0f`).
  More dramatic spark shower matching turret caliber.

- **Config: Bullet power scaling** — `BulletDamageSparkMultiplier`,
  `BulletPowerFragmentMultiplier`, `MinBulletPowerForFragments`,
  `TurretFragmentMultiplier`.

- **Bullet power formula** — `power = structureDamage × (1 + knockBack/10) × shotsPerFire`.
  Reads GunScript fields via reflection. Pistol≈45, Rifle≈189, Turret=80, Shotgun≈500.

## [0.8.5] — 2026-03-20
- **Fixed double-explosions and block flickering on clients:** Turret deaths and
  bullet impacts were incorrectly triggering shrapnel and vanilla explosions on the
  client locally, fighting with the server's authoritative sync. Clients now correctly
  wait for the server and only play visual/audio effects (no local block destruction).

## [0.8.4] — 2026-03-20
### Changed
- **Architecture: Client-side physics shrapnel replaces lightweight mirrors.**
  Clients now create real `ShrapnelProjectile` instances with `Rigidbody2D` and
  `CircleCollider2D` — identical physics to the server. Shards bounce off walls,
  ricochet off metal, and embed in terrain naturally via Unity's physics engine.
  All damage paths are gated by `IsServerAuthoritative` flag.
- **Protocol v4: `MSG_SNAPSHOT` removed entirely.** Server no longer sends position
  updates 10 times per second for every flying shard. Bandwidth reduced by ~50-70
  packets per explosion. Only 3 message types remain: `MSG_SPAWN`, `MSG_STATE`,
  `MSG_DESTROY`.
- **`ClientMirrorShrapnel` deleted.** The entire 600-line mirror system (local
  gravity simulation, snapshot correction, interpolation, camera LOD, landing blend)
  is replaced by the same `ShrapnelProjectile` component used on the server. Client
  shards run the identical FSM (Flying → Stuck/Debris) with damage gated off.
- **Server REST corrections.** When a server shard transitions to rest, `MSG_STATE`
  sends the authoritative final position and rotation. Client shard snaps to the
  server's resting position via `ForceToState`, correcting any drift from divergent
  physics (different collision timing, block destruction order).
### Added
- **`ShrapnelProjectile.IsServerAuthoritative` field.** Controls whether the shard
  deals damage, breaks blocks, embeds in limbs, spawns break fragments, and plays
  impact sounds. `true` on server/singleplayer, `false` on client.
- **`ShrapnelProjectile.ForceToState(ExternalState, Vector2)` method.** Allows
  network sync to force internal FSM state without collision logic. Handles
  Rigidbody/trail/heat/collider cleanup identically to organic state transitions.
- **`ShrapnelProjectile.BeginClientFadeOut()` method.** 150ms shrink+fade for
  flying client shards on `MSG_DESTROY`. At-rest shards destroy instantly.
- **`ShrapnelProjectile.CurrentState` property.** Exposes FSM state (0=Flying,
  1=Stuck, 2=Debris) for network sync diagnostics and REST correction logic.
### Fixed
- **Fixed shrapnel clipping through walls on clients.** Previously, lightweight
  mirrors had no collider and flew straight through terrain during the 100ms between
  server snapshots. Client shards now have real `CircleCollider2D` with identical
  `PhysicsMaterial2D` (bounciness=0.15, friction=0.6) and bounce naturally.
- **Fixed missing ricochets on clients.** Mirrors could not ricochet because they
  had no physics. Client shards now ricochet off metallic surfaces identically to
  server shards (same `Rigidbody2D` config, same `PhysicsMaterial2D`).
- **Fixed client shards not settling into terrain.** Mirrors floated in mid-air
  until a server snapshot corrected them. Client shards now transition to
  Stuck/Debris locally via the same velocity/lifetime thresholds as the server,
  with server `MSG_STATE` correcting final position.

## [0.8.3] — 2026-03-20
### Fixed
- **Fixed invisible shrapnel damage:** Shards that had visually decayed to near-invisible
  (≤32% scale, ≤24% alpha) could still deal damage via their active collider. Collider is
  now disabled before the shard becomes invisible, and the GameObject is destroyed when
  visually depleted (`NormalizedLifetime ≤ 0.02`). Affects both singleplayer and multiplayer.
- **Fixed massive chain-destruction bug:** Flying shrapnel would instantly destroy grounded
  debris upon touching it, causing 80%+ of shards to disappear prematurely. Debris and
  stuck shards now only break from high-velocity impacts with non-shrapnel objects (terrain,
  entities). Shrapnel-on-shrapnel contacts are filtered out entirely.
- **Fixed integer overflow desync:** Large world coordinates or rotations caused overflow
  in packed network fields, sending shards to completely wrong locations on clients.
  All packed fields now use `Mathf.Clamp` before cast to `short`.
- **Fixed client mirrors vanishing mid-flight:** Destroy messages from the server now
  trigger a smooth 150ms shrink-and-fade effect (`BeginFadeDestroy`) instead of instantly
  popping the mirror out of existence.
- **Fixed "ghost shard" desync (server has shard, client doesn't):** Client-side at-rest
  mirrors were self-destructing after 90 seconds (`RestTimeout`) while server shards live
  for 300+ seconds (configured debris lifetime). At-rest mirrors now use `_restTimer` vs
  `_debrisLifetime`, matching server `ShrapnelProjectile.UpdateDebris` lifetime exactly.
- **Fixed "phantom shard" desync (client has shard, server doesn't):** Flying mirrors
  orphaned by a lost `MSG_DESTROY` packet now fade out gracefully after 5 seconds without
  a server snapshot (`SnapshotStaleTimeout`), down from the previous 30-second hard timeout.
- **Fixed at-rest shards spawning with wrong rotation on client:** Spawn message now
  includes Z rotation (`RotZ`, ×100 precision). Client mirrors apply server rotation on
  creation instead of always spawning at 0°. Flying shards still derive rotation from
  velocity after first frame.
- **Fixed zero base scale on flying fragments:** `_originalScale` is now set immediately
  on `Create` for all mirror states. Previously only set in `ApplyRestState`, causing
  flying mirrors and mirrors created as `atRest=true` to have `_originalScale = (0,0,0)`,
  making them completely invisible during visual decay and fade-out.
- **Fixed misleading network diagnostics:** `States sent` counter was counting packets
  instead of individual state transitions, showing `39 sent` vs `154 received` for the
  same session. Both sides now count individual transitions.
### Changed
- Spawn packet size increased by 2 bytes per entry (15 → 17 bytes) to accommodate
  rotation field. `MaxSpawnsPerPacket` adjusted from 80 → 70 to keep packets under MTU.

## [0.8.2] — 2026-03-20
### Fixed
- Fixed a major bug where singleplayer explosions would not trigger correctly when the
  multiplayer mod was installed but inactive.
- Improved network synchronization interpolation to significantly reduce "jerking" and
  stuttering of shrapnel on clients.
- Minor performance tweaks to the new Zero-GC object pooling system.
### Added
- More info about project: BUILD.md, CHANGELOG.md
- Added `-net` and `-v` (verbose) flags to `shrapnel_explode` and `shrapnel_debris`
  console commands to easily debug and simulate the full Server-to-Client explosion
  pipeline locally.

## [0.8.1] — 2026-03-19
### Fixed
- Fixed a critical "Ghost Explosion" bug where Grav Bags would trigger a lethal explosion
  when inserting/removing batteries (causing fake fall damage).
- Fixed a multiplayer desync bug where clients would encounter "phantom shards" that were
  not visible but still caused damage.
- Fixed various material shader corruptions caused by vanilla chunk unloading.
### Added
- **Shrapnel now correctly damages enemies (spiders), turrets, and other BuildingEntities!**
- Advanced console command argument parsing (commands are now order-independent and
  support shorthand flags).
- Added `shrapnel_net` command for in-depth network diagnostics.
### Changed
- Reduced default ground debris particle counts by 50% for cleaner environments based on
  player feedback.

## [0.8.0] — 2026-03-18
### Added
- **Complete Multiplayer Synchronization:** Physics shrapnel is now 100% server-authoritative.
- **Custom Network Protocol:** Implemented a lightweight network sync via Unity NGO
  CustomMessagingManager.
- **Zero Compile-Time Dependency:** All Netcode operations are resolved via advanced
  reflection. The mod works flawlessly even if the Multiplayer Mod is not installed.
- **Client-Side Prediction:** Added `ClientMirrorShrapnel` with parabolic extrapolation
  and gravity-corrected velocity back-computation for buttery smooth visual sync on clients.
- **Zero-GC Object Pooling:** Replaced Unity's instantiation overhead with a custom
  `AshParticlePoolManager` to completely eliminate micro-stutters during massive explosions.
- **GPU-Batched Sparks:** Migrated small sparks to Unity's `ParticleSystem`
  (`ParticlePoolManager`) to handle thousands of trailing sparks without CPU bottlenecking.

## [0.7.0] — 2026-03-17
### Added
- True Lighting & Shadows: Inert particles now correctly respect the game's 2D lighting
  and will be pitch black in dark areas.
- Universal Language Support: Fixed a major bug where the mod failed to recognize blocks
  if the game wasn't in English.
- True Multi-Directional Shockwaves: Explosions now scan every single block face in the
  blast radius.
- Visible Pressure Waves: Added expanding dust rings traveling through open air.
- Perfect Explosion Symmetry: Fixed logic bugs that caused debris to favor one side of
  the screen.

## [0.6.0] — 2026-03-16
### Added
- Advanced Explosion Effects: Towering smoke columns, glowing fire embers, and lingering
  crater dust.
- Biome Reactions: Steam clouds in freezing temperatures and thick sand dust in deserts.
- Block Debris: Destroying blocks with explosives now generates physical debris particles.
- Enhanced Gunplay Feedback: Bright impact flashes, dynamic spark showers, and scattering
  metal chips when shooting metal.
- Visual Decay: Shrapnel elegantly shrinks and fades away at the end of its lifetime.