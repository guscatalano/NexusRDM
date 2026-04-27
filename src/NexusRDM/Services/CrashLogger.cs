using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusRDM.Services;

/// <summary>
/// Single sink for unhandled-exception reporting. Writes rich, machine- and
/// human-readable entries to <c>%LocalAppData%\NexusRDM\crash.log</c> so a
/// crash leaves enough context to diagnose without a debugger attached.
///
/// Hooked from <see cref="App"/>: WinUI <c>UnhandledException</c>,
/// <c>AppDomain.CurrentDomain.UnhandledException</c>, and
/// <c>TaskScheduler.UnobservedTaskException</c>. Also called manually from
/// catch blocks where we want to record-but-continue.
/// </summary>
public static class CrashLogger
{
    private static readonly object _gate = new();
    private static string? _path;

    public static void Initialize(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        _path = Path.Combine(appDataDir, "crash.log");
    }

    public static void Log(Exception ex, string source, bool fatal = false)
    {
        try
        {
            var entry = Format(ex, source, fatal);
            lock (_gate)
            {
                if (_path is not null) File.AppendAllText(_path, entry);
            }
            // Mirror to stderr so devs running under a debugger / console
            // see it immediately without tailing the log.
            try { System.Diagnostics.Debug.WriteLine(entry); } catch { }
        }
        catch
        {
            // Logging must never throw — swallow file IO / formatting errors.
        }
    }

    private static string Format(Exception ex, string source, bool fatal)
    {
        var sb = new StringBuilder();
        sb.Append("=========== ").Append(fatal ? "FATAL" : "ERROR")
          .Append(' ').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
          .Append(" ===========").AppendLine();
        sb.Append("Source: ").AppendLine(source);

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            sb.Append("App   : ").Append(asm.GetName().Name).Append(' ')
              .Append(asm.GetName().Version).AppendLine();
        }
        catch { }
        try
        {
            sb.Append("OS    : ").Append(RuntimeInformation.OSDescription)
              .Append(" / ").Append(RuntimeInformation.OSArchitecture).AppendLine();
            sb.Append("CLR   : ").Append(RuntimeInformation.FrameworkDescription).AppendLine();
        }
        catch { }
        try
        {
            sb.Append("Thread: ")
              .Append(Environment.CurrentManagedThreadId)
              .Append(" (")
              .Append(Thread.CurrentThread.IsThreadPoolThread ? "pool"
                    : Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? "STA"
                    : Thread.CurrentThread.IsBackground ? "background" : "foreground")
              .Append(") name=")
              .Append(Thread.CurrentThread.Name ?? "<null>").AppendLine();
        }
        catch { }

        AppendException(sb, ex, depth: 0);
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append(depth == 0 ? "Exception: " : "  → Inner: ")
          .Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);

        if (ex is COMException com)
            sb.Append(indent).Append("  HResult: 0x").AppendLine(com.HResult.ToString("X8"));
        else if (ex.HResult != 0)
            sb.Append(indent).Append("  HResult: 0x").AppendLine(ex.HResult.ToString("X8"));

        if (ex.Data.Count > 0)
        {
            sb.Append(indent).AppendLine("  Data:");
            foreach (System.Collections.DictionaryEntry kv in ex.Data)
                sb.Append(indent).Append("    ").Append(kv.Key).Append(" = ").AppendLine(kv.Value?.ToString());
        }

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.Append(indent).AppendLine("  Stack:");
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.Append(indent).Append("    ").AppendLine(line.TrimEnd('\r'));
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                AppendException(sb, inner, depth + 1);
        }
        else if (ex.InnerException is not null)
        {
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }
}
