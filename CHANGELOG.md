# Changelog

All notable changes to the Shrapnel Overhaul Mod will be documented here.

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