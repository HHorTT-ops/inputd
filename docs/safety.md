# Safety and failure behavior

## Target identity

Creating a session captures or accepts a target HWND and verifies its PID, executable name/path, and process start time. An empty `allowed_processes` request becomes a one-entry allowlist containing the bound target name. Every action requires the exact target HWND to be foreground.

UIPI can prevent a normal-integrity `inputd` from injecting into an elevated target. `SendInput` may report zero without a Win32 error in that case; the backend returns `INPUT_INJECTION_FAILED` and disarms.

## Stop conditions

All of these cancel pending work, release tracked keys/buttons, clear queues, and revoke the arm lease:

- realtime WebSocket disconnect;
- realtime lease timeout;
- target focus/identity loss;
- bound client process exit or PID reuse;
- arm expiration;
- command cancellation or execution failure;
- explicit disarm or emergency release;
- `Ctrl+Alt+F12`;
- graceful application shutdown, process-exit callback, or unhandled-exception callback.

An operating-system hard kill, power loss, or runtime corruption cannot be made perfectly recoverable by user-mode software. The process-exit and unhandled-exception paths are best effort and wait up to two seconds for the scheduler thread to emit releases.

## Limits

- command FIFO capacity: 64;
- command actions: 1–256;
- realtime pending capacity: 1;
- relative delta: ±32768 counts per axis;
- held keys: 32;
- held buttons: 5;
- text: 16,384 UTF-16 code units;
- arm duration: 1–3600 seconds;
- realtime lease: 50–10,000 ms;
- WebSocket message: 64 KiB.
