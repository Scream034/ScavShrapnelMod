# ScavShrapnelMod — Build & Usage Guide

> **BUILD.md version:** `0.8.4`

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Setting up Libraries](#setting-up-libraries)
3. [Configuring Game Path](#configuring-game-path)
4. [Building](#building)
5. [Manual Install](#manual-install)
6. [Game Version Compatibility](#game-version-compatibility)
7. [Console Commands](#console-commands)

---

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| VS Code | 1.80+ | With C# Dev Kit extension |
| .NET Framework SDK | 4.8 | [Download](https://dotnet.microsoft.com/download/dotnet-framework/net48) |
| MSBuild | 17.x+ | Included with VS Build Tools or Visual Studio |
| Game | Casualties Unknown | Any supported version (see [compatibility](#game-version-compatibility)) |
| BepInEx | 5.4.x | Installed in game folder |

### Required VS Code Extensions

Recommended extensions are listed in `.vscode/extensions.json` and prompted on first open:

| Extension | Purpose |
|-----------|---------|
| `ms-dotnettools.csdevkit` | C# language support, IntelliSense |
| `ms-dotnettools.csharp` | OmniSharp C# engine |
| `redhat.vscode-xml` | XML/csproj editing |

### MSBuild in PATH

The build task uses `msbuild` from the shell. If it's not found:

**Option A** — Use VS Developer Command Prompt as VS Code terminal:
```
Menu → Terminal → New Terminal → Select "Developer Command Prompt"
```

**Option B** — Add MSBuild to your system PATH:
```
C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin
```
or (Build Tools only):
```
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin
```

## Configuring Game Path

The post-build task copies `ScavShrapnelMod.dll` to `<GameDir>\BepInEx\plugins\`.  
The game path must be set before building.

### Option A — `Directory.Build.props` (checked in, team default)

Edit the default in `Directory.Build.props`:
```xml
<GameDir Condition=" '$(GameDir)' == '' ">D:\Games\Scav</GameDir>
```
This applies to everyone who doesn't override it.

### Option B — `ScavShrapnelMod.csproj.user` (git-ignored, recommended)

Create `ScavShrapnelMod.csproj.user` next to the csproj:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <GameDir>D:\SteamLibrary\steamapps\common\CasualtiesUnknown</GameDir>
  </PropertyGroup>
</Project>
```

### Option C — Environment variable

```cmd
set GameDir=D:\Games\Scav
```
MSBuild picks up environment variables as properties.

### Also update VS Code settings

Edit `.vscode/settings.json` to match your game path:
```json
{
    "scavGame.path": "D:\\Games\\Scav\\CasualtiesUnknown.exe",
    "scavGame.dir": "D:\\Games\\Scav"
}
```
These are used by the `launch-game` task.

---

## Building

### Debug build (Ctrl+Shift+B)

The default VS Code build task runs:
```
dotnet build ScavShrapnelMod.csproj /p:Configuration=Debug
```

- Full PDB symbols, no optimization
- DLL auto-deployed to game plugins folder
- Console output shows deploy path or missing-directory warning

### Release build

From VS Code Command Palette (`Ctrl+Shift+P`):
```
Tasks: Run Task → build-release
```

Or from terminal:
```
dotnet build ScavShrapnelMod.csproj /p:Configuration=Release
```

- Optimized, no debug symbols
- Output: `bin\Release\ScavShrapnelMod.dll`

### Build and launch game

From Command Palette:
```
Tasks: Run Task → build-and-launch
```

Builds in Debug, then starts the game executable.

### If `dotnet` is not found

Install the [.NET SDK](https://dotnet.microsoft.com/download) (any version 6+).
The `dotnet build` command can build .NET Framework 4.8 projects when the targeting pack is installed.

### Build and launch game

From Command Palette:
```
Tasks: Run Task → build-and-launch
```

Builds in Debug, then starts the game executable.

### Post-build behavior

| Condition | Result |
|-----------|--------|
| `<GameDir>\BepInEx\plugins\` exists | DLL copied, success message |
| Directory doesn't exist | Build succeeds, warning printed |
| DLL unchanged since last copy | Skip (SkipUnchangedFiles) |

---

## Manual Install

Copy the built DLL to:
```
<GameDir>\BepInEx\plugins\ScavShrapnelMod.dll
```

No other files required. Config is generated on first launch.

---

## Game Version Compatibility

The mod detects the game version at startup by reading the version label from the main menu UI.

| Game Version | Code | Status |
|-------------|------|--------|
| 5.1 | `5.1` | Tested |
| V5 Pre-testing 5 | `v5p5` | Tested |
| V5 Pre-testing 4 | `v5p4` | Tested |
| v5 release | `v5d` | Tested |
| Unknown / newer | — | ⚠️ Warning, mod still loads |

### What happens on unsupported version

1. Warning in BepInEx log: `[Version] WARNING: Game version 'X' is not tested...`
2. Warning in in-game console on first explosion
3. **Mod continues to run** — no hard block, graceful degradation
4. Report issues if effects break on new versions

### Adding support for new versions

Edit `Helpers/GameVersionChecker.cs`:
```csharp
private static readonly (string ObjectName, string LabelContains, string Code)[]
    KnownVersions =
{
    ("VersionObjectName", "Version Label Text", "code"),
    // ... existing entries
};

private static readonly string[] SupportedCodes = { "code", /* ... */ };
```

---

## Configuration

### File location

Auto-generated on first run:
```
<GameDir>\BepInEx\config\ScavShrapnelMod.cfg
```

Every parameter is documented in the config file itself.

### Automatic migration

When the mod version changes:
1. Old config backed up → `ScavShrapnelMod.cfg.backup.<oldversion>`
2. Old config deleted
3. Fresh config generated with new defaults
4. Notification shown in console on first load

---

## Console Commands

Open the in-game console (`~`). All commands support flexible, order-independent arguments.

### `shrapnel_explode`

Creates an explosion with full shrapnel effects.

```
shrapnel_explode [type] [origin] [-e] [-net]
```

| Argument | Values | Default | Description |
|----------|--------|---------|-------------|
| type | `mine` `dynamite` `turret` `gravbag` | `mine` | Explosion profile |
| origin | `player` `cursor` | `cursor` | Spawn location |
| `-e` | — | — | Effects only (no block destruction) |
| `-net` | — | — | Print network diagnostics after |

```
shrapnel_explode dynamite player -e    # visual dynamite at player
shrapnel_explode turret -net           # real turret explosion + net info
shrapnel_explode                       # mine at cursor (default)
```

### `shrapnel_debris`

Spawns physics shrapnel fragments at cursor.

```
shrapnel_debris [count] [force] [type] [-v] [-net]
```

| Argument | Values | Default | Description |
|----------|--------|---------|-------------|
| count | `1`–`100` | `5` | Number of fragments |
| force | float | `0` | Launch force |
| type | `metal` `stone` `heavy` `wood` `electronic` `all` | `metal` | Material type |
| `-v` | — | — | Verbose: log each fragment |
| `-net` | — | — | Print network status after |

```
shrapnel_debris 10 50 wood -v     # 10 wood fragments, force 50, verbose
shrapnel_debris all               # cycle all types and weights
shrapnel_debris 20 metal -net     # 20 metal with net diagnostics
```

### `shrapnel_clear`

Destroys all active physics shrapnel and clears particle pools.

### `shrapnel_status`

Brief performance overview.

```
v0.8.4 | Phys:42 Lit:1200/6000 Unlit:300/2500 Spark:50 | Total: 1592 | MP:HOST | NET:SERVER tracked=42
```

### `shrapnel_net`

Detailed network sync diagnostics: MP detection, role, tracked/mirror counts, message counters, NGO reflection state.

### `shrapnel_testmat`

Checks for shader/material corruption from vanilla chunk unloading. Reports Lit/Unlit/Trail material status, shader names, and count of corrupted SpriteRenderers in scene.