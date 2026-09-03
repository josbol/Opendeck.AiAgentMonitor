using SkiaSharp;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Rendering;

/// <summary>Draws 144×144 key images (OpenDeck's canvas size) and returns them as PNG data URLs.</summary>
public sealed class KeyRenderer
{
    public const int Size = 144;

    // palette
    static readonly SKColor Bg = SKColor.Parse("#0F1115");
    static readonly SKColor Card = SKColor.Parse("#1B1F27");
    static readonly SKColor Text = SKColor.Parse("#ECEFF4");
    static readonly SKColor Muted = SKColor.Parse("#8A93A6");
    static readonly SKColor Working = SKColor.Parse("#22C55E");
    static readonly SKColor Waiting = SKColor.Parse("#F59E0B");
    static readonly SKColor Idle = SKColor.Parse("#5B6577");
    static readonly SKColor Ended = SKColor.Parse("#EF4444");
    static readonly SKColor ClaudeAccent = SKColor.Parse("#E0865F");
    static readonly SKColor CodexAccent = SKColor.Parse("#3DD0A4");
    static readonly SKColor CopilotAccent = SKColor.Parse("#A78BFA");
    static readonly SKColor Good = SKColor.Parse("#22C55E");
    static readonly SKColor Warn = SKColor.Parse("#F59E0B");
    static readonly SKColor Bad = SKColor.Parse("#EF4444");

    private readonly SKTypeface _regular, _bold;

    public KeyRenderer()
    {
        (_regular, _bold) = LoadFonts();
    }

    private static (SKTypeface, SKTypeface) LoadFonts()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, "fonts"), Path.Combine(baseDir, "..", "..", "assets", "fonts"), Path.Combine(baseDir, "..", "assets", "fonts"), Path.Combine(baseDir, "assets", "fonts") })
        {
            var r = Path.Combine(dir, "DejaVuSans.ttf"); var b = Path.Combine(dir, "DejaVuSans-Bold.ttf");
            if (File.Exists(r) && File.Exists(b))
            {
                var tr = SKTypeface.FromFile(r); var tb = SKTypeface.FromFile(b);
                if (tr is not null && tb is not null) { Log.Info($"Fonts loaded from {dir}"); return (tr, tb); }
            }
        }
        foreach (var family in new[] { "DejaVu Sans", "Noto Sans", "Liberation Sans", "Cantarell", "sans-serif" })
        {
            var tr = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            var tb = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (tr is not null && tb is not null && tr.FamilyName != "" ) { Log.Info($"Using system font {tr.FamilyName}"); return (tr, tb); }
        }
        Log.Warn("No font found; using SkiaSharp default");
        return (SKTypeface.Default, SKTypeface.Default);
    }

    // ---- public renders -------------------------------------------------------------------

    public string AgentKey(AgentInfo a, DateTimeOffset now, int? index = null, int? total = null)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var status = StatusColor(a.State);
        var accent = Accent(a.Provider);

        // top band
        Fill(c, new SKRect(0, 0, Size, 24), status);
        var bandText = a.State == AgentState.Waiting ? SKColors.Black : SKColors.White;
        DrawText(c, ProviderInfo.Label(a.Provider), 8, 17, 11, bandText, bold: true);
        var right = index is not null && total is not null ? $"{index}/{total}" : a.Host;
        DrawText(c, right, Size - 8, 17, 10, bandText, align: SKTextAlign.Right);

        // name (+ session title in the selected view, unless a request or an error needs the room)
        DrawFitted(c, a.ProjectName, Size / 2f, 56, 17, Text, bold: true, maxWidth: Size - 16);
        var showTitle = a.Title is not null && index is not null && a.Approval is null && a.State != AgentState.Error;
        if (showTitle) DrawFitted(c, a.Title!, Size / 2f, 72, 10, Muted, maxWidth: Size - 16);

        // status line
        var since = Elapsed(now - a.StateSince);
        var (word, wordColor) = a.State switch
        {
            AgentState.Working => ($"working {since}", Working),
            AgentState.Waiting when a.Approval is not null => ("APPROVE?", Waiting),
            AgentState.Waiting => ("NEEDS YOU", Waiting),
            AgentState.Error => ("ERROR", Bad),
            AgentState.Idle => ($"idle {since}", Muted),
            _ => ("ended", Ended),
        };
        DrawText(c, word, Size / 2f, showTitle ? 92 : 84, a.NeedsAttention ? 16 : 13, wordColor, bold: a.NeedsAttention, align: SKTextAlign.Center);

        // detail
        var detail = a.Detail;
        if (a.Approval is { } req)
        {
            // the full request, wrapped: the app's own prompt is not visible while the hook holds it
            DrawWrapped(c, Hooks.ApprovalNotifier.FullText(req).Replace(":\n", ": "), 8, 96, 9, Waiting, Size - 16, index is not null ? 3 : 2);
        }
        else if (a.State == AgentState.Error && detail is not null) DrawWrapped(c, detail, 8, 96, 9, Bad, Size - 16, index is not null ? 3 : 2);
        else if (a.State == AgentState.Waiting && detail is not null) DrawFitted(c, $"{detail} · {since}", Size / 2f, 106, 10, Waiting, maxWidth: Size - 16);
        else if (detail is not null) DrawFitted(c, detail, Size / 2f, 100, 10, Muted, maxWidth: Size - 16);
        else if (index is null && a.Host.Length > 0 && a.SubAgents > 0) DrawText(c, $"+{a.SubAgents} sub", Size / 2f, 100, 10, Muted, align: SKTextAlign.Center);
        else if (index is not null) DrawText(c, a.Host + (a.SubAgents > 0 ? $" · +{a.SubAgents} sub" : ""), Size / 2f, 106, 10, Muted, align: SKTextAlign.Center);

        // bottom: model + context bar
        var model = ShortModel(a.Model);
        DrawText(c, model, 8, 128, 9, accent);
        if (a.ContextPct is { } pct)
        {
            var barRect = new SKRect(8, 132, Size - 8, 137);
            FillRound(c, barRect, 2.5f, Card);
            var w = (float)((barRect.Width) * Math.Clamp(pct, 0, 100) / 100.0);
            FillRound(c, new SKRect(barRect.Left, barRect.Top, barRect.Left + Math.Max(w, 3), barRect.Bottom), 2.5f, Threshold(pct, 70, 90));
            DrawText(c, $"ctx {pct:0}%", Size - 8, 128, 9, Muted, align: SKTextAlign.Right);
        }
        else if (a.ContextTokens is { } tokens)
            DrawText(c, $"ctx {Tokens(tokens)}", Size - 8, 128, 9, Muted, align: SKTextAlign.Right);   // size known, window not (Copilot)

        if (a.State == AgentState.Waiting) Border(c, Waiting, 5);
        else if (a.State == AgentState.Error) Border(c, Bad, 5);
        return Encode(s);
    }

    public string EmptySlot(int slot, Provider? filter)
    {
        using var s = NewSurface(); var c = s.Canvas;
        DrawText(c, filter is { } f ? ProviderInfo.Label(f) : "AGENT", Size / 2f, 60, 12, Idle, bold: true, align: SKTextAlign.Center);
        DrawText(c, $"slot {slot}", Size / 2f, 82, 11, Idle, align: SKTextAlign.Center);
        DrawText(c, "—", Size / 2f, 104, 14, Idle, align: SKTextAlign.Center);
        return Encode(s);
    }

    public string QuotaKey(Provider p, ProviderQuota? q, DateTimeOffset now)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var accent = Accent(p);
        DrawText(c, ProviderInfo.Name(p), 8, 18, 13, accent, bold: true);
        if (q?.Plan is { Length: > 0 } plan) DrawText(c, plan.ToUpperInvariant(), Size - 8, 18, 9, Muted, align: SKTextAlign.Right);

        var primary = q?.Primary;
        var secondary = q?.Secondary;

        if (primary is null)
        {
            DrawText(c, q?.Error is not null ? "!" : "…", Size / 2f, 88, 40, q?.Error is not null ? Bad : Muted, bold: true, align: SKTextAlign.Center);
            DrawFitted(c, q?.Error ?? "loading", Size / 2f, 118, 11, Muted, maxWidth: Size - 12);
            return Encode(s);
        }

        // ring
        var center = new SKPoint(Size / 2f, 76);
        const float radius = 38, stroke = 9;
        var oval = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = Card, StrokeCap = SKStrokeCap.Round })
            c.DrawArc(oval, -90, 360, false, ring);
        var pct = Math.Clamp(primary.UsedPct, 0, 100);
        using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = Threshold(pct, 50, 80), StrokeCap = SKStrokeCap.Round })
            if (pct > 0.5) c.DrawArc(oval, -90, (float)(360 * pct / 100), false, arc);
        DrawText(c, $"{pct:0}%", center.X, center.Y + 8, 24, Text, bold: true, align: SKTextAlign.Center);
        DrawText(c, primary.Label + " used", center.X, center.Y + 22, 9, Muted, align: SKTextAlign.Center);

        // footer
        var footer = secondary is not null ? $"{secondary.Label} {secondary.UsedPct:0}%" : "";
        var reset = primary.TimeToReset(now);
        if (reset is not null) footer += (footer.Length > 0 ? "  ↻" : "↻") + Elapsed(reset.Value);
        DrawFitted(c, footer, Size / 2f, 134, 11, secondary is not null ? Threshold(secondary.UsedPct, 50, 80) : Muted, maxWidth: Size - 12);
        var age = now - q!.FetchedAt;
        if (q.Error is not null || age > TimeSpan.FromMinutes(20))
            DrawText(c, (q.Error is "rate limited" ? "rate limited · " : "stale · ") + Elapsed(age), Size - 8, 30, 8, Warn, align: SKTextAlign.Right);
        return Encode(s);
    }

    /// <summary>Counts per state and per provider, plus each provider's usage. With <paramref name="backGlyph"/> a ◀ in the
    /// corner marks the key as the way back to the main layout (the Attention action in "back" mode draws this).</summary>
    public string OverviewKey(Snapshot snap, DateTimeOffset now, bool backGlyph = false)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var working = snap.Count(AgentState.Working); var waiting = snap.Count(AgentState.Waiting); var idle = snap.Count(AgentState.Idle);
        var errors = snap.Count(AgentState.Error);
        var attention = waiting + errors;
        DrawText(c, "AGENTS", Size / 2f, 18, 11, Muted, bold: true, align: SKTextAlign.Center);
        if (backGlyph) DrawText(c, "◀", 8, 18, 12, Muted, bold: true);
        if (working + attention + idle == 0)
        {
            DrawText(c, "none", Size / 2f, 84, 20, Idle, bold: true, align: SKTextAlign.Center);
            DrawText(c, "no sessions running", Size / 2f, 106, 10, Muted, align: SKTextAlign.Center);
            return Encode(s);
        }
        void Cell(float cx, int n, string label, SKColor color, bool highlight = false)
        {
            FillRound(c, new SKRect(cx - 20, 30, cx + 20, 86), 6, highlight ? color : Card);
            var fg = highlight ? (color == Bad ? SKColors.White : SKColors.Black) : (n > 0 ? color : Idle);
            DrawText(c, n.ToString(), cx, 64, 28, fg, bold: true, align: SKTextAlign.Center);
            DrawText(c, label, cx, 80, 9, fg, align: SKTextAlign.Center);
        }
        Cell(26, working, "run", Working);
        Cell(72, attention, waiting == 0 && errors > 0 ? "error" : "wait", waiting > 0 ? Waiting : Bad, attention > 0);
        Cell(118, idle, "idle", Idle);
        // per-provider counts (those with a session) and usage (those with a budget), each in the provider's colour
        var counts = ProviderInfo.All.Where(p => snap.Count(p) > 0).Select(p => ($"{ProviderInfo.Name(p)} {snap.Count(p)}", Accent(p))).ToList();
        DrawSegments(c, counts, Size / 2f, 110, 11, bold: true, gap: 10);
        var usage = ProviderInfo.All.Select(p => (p, w: snap.Quota(p)?.Primary)).Where(x => x.w is not null)
            .Select(x => ($"{ProviderInfo.Initial(x.p)} {x.w!.UsedPct:0}%", Accent(x.p))).ToList();
        DrawSegments(c, usage, Size / 2f, 132, 10);
        if (attention > 0) Border(c, waiting > 0 ? Waiting : Bad, 4);
        return Encode(s);
    }

    /// <summary>The small key for the main layout: the number of agents that need you, else what is running.</summary>
    public string AttentionKey(Snapshot snap, DateTimeOffset now)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var waiting = snap.Count(AgentState.Waiting); var working = snap.Count(AgentState.Working); var idle = snap.Count(AgentState.Idle);
        var errors = snap.Count(AgentState.Error);
        var attention = waiting + errors;
        var fg = waiting > 0 ? SKColors.Black : SKColors.White; // black on amber, white on red
        if (attention > 0)
        {
            Fill(c, new SKRect(0, 0, Size, Size), waiting > 0 ? Waiting : Bad);
            DrawText(c, attention.ToString(), Size / 2f, 82, 60, fg, bold: true, align: SKTextAlign.Center);
            var label = waiting > 0 ? (attention == 1 ? "NEEDS YOU" : "NEED YOU") : (errors == 1 ? "ERROR" : "ERRORS");
            DrawText(c, label, Size / 2f, 108, 14, fg, bold: true, align: SKTextAlign.Center);
            var who = snap.Ordered().FirstOrDefault(a => a.NeedsAttention);
            if (who is not null) DrawFitted(c, who.ProjectName, Size / 2f, 126, 11, fg, maxWidth: Size - 12);
        }
        else
        {
            DrawText(c, "AI", Size / 2f, 46, 20, Muted, bold: true, align: SKTextAlign.Center);
            if (working > 0)
            {
                DrawText(c, working.ToString(), Size / 2f, 90, 40, Working, bold: true, align: SKTextAlign.Center);
                DrawText(c, working == 1 ? "running" : "running", Size / 2f, 108, 12, Working, align: SKTextAlign.Center);
            }
            else if (idle > 0)
            {
                DrawText(c, idle.ToString(), Size / 2f, 90, 40, Idle, bold: true, align: SKTextAlign.Center);
                DrawText(c, "idle", Size / 2f, 108, 12, Muted, align: SKTextAlign.Center);
            }
            else DrawText(c, "quiet", Size / 2f, 96, 16, Idle, align: SKTextAlign.Center);
        }
        DrawText(c, "▶ monitor", Size / 2f, 140, 9, attention > 0 ? fg : Muted, align: SKTextAlign.Center);
        return Encode(s);
    }

    /// <summary>Approve / Deny key showing what would be decided.</summary>
    public string DecisionKey(PendingApproval? p, AgentInfo? agent, bool allow, int more, DateTimeOffset now)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var color = allow ? Good : Bad;
        var glyph = allow ? "✓" : "✕";
        if (p is null)
        {
            // dim but clearly alive: an all-dark key reads as dead when a request just vanished
            FillRound(c, new SKRect(4, 4, Size - 4, Size - 4), 8, Card);
            DrawText(c, glyph, Size / 2f, 78, 44, Idle, bold: true, align: SKTextAlign.Center);
            DrawText(c, allow ? "approve" : "deny", Size / 2f, 104, 12, Muted, align: SKTextAlign.Center);
            DrawText(c, "no request", Size / 2f, 124, 10, Idle, align: SKTextAlign.Center);
            return Encode(s);
        }
        FillRound(c, new SKRect(4, 4, Size - 4, Size - 4), 8, allow ? SKColor.Parse("#123D25") : SKColor.Parse("#3F1A1A"));
        Border(c, color, 4);
        DrawText(c, glyph, Size / 2f, 58, 40, color, bold: true, align: SKTextAlign.Center);
        DrawText(c, allow ? "APPROVE" : "DENY", Size / 2f, 78, 14, Text, bold: true, align: SKTextAlign.Center);
        var who = agent?.ProjectName ?? System.IO.Path.GetFileName(p.Cwd.TrimEnd('/'));
        DrawFitted(c, $"{ProviderInfo.Name(p.Provider)} · {who}", Size / 2f, 94, 10, Muted, maxWidth: Size - 16);
        DrawWrapped(c, Hooks.ApprovalNotifier.FullText(p).Replace(":\n", ": "), 8, 108, 9, Text, Size - 16, 2);
        var foot = KeyRenderer.Elapsed(now - p.ReceivedAt) + (more > 0 ? $"  ·  +{more} more" : "");
        DrawText(c, foot, Size / 2f, 136, 9, Muted, align: SKTextAlign.Center);
        return Encode(s);
    }

    public string MessageKey(string title, string body, SKColor? color = null)
    {
        using var s = NewSurface(); var c = s.Canvas;
        DrawText(c, title, Size / 2f, 60, 14, color ?? Text, bold: true, align: SKTextAlign.Center);
        DrawFitted(c, body, Size / 2f, 84, 11, Muted, maxWidth: Size - 12);
        return Encode(s);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static SKSurface NewSurface()
    {
        var s = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        s.Canvas.Clear(Bg);
        return s;
    }

    private static string Encode(SKSurface s)
    {
        using var img = s.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.AsSpan());
    }

    private static SKColor Accent(Provider p) => p switch { Provider.Claude => ClaudeAccent, Provider.Codex => CodexAccent, _ => CopilotAccent };
    private static SKColor StatusColor(AgentState st) => st switch { AgentState.Working => Working, AgentState.Waiting => Waiting, AgentState.Error => Bad, AgentState.Idle => Idle, _ => Ended };
    private static SKColor Threshold(double pct, double warn, double bad) => pct >= bad ? Bad : pct >= warn ? Warn : Good;

    private static void Fill(SKCanvas c, SKRect r, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = false };
        c.DrawRect(r, p);
    }
    private static void FillRound(SKCanvas c, SKRect r, float radius, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(r, radius, radius, p);
    }
    private static void Border(SKCanvas c, SKColor color, float width)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };
        c.DrawRoundRect(new SKRect(width / 2, width / 2, Size - width / 2, Size - width / 2), 6, 6, p);
    }

    private void DrawText(SKCanvas c, string text, float x, float y, float size, SKColor color, bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(text, x, y, align, font, paint);
    }

    /// <summary>Draws centred text, shrinking then truncating with an ellipsis until it fits.</summary>
    private void DrawFitted(SKCanvas c, string text, float cx, float y, float size, SKColor color, bool bold = false, float maxWidth = Size - 12)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        var t = text;
        while (font.Size > size * 0.8f && font.MeasureText(t) > maxWidth) font.Size -= 1;
        while (t.Length > 1 && font.MeasureText(t + "…") > maxWidth && font.MeasureText(t) > maxWidth) t = t[..^1];
        if (t.Length < text.Length) t = t.TrimEnd() + "…";
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(t, cx, y, SKTextAlign.Center, font, paint);
    }

    /// <summary>Left-aligned word-wrapped text; the last allowed line gets an ellipsis when text remains.</summary>
    private void DrawWrapped(SKCanvas c, string text, float x, float top, float size, SKColor color, float maxWidth, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var font = new SKFont(_regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var words = text.Replace("\r", "").Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>(); var cur = "";
        foreach (var w in words)
        {
            if (lines.Count > maxLines) break;   // enough to know it overflows
            var candidate = cur.Length == 0 ? w : cur + " " + w;
            if (font.MeasureText(candidate) <= maxWidth) { cur = candidate; continue; }
            if (cur.Length > 0) { lines.Add(cur); cur = ""; }
            // a single word longer than the line: hard-break it
            var piece = w;
            while (font.MeasureText(piece) > maxWidth && piece.Length > 1)
            {
                var cut = piece.Length;
                while (cut > 1 && font.MeasureText(piece[..cut]) > maxWidth) cut--;
                lines.Add(piece[..cut]); piece = piece[cut..];
            }
            cur = piece;
        }
        if (cur.Length > 0) lines.Add(cur);
        var truncated = lines.Count > maxLines;
        if (truncated) lines = lines.Take(maxLines).ToList();
        if (truncated && lines.Count > 0)
        {
            var last = lines[^1];
            while (last.Length > 1 && font.MeasureText(last + "…") > maxWidth) last = last[..^1];
            lines[^1] = last + "…";
        }
        var lineHeight = size * 1.25f;
        for (var i = 0; i < lines.Count; i++) c.DrawText(lines[i], x, top + i * lineHeight, SKTextAlign.Left, font, paint);
    }

    /// <summary>One centred line of differently coloured pieces; the font shrinks (down to 7 px) until the line fits.</summary>
    private void DrawSegments(SKCanvas c, IReadOnlyList<(string Text, SKColor Color)> segments, float cx, float y, float size, bool bold = false, float maxWidth = Size - 12, float gap = 8)
    {
        if (segments.Count == 0) return;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        float Total() => segments.Sum(s => font.MeasureText(s.Text)) + gap * (segments.Count - 1);
        while (font.Size > 7 && Total() > maxWidth) font.Size -= 0.5f;
        var x = cx - Total() / 2;
        foreach (var (text, color) in segments)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            c.DrawText(text, x, y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(text) + gap;
        }
    }

    public static string Elapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return "<1m";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h{t.Minutes:00}";
        return $"{(int)t.TotalDays}d{t.Hours}h";
    }

    private static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{Math.Round(n / 1000.0)}k" : n.ToString();

    private static string ShortModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return "";
        var m = model.Replace("claude-", "").Replace("-20", "-");
        var dash = m.IndexOf("-20", StringComparison.Ordinal);
        if (dash > 0) m = m[..dash];
        return m.Length > 14 ? m[..14] : m;
    }
}
