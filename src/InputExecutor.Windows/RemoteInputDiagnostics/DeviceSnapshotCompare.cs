using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

/// <summary>
/// Compares Raw Input device lists before/after UU connect.
/// VHF_DEVICE_CANDIDATE is never asserted as UU ownership.
/// </summary>
public static class DeviceSnapshotCompare
{
    public static object Compare(SessionEnvironmentSnapshot before, SessionEnvironmentSnapshot after)
    {
        var beforeByHandle = before.RawInputDevices.ToDictionary(d => d.DeviceHandle, StringComparer.OrdinalIgnoreCase);
        var afterByHandle = after.RawInputDevices.ToDictionary(d => d.DeviceHandle, StringComparer.OrdinalIgnoreCase);

        var added = after.RawInputDevices
            .Where(d => !beforeByHandle.ContainsKey(d.DeviceHandle))
            .Select(Describe)
            .ToList();
        var removed = before.RawInputDevices
            .Where(d => !afterByHandle.ContainsKey(d.DeviceHandle))
            .Select(Describe)
            .ToList();
        var changed = new List<object>();
        foreach (var afterDev in after.RawInputDevices)
        {
            if (!beforeByHandle.TryGetValue(afterDev.DeviceHandle, out var beforeDev)) continue;
            if (!SameIdentity(beforeDev, afterDev))
            {
                changed.Add(new
                {
                    device_handle = afterDev.DeviceHandle,
                    before = Describe(beforeDev),
                    after = Describe(afterDev)
                });
            }
        }

        return new
        {
            compared_at_utc = DateTimeOffset.UtcNow.ToString("o"),
            before_captured_at_utc = before.CapturedAtUtc,
            after_captured_at_utc = after.CapturedAtUtc,
            before_count = before.RawInputDevices.Count,
            after_count = after.RawInputDevices.Count,
            added,
            removed,
            changed,
            caveat = "Added/removed/changed devices are correlation candidates only. VHF_DEVICE_CANDIDATE / VIRTUAL_HID_CANDIDATE must NOT be asserted as NetEase UU ownership."
        };
    }

    public static object CompareFiles(string beforeJsonPath, string afterJsonPath)
    {
        var before = LoadSnapshot(beforeJsonPath);
        var after = LoadSnapshot(afterJsonPath);
        return Compare(before, after);
    }

    public static SessionEnvironmentSnapshot CaptureDevicesOnly() => SessionEnvironmentSnapshot.Capture();

    private static SessionEnvironmentSnapshot LoadSnapshot(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var devices = new List<RawInputDeviceInfoDto>();
        if (root.TryGetProperty("raw_input_devices", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                devices.Add(new RawInputDeviceInfoDto
                {
                    DeviceHandle = el.TryGetProperty("device_handle", out var h) ? h.GetString() ?? "0x0" : "0x0",
                    DeviceType = el.TryGetProperty("device_type", out var t) ? t.GetString() ?? "" : "",
                    DeviceNameRedacted = el.TryGetProperty("device_name_redacted", out var n) ? n.GetString() : null,
                    CandidateClass = el.TryGetProperty("candidate_class", out var c) ? c.GetString() ?? DeviceCandidateClass.Unknown : DeviceCandidateClass.Unknown,
                    LooksLikeVirtualHid = el.TryGetProperty("looks_like_virtual_hid", out var v) && v.GetBoolean(),
                    HidUsagePage = el.TryGetProperty("hid_usage_page", out var up) && up.ValueKind != JsonValueKind.Null ? up.GetUInt16() : null,
                    HidUsage = el.TryGetProperty("hid_usage", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetUInt16() : null,
                    VendorId = el.TryGetProperty("vendor_id", out var vid) && vid.ValueKind != JsonValueKind.Null ? vid.GetUInt32() : null,
                    ProductId = el.TryGetProperty("product_id", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetUInt32() : null
                });
            }
        }

        return new SessionEnvironmentSnapshot
        {
            CapturedAtUtc = root.TryGetProperty("captured_at_utc", out var at) ? at.GetString() ?? "" : "",
            VirtualScreen = new { },
            PrimaryScreen = new { },
            RawInputDevices = devices
        };
    }

    private static bool SameIdentity(RawInputDeviceInfoDto a, RawInputDeviceInfoDto b) =>
        string.Equals(a.DeviceNameRedacted, b.DeviceNameRedacted, StringComparison.OrdinalIgnoreCase)
        && a.DeviceType == b.DeviceType
        && a.HidUsagePage == b.HidUsagePage
        && a.HidUsage == b.HidUsage
        && a.VendorId == b.VendorId
        && a.ProductId == b.ProductId
        && a.CandidateClass == b.CandidateClass;

    private static object Describe(RawInputDeviceInfoDto d) => new
    {
        d.DeviceHandle,
        d.DeviceType,
        d.DeviceNameRedacted,
        d.CandidateClass,
        d.HidUsagePage,
        d.HidUsage,
        d.VendorId,
        d.ProductId
    };
}

/// <summary>
/// Post-hoc verdicts for Founder interactive stimuli — never marks PASS without evidence.
/// </summary>
public static class StimulusVerdict
{
    public const int MinDragMovePx = 200;

    public sealed record Verdict(string Status, string Reason, object Evidence);

    public static Verdict Evaluate(string label, IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var normalized = label.ToLowerInvariant();
        if (normalized.Contains("test-a") || normalized.Contains("uu-move"))
            return EvaluateMoveOnly(raw, hooks);
        if (normalized.Contains("test-b2") || normalized.Contains("right-click"))
            return EvaluateRightClick(raw, hooks);
        if (normalized.Contains("test-b") && normalized.Contains("left"))
            return EvaluateLeftClick(raw, hooks);
        if (normalized.Contains("test-b") && normalized.Contains("right"))
            return EvaluateRightClick(raw, hooks);
        if (normalized.Contains("test-b"))
            return EvaluateClicksCombined(raw, hooks);
        if (normalized.Contains("test-c") && normalized.Contains("hwheel"))
            return EvaluateHWheel(raw, hooks);
        if (normalized.Contains("test-c") && normalized.Contains("wheel") && !normalized.Contains("h"))
            return EvaluateVWheel(raw, hooks);
        if (normalized.Contains("test-c"))
            return EvaluateWheelCombined(raw, hooks);
        if (normalized.Contains("test-d") || normalized.Contains("drag"))
            return EvaluateDrag(raw, hooks);
        if (normalized.Contains("test-g") || normalized.Contains("console-physical"))
            return EvaluateConsolePhysical(raw, hooks);
        return new Verdict("NO_RULE", "No stimulus verdict rule for label", new { label, raw = raw.Count, hooks = hooks.Count });
    }

    private static Verdict EvaluateMoveOnly(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var moves = hooks.Count(h => h.Message == "WM_MOUSEMOVE");
        var wheels = hooks.Count(h => h.Message is "WM_MOUSEWHEEL" or "WM_MOUSEHWHEEL");
        var buttons = hooks.Count(h => h.Message.Contains("BUTTON", StringComparison.Ordinal));
        if (raw.Count == 0 && hooks.Count == 0)
            return Fail("ZERO_EVENTS", "No events — cannot PASS move-only");
        if (wheels > 0 || buttons > 0)
            return Fail("CONTAMINATED", $"Move-only requires no wheel/click; wheels={wheels} buttons={buttons} moves={moves}");
        if (moves == 0 && raw.All(r => r.Kind != "move"))
            return Fail("NO_MOVE", "No WM_MOUSEMOVE / raw move observed");
        return Pass("Move-only clean", new { moves, wheels, buttons, raw = raw.Count });
    }

    private static Verdict EvaluateLeftClick(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var down = hooks.Count(h => h.Message == "WM_LBUTTONDOWN");
        var up = hooks.Count(h => h.Message == "WM_LBUTTONUP");
        if (down == 0 || up == 0)
            return Fail("MISSING_LEFT", $"Need LBUTTON down+up; down={down} up={up}");
        return Pass("Left click observed", new { down, up, raw_left = raw.Count(r => r.Kind == "left_button") });
    }

    private static Verdict EvaluateRightClick(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var down = hooks.Count(h => h.Message == "WM_RBUTTONDOWN");
        var up = hooks.Count(h => h.Message == "WM_RBUTTONUP");
        if (down == 0 || up == 0)
            return Fail("MISSING_RIGHT", $"Need RBUTTON down+up; down={down} up={up}");
        return Pass("Right click observed", new { down, up, raw_right = raw.Count(r => r.Kind == "right_button") });
    }

    private static Verdict EvaluateClicksCombined(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var left = EvaluateLeftClick(raw, hooks);
        var right = EvaluateRightClick(raw, hooks);
        if (left.Status != "PASS" || right.Status != "PASS")
            return Fail("INCOMPLETE_CLICKS", $"left={left.Status}:{left.Reason}; right={right.Status}:{right.Reason}");
        return Pass("Left and right clicks observed", new { left = left.Evidence, right = right.Evidence });
    }

    private static Verdict EvaluateVWheel(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var n = hooks.Count(h => h.Message == "WM_MOUSEWHEEL");
        if (n == 0) return Fail("NO_VWHEEL", "No WM_MOUSEWHEEL");
        return Pass("Vertical wheel observed", new { n, raw_wheel = raw.Count(r => r.Kind == "wheel") });
    }

    private static Verdict EvaluateHWheel(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var n = hooks.Count(h => h.Message == "WM_MOUSEHWHEEL");
        if (n == 0) return Fail("NO_HWHEEL", "No WM_MOUSEHWHEEL");
        return Pass("Horizontal wheel observed", new { n, raw_hwheel = raw.Count(r => r.Kind == "hwheel") });
    }

    private static Verdict EvaluateWheelCombined(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var v = EvaluateVWheel(raw, hooks);
        var h = EvaluateHWheel(raw, hooks);
        if (v.Status != "PASS" || h.Status != "PASS")
            return Fail("INCOMPLETE_WHEEL", $"v={v.Status}:{v.Reason}; h={h.Status}:{h.Reason}");
        return Pass("Vertical and horizontal wheel observed", new { v = v.Evidence, h = h.Evidence });
    }

    private static Verdict EvaluateDrag(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        var downIdx = -1;
        var upIdx = -1;
        for (var i = 0; i < hooks.Count; i++)
        {
            if (downIdx < 0 && hooks[i].Message == "WM_LBUTTONDOWN") downIdx = i;
            if (hooks[i].Message == "WM_LBUTTONUP") upIdx = i;
        }
        if (downIdx < 0 || upIdx < 0 || upIdx <= downIdx)
            return Fail("NO_DRAG_BUTTONS", $"Need LBUTTON down then up; downIdx={downIdx} upIdx={upIdx}");

        var moves = new List<DiagnosticsCorrelation.HookEvent>();
        for (var i = downIdx + 1; i < upIdx; i++)
        {
            if (hooks[i].Message == "WM_MOUSEMOVE")
                moves.Add(hooks[i]);
        }
        if (moves.Count == 0)
            return Fail("NO_MOVE_BETWEEN_BUTTONS", "Drag requires WM_MOUSEMOVE between LBUTTONDOWN and LBUTTONUP");

        var maxDist = 0;
        var origin = hooks[downIdx];
        foreach (var m in moves)
        {
            var dist = Math.Abs(m.PtX - origin.PtX) + Math.Abs(m.PtY - origin.PtY);
            if (dist > maxDist) maxDist = dist;
        }
        if (maxDist < MinDragMovePx)
            return Fail("DRAG_TOO_SHORT", $"Need ≥{MinDragMovePx}px Manhattan travel while held; observed={maxDist}");

        return Pass("Drag with motion between down/up", new { moves_between = moves.Count, max_manhattan_px = maxDist });
    }

    private static Verdict EvaluateConsolePhysical(IReadOnlyList<DiagnosticsCorrelation.RawEvent> raw, IReadOnlyList<DiagnosticsCorrelation.HookEvent> hooks)
    {
        if (raw.Count == 0 && hooks.Count == 0)
            return Fail("ZERO_EVENTS", "No console physical events — use NOT_RUN if cannot operate local mouse");
        var nonZero = raw.Count(r => r.HDevice is not null && r.HDevice != "0x0");
        return Pass("Console physical activity observed", new
        {
            raw = raw.Count,
            hooks = hooks.Count,
            raw_hdevice_nonzero = nonZero,
            note = "Non-zero hDevice suggests device-attributed path; still do not over-claim"
        });
    }

    private static Verdict Pass(string reason, object evidence) => new("PASS", reason, evidence);
    private static Verdict Fail(string code, string reason) => new("FAIL", $"{code}: {reason}", new { code });
}
