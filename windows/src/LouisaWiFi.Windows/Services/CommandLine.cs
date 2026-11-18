namespace LouisaWiFi.Windows.Services;

public enum CommandKind { Show, Nearest, Connect, Stats, Update, Updated, UpdateFailed }

public sealed record AppCommand(CommandKind Kind, string? Store = null);

/// LouisaWiFi.exe [--nearest | --connect 門市 | --show | --stats | --update]; the macOS URL scheme's commands.
public static class CommandLine
{
    public const string Usage = "可用指令：--nearest、--connect <門市>、--show、--stats、--update";

    /// No arguments → null. Unknown or malformed → Error.
    public static (AppCommand? Command, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return (null, null);
        var name = args[0].ToLowerInvariant();
        return name switch
        {
            "--nearest" when args.Count == 1 => (new AppCommand(CommandKind.Nearest), null),
            "--show" when args.Count == 1 => (new AppCommand(CommandKind.Show), null),
            "--stats" when args.Count == 1 => (new AppCommand(CommandKind.Stats), null),
            "--update" when args.Count == 1 => (new AppCommand(CommandKind.Update), null),
            // Passed by the updater to the version it just started.
            "--updated" when args.Count == 1 => (new AppCommand(CommandKind.Updated), null),
            "--update-failed" when args.Count == 1 => (new AppCommand(CommandKind.UpdateFailed), null),
            "--connect" when args.Count == 2 && !string.IsNullOrWhiteSpace(args[1]) && args[1].Length <= 100 =>
                (new AppCommand(CommandKind.Connect, args[1].Trim()), null),
            _ => (null, "不支援的指令。" + Usage)
        };
    }
}
