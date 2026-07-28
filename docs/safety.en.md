# Safety and Failure Behavior

[简体中文](safety.md) | [English](safety.en.md) | [日本語](safety.ja.md)

## Target identity

Creating a session captures or accepts a target HWND and verifies its PID, executable name/path, and process start time. An empty `allowed_processes` request becomes a one-entry allowlist containing the bound target process name. Every action requires the exact bound HWND to be foreground.

Windows UIPI can prevent a normal-integrity inputd from injecting into an elevated target. `SendInput` may return zero without setting a Win32 error; the backend reports `INPUT_INJECTION_FAILED` and disarms the session.

## Stop conditions

Every condition below cancels pending work, releases state-machine-tracked keys and buttons, clears queues, and revokes the arm lease:

- realtime WebSocket disconnect;
- realtime lease timeout;
- target focus or identity loss;
- bound client exit or PID reuse;
- arm expiration;
- command cancellation or execution failure;
- explicit disarm or emergency release;
- `Ctrl+Alt+F12`;
- graceful shutdown, process-exit callback, or unhandled-exception callback.

An operating-system hard kill, power loss, or runtime corruption cannot be made perfectly recoverable by user-mode software. Process-exit and unhandled-exception handling is best effort and waits up to two seconds for the scheduler thread to emit releases.

## Limits

- command FIFO capacity: 64;
- actions per command: 1–256;
- realtime pending capacity: 1;
- relative delta per axis: ±32768 counts;
- held keys: 32;
- held buttons: 5;
- text: 16,384 UTF-16 code units;
- arm duration: 1–3600 seconds;
- realtime lease: 50–10,000 ms;
- WebSocket message: 64 KiB.
