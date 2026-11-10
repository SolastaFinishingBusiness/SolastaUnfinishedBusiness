using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        InstallWindow window = null;

        try
        {
            // The game starts this program as soon as the update is downloaded and verified, before the player
            // chooses Restart or Later, so the window opens only once the game is on its way out (tested 2026-11-10:
            // opened at once, it sat over the game's Restart prompt, and after Later it would stay for the session)
            WaitForExit(Get(options, "pid"), () => window ??= InstallWindow.Open(Get(options, "texts"), Log));
            window ??= InstallWindow.Open(Get(options, "texts"), Log);
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Keeps the install window up until the game it started is showing, so there is never a gap with nothing on
    // screen. The game's process has its main window (UnityWndClass) within about 3 s, but in one 2026-11-10 test
    // nothing of the game showed for about 7 s after that, so closing on the handle alone left the desktop bare.
    // Closes when the game's window is the foreground window; else 15 s after its window appeared; else after 60 s.
    // In practice the 15 s rule is the one that closes it: when the old game's window goes, Windows gives the focus to
    // the shell, so neither this program nor the game it starts may take the foreground (Windows' foreground lock),
    // and the game stays drawn on top without becoming the foreground window (seen in the second 2026-11-10 test,
    // where the game covered the screen 1 s after its window appeared). This window then just sits behind the game
    // until it closes. Without a game, a failed install's message stays readable for a few seconds instead. Nothing to
    // wait for when there is no window. Logs what it saw.
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

        var started = DateTime.Now;
        DateTime? windowAt = null;

        try
        {
            while ((DateTime.Now - started).TotalSeconds < 60 && !game.HasExited)
            {
                game.Refresh();

                var handle = game.MainWindowHandle;

                if (handle != IntPtr.Zero && windowAt == null)
                {
                    windowAt = DateTime.Now;
                    Log($"The game's window appeared after {(windowAt.Value - started).TotalSeconds:0.0} s");
                }

                if (handle != IntPtr.Zero && GetForegroundWindow() == handle)
                {
                    Log($"The game came to the front after {(DateTime.Now - started).TotalSeconds:0.0} s");

                    return;
                }

                if (windowAt != null && (DateTime.Now - windowAt.Value).TotalSeconds >= 15)
                {
                    Log("The game's window didn't come to the front within 15 s");

                    return;
                }

                Thread.Sleep(250);
            }

            Log(game.HasExited ? "The started game ended" : "No game window within 60 s");
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

    // Waits for the game to exit. onClosing runs once the game is closing: its window, seen while it ran, has gone
    // (Unity removes it at quit, while the process can take another half minute to end, e.g. through the known quit
    // crash and its dump), or the process ended. Polled twice a second, which costs nothing while the player keeps
    // playing after Later.
    private static void WaitForExit(string pid, Action onClosing)
    {
        if (!int.TryParse(pid, out var id))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            var windowSeen = false;
            var closingReported = false;

            Log($"Waiting for process {id} to exit");

            while (!process.WaitForExit(500))
            {
                if (closingReported)
                {
                    continue;
                }

                process.Refresh();

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    windowSeen = true;
                }
                else if (windowSeen)
                {
                    Log($"Process {id} closed its window");
                    closingReported = true;
                    onClosing();
                }
            }
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
