using System.Text;
using System.Text.Json;
using Cmux.Core.IPC;
using Cmux.Core.Terminal;

namespace Cmux.Cli;

/// <summary>
/// cmux CLI tool — Windows equivalent of the cmux macOS CLI.
/// Communicates with the running cmux app via named pipes.
///
/// Usage:
///   cmux notify --title "Title" --body "Body"
///   cmux workspace list
///   cmux workspace create --name "My Workspace"
///   cmux workspace select --index 0
///   cmux surface create
///   cmux split right
///   cmux split down
///   cmux pane list
///   cmux send --text "git status" --enter
///   cmux send-key ctrl-c
///   cmux status
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "notify" => await HandleNotify(args[1..]),
                "workspace" => await HandleWorkspace(args[1..]),
                "surface" => await HandleSurface(args[1..]),
                "split" => await HandleSplit(args[1..]),
                "pane" => await HandlePane(args[1..]),
                "send" => await HandleSend(args[1..]),
                "send-key" or "sendkey" => await HandleSendKey(args[1..]),
                "status" => await HandleStatus(),
                "help" or "--help" or "-h" => PrintHelp(),
                "version" or "--version" or "-v" => PrintVersion(),
                _ => Error($"Unknown command: {command}"),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Error: Could not connect to cmux. Is it running?");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> HandleNotify(string[] args)
    {
        var parsed = ParseArgs(args);
        var title = parsed.GetValueOrDefault("title", parsed.GetValueOrDefault("_arg0", "Terminal"));
        var body = parsed.GetValueOrDefault("body", parsed.GetValueOrDefault("_arg1", ""));
        var subtitle = parsed.GetValueOrDefault("subtitle");

        var cmdArgs = new Dictionary<string, string>
        {
            ["title"] = title,
            ["body"] = body,
        };
        if (subtitle != null) cmdArgs["subtitle"] = subtitle;

        var response = await NamedPipeClient.SendCommand("NOTIFY", cmdArgs);
        Console.WriteLine(response);
        return 0;
    }

    private static async Task<int> HandleWorkspace(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux workspace <list|create|select>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();
        var parsed = ParseArgs(args[1..]);

        return subcommand switch
        {
            "list" or "ls" => await SendAndPrint("WORKSPACE.LIST"),
            "create" or "new" => await SendAndPrint("WORKSPACE.CREATE", parsed),
            "select" => await SendAndPrint("WORKSPACE.SELECT", parsed),
            "next" => await SendAndPrint("WORKSPACE.NEXT"),
            "previous" or "prev" => await SendAndPrint("WORKSPACE.PREVIOUS"),
            _ => Error($"Unknown workspace command: {subcommand}"),
        };
    }

    private static async Task<int> HandleSurface(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux surface <create>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();

        return subcommand switch
        {
            "create" or "new" => await SendAndPrint("SURFACE.CREATE"),
            "next" => await SendAndPrint("SURFACE.NEXT"),
            "previous" or "prev" => await SendAndPrint("SURFACE.PREVIOUS"),
            _ => Error($"Unknown surface command: {subcommand}"),
        };
    }

    private static async Task<int> HandleSplit(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux split <right|down>");
            return 1;
        }

        var direction = args[0].ToLowerInvariant();

        return direction switch
        {
            "right" or "vertical" or "v" => await SendAndPrint("SPLIT.RIGHT"),
            "down" or "horizontal" or "h" => await SendAndPrint("SPLIT.DOWN"),
            _ => Error($"Unknown split direction: {direction}"),
        };
    }

    private static async Task<int> HandleStatus()
    {
        return await SendAndPrint("STATUS");
    }

    private static async Task<int> HandlePane(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cmux pane <list>");
            return 1;
        }

        var subcommand = args[0].ToLowerInvariant();

        if (subcommand is not ("list" or "ls"))
            return Error($"Unknown pane command: {subcommand}");

        var cmdArgs = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--workspace":
                    if (!TryTakeIndexValue(args, ref i, "--workspace", out var wsIndex)) return 1;
                    cmdArgs["workspaceIndex"] = wsIndex;
                    break;
                case "--surface":
                    if (!TryTakeIndexValue(args, ref i, "--surface", out var sfIndex)) return 1;
                    cmdArgs["surfaceIndex"] = sfIndex;
                    break;
                default:
                    return Error($"Unknown option for pane list: {args[i]}");
            }
        }

        return await SendAndPrint("PANE.LIST", cmdArgs);
    }

    private static async Task<int> HandleSend(string[] args)
    {
        string? text = null;
        bool enter = false;
        bool paste = false;
        var cmdArgs = new Dictionary<string, string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--text":
                    if (!TryTakeValue(args, ref i, "--text", out var textValue)) return 1;
                    text = textValue;
                    break;
                case "--enter":
                    enter = true;
                    break;
                case "--paste":
                    paste = true;
                    break;
                default:
                    if (!TryParseTargetOption(args, ref i, cmdArgs, out var handled)) return 1;
                    if (!handled) return Error($"Unknown option for send: {args[i]}");
                    break;
            }
        }

        // Fall back to stdin when --text is absent (e.g. `type prompt.txt | cmux send --paste`).
        if (text == null && Console.IsInputRedirected)
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            text = await stdin.ReadToEndAsync();

            // Strip one trailing newline so submission stays explicit via --enter.
            if (text.EndsWith("\r\n")) text = text[..^2];
            else if (text.EndsWith('\n') || text.EndsWith('\r')) text = text[..^1];
        }

        if (string.IsNullOrEmpty(text) && !enter)
            return Error("Nothing to send. Provide --text, pipe a payload via stdin, or pass --enter.");

        cmdArgs["data"] = PaneInputEncoder.EncodeBase64Payload(text ?? "");
        if (enter) cmdArgs["enter"] = "true";
        if (paste) cmdArgs["paste"] = "true";

        return await SendInputAndReport("PANE.SEND", cmdArgs);
    }

    private static async Task<int> HandleSendKey(string[] args)
    {
        string? key = null;
        var cmdArgs = new Dictionary<string, string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                if (key != null)
                    return Error($"Unexpected argument: {args[i]} (only one key per invocation)");
                key = args[i];
                continue;
            }

            if (!TryParseTargetOption(args, ref i, cmdArgs, out var handled)) return 1;
            if (!handled) return Error($"Unknown option for send-key: {args[i]}");
        }

        if (string.IsNullOrWhiteSpace(key))
            return Error($"Usage: cmux send-key <key> [target options]. Supported keys: {PaneInputEncoder.SupportedKeysDescription}");

        if (!PaneInputEncoder.TryResolveKey(key, out _))
            return Error($"Unknown key: {key}. Supported: {PaneInputEncoder.SupportedKeysDescription}");

        cmdArgs["key"] = key;
        return await SendInputAndReport("PANE.SENDKEY", cmdArgs);
    }

    /// <summary>
    /// Parses the target options shared by send and send-key
    /// (--workspace/--surface/--pane/--all/--all-in-workspace).
    /// Returns false on a malformed value; sets handled=false for unknown options.
    /// </summary>
    private static bool TryParseTargetOption(string[] args, ref int i, Dictionary<string, string> cmdArgs, out bool handled)
    {
        handled = true;

        switch (args[i].ToLowerInvariant())
        {
            case "--workspace":
                if (!TryTakeIndexValue(args, ref i, "--workspace", out var wsIndex)) return false;
                cmdArgs["workspaceIndex"] = wsIndex;
                return true;
            case "--surface":
                if (!TryTakeIndexValue(args, ref i, "--surface", out var sfIndex)) return false;
                cmdArgs["surfaceIndex"] = sfIndex;
                return true;
            case "--pane":
                if (!TryTakeIndexValue(args, ref i, "--pane", out var paneIndex)) return false;
                cmdArgs["paneIndex"] = paneIndex;
                return true;
            case "--all":
                cmdArgs["all"] = "true";
                return true;
            case "--all-in-workspace":
                cmdArgs["allInWorkspace"] = "true";
                return true;
            default:
                handled = false;
                return true;
        }
    }

    private static bool TryTakeValue(string[] args, ref int i, string option, out string value)
    {
        if (i + 1 < args.Length)
        {
            value = args[++i];
            return true;
        }

        value = "";
        Console.Error.WriteLine($"Error: {option} requires a value.");
        return false;
    }

    private static bool TryTakeIndexValue(string[] args, ref int i, string option, out string value)
    {
        if (!TryTakeValue(args, ref i, option, out value))
            return false;

        if (!int.TryParse(value, out _))
        {
            Console.Error.WriteLine($"Error: {option} requires an integer index, got: {value}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sends an input-injection command and reports the outcome: errors and
    /// per-pane broadcast failures go to stderr with a non-zero exit code.
    /// </summary>
    private static async Task<int> SendInputAndReport(string command, Dictionary<string, string> cmdArgs)
    {
        var response = await NamedPipeClient.SendCommand(command, cmdArgs);

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                Console.Error.WriteLine($"Error: {error.GetString()}");
                return 1;
            }

            if (root.TryGetProperty("failed", out var failed)
                && failed.ValueKind == JsonValueKind.Array
                && failed.GetArrayLength() > 0)
            {
                var delivered = root.TryGetProperty("delivered", out var d) ? d.GetInt32() : 0;
                Console.WriteLine($"Delivered to {delivered} pane(s); {failed.GetArrayLength()} failed:");
                foreach (var f in failed.EnumerateArray())
                {
                    var workspace = f.TryGetProperty("workspace", out var w) ? w.GetString() : "?";
                    var surface = f.TryGetProperty("surface", out var s) ? s.GetString() : "?";
                    var reason = f.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
                    Console.Error.WriteLine($"  workspace \"{workspace}\", surface \"{surface}\": {reason}");
                }
                return 1;
            }

            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(pretty);
            return 0;
        }
        catch (JsonException)
        {
            Console.WriteLine(response);
            return 0;
        }
    }

    private static async Task<int> SendAndPrint(string command, Dictionary<string, string>? args = null)
    {
        var response = await NamedPipeClient.SendCommand(command, args);

        // Pretty-print JSON
        try
        {
            using var doc = JsonDocument.Parse(response);
            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(pretty);
        }
        catch
        {
            Console.WriteLine(response);
        }

        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>();
        int positional = 0;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
            else if (arg.StartsWith('-') && arg.Length == 2)
            {
                var key = arg[1..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
            else
            {
                result[$"_arg{positional}"] = arg;
                positional++;
            }
        }

        return result;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            cmux - Terminal multiplexer for AI coding agents (Windows)

            Usage:
              cmux <command> [options]

            Commands:
              notify                Send a notification
                --title <text>      Notification title (default: "Terminal")
                --body <text>       Notification body
                --subtitle <text>   Notification subtitle

              workspace             Manage workspaces
                list                List all workspaces
                create              Create a new workspace
                  --name <text>     Workspace name
                select              Select a workspace
                  --index <n>       Workspace index (0-based)
                  --id <id>         Workspace ID
                next                Switch to next workspace
                previous            Switch to previous workspace

              surface               Manage surfaces (tabs within workspace)
                create              Create a new surface
                next                Switch to next surface
                previous            Switch to previous surface

              split                 Split the focused pane
                right               Split vertically (left/right)
                down                Split horizontally (top/bottom)

              pane                  Inspect panes
                list                List panes in a surface
                  --workspace <n>   Workspace index (default: active)
                  --surface <n>     Surface index (default: active)

              send                  Type text into a terminal pane
                --text <text>       Text payload (reads stdin when omitted)
                --enter             Press Enter after the payload
                --paste             Bracketed paste (multiline text lands as one block)
                --workspace <n>     Target workspace index (default: active)
                --surface <n>       Target surface index (default: active)
                --pane <n>          Target pane index (default: focused/active pane)
                --all               Broadcast to every pane in every workspace
                --all-in-workspace  Broadcast to every pane in the target workspace

              send-key <key>        Press a key in a terminal pane
                                    Keys: enter, tab, escape, backspace, space,
                                    up/down/left/right, home, end, insert, delete,
                                    pageup, pagedown, f1-f12, ctrl-a..ctrl-z
                                    (same target options as send)

              status                Show cmux status

            Keyboard Shortcuts (in the app):
              Ctrl+N                New workspace
              Ctrl+1-8              Jump to workspace 1-8
              Ctrl+9                Jump to last workspace
              Ctrl+Shift+W          Close workspace
              Ctrl+B                Toggle sidebar
              Ctrl+T                New surface (tab)
              Ctrl+W                Close surface
              Ctrl+D                Split right
              Ctrl+Shift+D          Split down
              Ctrl+Alt+Arrow        Focus pane directionally
              Ctrl+I                Toggle notification panel
              Ctrl+Shift+U          Jump to latest unread
            """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("cmux 1.0.6 (Windows)");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}
