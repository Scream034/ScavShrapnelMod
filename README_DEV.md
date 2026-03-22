# Shrapnel Overhaul Mod — Developer & Architecture Guide

Welcome under the hood. This document details the technical architecture, network protocol, and algorithms used in the Shrapnel Overhaul Mod (v0.9.2). 

If you are a modder or a C# developer, this guide explains how the mod bypasses Unity's standard limitations to achieve massive scale and perfect multiplayer synchronization.

## 🏗️ Core Architecture & Performance

The mod replaces standard Unity `Instantiate`/`Destroy` calls with a custom engine to achieve zero-GC (Garbage Collection) allocations during steady-state gameplay.

### 1. Zero-GC Particle Engine (`AshParticlePoolManager`)
- **Flat-Array Ring Buffer:** Pre-allocates up to 8,500 custom particle slots (`AshParticlePooled`). Uses `SetActive(false)` for recycling.
- **Frame-Staggered Physics:** Particles simulate quadratic drag ($F \propto v^2$) and thermal lift. Expensive Perlin-noise turbulence is staggered (calculated every 3rd frame with a $\times3$ multiplier) to drastically reduce CPU overhead.
- **GPU-Batched Sparks:** Small, fast sparks bypass GameObjects entirely. They are routed to Unity's `ParticleSystem` (`ParticlePoolManager`) using `RenderMode.Stretch`. This allows thousands of sparks to render with near-zero CPU cost.
- **Shader Self-Healing:** Vanilla Scav has a bug where chunk unloading corrupts `Shader.Find` references. The mod runs a background routine (`HealMaterials`) every 60 frames to detect and repair broken materials on the fly.

### 2. Reflection & Shot Detection (`ShotDetectorPatches`)
Unity 2022.3 Mono JIT occasionally ignores Harmony Transpilers. To ensure 100% reliable shot detection without breaking the game:
- **Hybrid Polling:** Instead of risky IL injection, the mod uses lightweight `MonoBehaviour` polling to detect rising edges on `GunScript.muzzleParticle.isEmitting` and `TurretScript.didShoot`.
- **Dynamic Power Scaling:** Weapon power is extracted via Reflection:  
  `Power = structureDamage * (1 + knockBack/10) * max(1, shotsPerFire)`
  *(v0.9.2 fix: `Convert.ToSingle` is used for type-safe unboxing to prevent `InvalidCastException` on boxed ints).*

## 🌐 Network Protocol v4 (Client Physics Rewrite)

The mod features a custom, server-authoritative network protocol built via Reflection into `Unity.Netcode`. It **does not** require the MP mod to compile or run in singleplayer.

In v0.8.4, the network architecture was completely rewritten to drop bandwidth by ~90%.

### The Elimination of `MSG_SNAPSHOT`
Previous versions sent position updates 10 times a second for every flying shard. **Protocol v4 deletes this entirely.**
1. **`MSG_SPAWN` (Reliable, Batched):** Server sends initial vectors (Position, Velocity, Rotation, Type, Weight, Heat).
2. **Real Client Physics:** Clients create a **real** `Rigidbody2D` and `CircleCollider2D`. The local Unity physics engine handles bouncing and ricochets perfectly.
3. **Damage Gating:** On the client, `IsServerAuthoritative = false` is set. The FSM runs, but collisions with limbs/entities are silently ignored to prevent phantom double-damage.
4. **`MSG_STATE` (Rest Correction):** Due to float drift and block destruction timing, server and client physics may slightly diverge. When a shard stops moving on the server, it sends a final `MSG_STATE`. The client smoothly snaps the shard to this authoritative resting position (`ForceToState`).
5. **`MSG_DESTROY`:** Triggers a 150ms visual fade-out on the client to hide any slight positional desync before deletion.

## 💥 Kinetic Energy Transfer & Geometry Algorithms (v0.9.2)

To make bullet impacts and muzzle blasts look realistic, the mod calculates geometry in real-time.

### 1. Column-Scan Surface Detection
Instead of a slow $O(N^2)$ brute-force grid scan, the mod uses a $O(scanDepth)$ column-scan algorithm for muzzle blasts. 
For each X-offset, it scans downward to find the first Air $\rightarrow$ Solid transition. This guarantees finding the actual exposed ground, ceiling, and walls efficiently, allowing the blast radius to safely increase from 8 to 12 blocks.

### 2. Kinetic Impact Model
When a bullet hits a block, it doesn't just create sparks; it transfers momentum.
- **Directional Plume:** Dust sprays *backward* from the impact point (`-bulletDir`), simulating material blowing out of the entry hole.
- **Material Conductivity:** Metal blocks conduct kinetic energy further (scan radius $\times1.5$). Soft blocks (sand/flesh) absorb energy (radius $\times0.7$, particle density $\times1.4$).
- **SafeBlastDirection:** A dot-product check (`Vector2.Dot`) ensures particles never spray *into* a solid block. If the blast direction opposes the face normal, the vector is reflected.

## 💻 Advanced Console Commands

Open the in-game console (`~`). Commands support flexible, order-independent arguments.

### `shrapnel_explode`
Creates an explosion with full shrapnel effects.
`shrapnel_explode [type] [origin] [-e][-net]`
* `type`: `mine`, `dynamite`, `turret`, `gravbag`
* `-e`: Effects only (no block destruction)
* `-net`: Print network diagnostics after execution.

### `shrapnel_shot`
Tests the full bullet effect pipeline at the cursor position.
`shrapnel_shot [preset|power] [L|R|U|D] [-metal][muzzle|impact|sparks]`
* `preset`: `pistol`, `rifle`, `shotgun`, `turret`
* `direction`: `L`, `R`, `U`, `D` (Bullet travel direction)
* `-metal`: Forces the system to treat the impact surface as metal.
* *Example:* `shrapnel_shot rifle R -metal` (Simulates a rifle shot from the right hitting metal).

### `shrapnel_guninfo [all]`
Dumps `GunScript` fields of the currently held weapon via Reflection to diagnose power calculation issues.

### `shrapnel_highlight [seconds]`
Sets all physics shards to top sorting order and applies a bright orange glow. Useful for debugging shards clipping into terrain. (Default: 10s. Use `0` to disable).

### `shrapnel_status [mat | full]`
* `(none)`: Shows Pool counts, active particles, and MP role.
* `mat`: Tests material/shader state (useful for debugging Unity chunk-unload shader corruption).
* `full`: Dumps all current config values.

### `shrapnel_net [diag]`
Dumps detailed network sync diagnostics. `diag` provides a step-by-step check of why fragments might not be syncing to clients.