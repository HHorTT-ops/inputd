# Relative Mouse Replay Backends

[简体中文](raw-input-replay.md) | [English](raw-input-replay.en.md) | [日本語](raw-input-replay.ja.md)

## Why the desktop pointer moves while the game camera does not

Recorded mouse `dx/dy` values are physical Raw Input counts. Relative Windows `SendInput` can move the desktop pointer and can be observed precisely by an ordinary Raw Input window, but some games do not use that synthetic relative path for camera motion.

Dashboard replay therefore uses Win32 `mouse_event` for relative movement by default while preserving the same signed counts, relative timing, and held state. Compatibility must still be verified in an offline or explicitly permitted target.

The player may combine adjacent high-polling-rate mouse packets with identical held state and at most 2 ms total spacing. It preserves every source event's exact signed `dx/dy/wheel` sum and prevents individual `±1` counts from being rounded away by Windows pointer speed. It cannot change a game's policy for accepting synthetic input sources.

## Dashboard modes

- **Kernel mouse compatibility unchecked (default):** `WindowsLegacyMouseReplayBackend` handles relative movement; `WindowsSendInputBackend` handles every other action without the executor event tag. This is the recommended driver-free path.
- **Kernel mouse compatibility checked:** `WindowsInterceptionInputBackend` handles relative movement, mouse buttons, and wheel for explicit compatibility tests. If the runtime, driver, mouse device, or driver report is unavailable, playback fails before any input is injected.

Every backend remains serialized by the dedicated `InputScheduler`. Stopping playback, focus loss, target exit, application shutdown, or emergency stop clears the queue and calls `release_all`.

## Interception deployment boundary

The repository contains only an adapter. It does not bundle or automatically install the third-party `Interception` kernel driver or DLL. Before enabling it:

1. the user must explicitly approve installing a third-party mouse-class filter driver;
2. install it with administrator privileges and restart when required;
3. place the matching x64 `interception.dll` next to `inputd-dashboard.exe`;
4. reopen the Dashboard and verify the option no longer reports “not installed.”

`DefaultDllImportSearchPaths(ApplicationDirectory)` restricts loading to the application directory; the current directory and `PATH` are not searched. An unavailable driver never falls back to a mode that would report a false game-replay success.

The public Interception release is old and its commercial and non-commercial licensing terms differ. Review its source, release files, and license yourself. Protected online games may prohibit filter drivers or block them through anti-cheat. inputd provides no anti-cheat bypass; use it only where permitted.

## Long-term production direction

A controllable long-term option is a dedicated, signed Windows Virtual HID Framework (VHF) driver for inputd. A VHF HID source runs in kernel mode and requires the WDK, driver signing, an installer, and reboot testing. This can reduce third-party dependencies, but it is not an ordinary user-mode update that may modify the system without explicit authorization.
