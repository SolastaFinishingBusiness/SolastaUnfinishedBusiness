using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Installer;

// Applies a verified, staged update of the mod (or rolls the last one back) once the game has exited, then starts the
// game again. Started by the mod's updater, hidden, from a copy outside the mod folder so the update can replace the
// installed copy of this program too. It writes what happened to a result file the mod reads on its next start; the
// mod reports that result, so a started installer never counts as an installed update.
//   --pid <game process> --mode install|rollback --target <mod folder> --transaction <backup folder>
//   [--source <staged mod folder>] --result <result file> --log <log file> [--relaunch <game executable>]
//   [--texts <window texts>]
// While it works it shows InstallWindow. Mods older than this helper started their own (hidden, textless) copy, so the
// window appears from the first update made by a version that contains it.
internal static class Program
{
    private const int Attempts = 5;

    private static string _log;

    private static int Main(string[] args)
    {
        var options = Parse(args);

        _log = Get(options, "log");

        string status;
        var message = string.Empty;
        var transaction = Get(options, "transaction");
        var window = InstallWindow.Open(Get(options, "texts"), Log);

        try
        {
            WaitForExit(Get(options, "pid"));
            window?.ShowStep("working");

            var mode = Get(options, "mode");
            var target = Get(options, "target");

            if (mode == "rollback")
            {
                Log($"Rolling back {target} from {transaction}");
                Retry(() => ModInstallTransaction.Recover(target, transaction));
                status = "rolledback";
            }
            else
            {
                // a failed attempt keeps its own backup folder, so each retry gets a new one; a retry only follows an
                // attempt that put the previous files back, or it would back up half-installed files
                var source = Get(options, "source");
                var attempt = 0;
                var first = transaction;

                Retry(() =>
                {
                    if (attempt > 0 && !Untouched(transaction))
                    {
                        throw new InvalidOperationException(
                            $"The failed attempt couldn't restore the previous files; they are in {transaction}");
                    }

                    // an attempt that stopped before changing anything has no backup worth keeping. One that stopped
                    // even before creating its backup folder (a missing or empty staged update, the installer lock
                    // held) has none to delete: deleting it anyway threw "Could not find a part of the path
                    // ...\transaction", which every later attempt repeated and which then replaced the real error in
                    // the result the game reports
                    if (attempt > 0 && !File.Exists(Path.Combine(transaction, "journal.xml")) &&
                        Directory.Exists(transaction))
                    {
                        Directory.Delete(transaction, true);
                    }

                    transaction = attempt++ == 0 ? first : $"{first}-{attempt}";
                    Log($"Installing {source} into {target} (backup {transaction})");
                    ModInstallTransaction.Apply(source, target, transaction);
                });
                status = "installed";
            }
        }
        catch (Exception e)
        {
            status = Get(options, "mode") == "rollback" ? "rollback-failed" : "failed";
            message = e.Message;
            Log($"Failed: {e}");
        }

        Log($"Result: {status}");
        // the details are the game's to show (its next start reports the result); the window only says it failed
        window?.ShowStep(status is "installed" or "rolledback" ? "starting" : "failed");
        WriteResult(Get(options, "result"), new Dictionary<string, string>
        {
            ["status"] = status,
            ["mode"] = Get(options, "mode"),
            ["version"] = Get(options, "version"),
            ["transaction"] = transaction,
            // after a failed install: whether the previous files were put back
            ["restored"] = Untouched(transaction) ? "yes" : "no",
            ["log"] = _log,
            ["message"] = message.Replace('\r', ' ').Replace('\n', ' '),
            ["time"] = DateTime.Now.ToString("s")
        });

        using (var game = Relaunch(Get(options, "relaunch")))
        {
            WaitForGameWindow(game, window != null);
        }

        window?.CloseWindow();

        return status is "installed" or "rolledback" ? 0 : 1;
    }

    // Keeps the install window up until the game it started shows its own window (up to 60 s: the game's splash
    // appears within seconds), so there is never a gap with nothing on screen. Without a game, a failed install's
    // message stays readable for a few seconds instead. Nothing to wait for when there is no window.
    private static void WaitForGameWindow(Process game, bool hasWindow)
    {
        if (!hasWindow)
        {
            return;
        }

        if (game == null)
        {
            Thread.Sleep(5000);

            return;
        }

        var deadline = DateTime.Now.AddSeconds(60);

        try
        {
            while (DateTime.Now < deadline && !game.HasExited)
            {
                game.Refresh();

                if (game.MainWindowHandle != IntPtr.Zero)
                {
                    return;
                }

                Thread.Sleep(250);
            }
        }
        catch (Exception e)
        {
            Log($"Couldn't watch the started game: {e.Message}");
        }
    }

    // the installed files are as they were before this attempt: it stopped before changing any (its journal is
    // written just before the first change), or it put them all back
    private static bool Untouched(string transaction)
    {
        return !File.Exists(Path.Combine(transaction, "journal.xml")) ||
               File.Exists(Path.Combine(transaction, "recovered"));
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>();

        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            options[args[i].TrimStart('-')] = args[i + 1];
        }

        return options;
    }

    private static string Get(Dictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static void WaitForExit(string pid)
    {
        if (!int.TryParse(pid, out var id))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);

            Log($"Waiting for process {id} to exit");
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // already gone
        }

        // the game's files and the mod's DLL stay open for a moment after the process ends
        Thread.Sleep(2000);
    }

    // a file still open right after the exit fails the first try; each failed try has already restored the backup
    private static void Retry(Action action)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();

                return;
            }
            catch (IOException e) when (i < Attempts)
            {
                Log($"Attempt {i} failed: {e.Message}; retrying");
                Thread.Sleep(3000);
            }
            catch (UnauthorizedAccessException e) when (i < Attempts)
            {
                Log($"Attempt {i} failed: {e.Message}; retrying");
                Thread.Sleep(3000);
            }
        }
    }

    private static void WriteResult(string path, Dictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var temporary = path + ".tmp";

        File.WriteAllLines(temporary, values.Select(x => $"{x.Key}={x.Value}"));

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temporary, path);
    }

    // Steam can refuse a launch right after the game exits, so it waits a little first.
    // The game is started without the environment variables Unity Doorstop (the mod loader, winhttp.dll beside the
    // game) set in the old game: this program inherits them from the game that started it, and Doorstop 4 does nothing
    // in a process where DOORSTOP_INITIALIZED is already set ("DOORSTOP_INITIALIZED is set! Skipping!"), so a game
    // started with them ran without Unity Mod Manager or any mod (found testing the 1.10.0 to 1.11.0 update). Setting a
    // child's environment needs UseShellExecute false; the working directory stays the game's folder, which Doorstop's
    // relative target_assembly path in doorstop_config.ini depends on.
    // returns the started game (for WaitForGameWindow; the caller disposes it), or null when none was started
    private static Process Relaunch(string executable)
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            return null;
        }

        try
        {
            Thread.Sleep(5000);

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty, UseShellExecute = false
            };
            var removed = RemoveDoorstopVariables(start);

            Log($"Starting {executable}" + (removed.Count > 0 ? $" without {string.Join(", ", removed)}" : string.Empty));

            return Process.Start(start);
        }
        catch (Exception e)
        {
            Log($"Couldn't start the game: {e.Message}");

            return null;
        }
    }

    // every DOORSTOP_* variable (Doorstop 4 sets DOORSTOP_INITIALIZED, DOORSTOP_INVOKE_DLL_PATH,
    // DOORSTOP_MANAGED_FOLDER_DIR, DOORSTOP_PROCESS_PATH and DOORSTOP_DLL_SEARCH_DIRS); a fresh start sets its own.
    // Returns the names removed, for the log
    private static List<string> RemoveDoorstopVariables(ProcessStartInfo start)
    {
        var names = start.EnvironmentVariables.Keys.Cast<string>()
            .Where(x => x.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in names)
        {
            start.EnvironmentVariables.Remove(name);
        }

        return names;
    }

    private static void Log(string line)
    {
        if (string.IsNullOrEmpty(_log))
        {
            return;
        }

        try
        {
            File.AppendAllText(_log, $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch
        {
            // the log is a help, never a reason to stop
        }
    }
}
