using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityModManagerNet;
using Debug = UnityEngine.Debug;

namespace SolastaUnfinishedBusiness.Models;

// The in-game updater. Checking, downloading and verifying run in the background, so launch and play stay responsive,
// and an update found by the automatic check is only offered at the main menu. A download is verified against the
// release's published size and SHA-256 and its contents are checked before anything is installed. On Windows the
// verified update is installed after the game exits, by a helper (FinishingBusinessInstaller.exe, copied outside the
// mod folder) that applies it through ModInstallTransaction, keeps a backup, writes its result and starts the game
// again; the next start reports that result, so starting the helper never counts as an installed update. Elsewhere,
// where open files can be replaced, it is installed at once and needs a restart. Rollback restores the backup of the
// last update, through the same helper, and is only offered while that backup is complete.
internal static class UpdateContext
{
    private const string PackageName = "SolastaUnfinishedBusiness.zip";
    private const string InstallerName = "FinishingBusinessInstaller.exe";
    private const string ModId = "SolastaUnfinishedBusiness";

    // backups of in-game updates made before 1.9.1, which replaced files while the game ran
    private const string ArchiveFolderName = "Archive";
    private const string AddedFilesList = "added-files.txt";

    // older update backups beyond these are removed once a newer update is reported
    private const int KeptTransactions = 2;

    private static readonly ConcurrentQueue<Action> MainThread = new();
    private static readonly List<Action> PendingPrompts = [];

    // the text last put in the download's progress message, or null when that message isn't open, and whether that
    // message has the progress bar
    [CanBeNull] private static string _progressMessageText;
    private static bool _progressMessageHasBar;

    private static string ArchiveFolder => Path.Combine(Main.ModFolder, ArchiveFolderName);

    // beside the game's Mods folder, never inside the mod folder the update replaces
    private static string UpdatesFolder =>
        Path.GetFullPath(Path.Combine(Main.ModFolder, "..", "..", "FinishingBusinessUpdates"));

    private static string TransactionsFolder => Path.Combine(UpdatesFolder, "Transactions");
    private static string ResultFile => Path.Combine(UpdatesFolder, "result.txt");
    private static string LogFile => Path.Combine(UpdatesFolder, "installer.log");

    private static InfoJson Info { get; set; }
    private static string InstalledVersion { get; set; }
    private static string LatestVersion { get; set; }
    private static RollbackTarget Previous { get; set; }

    internal static UpdateState State { get; private set; }
    private static string StateDetail { get; set; }
    internal static int Progress { get; private set; }

    internal static bool InProgress => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying;
    internal static bool IsUpdateAvailable => State == UpdateState.Available;
    internal static bool CanCheck => !InProgress && State is not (UpdateState.Queued or UpdateState.Installed);
    internal static bool CanRollback => Previous != null && !InProgress && State != UpdateState.Queued;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal enum UpdateState
    {
        Idle,
        Checking,
        UpToDate,
        CheckFailed,
        Available,
        Downloading,
        Verifying,
        Queued,
        Installed,
        Failed
    }

    // The version of the code actually running: the build version compiled into this DLL (the project file's
    // FbComputeBuildVersion target, the same string as the built Info.json's "Version", "-dev.<commit>" included).
    // Null only for a DLL built without that target. Unity Mod Manager's version, and everything shown from it, comes
    // from Info.json, which can be newer than the DLL that was loaded (see CheckLoadedVersion).
    [CanBeNull]
    internal static string LoadedBuildVersion { get; } = typeof(UpdateContext).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == "FbBuildVersion")?.Value;

    // what to report as running: the loaded code's version, or Info.json's for a DLL without the mark
    internal static string RunningVersion => LoadedBuildVersion ?? Main.ModVersion;

    internal static void Load()
    {
        var infoPayload = File.ReadAllText(Path.Combine(Main.ModFolder, "Info.json"));

        Info = JsonConvert.DeserializeObject<InfoJson>(infoPayload);
        InstalledVersion = Info.Version;

        var driver = new GameObject("FbUpdater");

        UnityEngine.Object.DontDestroyOnLoad(driver);
        driver.AddComponent<Driver>();

        CheckLoadedVersion();
        ReportInstallResult();
        Previous = FindRollbackTarget();
        CustomModels.AlertIfModelsNotFound();

        if (Main.Settings.DisplayModMessage == 0)
        {
            DisplayWelcomeMessage();
        }

        // display mod message every 100 launches
        Main.Settings.DisplayModMessage = (Main.Settings.DisplayModMessage + 1) % 100;

        CheckForUpdates(false);
    }

    // what the mod menu shows under its update buttons
    internal static string StatusText => State switch
    {
        UpdateState.Checking => Gui.Localize("ModUi/&UpdateStatusChecking"),
        UpdateState.UpToDate => Gui.Format("ModUi/&UpdateStatusUpToDate", InstalledVersion),
        UpdateState.CheckFailed => Gui.Format("ModUi/&UpdateStatusCheckFailed", StateDetail),
        UpdateState.Available => Gui.Format("ModUi/&UpdateStatusAvailable", LatestVersion),
        UpdateState.Downloading => Gui.Format("ModUi/&UpdateStatusDownloading", LatestVersion, Progress.ToString()),
        UpdateState.Verifying => Gui.Format("ModUi/&UpdateStatusVerifying", LatestVersion),
        UpdateState.Queued => Gui.Format("ModUi/&UpdateStatusQueued", StateDetail),
        UpdateState.Installed => Gui.Format("ModUi/&UpdateStatusInstalled", StateDetail),
        UpdateState.Failed => Gui.Format("ModUi/&UpdateStatusFailed", StateDetail),
        _ => string.Empty
    };

    #region Checking

    // the automatic check at launch offers an update at the main menu; a check asked for reports its result at once
    internal static void CheckForUpdates(bool manual)
    {
        if (!CanCheck)
        {
            if (manual)
            {
                ShowMessage(StatusText, "Message/&MessageOkTitle");
            }

            return;
        }

        State = UpdateState.Checking;
        StateDetail = null;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var version = FetchLatestVersion();

                MainThread.Enqueue(() => OnChecked(version, null, manual));
            }
            catch (Exception e)
            {
                MainThread.Enqueue(() => OnChecked(null, e.Message, manual));
            }
        });
    }

    private static string FetchLatestVersion()
    {
        using var wc = NewWebClient();
        var infoJson = JsonConvert.DeserializeObject<JObject>(wc.DownloadString(Info.VersionURL));
        var version = infoJson?["Version"]?.Value<string>();

        if (string.IsNullOrEmpty(version) || version!.Split('.').Any(x => !int.TryParse(x, out _)))
        {
            throw new InvalidDataException("the latest version can't be read");
        }

        return version;
    }

    // a failed check is never reported as up to date
    private static void OnChecked(string version, string error, bool manual)
    {
        if (error != null)
        {
            State = UpdateState.CheckFailed;
            StateDetail = error;
            Main.Error($"cannot fetch update data: {error}");

            if (manual)
            {
                ShowMessage(Gui.Format("Message/&UpdateCheckFailed", error), "Message/&MessageOkTitle");
            }

            return;
        }

        LatestVersion = version;

        if (CompareVersions(version, InstalledVersion) > 0)
        {
            State = UpdateState.Available;

            if (manual)
            {
                DisplayUpdateMessage();
            }
            else
            {
                PendingPrompts.Add(DisplayUpdateMessage);
            }

            return;
        }

        State = UpdateState.UpToDate;

        if (manual)
        {
            ShowMessage(Gui.Format("Message/&UpdateUpToDate", InstalledVersion), "Message/&MessageOkTitle");
        }
    }

    // part by part as numbers, so 1.6.10.0 is newer than 1.6.9.0; a missing part counts as 0. A development build's
    // mark ("-dev.<commit>", see the project file's StampBuildVersion) is left out, so such a build counts as its
    // release's version: no update back to that release, and an update to the next one
    private static int CompareVersions(string left, string right)
    {
        var a = WithoutMark(left).Split('.');
        var b = WithoutMark(right).Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var parsedA) ? parsedA : 0;
            var y = i < b.Length && int.TryParse(b[i], out var parsedB) ? parsedB : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    // the version without a pre-release mark: everything from the first "-" on
    private static string WithoutMark(string version)
    {
        var index = version.IndexOf('-');

        return index < 0 ? version : version.Substring(0, index);
    }

    #endregion

    #region Updating

    internal static void UpdateMod()
    {
        if (State != UpdateState.Available)
        {
            ShowMessage(StatusText, "Message/&MessageOkTitle");

            return;
        }

        var version = LatestVersion;

        State = UpdateState.Downloading;
        Progress = 0;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var staged = DownloadAndVerify(version);

                MainThread.Enqueue(() => OnStaged(version, staged));
            }
            catch (Exception e)
            {
                MainThread.Enqueue(() => Fail(version, e.Message));
            }
        });

        // on the next frame: UpdateMod usually runs from the update message's own Update button, while the game is
        // still closing that message (MessageModal.OnEndHide calls the button's handler, then GuiPanel.OnEndHide, which
        // unhooks the input-scheme handler that a message shown inside the handler would just have hooked)
        MainThread.Enqueue(ShowProgressMessage);
    }

    // background thread: the release's package, checked against its published size and SHA-256, then its contents;
    // returns the staged mod folder
    private static string DownloadAndVerify(string version)
    {
        var folder = Path.Combine(UpdatesFolder, version);
        var package = Path.Combine(folder, PackageName);
        var staged = Path.Combine(folder, "staged");
        var (url, size, sha256) = FetchPackageInfo(version);

        Directory.CreateDirectory(folder);

        using (var wc = NewWebClient())
        using (var input = wc.OpenRead(url) ?? throw new IOException("no download stream"))
        using (var output = File.Create(package))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                done += read;
                Progress = size > 0 ? (int)Math.Min(100, done * 100 / size) : 0;
            }
        }

        MainThread.Enqueue(() => State = UpdateState.Verifying);

        if (new FileInfo(package).Length != size)
        {
            throw new InvalidDataException("Message/&UpdateVerifySize");
        }

        using (var stream = File.OpenRead(package))
        using (var hash = SHA256.Create())
        {
            var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty);

            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Message/&UpdateVerifyHash");
            }
        }

        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, true);
        }

        Extract(package, staged);

        var mod = Path.Combine(staged, ModId);
        var info = Path.Combine(mod, "Info.json");
        var infoJson = File.Exists(info) ? JsonConvert.DeserializeObject<JObject>(File.ReadAllText(info)) : null;

        if (infoJson?["Id"]?.Value<string>() != ModId || infoJson["Version"]?.Value<string>() != version ||
            !File.Exists(Path.Combine(mod, ModId + ".dll")) ||
            Directory.GetFiles(staged, "*", SearchOption.AllDirectories).Any(x => Path.GetDirectoryName(x) != mod &&
                !Path.GetFullPath(x).StartsWith(mod + Path.DirectorySeparatorChar)))
        {
            throw new InvalidDataException("Message/&UpdateVerifyContents");
        }

        // the player's own settings are never replaced
        if (Directory.GetFiles(mod, "Settings.xml", SearchOption.AllDirectories).Length > 0)
        {
            throw new InvalidDataException("Message/&UpdateVerifySettings");
        }

        File.Delete(package);

        return mod;
    }

    // the release's package: its download address, size and the SHA-256 GitHub publishes for it
    private static (string url, long size, string sha256) FetchPackageInfo(string version)
    {
        var repository = new Uri(Info.Repository).AbsolutePath.Trim('/');

        using var wc = NewWebClient();

        wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";

        var release = JsonConvert.DeserializeObject<JObject>(
            wc.DownloadString($"https://api.github.com/repos/{repository}/releases/tags/{version}"));
        var asset = release?["assets"]?.FirstOrDefault(x => x["name"]?.Value<string>() == PackageName);
        var digest = asset?["digest"]?.Value<string>();

        if (asset == null || string.IsNullOrEmpty(digest) || !digest!.StartsWith("sha256:"))
        {
            throw new InvalidDataException("Message/&UpdateVerifyNoChecksum");
        }

        return (asset["browser_download_url"]!.Value<string>(), asset["size"]!.Value<long>(),
            digest.Substring("sha256:".Length));
    }

    // every entry must land inside the staging folder
    private static void Extract(string package, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(package);

        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Message/&UpdateVerifyContents");
            }

            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
            {
                Directory.CreateDirectory(path);

                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
    }

    private static void OnStaged(string version, string staged)
    {
        var transaction = Path.Combine(TransactionsFolder, $"{version}-{DateTime.Now:yyyyMMdd-HHmmss}");

        if (StartInstaller("install", version, staged, transaction))
        {
            State = UpdateState.Queued;
            StateDetail = version;
            // "Would you like to restart Solasta to install the mod?" with Restart and Later (user direction,
            // 2026-11-02): quitting is the restart, since the helper installs the update and starts the game again
            ShowMessage(Gui.Format("Message/&UpdateQueued", version), "Message/&UpdateRestartTitle", Application.Quit,
                "Message/&MessageLaterTitle");

            return;
        }

        // without the helper (not Windows, or no helper in this build) the files are replaced now
        try
        {
            Directory.CreateDirectory(TransactionsFolder);
            ModInstallTransaction.Apply(staged, Main.ModFolder, transaction);
            State = UpdateState.Installed;
            StateDetail = version;
            ShowMessage(Gui.Format("Message/&UpdateInstalledRestart", version), "Message/&MessageOkTitle");
        }
        catch (Exception e)
        {
            Main.Error($"Failed to install update {version}: {e}");
            // untouched: stopped before its journal (written just before the first change), or put everything back
            var untouched = !File.Exists(Path.Combine(transaction, "journal.xml")) ||
                            File.Exists(Path.Combine(transaction, "recovered"));

            Fail(version, Gui.Format(untouched
                ? "Message/&UpdateFailedRestored"
                : "Message/&UpdateFailedNotRestored", e.Message, transaction));
        }
    }

    // Unity Mod Manager reads Info.json, then loads the DLL through its own cached copy, named only by the DLL file's
    // time (SolastaUnfinishedBusiness.dll.<id>.cache; evidence/updater-stale-dll). Whatever the cause, when the code
    // that runs isn't the version Info.json names, everything that shows the version (the mod manager, the lobby, the
    // update result) is wrong, and multiplayer goes out of sync with players who run the real build. A player once saw
    // 1.14.0 and desynced until a full restart (2026-11-08). This says so at the main menu and in the log; the version
    // published to other players is the loaded one (MultiplayerSessionContext.BuildVersion), so they see it too.
    private static void CheckLoadedVersion()
    {
        if (LoadedBuildVersion == null || LoadedBuildVersion == Main.ModVersion)
        {
            return;
        }

        Main.Error($"Info.json says {Main.ModVersion}, but the loaded DLL is {LoadedBuildVersion}; restart the game");
        PendingPrompts.Add(() => ShowMessage(
            Gui.Format("Message/&UpdateLoadedVersionMismatch", Main.ModVersion, LoadedBuildVersion),
            "Message/&MessageOkTitle", severity: MessageModal.Severity.Serious3));
    }

    // reason is a term (from the background checks) or a system message
    private static void Fail(string version, string reason)
    {
        if (reason.StartsWith("Message/&"))
        {
            reason = Gui.Localize(reason);
        }

        State = UpdateState.Failed;
        StateDetail = $"{version}: {reason}";
        Main.Error($"Update to {version} failed: {reason}");
        ShowMessage(Gui.Format("Message/&UpdateFailed", version, reason), "Message/&MessageOkTitle",
            severity: MessageModal.Severity.Serious3);
    }

    // Windows only: the helper waits for this game to exit, then installs (or rolls back) and starts the game again
    private static bool StartInstaller(string mode, string version, string staged, string transaction)
    {
        var installer = Path.Combine(Main.ModFolder, InstallerName);

        if (!IsWindows || !File.Exists(installer))
        {
            return false;
        }

        try
        {
            var folder = Path.Combine(UpdatesFolder, "installer");
            var copy = Path.Combine(folder, InstallerName);

            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(TransactionsFolder);
            File.Copy(installer, copy, true);

            using var game = Process.GetCurrentProcess();

            // Solasta_Data sits beside Solasta.exe
            var executable = Path.Combine(Path.GetDirectoryName(Application.dataPath)!,
                Path.GetFileName(Application.dataPath).Replace("_Data", string.Empty) + ".exe");
            var arguments = new List<string>
            {
                "--pid", game.Id.ToString(),
                "--mode", mode,
                "--version", version,
                "--target", Main.ModFolder,
                "--transaction", transaction,
                "--result", ResultFile,
                "--log", LogFile,
                "--relaunch", executable
            };

            if (staged != null)
            {
                arguments.AddRange(["--source", staged]);
            }

            // the helper's window texts, in the game's language (the helper can't read the mod's translations)
            var texts = Path.Combine(folder, "texts.txt");

            WriteInstallerTexts(texts, mode, version);
            arguments.AddRange(["--texts", texts]);

            // not hidden: from this version the helper shows a small window while the game is closed, so the player
            // can see the update being installed and that Solasta starts again by itself (Steam shows the game as
            // running, STOP, the whole time; evidence/updater-steam-state). CreateNoWindow only concerns consoles.
            Process.Start(new ProcessStartInfo(copy,
                string.Join(" ", arguments.Select(x => "\"" + x.TrimEnd('\\') + "\"")))
            {
                UseShellExecute = false, CreateNoWindow = true
            });

            Main.Info($"Update helper started ({mode} {version}); it acts after the game exits");

            return true;
        }
        catch (Exception e)
        {
            Main.Error($"Couldn't start the update helper: {e.Message}");

            return false;
        }
    }

    // key=value lines, UTF-8, read by the helper's InstallWindow; line breaks inside a text are written as "\n". The
    // version is filled in here, so the helper only shows the texts. A missing or unreadable file makes it use English.
    private static void WriteInstallerTexts(string path, string mode, string version)
    {
        string Line(string key, string text) => key + "=" + text.Replace("\r", string.Empty).Replace("\n", "\\n");

        File.WriteAllLines(path,
        [
            Line("title", Gui.Localize("Message/&UpdateInstallerTitle")),
            Line("waiting", Gui.Localize("Message/&UpdateInstallerWaiting")),
            Line("working", Gui.Format(mode == "rollback"
                ? "Message/&UpdateInstallerRollingBack"
                : "Message/&UpdateInstallerInstalling", version)),
            Line("starting", Gui.Localize("Message/&UpdateInstallerStarting")),
            Line("failed", Gui.Localize("Message/&UpdateInstallerFailed")),
            Line("hint", Gui.Localize("Message/&UpdateInstallerHint"))
        ], new System.Text.UTF8Encoding(false));
    }

    #endregion

    #region Results and rollback

    // the helper's result from the last exit, reported once and then kept beside it under its time
    private static void ReportInstallResult()
    {
        if (!File.Exists(ResultFile))
        {
            return;
        }

        Dictionary<string, string> result;

        try
        {
            result = File.ReadAllLines(ResultFile)
                .Select(x => x.Split(['='], 2))
                .Where(x => x.Length == 2)
                .ToDictionary(x => x[0], x => x[1]);
            File.Move(ResultFile, Path.Combine(UpdatesFolder, $"result-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
        }
        catch (Exception e)
        {
            Main.Error($"Couldn't read the update result: {e.Message}");

            return;
        }

        string Value(string key) => result.TryGetValue(key, out var value) ? value : string.Empty;

        var version = Value("version");
        // compared with the code that is running, not only Info.json: an update counts as installed only when this
        // game runs it (see CheckLoadedVersion)
        var running = LoadedBuildVersion ?? InstalledVersion;
        var message = Value("status") switch
        {
            "installed" when version == running => Gui.Format("Message/&UpdateResultInstalled", version),
            "installed" => Gui.Format("Message/&UpdateResultMismatch", version, running, Value("log")),
            "failed" when Value("restored") == "yes" =>
                Gui.Format("Message/&UpdateResultFailedRestored", version, Value("message"), Value("transaction"),
                    Value("log")),
            "failed" => Gui.Format("Message/&UpdateResultFailed", version, Value("message"), Value("transaction"),
                Value("log")),
            "rolledback" => Gui.Format("Message/&UpdateResultRolledBack", InstalledVersion),
            _ => Gui.Format("Message/&UpdateResultRollbackFailed", Value("message"), Value("transaction"),
                Value("log"))
        };

        var succeeded = (Value("status") == "installed" && version == running) ||
                        Value("status") == "rolledback";

        PendingPrompts.Add(() => ShowMessage(message, "Message/&MessageOkTitle",
            severity: succeeded ? MessageModal.Severity.Informative1 : MessageModal.Severity.Serious3));

        if (Value("status") == "installed")
        {
            RemoveOldTransactions();
        }
    }

    // the newest update backup that is complete enough to put back, or a backup of the old in-game updater
    private static RollbackTarget FindRollbackTarget()
    {
        if (Directory.Exists(TransactionsFolder))
        {
            foreach (var folder in Directory.GetDirectories(TransactionsFolder)
                         .OrderByDescending(Directory.GetCreationTimeUtc))
            {
                if (File.Exists(Path.Combine(folder, "recovered")))
                {
                    continue;
                }

                var target = TransactionTarget(folder);

                if (target != null)
                {
                    return target;
                }
            }
        }

        return LegacyTarget();
    }

    // every file the journal says existed must still be in the backup, and the backup must say which version it is
    private static RollbackTarget TransactionTarget(string folder)
    {
        try
        {
            var journal = XElement.Load(Path.Combine(folder, "journal.xml"));
            var backup = Path.Combine(folder, "backup");

            if (journal.Elements("file").Any(x =>
                    (bool)x.Attribute("existed") && !File.Exists(Path.Combine(backup, (string)x.Attribute("path")))))
            {
                return null;
            }

            var version = ReadVersion(Path.Combine(backup, "Info.json"));
            var unfinished = !File.Exists(Path.Combine(folder, "complete"));

            // a finished update only rolls back from the version it installed
            if (string.IsNullOrEmpty(version) ||
                (!unfinished && ReadVersion(Path.Combine(folder, "staged", "Info.json")) != InstalledVersion))
            {
                return null;
            }

            return new RollbackTarget(version, folder, unfinished);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadVersion(string infoJson)
    {
        return File.Exists(infoJson)
            ? JsonConvert.DeserializeObject<JObject>(File.ReadAllText(infoJson))?["Version"]?.Value<string>()
            : null;
    }

    private static RollbackTarget LegacyTarget()
    {
        if (!Directory.Exists(ArchiveFolder))
        {
            return null;
        }

        var archive = Directory.GetDirectories(ArchiveFolder)
            .Where(x => CompareVersions(Path.GetFileName(x), InstalledVersion) < 0)
            .Where(x => File.Exists(Path.Combine(x, AddedFilesList)) && File.Exists(Path.Combine(x, "Info.json")))
            .OrderByDescending(x => Path.GetFileName(x), Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();

        return archive == null ? null : new RollbackTarget(Path.GetFileName(archive), archive, false, true);
    }

    private static void RemoveOldTransactions()
    {
        try
        {
            foreach (var old in Directory.GetDirectories(TransactionsFolder)
                         .Where(x => File.Exists(Path.Combine(x, "complete")) ||
                                     File.Exists(Path.Combine(x, "recovered")))
                         .OrderByDescending(Directory.GetCreationTimeUtc)
                         .Skip(KeptTransactions))
            {
                Directory.Delete(old, true);
            }
        }
        catch (Exception e)
        {
            Main.Error($"Couldn't remove old update backups: {e.Message}");
        }
    }

    internal static void DisplayRollbackMessage()
    {
        if (!CanRollback)
        {
            return;
        }

        ShowMessage(Gui.Format(Previous.Unfinished ? "Message/&UpdateRollbackUnfinished" : "Message/&UpdateRollbackAsk",
                Previous.Version, Previous.Folder),
            "Message/&MessageOkTitle", Rollback, "Message/&MessageCancelTitle");
    }

    private static void Rollback()
    {
        var target = Previous;

        if (target.Legacy)
        {
            LegacyRollback(target);

            return;
        }

        if (StartInstaller("rollback", target.Version, null, target.Folder))
        {
            State = UpdateState.Queued;
            StateDetail = target.Version;
            ShowMessage(Gui.Format("Message/&UpdateRollbackQueued", target.Version), "Message/&UpdateQuitNow",
                Application.Quit, "Message/&MessageLaterTitle");

            return;
        }

        try
        {
            ModInstallTransaction.Recover(Main.ModFolder, target.Folder);
            Previous = FindRollbackTarget();
            ShowMessage(Gui.Format("Message/&UpdateRolledBackRestart", target.Version), "Message/&MessageOkTitle");
        }
        catch (Exception e)
        {
            Main.Error($"Failed to roll back to {target.Version}: {e}");
            ShowMessage(Gui.Format("Message/&UpdateRollbackFailed", target.Version, e.Message, target.Folder),
                "Message/&MessageOkTitle", severity: MessageModal.Severity.Serious3);
        }
    }

    // puts back the files the old in-game updater replaced and removes the ones it added
    private static void LegacyRollback(RollbackTarget target)
    {
        var archive = target.Folder;

        try
        {
            var addedList = Path.Combine(archive, AddedFilesList);

            foreach (var added in File.ReadAllLines(addedList).Where(x => x.Length > 0))
            {
                var file = Path.Combine(Main.ModFolder, added);

                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            foreach (var archived in Directory.GetFiles(archive, "*", SearchOption.AllDirectories)
                         .Where(file => file != addedList))
            {
                var destFile = Path.Combine(Main.ModFolder, archived.Substring(archive.Length + 1));

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }

                File.Move(archived, destFile);
            }

            Directory.Delete(archive, true);
            Previous = FindRollbackTarget();
            ShowMessage(Gui.Format("Message/&UpdateRolledBackRestart", target.Version), "Message/&MessageOkTitle");
        }
        catch (Exception err)
        {
            Main.Error($"Failed to roll back mod: {err.Message}: {err.StackTrace}");
            ShowMessage(Gui.Format("Message/&UpdateRollbackFailed", target.Version, err.Message, archive),
                "Message/&MessageOkTitle", severity: MessageModal.Severity.Serious3);
        }
    }

    private sealed class RollbackTarget(string version, string folder, bool unfinished, bool legacy = false)
    {
        internal string Version { get; } = version;
        internal string Folder { get; } = folder;
        internal bool Unfinished { get; } = unfinished;
        internal bool Legacy { get; } = legacy;
    }

    #endregion

    #region Messages

    // Update and Later; Later only closes it, and the changelog stays a button in the mod menu
    private static void DisplayUpdateMessage()
    {
        ShowMessage(Gui.Format("Message/&UpdateAvailable", LatestVersion, InstalledVersion),
            "Message/&MessageUpdateTitle", UpdateMod, "Message/&MessageLaterTitle");
    }

    // The download's progress, in a message that stays open while the update downloads and is checked (user direction,
    // 2026-11-02: show it in the game's own UI, not only in the mod menu, as a filling bar with the percentage in its
    // centre). The message says what is happening, with the bar under it (CustomUI.UpdateProgressBar: the game's own
    // timer bar, full while the download is checked). Its one button, Hide, only closes it: the download goes on, and
    // what comes next (the message offering to restart Solasta, or the failure) opens by itself as before, replacing
    // this message if it is still open (MessageModal.Show re-shows the modal with the new
    // content). The text and the bar are updated in place (RefreshProgressMessage) while this message is the one
    // showing. Without the game's bar (its window not loaded) the text shows the percentage instead.

    // what the progress message shows now: the version, whether the download is being checked, and the percentage
    private static (string version, bool checking, int percent) ProgressNow
    {
        get
        {
#if FB_DEVELOPMENT
            if (_previewStart >= 0f)
            {
                var elapsed = Time.unscaledTime - _previewStart;

                return (PreviewVersion, elapsed >= PreviewDownloadSeconds,
                    (int)Mathf.Min(100f, elapsed * 100f / PreviewDownloadSeconds));
            }
#endif

            return (LatestVersion, State == UpdateState.Verifying, State == UpdateState.Verifying ? 100 : Progress);
        }
    }

    private static bool ProgressActive
    {
        get
        {
#if FB_DEVELOPMENT
            if (_previewStart >= 0f)
            {
                return true;
            }
#endif

            return InProgress;
        }
    }

    private static string ProgressMessageText(bool withBar)
    {
        var (version, checking, percent) = ProgressNow;
        var status = checking
            ? Gui.Format("ModUi/&UpdateStatusVerifying", version)
            : withBar
                ? Gui.Format("Message/&UpdateProgressDownloading", version)
                : Gui.Format("ModUi/&UpdateStatusDownloading", version, percent.ToString());

        return status + (withBar ? UpdateProgressBar.Room : "");
    }

    private static void ShowProgressMessage()
    {
        if (!ProgressActive)
        {
            // the download already ended (its own message is showing)
            return;
        }

        _progressMessageHasBar = UpdateProgressBar.CanShow;
        _progressMessageText = ProgressMessageText(_progressMessageHasBar);
        ShowMessage(_progressMessageText, "Message/&UpdateHideTitle",
            () => _progressMessageText = null, onCancelled: () => _progressMessageText = null);

        if (!_progressMessageHasBar)
        {
            return;
        }

        var messageModal = Gui.GuiService.GetScreen<MessageModal>();

        if (UpdateProgressBar.Attach(messageModal))
        {
            RefreshProgressBar();

            return;
        }

        // the copy failed: the percentage goes back into the text (the message keeps the room it was sized with)
        _progressMessageHasBar = false;
        _progressMessageText = ProgressMessageText(false);
        messageModal.contentLabel.Text = _progressMessageText;
    }

    private static void RefreshProgressBar()
    {
        var (_, checking, percent) = ProgressNow;

        UpdateProgressBar.Set(checking ? 1f : percent / 100f, checking ? "100%" : $"{percent}%");
    }

    // every frame: puts the current step and percentage into the progress message. Only while the message showing is
    // still the progress message: its label still holds the text last put there (GuiLabel.Text returns the string as
    // set, before translation), so a message that replaced it is never written over, and the bar leaves with it (in
    // the same frame when the next message comes from the main-thread queue, which runs just before this)
    private static void RefreshProgressMessage()
    {
#if FB_DEVELOPMENT
        // a preview runs on after Hide, like a real download, and ends with the downloaded message either way
        if (_previewStart >= 0f && Time.unscaledTime - _previewStart >= PreviewDownloadSeconds + PreviewCheckSeconds)
        {
            EndProgressPreview();

            return;
        }
#endif

        if (_progressMessageText == null || Gui.GuiService == null)
        {
            UpdateProgressBar.Remove();

            return;
        }

        var messageModal = Gui.GuiService.GetScreen<MessageModal>();

        if (!ProgressActive || !messageModal || !messageModal.Visible ||
            messageModal.contentLabel.Text != _progressMessageText)
        {
            _progressMessageText = null;
            UpdateProgressBar.Remove();

            return;
        }

        if (_progressMessageHasBar)
        {
            RefreshProgressBar();
        }

        var text = ProgressMessageText(_progressMessageHasBar);

        if (text == _progressMessageText)
        {
            return;
        }

        // the modal sized itself to the first text; later ones keep its line count (only the number or the step's
        // short wording changes)
        messageModal.contentLabel.Text = text;
        _progressMessageText = text;
    }

#if FB_DEVELOPMENT
    // A development tool (compiled only into development builds, FB_DEVELOPMENT, like the vote screen preview): shows the
    // progress message with made-up progress, so the bar can be seen without a real update. It fills over
    // PreviewDownloadSeconds, stays full for PreviewCheckSeconds, then shows the downloaded message with the real one's
    // Restart and Later, where Restart only closes it; nothing is downloaded or installed, and the game never quits.
    private const string PreviewVersion = "9.9.9";
    private const float PreviewDownloadSeconds = 12f;
    private const float PreviewCheckSeconds = 3f;
    private static float _previewStart = -1f;

    internal static bool CanPreviewProgress => !InProgress && _previewStart < 0f;

    internal static void StartProgressPreview()
    {
        if (!CanPreviewProgress)
        {
            return;
        }

        _previewStart = Time.unscaledTime;
        MainThread.Enqueue(ShowProgressMessage);
    }

    private static void EndProgressPreview()
    {
        _previewStart = -1f;
        _progressMessageText = null;
        UpdateProgressBar.Remove();
        // the real message's buttons, but neither quits: Restart only closes the preview
        ShowMessage(Gui.Format("Message/&UpdateQueued", PreviewVersion), "Message/&UpdateRestartTitle", null,
            "Message/&MessageLaterTitle");
    }
#endif

    // asks before updating from the game's settings menu, which has no buttons, only toggles
    internal static void DisplayUpdateConfirmation()
    {
        if (State != UpdateState.Available)
        {
            return;
        }

        DisplayUpdateMessage();
    }

    private static void DisplayWelcomeMessage()
    {
        ShowMessage("Message/&MessageModWelcomeDescription",
            "ChangeLog", OpenChangeLog);
    }

    // at the main menu (no game loaded, no other message open), one prompt at a time
    private static void ShowPendingPrompt()
    {
        if (PendingPrompts.Count == 0 || Gui.GuiService == null ||
            ServiceRepository.GetService<IGameService>()?.Game != null)
        {
            return;
        }

        var messageModal = Gui.GuiService.GetScreen<MessageModal>();

        if (messageModal && messageModal.Visible)
        {
            return;
        }

        var prompt = PendingPrompts[0];

        PendingPrompts.RemoveAt(0);
        prompt();
    }

    private static void ShowMessage(
        string content,
        string validateCaption,
        [CanBeNull] MessageModal.MessageValidatedHandler onValidated = null,
        string cancelCaption = "",
        [CanBeNull] MessageModal.MessageCancelledHandler onCancelled = null,
        string title = "Message/&MessageModWelcomeTitle",
        MessageModal.Severity severity = MessageModal.Severity.Attention2
    )
    {
        onValidated ??= () => { };
        onCancelled ??= () => { };
        Gui.GuiService.ShowMessage(severity, title, content, validateCaption, cancelCaption, onValidated, onCancelled);

        UnityModManager.UI.Instance.ToggleWindow(false);
    }

    internal static void OpenChangeLog()
    {
        OpenUrl(Info.Changelog);
    }

    internal static void OpenDocumentation(string filename)
    {
        OpenUrl($"file://{Main.ModFolder}/Documentation/{filename}");
    }

    private static WebClient NewWebClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var wc = new WebClient { Encoding = Encoding.UTF8 };

        // GitHub's API refuses requests without one
        wc.Headers[HttpRequestHeader.UserAgent] = $"FinishingBusiness/{InstalledVersion}";

        return wc;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(url);
        }
        catch
        {
            // because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw;
            }
        }
    }

    #endregion

    // runs the background work's results on the main thread and shows waiting prompts at the main menu
    private sealed class Driver : MonoBehaviour
    {
        private void Update()
        {
            while (MainThread.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            RefreshProgressMessage();
            ShowPendingPrompt();
        }
    }
}
