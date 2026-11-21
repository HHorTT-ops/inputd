using System.Text.Json;

namespace InputExecutor.Windows.RemoteInputDiagnostics;

public static class DiagnosticsCorrelation
{
    public const long DefaultTimeWindowUs = 20_000;
    public const int PositionTolerancePx = 8;
    public const int DeltaTolerancePx = 12;

    public sealed record RawEvent(
        long TimestampUs,
        string? HDevice,
        bool Absolute,
        int Dx,
        int Dy,
        int? ScreenX,
        int? ScreenY,
        ushort ButtonFlags,
        string Kind,
        int? ScreenXMetrics = null,
        int? ScreenYMetrics = null);

    public sealed record HookEvent(
        long TimestampUs,
        string Message,
        int PtX,
        int PtY,
        bool Injected);

    public sealed record PairResult(
        string Status, // MATCHED | AMBIGUOUS | UNMATCHED_RAW
        RawEvent Raw,
        HookEvent? Hook,
        string? Reason);

    public static object BuildReport(
        IReadOnlyList<RawEvent> rawEvents,
        IReadOnlyList<HookEvent> hookEvents,
        string stimulusLabel,
        DiagnosticsStats stats)
    {
        var pairs = MatchOneToOne(rawEvents, hookEvents);
        var matched = pairs.Count(p => p.Status == "MATCHED");
        var ambiguous = pairs.Count(p => p.Status == "AMBIGUOUS");
        var unmatched = pairs.Count(p => p.Status == "UNMATCHED_RAW");
        var matchedNearInjected = pairs.Count(p =>
            p.Status == "MATCHED" && p.Hook is { Injected: true });

        return new
        {
            stimulus_label = stimulusLabel,
            note = "Results are scoped to this stimulus only; do not attribute TEST E/F findings to UU.",
            raw_events = rawEvents.Count,
            hook_events = hookEvents.Count,
            matched_one_to_one = matched,
            ambiguous = ambiguous,
            unmatched_raw = unmatched,
            matched_near_injected_hook = matchedNearInjected,
            stats,
            sample_pairs = pairs.Take(12).Select(p => new
            {
                p.Status,
                p.Reason,
                raw = new
                {
                    p.Raw.TimestampUs,
                    p.Raw.Kind,
                    p.Raw.HDevice,
                    p.Raw.Absolute,
                    p.Raw.Dx,
                    p.Raw.Dy,
                    p.Raw.ScreenX,
                    p.Raw.ScreenY,
                    p.Raw.ScreenXMetrics,
                    p.Raw.ScreenYMetrics,
                    p.Raw.ButtonFlags
                },
                hook = p.Hook is null ? null : new { p.Hook.TimestampUs, p.Hook.Message, p.Hook.PtX, p.Hook.PtY, p.Hook.Injected }
            }),
            answers_preliminary = new
            {
                scoped_to_stimulus = stimulusLabel,
                llmhf_injected_seen_in_matches = matchedNearInjected > 0,
                hDevice_always_zero = stats.RawHDeviceNonZero == 0 && stats.RawEvents > 0,
                absolute_vs_relative = new { absolute = stats.RawAbsolute, relative = stats.RawRelative },
                correlation_passed = matched > 0 && unmatched == 0 && ambiguous == 0,
                note = "Cross-check only within this stimulus; do not conclude from a single metric. correlation_passed requires all raw events matched uniquely."
            }
        };
    }

    public static List<PairResult> MatchOneToOne(
        IReadOnlyList<RawEvent> rawEvents,
        IReadOnlyList<HookEvent> hookEvents,
        long timeWindowUs = DefaultTimeWindowUs,
        CoordinateSpaceConverter? aligner = null)
    {
        var usedHooks = new HashSet<int>();
        var results = new List<PairResult>(rawEvents.Count);

        for (var ri = 0; ri < rawEvents.Count; ri++)
        {
            var raw = rawEvents[ri];
            var candidates = new List<(int Index, int Score, string Reason)>();
            for (var hi = 0; hi < hookEvents.Count; hi++)
            {
                if (usedHooks.Contains(hi)) continue;
                var hook = hookEvents[hi];
                if (Math.Abs(hook.TimestampUs - raw.TimestampUs) > timeWindowUs) continue;
                if (!KindCompatible(raw.Kind, hook.Message)) continue;

                var score = 0;
                var reasons = new List<string> { "time", "kind" };

                var (rx, ry) = ResolveRawPositionForMatch(raw, aligner);
                if (rx is int sx && ry is int sy)
                {
                    var dist = Math.Abs(hook.PtX - sx) + Math.Abs(hook.PtY - sy);
                    if (dist <= PositionTolerancePx)
                    {
                        score += 2;
                        reasons.Add("position");
                    }
                    else if (dist > PositionTolerancePx * 4)
                    {
                        continue;
                    }
                }

                if (raw.Kind == "move" && (raw.Dx != 0 || raw.Dy != 0 || raw.Absolute))
                {
                    score += 1;
                    reasons.Add(raw.Absolute ? "absolute_move" : "delta_present");
                }

                candidates.Add((hi, score, string.Join("+", reasons)));
            }

            if (candidates.Count == 0)
            {
                results.Add(new PairResult("UNMATCHED_RAW", raw, null, "no_compatible_hook_in_window"));
                continue;
            }

            var bestScore = candidates.Max(c => c.Score);
            var best = candidates.Where(c => c.Score == bestScore).ToList();
            if (best.Count != 1)
            {
                results.Add(new PairResult("AMBIGUOUS", raw, null, $"tied_candidates={best.Count};score={bestScore}"));
                continue;
            }

            var chosen = best[0];
            usedHooks.Add(chosen.Index);
            results.Add(new PairResult("MATCHED", raw, hookEvents[chosen.Index], chosen.Reason));
        }

        return results;
    }

    /// <summary>
    /// Prefer already hook-aligned ScreenX/Y; otherwise convert metrics coords if an aligner is provided.
    /// </summary>
    public static (int? X, int? Y) ResolveRawPositionForMatch(RawEvent raw, CoordinateSpaceConverter? aligner)
    {
        if (raw.ScreenX is int sx && raw.ScreenY is int sy)
            return (sx, sy);
        if (aligner is not null && raw.ScreenXMetrics is int mx && raw.ScreenYMetrics is int my)
        {
            var aligned = aligner.MetricsToHookSpace(mx, my);
            return (aligned.X, aligned.Y);
        }
        return (null, null);
    }

    /// <summary>
    /// Absolute packets (MOUSE_MOVE_ABSOLUTE) are moves even when dx=dy=0.
    /// Button flags always win over move classification.
    /// </summary>
    public static string ClassifyRawKind(ushort buttonFlags, int dx, int dy, bool absolute = false)
    {
        const ushort wheel = 0x0400, hwheel = 0x0800;
        const ushort leftDown = 0x0001, leftUp = 0x0002;
        const ushort rightDown = 0x0004, rightUp = 0x0008;
        const ushort middleDown = 0x0010, middleUp = 0x0020;
        if ((buttonFlags & wheel) != 0) return "wheel";
        if ((buttonFlags & hwheel) != 0) return "hwheel";
        if ((buttonFlags & (leftDown | leftUp)) != 0) return "left_button";
        if ((buttonFlags & (rightDown | rightUp)) != 0) return "right_button";
        if ((buttonFlags & (middleDown | middleUp)) != 0) return "middle_button";
        if (absolute || dx != 0 || dy != 0) return "move";
        return "other";
    }

    /// <summary>
    /// Only explicitly compatible kinds may correlate. "other" never matches any hook message.
    /// </summary>
    public static bool KindCompatible(string rawKind, string hookMessage) => rawKind switch
    {
        "move" => hookMessage is "WM_MOUSEMOVE",
        "left_button" => hookMessage is "WM_LBUTTONDOWN" or "WM_LBUTTONUP",
        "right_button" => hookMessage is "WM_RBUTTONDOWN" or "WM_RBUTTONUP",
        "middle_button" => hookMessage is "WM_MBUTTONDOWN" or "WM_MBUTTONUP",
        "wheel" => hookMessage is "WM_MOUSEWHEEL",
        "hwheel" => hookMessage is "WM_MOUSEHWHEEL",
        _ => false
    };

    public static RawEvent? TryParseRaw(JsonElement el)
    {
        try
        {
            var ts = el.GetProperty("timestamp_us").GetInt64();
            var dx = el.TryGetProperty("dx", out var dxEl) ? dxEl.GetInt32() : 0;
            var dy = el.TryGetProperty("dy", out var dyEl) ? dyEl.GetInt32() : 0;
            var flags = el.TryGetProperty("usButtonFlags", out var bf) ? bf.GetUInt16() : (ushort)0;
            var absolute = el.TryGetProperty("mouse_move_absolute", out var abs) && abs.GetBoolean();
            return new RawEvent(
                ts,
                el.TryGetProperty("hDevice", out var hd) ? hd.GetString() : null,
                absolute,
                dx,
                dy,
                el.TryGetProperty("screen_x", out var sx) && sx.ValueKind != JsonValueKind.Null ? sx.GetInt32() : null,
                el.TryGetProperty("screen_y", out var sy) && sy.ValueKind != JsonValueKind.Null ? sy.GetInt32() : null,
                flags,
                ClassifyRawKind(flags, dx, dy, absolute),
                el.TryGetProperty("screen_x_metrics", out var sxm) && sxm.ValueKind != JsonValueKind.Null ? sxm.GetInt32() : null,
                el.TryGetProperty("screen_y_metrics", out var sym) && sym.ValueKind != JsonValueKind.Null ? sym.GetInt32() : null);
        }
        catch { return null; }
    }

    public static HookEvent? TryParseHook(JsonElement el)
    {
        try
        {
            return new HookEvent(
                el.GetProperty("timestamp_us").GetInt64(),
                el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                el.TryGetProperty("pt_x", out var x) ? x.GetInt32() : 0,
                el.TryGetProperty("pt_y", out var y) ? y.GetInt32() : 0,
                el.TryGetProperty("llmhf_injected", out var inj) && inj.GetBoolean());
        }
        catch { return null; }
    }
}
