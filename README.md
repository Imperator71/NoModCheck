# NoModCheck

An [AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch) patch that stops Aurora 4X's anti-modding checks (added in 3.3) from quietly breaking features when the game runs under AuroraPatch.

## What Aurora does

Aurora 3.3 added five checks. None of them shows a warning; each one silently disables part of the game. NoModCheck tracks the current public Aurora release, 3.3.1, where the checks are the same as in 3.3.0 apart from renamed obfuscated identifiers:

| # | Check (3.3.1 obfuscated name) | Trigger | Effect |
|---|---|---|---|
| 1 | `l4.e()`, called from the time increment with a 1-in-100 roll | Harmony reports any patched Aurora method | For the rest of the session: standing orders stop running, naval commanders are no longer auto-assigned, and the Economics industry "Create" button does nothing |
| 2 | Naval Organization `Load` (`gk.ej`) | `Assembly.GetEntryAssembly()` isn't Aurora | The window opens but its setup is skipped (empty fleet tree) |
| 3 | Economics `Load` (`hx.ey`) | Same as above | The window opens but its setup is skipped (empty population list) |
| 4 | Shipyard "Create Task" (`hx.eb`) | The process file name doesn't end with `Aurora.exe` | The button does nothing |
| 5 | Commanders race selector (`a6.ab`) | Any `AuroraPatch` frame on the call stack | The selector does nothing (empty commander tree) |

Checks 2–5 fire whenever the game is started by AuroraPatch, even with no patches loaded. Check 1 needs at least one Harmony patch on Aurora.

## What NoModCheck does

- **Check 1:** a Harmony prefix makes the detection method return `false`.
- **Checks 2–5:** a transpiler rewrites the calls inside the affected Aurora methods only. The rest of the game is untouched.
  - `GetEntryAssembly()` returns the Aurora assembly.
  - `ProcessModule.FileName` returns the path of `Aurora.exe`.
  - `StackTrace.GetFrames()` returns the frames with AuroraPatch's frames removed.

The targets are found by what their code contains (the `"GetAllPatchedMethods"` string, and calls to those three APIs), not by obfuscated name, so it keeps working when a new build renames them. It worked unchanged when 3.3.1 renamed `l1`/`hu` to `l4`/`hx`. On versions before 3.3 it finds nothing and logs that.

## Install

1. Use an AuroraPatch build that supports 3.3 (see [Aurora-Modders/AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch)).
2. Copy `NoModCheck.dll` into `Patches\NoModCheck\` in your Aurora folder.
3. Start the game with `AuroraPatch.exe`. `AuroraPatch.log` should contain:

```
Patch NoModCheck: l4.e: mod detection -> false
Patch NoModCheck: a6.ab: environment checks redirected
Patch NoModCheck: gk.ej: environment checks redirected
Patch NoModCheck: hx.ey: environment checks redirected
Patch NoModCheck: hx.eb: environment checks redirected
```

## Build

```
dotnet build -c Release -p:AuroraPatchDir=<folder containing AuroraPatch.exe>
```

The build targets .NET Framework 4.8, references `AuroraPatch.exe`, and compiles against Lib.Harmony 2.2.2, the version AuroraPatch ships.

## Testing

Tested on Aurora 3.3.1 (AuroraPatch checksum `9jXVI8`), running a loaded game under AuroraPatch. Checks 2–5 were each tested with NoModCheck turned off and then on (the "without" column is from 3.3.0, which has the same checks; on 3.3.1 all four were confirmed working with NoModCheck on):

| | Without NoModCheck | With NoModCheck |
|---|---|---|
| Naval Organization | Fleet tree empty | Populated |
| Economics | Population list empty | Populated |
| Shipyard Create Task | No response | Reaches the game's own validation |
| Commanders race selector | Commander tree empty | Populated |

For check 1, the log confirms the detection method is patched at load. It hasn't yet been observed through a long enough run of increments for the 1-in-100 roll to fire.

## Please don't report bugs from a patched game

If you hit a bug, reproduce it in an unmodified Aurora before reporting it.
