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
            // one attempt only: a window that failed to open isn't tried again
            var windowTried = false;

            void OpenWindow()
            {
                if (windowTried)
                {
                    return;
                }

                windowTried = true;
                window = InstallWindow.Open(Get(options, "texts"), Get(options, "mode"), Log);
            }

            WaitForExit(Get(options, "pid"), OpenWindow);
            OpenWindow();
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
            WaitForGameWindow(game, Get(options, "relaunch"), window);
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
    // until it closes. Nothing to wait for when there is no window. Logs what it saw. Waits are measured with a
    // Stopwatch, so a clock change can't stretch or cut them (Codex's full review of PR #30).
    // When no game comes up, the window says so, tells the player to press OK and start Solasta from Steam, and stays
    // until it's closed or 10 minutes pass (it used to close after 5 s showing "Starting Solasta…"; Codex's review of
    // PR #30). That covers a game that couldn't be started at all, and one that ended without ever showing a window
    // when no other Solasta took its place within 20 s. A quick exit alone isn't a failure: when Steam has to start the
    // game itself, the game quits at once and Steam starts a new one, which is then watched instead.
    private static void WaitForGameWindow(Process game, string executable, InstallWindow window)
    {
        if (window == null)
        {
            return;
        }

        if (game == null)
        {
            ShowNotStarted(window);

            return;
        }

        if (WatchGame(game) != GameOutcome.EndedWithoutWindow)
        {
            return;
        }

        using var replacement = WaitForOtherGame(Path.GetFileNameWithoutExtension(executable), game.Id,
            TimeSpan.FromSeconds(20));

        if (replacement != null)
        {
            Log($"Another Solasta started (pid {replacement.Id}); watching it instead");

            if (WatchGame(replacement) != GameOutcome.EndedWithoutWindow)
            {
                return;
            }
        }

        Log("No Solasta came up after the restart");
        ShowNotStarted(window);
    }

    private enum GameOutcome
    {
        Showing,
        EndedWithoutWindow,
        NoWindowYet
    }

    // the started game until it shows (front, or 15 s after its window appeared), ends, or 60 s pass
    private static GameOutcome WatchGame(Process game)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan? windowAt = null;

        try
        {
            while (clock.Elapsed.TotalSeconds < 60 && !game.HasExited)
            {
                if (!TryGetMainWindow(game, out var handle))
                {
                    break;
                }

                if (handle != IntPtr.Zero && windowAt == null)
                {
                    windowAt = clock.Elapsed;
                    Log($"The game's window appeared after {windowAt.Value.TotalSeconds:0.0} s");
                }

                if (handle != IntPtr.Zero && GetForegroundWindow() == handle)
                {
                    Log($"The game came to the front after {clock.Elapsed.TotalSeconds:0.0} s");

                    return GameOutcome.Showing;
                }

                if (windowAt != null && (clock.Elapsed - windowAt.Value).TotalSeconds >= 15)
                {
                    Log("The game's window didn't come to the front within 15 s");

                    return GameOutcome.Showing;
                }

                Thread.Sleep(250);
            }

            if (game.HasExited || !TryGetMainWindow(game, out _))
            {
                Log(windowAt == null ? "The started game ended without a window" : "The started game ended");

                return windowAt == null ? GameOutcome.EndedWithoutWindow : GameOutcome.Showing;
            }

            Log("No game window within 60 s");

            return GameOutcome.NoWindowYet;
        }
        catch (Exception e)
        {
            Log($"Couldn't watch the started game: {e.Message}");

            return GameOutcome.NoWindowYet;
        }
    }

    // another process of the game's executable than the one that ended (Steam's own launch), or null if none appears
    private static Process WaitForOtherGame(string processName, int endedId, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout)
        {
            var others = Process.GetProcessesByName(processName);
            var found = others.FirstOrDefault(x => x.Id != endedId);

            foreach (var other in others.Where(x => x != found))
            {
                other.Dispose();
            }

            if (found != null)
            {
                return found;
            }

            Thread.Sleep(500);
        }

        Log($"No other {processName} within {timeout.TotalSeconds:0} s");

        return null;
    }

    private static void ShowNotStarted(InstallWindow window)
    {
        window.ShowStep("notstarted");

        if (!window.WaitUntilClosed(TimeSpan.FromMinutes(10)))
        {
            Log("Nobody closed the 'not started' message within 10 minutes");
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

    // The process's main window, or IntPtr.Zero while it has none. False when the process ended meanwhile: on .NET
    // Framework, MainWindowHandle with no window falls back to reading the process's information, which throws
    // InvalidOperationException once the process is gone (Codex's review of PR #30). That happens when the game closes
    // its window and exits within one poll; left uncaught, it reached Main's install-failure handler and skipped a
    // valid update.
    private static bool TryGetMainWindow(Process process, out IntPtr handle)
    {
        try
        {
            process.Refresh();
            handle = process.MainWindowHandle;

            return true;
        }
        catch (InvalidOperationException)
        {
            handle = IntPtr.Zero;

            return false;
        }
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

                if (!TryGetMainWindow(process, out var handle))
                {
                    // the process ended since WaitForExit(500) returned: the next WaitForExit ends the loop
                    continue;
                }

                if (handle != IntPtr.Zero)
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
