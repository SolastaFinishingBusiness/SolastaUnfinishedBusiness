using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SolastaUnfinishedBusiness.Installer;

// The small window the helper shows from the moment the game closes its window until the game it starts again is in
// front (user direction, 2026-11-10; when exactly: Program.WaitForExit and Program.WaitForGameWindow). Before it,
// the helper ran hidden: for the half minute to a minute between Restart and the game coming back nothing was on
// screen, while Steam showed the game as still running (STOP, seen in a test on 2026-11-09), so a player couldn't tell
// the update was being installed, or that Solasta would start again by itself. The window names the current step (waiting for the game to exit, installing or restoring, starting
// Solasta), says not to start or stop the game meanwhile, and shows a moving bar, since the steps have no measurable
// progress. It has no buttons or close box; the helper closes it once the game it started is in front, or shortly
// after if it couldn't start one. Closing it with Alt+F4 only removes the window: the install carries on.
//
// It runs on its own STA thread, so the install on the main thread is never held up by the window, and every call from
// the main thread is marshalled with BeginInvoke. If the window can't be created (no desktop, a missing GUI library),
// the helper works as before without it: every member is safe to call when the window never appeared.
internal sealed class InstallWindow : Form
{
    private const int SwShowNormal = 1;

    private readonly Label _status;
    private readonly Label _hint;
    private readonly ProgressBar _bar;
    private readonly Button _close;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private readonly bool _rollback;
    private bool _closeShown;

    // set by Open when it gave up waiting: the window then closes as soon as it shows
    private volatile bool _abandoned;

    private InstallWindow(IReadOnlyDictionary<string, string> texts, string mode)
    {
        Texts = texts;
        _rollback = mode == "rollback";
        Text = Get("title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 150);
        Font = new Font("Segoe UI", 9.75f);

        // Both texts wrap at 420 px and take the height they need: the status lines were a fixed 24 px, and the failure
        // text needs two lines in several languages, so its second line was cut off (Codex's re-review of PR #30,
        // measured with TextRenderer). Relayout stacks everything under them whenever a text changes.
        _status = new Label
        {
            Left = 20, Top = 18, AutoSize = true, MaximumSize = new Size(420, 0), Text = Get("waiting")
        };
        _bar = new ProgressBar
        {
            Left = 20, Width = 420, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30
        };
        _hint = new Label { Left = 20, AutoSize = true, MaximumSize = new Size(420, 0), Text = Get("hint") };

        // only shown when the player has to act (ShowStep "notstarted"): the window then stays until it is closed
        _close = new Button { Left = 340, Width = 100, Height = 28, Text = Get("close"), Visible = false };
        _close.Click += (_, _) => Close();

        Controls.AddRange([_status, _bar, _hint, _close]);
        FormClosed += (_, _) => _closed.Set();
        Relayout();
    }

    // status text, then the bar, then the hint, then the OK button when shown; the window's height follows
    private void Relayout()
    {
        _bar.Top = _status.Bottom + 10;
        _hint.Top = _bar.Bottom + 12;

        var bottom = _hint.Bottom;

        // its own flag, not _close.Visible: Control.Visible also reflects the parent, so it reads false whenever the
        // window itself isn't on screen (yet)
        if (_closeShown)
        {
            _close.Top = bottom + 12;
            bottom = _close.Bottom;
        }

        ClientSize = new Size(ClientSize.Width, bottom + 16);
    }

    private IReadOnlyDictionary<string, string> Texts { get; }

    // English texts for a missing or incomplete texts file (the game writes them in its language)
    private static readonly Dictionary<string, string> Fallback = new()
    {
        ["title"] = "Finishing Business update",
        ["waiting"] = "Waiting for Solasta to close…",
        ["working"] = "Installing the update…",
        ["restoring"] = "Restoring the previous version…",
        ["starting"] = "Starting Solasta…",
        ["failed"] = "Something went wrong. Solasta will start again and show what happened.",
        ["hint"] = "Solasta will start again by itself. Please don't start or stop it in Steam until then.",
        ["notstarted"] = "Solasta couldn't be started again.",
        // OK first: while this program runs, Steam counts the game as running and shows STOP, not Play (seen in a test
        // on 2026-11-09; Codex's re-review of PR #30). The game's copy names its own localized OK button.
        ["notstartedhint"] = "Press OK, then start Solasta from Steam. It will show how the update went.",
        ["close"] = "OK"
    };

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    // the game's text for the key, else the English fallback; "working" falls back by mode, so a rollback without its
    // texts says it is restoring, not installing (Codex's full review of PR #30)
    private string Get(string key)
    {
        if (Texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return key == "working" && _rollback ? Fallback["restoring"] : Fallback[key];
    }

    // Opens the window on its own thread and waits (briefly) until it is on screen. Returns the window only once it
    // has actually been shown with its message loop running; otherwise null, and the helper carries on without one.
    // Before, a window that was constructed but never shown (its thread failed, or it was still starting after 5 s)
    // was returned anyway: every Post to it was dropped, so the "not started" message never appeared while the helper
    // waited up to 10 minutes for an OK nobody could see, with Steam showing STOP all along (Codex's full review of
    // PR #30). A window that shows up after the wait gave up closes itself at once (_abandoned), and _closed is set
    // whenever the window's thread ends, so a waiter is never left hanging on a window that is gone.
    internal static InstallWindow Open(string textsFile, string mode, Action<string> log)
    {
        var texts = ReadTexts(textsFile, log);
        InstallWindow window = null;
        var settled = new ManualResetEventSlim(false); // shown, or the thread ended

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    window = new InstallWindow(texts, mode);
                    window.Shown += (_, _) =>
                    {
                        if (window._abandoned)
                        {
                            window.Close();

                            return;
                        }

                        // the game started this program with a hidden-window hint in older versions; the first
                        // ShowWindow of a process can take that hint instead of showing, so show it once more
                        ShowWindow(window.Handle, SwShowNormal);
                        window._ready.Set();
                        settled.Set();
                    };
                    Application.Run(window);
                }
                catch (Exception e)
                {
                    log($"The install window failed: {e.Message}");
                }
                finally
                {
                    window?._closed.Set();
                    settled.Set();
                }
            }) { IsBackground = true, Name = "InstallWindow" };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // never wait long: the install mustn't depend on the window
            settled.Wait(TimeSpan.FromSeconds(5));

            if (window is { _ready.IsSet: true, IsDisposed: false } && !window._closed.IsSet)
            {
                return window;
            }

            log("The install window didn't open in time; carrying on without it");

            if (window != null)
            {
                window._abandoned = true;
                window.Post(window.Close);
            }

            return null;
        }
        catch (Exception e)
        {
            log($"Couldn't open the install window: {e.Message}");

            return null;
        }
    }

    // key=value lines (UpdateContext.WriteInstallerTexts); "\n" in a value is a line break
    private static Dictionary<string, string> ReadTexts(string file, Action<string> log)
    {
        var texts = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return texts;
        }

        try
        {
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                var parts = line.Split(['='], 2);

                if (parts.Length == 2)
                {
                    texts[parts[0].Trim()] = parts[1].Replace("\\n", Environment.NewLine);
                }
            }
        }
        catch (Exception e)
        {
            log($"Couldn't read the window texts: {e.Message}");
        }

        return texts;
    }

    // The step shown: "waiting", "working", "starting", "failed" or "notstarted". "failed" and "notstarted" stop the
    // moving bar. "notstarted" (the helper couldn't start the game again; Codex's review of PR #30) also replaces the
    // hint with what to do, and shows the OK button, since nothing else will follow on screen: see WaitUntilClosed.
    internal void ShowStep(string step)
    {
        Post(() =>
        {
            _status.Text = Get(step);

            if (step is "failed" or "notstarted")
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = _bar.Maximum;
            }

            if (step == "notstarted")
            {
                _hint.Text = Get("notstartedhint");
                _closeShown = true;
                _close.Visible = true;
                _close.Focus();
            }

            Relayout();
        });
    }

    // waits until the player closes the window (or the time runs out); false if it timed out
    internal bool WaitUntilClosed(TimeSpan timeout)
    {
        return _closed.Wait(timeout);
    }

    internal void CloseWindow()
    {
        Post(Close);
    }

    private void Post(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(action);
            }
        }
        catch (Exception)
        {
            // the window already went (or never came); the helper carries on without it
        }
    }
}
