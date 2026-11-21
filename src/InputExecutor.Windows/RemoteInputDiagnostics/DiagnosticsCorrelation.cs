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
        // DPI APIs may report identity while UU absolute Raw maps ~½ of WH_MOUSE_LL pt.
        // Prefer an empirical Raw↔Hook scale measured from same-timestamp pairs (never hardcode 2.0).
        var empirical = EstimateScaleFromAbsoluteRawAndHooks(rawEvents, hookEvents);
        var aligner = empirical.Converter;
        var pairs = MatchOneToOne(rawEvents, hookEvents, aligner: aligner);
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
            empirical_alignment = empirical.Describe,
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
                empirical_scale_x = empirical.Converter?.ScaleX,
                empirical_scale_y = empirical.Converter?.ScaleY,
                note = "Cross-check only within this stimulus; do not conclude from a single metric. correlation_passed requires all raw events matched uniquely."
            }
        };
    }

    /// <summary>
    /// Measures Hook.pt / Raw.screen from near-simultaneous absolute-move pairs.
    /// Used when process DPI APIs claim identity but Raw and LL hook disagree (observed ~2:1 under UU).
    /// </summary>
    public static (CoordinateSpaceConverter? Converter, object Describe) EstimateScaleFromAbsoluteRawAndHooks(
        IReadOnlyList<RawEvent> rawEvents,
        IReadOnlyList<HookEvent> hookEvents,
        long timeWindowUs = DefaultTimeWindowUs)
    {
        var ratiosX = new List<double>();
        var ratiosY = new List<double>();
        foreach (var raw in rawEvents)
        {
            if (!raw.Absolute) continue;
            var sx = raw.ScreenX ?? raw.ScreenXMetrics;
            var sy = raw.ScreenY ?? raw.ScreenYMetrics;
            if (sx is null or 0 || sy is null or 0) continue;
            if (Math.Abs(sx.Value) < 32 || Math.Abs(sy.Value) < 32) continue;

            foreach (var hook in hookEvents)
            {
                if (hook.Message is not "WM_MOUSEMOVE") continue;
                if (Math.Abs(hook.TimestampUs - raw.TimestampUs) > timeWindowUs) continue;
                if (hook.PtX == 0 || hook.PtY == 0) continue;
                // Same quadrant / sign — avoid unstable ratios across virtual-desktop origins.
                if (Math.Sign(hook.PtX) != Math.Sign(sx.Value) && sx.Value != 0) continue;
                if (Math.Sign(hook.PtY) != Math.Sign(sy.Value) && sy.Value != 0) continue;
                var rx = hook.PtX / (double)sx.Value;
                var ry = hook.PtY / (double)sy.Value;
                if (rx is > 0.55 and < 4.0 && ry is > 0.55 and < 4.0)
                {
                    ratiosX.Add(rx);
                    ratiosY.Add(ry);
                }
                break; // one hook sample per raw
            }
        }

        if (ratiosX.Count < 5)
        {
            return (null, new
            {
                method = "none",
                sample_pairs = ratiosX.Count,
                note = "Insufficient absolute Raw↔Hook pairs to measure scale."
            });
        }

        ratiosX.Sort();
        ratiosY.Sort();
        var medX = ratiosX[ratiosX.Count / 2];
        var medY = ratiosY[ratiosY.Count / 2];
        // Treat ~1.0 as identity (no extra aligner); only apply when clearly non-unity.
        if (Math.Abs(medX - 1.0) < 0.08 && Math.Abs(medY - 1.0) < 0.08)
        {
            return (null, new
            {
                method = "empirical_near_identity",
                sample_pairs = ratiosX.Count,
                median_scale_x = medX,
                median_scale_y = medY
            });
        }

        var converter = CoordinateSpaceConverter.FromMeasuredScale(
            medX,
            medY,
            "empirical_raw_screen_vs_hook_pt",
            $"median of {ratiosX.Count} absolute pairs; not a hardcoded constant");
        return (converter, converter.Describe());
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
    /// Prefer metrics coords when an empirical/DPI aligner is present (scale from process-view → hook).
    /// Otherwise use ScreenX/Y as already written.
    /// </summary>
    public static (int? X, int? Y) ResolveRawPositionForMatch(RawEvent raw, CoordinateSpaceConverter? aligner)
    {
        if (aligner is not null && !aligner.IsIdentity)
        {
            if (raw.ScreenXMetrics is int mx && raw.ScreenYMetrics is int my)
            {
                var aligned = aligner.MetricsToHookSpace(mx, my);
                return (aligned.X, aligned.Y);
            }
            if (raw.ScreenX is int sx0 && raw.ScreenY is int sy0)
            {
                var aligned = aligner.MetricsToHookSpace(sx0, sy0);
                return (aligned.X, aligned.Y);
            }
        }
        if (raw.ScreenX is int sx && raw.ScreenY is int sy)
            return (sx, sy);
        if (aligner is not null && raw.ScreenXMetrics is int mx2 && raw.ScreenYMetrics is int my2)
        {
            var aligned = aligner.MetricsToHookSpace(mx2, my2);
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
