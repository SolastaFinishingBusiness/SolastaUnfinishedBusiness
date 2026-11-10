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
    private readonly ProgressBar _bar;
    private readonly ManualResetEventSlim _ready = new(false);

    private InstallWindow(IReadOnlyDictionary<string, string> texts)
    {
        Texts = texts;
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

        _status = new Label { Left = 20, Top = 18, Width = 420, Height = 24, Text = Get("waiting") };
        _bar = new ProgressBar
        {
            Left = 20, Top = 48, Width = 420, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30
        };

        var hint = new Label { Left = 20, Top = 78, Width = 420, Height = 56, Text = Get("hint") };

        Controls.AddRange([_status, _bar, hint]);
    }

    private IReadOnlyDictionary<string, string> Texts { get; }

    // English texts for a missing or incomplete texts file (the game writes them in its language)
    private static readonly Dictionary<string, string> Fallback = new()
    {
        ["title"] = "Finishing Business update",
        ["waiting"] = "Waiting for Solasta to close…",
        ["working"] = "Installing the update…",
        ["starting"] = "Starting Solasta…",
        ["failed"] = "Something went wrong. Solasta will start again and show what happened.",
        ["hint"] = "Solasta will start again by itself. Please don't start or stop it in Steam until then."
    };

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    private string Get(string key)
    {
        return Texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : Fallback[key];
    }

    // Opens the window on its own thread and waits (briefly) until it is up. Returns null if it couldn't be shown.
    internal static InstallWindow Open(string textsFile, Action<string> log)
    {
        var texts = ReadTexts(textsFile, log);
        InstallWindow window = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    window = new InstallWindow(texts);
                    window.Shown += (_, _) =>
                    {
                        // the game started this program with a hidden-window hint in older versions; the first
                        // ShowWindow of a process can take that hint instead of showing, so show it once more
                        ShowWindow(window.Handle, SwShowNormal);
                        window._ready.Set();
                    };
                    Application.Run(window);
                }
                catch (Exception e)
                {
                    log($"The install window failed: {e.Message}");
                    window?._ready.Set();
                }
            }) { IsBackground = true, Name = "InstallWindow" };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // never wait long: the install mustn't depend on the window
            for (var i = 0; i < 50 && (window == null || !window._ready.IsSet); i++)
            {
                Thread.Sleep(100);
            }

            return window is { IsDisposed: false } ? window : null;
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

    // the step shown: "waiting", "working", "starting" or "failed" (the failure also stops the moving bar)
    internal void ShowStep(string step)
    {
        Post(() =>
        {
            _status.Text = Get(step);

            if (step != "failed")
            {
                return;
            }

            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = _bar.Maximum;
        });
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
