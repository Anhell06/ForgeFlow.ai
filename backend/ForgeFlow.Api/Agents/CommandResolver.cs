using System.Diagnostics;

namespace ForgeFlow.Agents;

/// <summary>
/// Resolves a command name through PATH/PATHEXT. On Windows, npm-style shims (.cmd/.bat) cannot be started directly,
/// so they are launched through cmd.exe.
/// </summary>
public static class CommandResolver
{
    public static ProcessStartInfo Build(string command, IEnumerable<string> args)
    {
        var resolved = Resolve(command) ?? command;
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (OperatingSystem.IsWindows() && ext is ".cmd" or ".bat")
        {
            // cmd /s /c "<whole command>" strips only the outer quotes, so each inner argument keeps its own quoting.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"{string.Join(' ', new[] { resolved }.Concat(args).Select(QuoteForCmd))}\"";
            return psi;
        }
        psi.FileName = resolved;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static string QuoteForCmd(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:\\=".Contains(c))
            ? arg
            : "\"" + arg.Replace("\"", "\"\"") + "\"";

    public static string? Resolve(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            return File.Exists(command) ? command : null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var hasExt = Path.HasExtension(command);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (hasExt && File.Exists(Path.Combine(dir, command))) return Path.Combine(dir, command);
            foreach (var e in exts)
            {
                var candidate = Path.Combine(dir, command + e.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
