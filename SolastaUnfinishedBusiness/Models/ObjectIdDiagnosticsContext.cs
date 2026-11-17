using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Photon.Pun;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// TEMPORARY diagnostics (open issue 9, the object-ID desyncs in evidence/desync-issue-01/FINDINGS.md and
// evidence/object-id-code-investigation/). Every new
// ruleset object takes its ID from one counter (RulesetEntity.Register(true) -> RulesetEntityManager.GenerateGuid,
// which returns the counter and adds 1; unregistering never gives a number back), so an object made on one computer
// only shifts every later ID there. In the recorded session the host ended up 3 or 8 ahead of all the clients, once
// during a map change with no fight, and the gap came back within 500 ticks of a reload. Which objects the host made
// alone isn't known: this records every allocation so two computers' records can be compared line by line.
// What it records (in memory, a ring of RingSize entries; nothing is written per object):
// - every Register: the counter before, the ID given or kept, whether a new ID was made, the object's type and name;
// - GenerateGuid calls made outside Register (none known; counted in case);
// - UnregisterEntity, the counter being saved, loaded or reset, map unloads and loads, game loads, and character
//   reveals (the mod's fire-monster light replaces its light with a new ID on every reveal, even one the game skips:
//   a candidate cause);
// - a short call stack for every allocation made in a "window": from a map unload or load until WindowSeconds after the
//   last such event (real time, so it covers the loading screen and the first minute or so of play, where the gaps
//   formed; network ticks don't advance in single player). Outside windows only the cheap fields are kept.
// When it writes: at the end of each window (one file per window), and at a desync the mod detects (the whole ring),
// as text files in <UserData>\FinishingBusinessDiagnostics, at most MaxFilesPerLaunch per launch. Each computer
// writes its own; comparing the host's and one client's files for the same window shows the first object that differs.
// It never changes an ID, the counter or anything else in the game, and it doesn't force counters equal (live
// references may already point at differently numbered objects). It runs only in a multiplayer room, plus single
// player in development builds so it can be checked there. Remove once the cause is found.
internal static class ObjectIdDiagnosticsContext
{
    private const int RingSize = 2048;
    private const float WindowSeconds = 120f;
    private const int StackFrames = 8;
    private const int MaxFilesPerLaunch = 40;
    private const int MaxStaleEffectReports = 64;

    private static readonly Record[] Ring = new Record[RingSize];
    private static readonly string LaunchStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private static readonly object Lock = new();

    private static long _sequence;
    private static long _windowStartSequence = -1;
    private static float _windowEnd;
    private static string _windowReason;
    private static GameObject _driver;
    private static int _filesWritten;
    private static int _registerDepth;
    private static float _lastDesyncDump = -1000f;

    // Written to each file's header, so they can't push allocation records out of the ring. Counted since the launch.
    private static string _saveHandlerPatch = "not reached";
    private static long _lightingUnchanged;
    private static long _lightingPreviewSkipped;
    private static long _effectsWorkedOut;
    private static long _effectsStale;
    private static long _effectChecksFailed;

    // Register (via the patches) records whatever it allocated: in the prefix, the counter before; in the postfix, the
    // result. Nested registrations (an object registering its parts) each get their own record.
    [ThreadStatic] private static Stack<ulong> _countersBefore;

    // The switches are on the mod menu's Logging tab. The save patch's own result isn't behind them: it's one line per
    // launch, and an error there means a fix isn't in place.
    internal static bool Active =>
        Main.Settings is { EnableAdditionalLogging: true, LogObjectIdRecords: true } && InScope;

    // PowerBundle's stale effect check. It works effects out a second time, so it has its own switch.
    internal static bool StaleEffectChecksActive =>
        Main.Settings is { EnableAdditionalLogging: true, LogStaleEffectChecks: true } && InScope;

    private static bool InScope
    {
        get
        {
#if FB_DEVELOPMENT
            return true;
#else
            return PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;
#endif
        }
    }

    private static bool InWindow => _windowStartSequence >= 0;

    private static ulong Counter => ServiceRepository.GetService<IRulesetEntityService>()?.CurrentGuid ?? 0;

    private static long Tick =>
        (long)((ServiceRepository.GetService<INetworkingService>() as NetworkingManager)?.tick?.LocalTick ?? 0);

    #region Recording

    internal static void BeforeRegister()
    {
        _countersBefore ??= new Stack<ulong>();
        _countersBefore.Push(Counter);
        _registerDepth++;
    }

    internal static void AfterRegister(RulesetEntity entity, bool generateGuid)
    {
        _registerDepth = Math.Max(0, _registerDepth - 1);

        var before = _countersBefore is { Count: > 0 } ? _countersBefore.Pop() : 0;

        // a kept ID (generateGuid false) allocates nothing: recorded too, so restored objects can be told from new ones
        Add(generateGuid ? "new" : "keep", before, entity?.Guid ?? 0, entity, generateGuid || InWindow);
    }

    // a GenerateGuid call that didn't come from Register: none is known, so each one is worth a stack
    internal static void AfterGenerateGuid(ulong result)
    {
        if (_registerDepth > 0)
        {
            return;
        }

        Add("generate", result, result, null, true);
    }

    internal static void Unregistered(RulesetEntity entity)
    {
        Add("unregister", Counter, entity?.Guid ?? 0, entity, false);
    }

    // reading the counter from a save opens the window: it happens just before the "game loaded" marker, and the value
    // read is the one every computer should start from (in game, 9260c5b3, it fell outside the window)
    internal static void CounterSerialized(string mode)
    {
        if (mode == "Read")
        {
            Transition("counter read from save");
        }
        else
        {
            Marker($"counter {mode}");
        }
    }

    // Whether the patch stopping saves adding "effect ended" handlers really changed the game's code. It runs while the
    // mod loads, before any room exists, so it goes to the mod's log now and into each record file's header.
    internal static void SaveHandlerPatchResult(bool applied, string detail)
    {
        _saveHandlerPatch = applied ? "applied" : $"NOT applied ({detail})";

        if (applied)
        {
            Main.Info("[ObjectIds] save handler patch applied");
        }
        else
        {
            Main.Error($"[ObjectIds] save handler patch NOT applied: {detail}");
        }
    }

    // a light check that found nothing to change, which before would have used an ID: only counted, as there are many
    internal static void LightingUnchanged()
    {
        _lightingUnchanged++;
    }

    // a preview of the light that no longer touches the character: only counted, as there are many
    internal static void LightingPreviewSkipped()
    {
        _lightingPreviewSkipped++;
    }

    // a real change of a character's light conditions or effects. These should be the same on every computer, in order.
    internal static void LightingChanged(RulesetCharacter character, LocationDefinitions.LightingState state,
        IEnumerable<string> removed, IEnumerable<string> added, int effectsRemoved, int effectsAdded)
    {
        Add("lighting", Counter, character?.Guid ?? 0, character, false,
            $"state={state} removed=[{string.Join(",", removed)}] added=[{string.Join(",", added)}] " +
            $"effects -{effectsRemoved} +{effectsAdded}");
    }

    // Development builds only (a test's direct count, too costly to leave in players' builds). After a save: how many
    // handlers each active effect has. Every number should be 1, however many saves were made.
    [Conditional("FB_DEVELOPMENT")]
    internal static void RecordEffectHandlers(RulesetCharacter character)
    {
        if (character.SpellsCastByMe.Count + character.PowersUsedByMe.Count == 0)
        {
            return;
        }

        var spells = character.SpellsCastByMe.Select(x => x.TerminatedSelf?.GetInvocationList().Length ?? 0);
        var powers = character.PowersUsedByMe.Select(x => x.TerminatedSelf?.GetInvocationList().Length ?? 0);

        Add("handlers", Counter, character.Guid, character, false,
            $"after save: spells=[{string.Join(",", spells)}] powers=[{string.Join(",", powers)}]");
    }

    // Development builds only. How many handlers a character has after a panel binds or unbinds: these should go back
    // down on unbind, including for a hero downed while bound.
    [Conditional("FB_DEVELOPMENT")]
    internal static void RecordPanelHandlers(RulesetCharacter character, string when)
    {
        Add("handlers", Counter, character.Guid, character, false,
            $"{when}: powerActivated={character.PowerActivated?.GetInvocationList().Length ?? 0} " +
            $"refreshed={character.CharacterRefreshed?.GetInvocationList().Length ?? 0}");
    }

    // A short text of what a power's or spell's effect comes to, in the main things modifiers change. Two computers' texts
    // for the same use should be equal; a difference means they'd apply different rules. It's partial: equal texts don't
    // prove equal effects.
    internal static string SummarizeEffect(EffectDescription effect)
    {
        try
        {
            var text = new StringBuilder();

            text.Append("range=").Append(effect.RangeType).Append(':').Append(effect.RangeParameter)
                .Append(" target=").Append(effect.TargetType).Append(':').Append(effect.TargetParameter)
                .Append(',').Append(effect.TargetParameter2).Append(':').Append(effect.TargetSide)
                .Append(" duration=").Append(effect.DurationType).Append(':').Append(effect.DurationParameter)
                .Append(" save=").Append(effect.HasSavingThrow).Append(':').Append(effect.SavingThrowAbility)
                .Append(':').Append(effect.FixedSavingThrowDifficultyClass)
                .Append(':').Append(effect.DifficultyClassComputation)
                .Append(':').Append(effect.SavingThrowDifficultyAbility)
                .Append(" forms=");

            foreach (var form in effect.EffectForms)
            {
                text.Append('[').Append(form.FormType).Append(form.HasSavingThrow ? " save" : string.Empty);

                // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                switch (form.FormType)
                {
                    case EffectForm.EffectFormType.Damage when form.DamageForm != null:
                        var damage = form.DamageForm;
                        text.Append(' ').Append(damage.DamageType).Append(' ').Append(damage.DiceNumber)
                            .Append(damage.DieType).Append('+').Append(damage.BonusDamage)
                            .Append(damage.OverrideWithBardicInspirationDie ? " inspiration" : string.Empty);
                        break;
                    case EffectForm.EffectFormType.Healing when form.HealingForm != null:
                        var healing = form.HealingForm;
                        text.Append(' ').Append(healing.DiceNumber).Append(healing.DieType).Append('+')
                            .Append(healing.BonusHealing);
                        break;
                    case EffectForm.EffectFormType.Condition when form.ConditionForm != null:
                        text.Append(' ').Append(form.ConditionForm.ConditionDefinition?.Name);
                        break;
                    case EffectForm.EffectFormType.TemporaryHitPoints when form.TemporaryHitPointsForm != null:
                        text.Append(' ').Append(form.TemporaryHitPointsForm.BonusHitPoints);
                        break;
                    case EffectForm.EffectFormType.Summon when form.SummonForm != null:
                        text.Append(' ').Append(form.SummonForm.monsterDefinitionName).Append(' ')
                            .Append(form.SummonForm.effectProxyDefinitionName).Append(' ')
                            .Append(form.SummonForm.number);
                        break;
                }

                text.Append(']');
            }

            return text.ToString();
        }
        catch (Exception e)
        {
            return $"(no summary: {e.Message})";
        }
    }

    // A live effect's modified description was worked out. Goes in the ring to compare computers, but not the log.
    // Reads the power's save DC field directly: some modifiers set it, and the rules read it for this use.
    internal static void EffectWorkedOut(RulesetCharacter caster, BaseDefinition definition, RulesetEffect effect,
        EffectDescription description)
    {
        _effectsWorkedOut++;

        var saveDc = effect is RulesetEffectPower power ? $" powerSaveDC={power.usablePower?.saveDC}" : string.Empty;

        Add("effect", Counter, effect?.Guid ?? 0, caster, false,
            $"{definition?.Name}: {SummarizeEffect(description)}{saveDc}");
    }

    // The modifiers would now give something other than when the effect's result was kept: it's out of date, though an
    // effect may be meant to keep what it started with. The game still uses the kept result. Each kind of change is
    // reported once per effect (see PowerBundle), and the totals
    // are capped so a repeating case can't push allocation records out of the ring or fill the log.
    internal static void EffectStale(RulesetCharacter caster, BaseDefinition definition, RulesetEffect effect,
        string kept, string now, bool keptIsLate)
    {
        _effectsStale++;

        if (_effectsStale > MaxStaleEffectReports)
        {
            return;
        }

        var note = $"{definition?.Name} on {caster?.Name}: " +
                   $"{(keptIsLate ? "first seen, not from its start" : "kept")} [{kept}] but now [{now}]";

        Add("effect-stale", Counter, effect?.Guid ?? 0, caster, true, note);
        Main.Info($"[EffectCache] stale at tick {Tick}, frame {Time.frameCount}: {note}");
    }

    internal static void EffectCheckFailed(BaseDefinition definition, Exception e)
    {
        _effectChecksFailed++;

        if (_effectChecksFailed <= MaxStaleEffectReports)
        {
            Main.Info($"[EffectCache] couldn't check {definition?.Name}: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static void CounterReset()
    {
        Marker("counter reset");
    }

    internal static void Revealed(GameLocationCharacter character)
    {
        Add("reveal", Counter, character?.RulesetCharacter?.Guid ?? 0, character?.RulesetCharacter, false,
            $"visible before={character?.Visible}");
    }

    // a map unload or load, or a game load: a window opens (or continues) and stays open until WindowSeconds after the
    // last of them
    internal static void Transition(string reason)
    {
        if (!Active)
        {
            return;
        }

        lock (Lock)
        {
            if (!InWindow)
            {
                _windowStartSequence = _sequence;
                _windowReason = reason;
            }

            _windowEnd = Time.realtimeSinceStartup + WindowSeconds;
        }

        if (!_driver)
        {
            _driver = new GameObject("FbObjectIdDiagnostics");
            UnityEngine.Object.DontDestroyOnLoad(_driver);
            _driver.AddComponent<Driver>();
        }

        Marker(reason);
    }

    private static void Marker(string text)
    {
        Add("marker", Counter, 0, null, false, text);
    }

    private static void Add(string kind, ulong counterBefore, ulong guid, RulesetEntity entity, bool stack,
        string note = null)
    {
        if (!Active)
        {
            return;
        }

        try
        {
            var record = new Record
            {
                Kind = kind,
                Tick = Tick,
                Frame = Time.frameCount,
                CounterBefore = counterBefore,
                CounterAfter = Counter,
                Guid = guid,
                Type = entity?.GetType().Name,
                Name = NameOf(entity),
                Note = note,
                Stack = stack && InWindow ? ShortStack() : null
            };

            lock (Lock)
            {
                record.Sequence = _sequence;
                Ring[_sequence % RingSize] = record;
                _sequence++;
            }
        }
        catch (Exception e)
        {
            // a diagnostic never interrupts the game
            Main.Error($"[ObjectIds] record failed: {e.Message}");
        }
    }

    // The game's SystemName reads the object's definition without checking it (RulesetItem.itemDefinition,
    // RulesetCondition.conditionDefinition and most others), so an object registered before its definition is set
    // throws there. The mod registers such objects while it builds its content at start-up: in game (9260c5b3) that lost
    // 299 records with "Object reference not set", all before the mod finished loading. The record is kept without a name.
    private static string NameOf(RulesetEntity entity)
    {
        if (entity == null)
        {
            return null;
        }

        try
        {
            return entity.SystemName;
        }
        catch (NullReferenceException)
        {
            return "(no definition yet)";
        }
    }

    // the callers above the patch, as Type.Method, skipping Harmony's and this file's frames
    private static string ShortStack()
    {
        var frames = new StackTrace(3, false).GetFrames();

        if (frames == null)
        {
            return null;
        }

        // in game (9260c5b3) the first two frames were the patch's postfix and Harmony's patched copy
        // ("RulesetEntity.Register_Patch2"): neither says who registered the object
        return string.Join(" < ", frames
            .Select(x => x.GetMethod())
            .Where(x => x != null && x.DeclaringType != typeof(ObjectIdDiagnosticsContext) &&
                        x.DeclaringType?.DeclaringType != typeof(Patches.ObjectIdDiagnosticsPatcher) &&
                        !x.Name.Contains("_Patch") &&
                        !(x.DeclaringType?.FullName ?? string.Empty).StartsWith("HarmonyLib"))
            .Take(StackFrames)
            .Select(x => $"{x.DeclaringType?.Name}.{x.Name}"));
    }

    #endregion

    #region Writing

    // every frame (Driver): a window ends WindowSeconds after the last map unload or load
    private static void CloseWindowIfDue()
    {
        if (!InWindow || Time.realtimeSinceStartup < _windowEnd)
        {
            return;
        }

        long from;
        string reason;

        lock (Lock)
        {
            from = _windowStartSequence;
            reason = _windowReason;
            _windowStartSequence = -1;
        }

        Write($"window-{reason}", from);
    }

    // a desync the mod detected (the entity counter check, or a state desync): the whole ring, at most once a minute
    internal static void DesyncDetected(string what, ulong tick)
    {
        if (!Active || Time.realtimeSinceStartup - _lastDesyncDump < 60f)
        {
            return;
        }

        _lastDesyncDump = Time.realtimeSinceStartup;
        Write($"{what}-tick{tick}", 0);
    }

    private static void Write(string label, long fromSequence)
    {
        if (_filesWritten >= MaxFilesPerLaunch)
        {
            return;
        }

        try
        {
            List<Record> records;
            long dropped;

            lock (Lock)
            {
                var first = Math.Max(fromSequence, _sequence - RingSize);

                dropped = first - fromSequence;
                records = [];

                for (var s = first; s < _sequence; s++)
                {
                    records.Add(Ring[s % RingSize]);
                }
            }

            var role = PhotonNetwork.IsMasterClient ? "host" : "client";
            var folder = Path.Combine(Application.persistentDataPath, "FinishingBusinessDiagnostics");
            var file = Path.Combine(folder,
                $"objectids-{LaunchStamp}-{_filesWritten + 1:00}-p{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0}-{role}" +
                $"-{Sanitize(label)}.txt");
            var text = new StringBuilder();

            text.AppendLine($"# Finishing Business object-ID diagnostics: {label}");
            // not Gui.GameLocation?.: that skips Unity's destroyed-object check
            var location = Gui.GameLocation;
            var locationName = location ? location.LocationDefinition?.Name : null;

            text.AppendLine($"# player={PhotonNetwork.LocalPlayer?.NickName} actor=" +
                            $"{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0} role={role} " +
                            $"location={locationName} counter={Counter} tick={Tick}");
            text.AppendLine($"# saveHandlerPatch={_saveHandlerPatch} lightingUnchanged={_lightingUnchanged} " +
                            $"lightingPreviewSkipped={_lightingPreviewSkipped} " +
                            $"effectsWorkedOut={_effectsWorkedOut} effectsStale={_effectsStale} " +
                            $"effectChecksFailed={_effectChecksFailed}");
            text.AppendLine($"# records={records.Count} dropped={Math.Max(0, dropped)} " +
                            "(dropped > 0: the window outgrew the ring; the first records are missing)");
            text.AppendLine("# seq\ttick\tframe\tkind\tcounterBefore\tcounterAfter\tguid\ttype\tname\tnote\tstack");

            foreach (var r in records.Where(x => x != null))
            {
                text.Append(r.Sequence).Append('\t').Append(r.Tick).Append('\t').Append(r.Frame).Append('\t')
                    .Append(r.Kind).Append('\t').Append(r.CounterBefore).Append('\t').Append(r.CounterAfter)
                    .Append('\t').Append(r.Guid).Append('\t').Append(r.Type).Append('\t').Append(r.Name).Append('\t')
                    .Append(r.Note).Append('\t').Append(r.Stack).AppendLine();
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(file, text.ToString());
            _filesWritten++;
            Main.Info($"[ObjectIds] {records.Count} records ({label}) written to {file}");
        }
        catch (Exception e)
        {
            Main.Error($"[ObjectIds] couldn't write the records: {e.Message}");
        }
    }

    private static string Sanitize(string label)
    {
        return new string(label.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
    }

    #endregion

    // checks once a frame whether the window is over (kept across scene loads)
    private sealed class Driver : MonoBehaviour
    {
        private void Update()
        {
            CloseWindowIfDue();
        }
    }

    private sealed class Record
    {
        internal ulong CounterAfter;
        internal ulong CounterBefore;
        internal int Frame;
        internal ulong Guid;
        internal string Kind;
        internal string Name;
        internal string Note;
        internal long Sequence;
        internal string Stack;
        internal long Tick;
        internal string Type;
    }
}
