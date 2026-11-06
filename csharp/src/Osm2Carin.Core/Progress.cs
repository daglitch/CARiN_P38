using System.Diagnostics;

namespace Osm2Carin.Core;

/// <summary>A progress bar with elapsed time and ETA for one long step, written to stderr.
///   On a terminal: one line redrawn in place (at most 5 times a second), e.g.
///     tiles 0x00  [###########.........]  55%  20,512 / 37,281  0:14 elapsed, ETA 0:11
///   Redirected (a log file): a plain line at every 10%, so logs stay readable.
/// Thread-safe: Add/Set may be called from Parallel loops. Dispose prints the final line.
/// Steps announce themselves with Stage ("[3/7] Road tiles") so a whole run shows where it is.</summary>
public sealed class Progress : IDisposable
{
    /// <summary>Off for tests and quiet runs.</summary>
    public static bool Enabled = true;
    static readonly bool Tty = !Console.IsErrorRedirected;
    static readonly object Gate = new();

    readonly string label, unit;
    readonly long total;
    readonly Func<long, string> fmt;
    readonly Stopwatch sw = Stopwatch.StartNew();
    long done, lastDraw = -1000;
    int lastDecile = -1;
    bool closed;

    public Progress(string label, long total, string unit = "", Func<long, string>? format = null)
    {
        this.label = label; this.total = Math.Max(1, total); this.unit = unit;
        fmt = format ?? (v => v.ToString("N0"));
        Draw(force: true);
    }

    /// <summary>A byte counter shown in MiB.</summary>
    public static Progress Bytes(string label, long total) => new(label, total, "MiB", v => (v / 1048576.0).ToString("N0"));

    public static void Stage(int i, int n, string what)
    {
        if (!Enabled) return;
        lock (Gate) Console.Error.WriteLine($"[{i}/{n}] {what}");
    }

    public void Add(long n = 1) { Interlocked.Add(ref done, n); Draw(); }
    public void Set(long v) { Interlocked.Exchange(ref done, v); Draw(); }

    static string Time(double s) => s >= 3600 ? $"{(int)(s / 3600)}:{(int)(s / 60 % 60):00}:{(int)(s % 60):00}" : $"{(int)(s / 60)}:{(int)(s % 60):00}";

    string Line(bool final)
    {
        long d = Math.Min(Interlocked.Read(ref done), total);
        double f = (double)d / total, el = sw.Elapsed.TotalSeconds;
        const int w = 24;
        int k = (int)(f * w);
        string bar = new string('#', k) + new string('.', w - k);
        string eta = final ? $"done in {Time(el)}" : f > 0.01 && el > 1 ? $"{Time(el)} elapsed, ETA {Time(el / f * (1 - f))}" : $"{Time(el)} elapsed";
        string u = unit == "" ? "" : " " + unit;
        return $"  {label,-14} [{bar}] {f,4:P0}  {fmt(d)} / {fmt(total)}{u}  {eta}";
    }

    void Draw(bool force = false)
    {
        if (!Enabled || closed) return;
        long now = sw.ElapsedMilliseconds;
        if (Tty)
        {
            if (!force && now - Interlocked.Read(ref lastDraw) < 200) return;
            lock (Gate)
            {
                if (closed || (!force && now - lastDraw < 200)) return;
                lastDraw = now;
                var s = Line(false);
                int width = SafeWidth();
                if (s.Length > width - 1) s = s[..(width - 1)];
                Console.Error.Write("\r" + s.PadRight(width - 1));
            }
        }
        else
        {
            int dec = (int)(10.0 * Math.Min(Interlocked.Read(ref done), total) / total);
            if (dec <= lastDecile || dec == 10) return;
            lock (Gate)
            {
                if (closed || dec <= lastDecile) return;
                lastDecile = dec;
                if (dec > 0) Console.Error.WriteLine(Line(false));
            }
        }
    }

    static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); } catch { return 120; }
    }

    public void Dispose()
    {
        if (closed) return;
        Set(total);
        lock (Gate)
        {
            closed = true;
            if (!Enabled) return;
            var s = Line(true);
            if (Tty) Console.Error.WriteLine("\r" + s.PadRight(SafeWidth() - 1));
            else Console.Error.WriteLine(s);
        }
    }
}
