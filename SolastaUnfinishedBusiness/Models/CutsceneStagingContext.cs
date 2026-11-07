using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cinemachine;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// The cutscene camera side of whole-party staging, for official scenes with more than 4 heroes played off the map grid.
//
// Heroes are staged by height (MultiplayerContext.StageParty), so a hero often isn't on the marker the scene's script sent
// their role to. A close-up is authored looking at one marker, so it would show whoever stands there, or nobody. This keeps
// the authored angle and distance and moves the shot by the difference between that marker and the real speaker.
//
// All of this is local to one computer and only changes the picture: shots and off-grid hero positions aren't game state.
// Authored transforms are copied when the sequence is prepared and put back when it ends, because the shots stay in the
// scene and later cutscenes reuse them.
internal static class CutsceneStagingContext
{
    // shots authored for one hero; group, NPC and free shots aren't moved
    private static readonly Regex HeroShot = new("^Char(Single|Medium|Wide|CloseUp)[0-9]*$");

    // the party-facing shots of an answer prompt, the one kind of shot where no line of dialogue directs anyone's gaze
    private static readonly Regex PartyShot = new("^(PartyChoice|Group)");

    private static readonly Dictionary<CinemachineVirtualCamera, (Vector3 Position, Quaternion Rotation)> Shots = [];
    private static readonly HashSet<CinemachineVirtualCamera> Moved = [];
    private static readonly List<WorldLocationCharacter> Lookers = [];
    private static NarrativeSequence _sequence;
    private static NarrativeState _state;
    private static Vector3 _partyForward;
    private static int _logs;

    internal static List<WorldLocationCharacter> RearHeroes { get; } = [];


    internal static void Begin(NarrativeSequence sequence)
    {
        End();
        _sequence = sequence;

        foreach (var shot in sequence.CameraShotsMap?.Values.Where(x => x != null) ?? [])
        {
            var transform = shot.transform;

            Shots[shot] = (transform.position, transform.rotation);
        }
    }

    internal static void SetStage(List<WorldLocationCharacter> rearHeroes, Vector3 partyForward)
    {
        RearHeroes.Clear();
        RearHeroes.AddRange(rearHeroes);
        _partyForward = partyForward;
    }

    // BuildCamera has just chosen the state's shot and subject; the sequence activates that shot straight after
    internal static void OnCameraBuilt(NarrativeState state)
    {
        _state = state;
    }

    internal static void OnShotActivated(NarrativeSequence sequence, string shotName)
    {
        if (sequence != _sequence || _sequence == null)
        {
            return;
        }

        // Absolute every time: the game's CameraRestorationPackage can save a moved position as the shot's initial one.
        foreach (var moved in Moved.Where(x => x != null))
        {
            moved.transform.position = Shots[moved].Position;
        }

        Moved.Clear();

        if (!sequence.CameraShotsMap.TryGetValue(shotName, out var shot) || shot == null ||
            !Shots.TryGetValue(shot, out var authored))
        {
            return;
        }

        if (HeroShot.IsMatch(shotName) && _state?.Subject is WorldLocationCharacter hero &&
            sequence.PlayerActors.Contains(hero))
        {
            FrameHero(sequence, shot, shotName, authored.Position, authored.Rotation, hero);
        }

        if (_state is NarrativeStateAnswerChoice && PartyShot.IsMatch(shotName))
        {
            LookAtCamera(shot, authored.Position);
        }
    }

    private static void FrameHero(
        NarrativeSequence sequence,
        CinemachineVirtualCamera shot,
        string shotName,
        Vector3 position,
        Quaternion rotation,
        WorldLocationCharacter hero)
    {
        var markers = sequence.PlayerPlacementMarkers;
        var framed = CutsceneFormation.FindFramedPoint(
            ToPoint(position),
            ToPoint(rotation * Vector3.forward),
            markers.Select(x => ToPoint(x ? x.position : new Vector3(float.MaxValue, 0f, 0f))).ToList());

        // a shot that frames no marker, such as one following a walk, keeps its place
        if (framed < 0)
        {
            return;
        }

        var offset = hero.transform.position - markers[framed].position;

        offset.y = 0f;

        if (offset.sqrMagnitude < 0.0001f)
        {
            return;
        }

        shot.transform.position = position + offset;
        Moved.Add(shot);

        if (_logs++ < 24)
        {
            Main.Info($"[CutsceneParty] shot {shotName} framing marker {framed} moved {offset} for " +
                      $"{hero.GameLocationCharacter?.Name}");
        }
    }

    // Native head and eye look: the body keeps the row's facing. Only rear heroes the camera is in front of, since the shot
    // shows the others' faces anyway and a hero can't look through the back of their head.
    private static void LookAtCamera(CinemachineVirtualCamera shot, Vector3 position)
    {
        foreach (var hero in RearHeroes.Where(x => x))
        {
            if (Vector3.Dot(position - hero.transform.position, _partyForward) <= 0f)
            {
                continue;
            }

            hero.GraphicsCharacter?.StartLookAt(null, shot.transform, false);
            Lookers.Add(hero);
        }
    }

    // when the prompt ends, before the next state's speech can direct any gaze, so that this never overrides a scene's
    internal static void ReleaseLook()
    {
        foreach (var hero in Lookers.Where(x => x))
        {
            hero.GraphicsCharacter?.StartLookAt(null, null, false);
        }

        Lookers.Clear();
    }

    internal static void End()
    {
        ReleaseLook();

        foreach (var pair in Shots.Where(x => x.Key != null))
        {
            pair.Key.transform.SetPositionAndRotation(pair.Value.Position, pair.Value.Rotation);
        }

        Shots.Clear();
        Moved.Clear();
        RearHeroes.Clear();
        _sequence = null;
        _state = null;
    }

    internal static CutsceneFormation.Point ToPoint(Vector3 vector)
    {
        return new CutsceneFormation.Point(vector.x, vector.y, vector.z);
    }
}
