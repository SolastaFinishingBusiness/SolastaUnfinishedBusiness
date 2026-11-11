using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Subclasses;
using SolastaUnfinishedBusiness.Subclasses.Builders;
using TA;
using UnityEngine;
using static MotionForm;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using GravityFissure = SolastaUnfinishedBusiness.Spells.SpellBuilders.PowerOrSpellFinishedByMeGravityFissure;

namespace SolastaUnfinishedBusiness.CustomUI;

public class CursorMotionHelper : MonoBehaviour
{
    private static GameObject _chainHelperPrefab;
    internal static readonly Vector3 Center = new(0.5f, 0.5f, 0.5f);
    internal static readonly Vector3 CursorHoverShift = new(0.0f, 0.5f, 0.0f);

    private readonly List<int3> _gravityFissureTiles = [];

    private readonly Dictionary<ulong, ActionChainHelper> _helpers = [];
    private Vector3 _actingCharacterCenter = Vector3.zero;

    // the creature under the pointer while aiming, or 0
    private ulong _hovered;
    private int3 _aimedPosition = int3.zero;
    private IGameLocationCharacterService _characterService;

    private CursorLocation _cursor;
    private IGameLocationEnvironmentService _envService;
    [CanBeNull] private MotionInfo _info;

    private bool _isGravityFissure;
    private IGameLocationPositioningService _positioningService;
    private string _positionsKey = string.Empty;
    private IGameLocationSelectionService _selectionService;
    private IGameLocationService _locationService;

    private CharacterActionParams ActionParams => _cursor.ActionParams;
    private GameLocationCharacter ActingCharacter => ActionParams.ActingCharacter;

    internal static void Initialize(GameObject chainHelperPrefab)
    {
        _chainHelperPrefab = chainHelperPrefab;
    }

    internal static void Activate(CursorLocation cursor)
    {
        if (!Main.Settings.ShowMotionFormPreview) { return; }

        if (!cursor.TryGetComponent<CursorMotionHelper>(out var helper))
        {
            helper = cursor.gameObject.AddComponent<CursorMotionHelper>();
            helper.Init(cursor);
        }

        helper.DoActivate();
    }

    internal static void Deactivate(CursorLocation cursor)
    {
        if (cursor.TryGetComponent<CursorMotionHelper>(out var helper))
        {
            helper.DoDeactivate();
        }
    }

    internal static void RefreshHover(CursorLocationGeometricShape cursor)
    {
        if (!Main.Settings.ShowMotionFormPreview) { return; }

        if (cursor.TryGetComponent<CursorMotionHelper>(out var helper))
        {
            helper.RefreshAoETargets(cursor);
        }
    }

    private void Init(CursorLocation cursor)
    {
        _cursor = cursor;
    }

    private void DoActivate()
    {
        _selectionService = _cursor.SelectionService;
        _positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        _locationService = ServiceRepository.GetService<IGameLocationService>();
        _envService = ServiceRepository.GetService<IGameLocationEnvironmentService>();
        _characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        _actingCharacterCenter = _positioningService.ComputeGravityCenterPosition(ActingCharacter);
        _info = BuildInfo();

        if (_info == null) { return; }

        if (_cursor is not CursorLocationSelectTarget)
        {
            return;
        }

        _selectionService.CharacterHoverChange += HoverChanged;
        _selectionService.TargetSelectionChange += TargetsChanged;
    }

    private void DoDeactivate()
    {
        if (_cursor is CursorLocationSelectTarget)
        {
            _selectionService.CharacterHoverChange -= HoverChanged;
            _selectionService.TargetSelectionChange -= TargetsChanged;
        }

        _gravityFissureTiles.Clear();
        _hovered = 0;
        DestroyHelpers();
    }

    private void UpdateHelper([CanBeNull] GameLocationCharacter target)
    {
        if (target == null) { return; }

        var helper = GetHelper(target);

        if (!helper) { return; }

        helper.Clear();

        if (_info is { PushThenPull: true })
        {
            UpdatePushThenPullHelper(helper, target);

            return;
        }

        var shift = GetTargetShift(target, BeamsOn(target));

        if (shift == int3.zero) { return; }

        var pos = target.LocationPosition + shift;
        var src = _positioningService.ComputeGravityCenterPosition(target);
        var dst = src + shift.ToVector3();
        var fall = GetFallShift(target, pos);
        var willFall = fall != int3.zero;

        helper.PlaceGhostWithoutPath(target, pos, !willFall);
        helper.PlaceDropLine(src, dst, true);
        if (willFall)
        {
            helper.PlaceDropLine(dst, dst + fall.ToVector3(), false);
        }
    }

    // How many beams of the spell being aimed are on this creature: the game lets the same creature be picked
    // several times. A creature that is only hovered counts as one. With "once for each hit" off, only the first
    // push or pull of a cast moves a creature, so the preview stays at one.
    private int BeamsOn(GameLocationCharacter target)
    {
        if (_info is not { PerBeam: true } || !Main.Settings.PushOrPullOncePerHit)
        {
            return 1;
        }

        var beams = _selectionService.SelectedTargets.Count(t => t.Guid == target.Guid);

        // The creature under the pointer would get the next beam too, while there is one left to aim: so a creature
        // already picked once shows the move for two beams when it is hovered again (user, 2026-11-11).
        if (_hovered == target.Guid && _cursor is CursorLocationSelectTarget { remainingTargets: > 0 })
        {
            beams++;
        }

        // no upper limit: the count is whatever the spell being cast allows (two beams at level 5, more later, and
        // possibly more still with other mods)
        return Math.Max(1, beams);
    }

    // Repelling Blast and Grasp of Hadar together (issue #37): each beam pushes the creature and then pulls it
    // back, so both moves are drawn, side by side. The push is worked out exactly and the ghost stands where it
    // ends. The pull that follows can't be worked out from a cell the creature isn't in, so its line shows where
    // a full pull from there would bring it: the same as one pull from where it stands now, shortened by the cells
    // the push took it away. However many beams are aimed at the creature, one beam's push and pull are shown.
    private void UpdatePushThenPullHelper(ActionChainHelper helper, GameLocationCharacter target)
    {
        var start = _positioningService.ComputeGravityCenterPosition(target);
        var pushShift = int3.zero;

        if (_envService.ComputePushDestination(
                _actingCharacterCenter, target, _info!.Distance, false, _positioningService, out var pushCell, out _))
        {
            pushShift = pushCell - target.LocationPosition;
        }

        var pushedCells = Math.Max(Math.Abs(pushShift.x), Math.Max(Math.Abs(pushShift.y), Math.Abs(pushShift.z)));
        var pullShift = int3.zero;
        var pullCells = _info.Distance - pushedCells;

        if (pullCells > 0 &&
            _envService.ComputePushDestination(
                _actingCharacterCenter, target, pullCells, true, _positioningService, out var pullCell, out _))
        {
            pullShift = pullCell - target.LocationPosition;
        }

        if (pushShift == int3.zero && pullShift == int3.zero) { return; }

        var pushEnd = start + pushShift.ToVector3();
        var pullEnd = start + pullShift.ToVector3();

        // a small step sideways, at right angles to the line from the caster, so the two lines don't hide each other
        var away = start - _actingCharacterCenter;
        var side = Vector3.Cross(Vector3.up, away);

        side = side.sqrMagnitude > 0.0001f ? side.normalized * 0.18f : new Vector3(0.18f, 0f, 0f);

        if (pushShift != int3.zero)
        {
            // as in the single preview: a push that ends over a drop shows the fall, and the ghost is marked as
            // not standing there
            var pushedCell = target.LocationPosition + pushShift;
            var fall = GetFallShift(target, pushedCell);
            var willFall = fall != int3.zero;

            helper.PlaceGhostWithoutPath(target, pushedCell, !willFall);
            helper.PlaceDropLine(start + side, pushEnd + side, true);

            if (willFall)
            {
                // where the pull would then start from isn't known, so no pull line is drawn
                helper.PlaceDropLine(pushEnd + side, pushEnd + side + fall.ToVector3(), false);

                return;
            }
        }

        // back from where the push ends to where the pull would leave the creature
        if (pushEnd != pullEnd)
        {
            helper.PlaceDropLine(pushEnd - side, pullEnd - side, true);
        }
    }

    private int3 GetFallShift(GameLocationCharacter target, int3 pos)
    {
        var below = pos;
        var accessor = new GridAccessor(_locationService);
        while (!_positioningService.CanCharacterStayAtPosition(target, below, true)
               && accessor.GetCellId(below) != CellId.Invalid)
        {
            below.y--;
        }

        return below - pos;
    }

    private bool IsValidTarget(GameLocationCharacter target)
    {
        if (_info == null) { return false; }

        return _cursor is not CursorLocationSelectTarget selectTarget || selectTarget.IsValidTarget(target);
    }

    private bool HasTarget(ulong guid)
    {
        return _selectionService.SelectedTargets.Any(t => t.Guid == guid) ||
               (_cursor as CursorLocationGeometricShape)?.affectedCharacters.Any(c => c.Guid == guid) == true;
    }

    [CanBeNull]
    private ActionChainHelper GetHelper([CanBeNull] GameLocationCharacter target)
    {
        if (target == null || !IsValidTarget(target)) { return null; }

        var guid = target.Guid;

        if (_helpers.TryGetValue(guid, out var helper))
        {
            return helper;
        }

        helper = Instantiate(_chainHelperPrefab, transform).GetComponent<ActionChainHelper>();
        helper.Activate(target.RulesetCharacter);
        _helpers.Add(guid, helper);

        return helper;
    }

    private static void DestroyHelper(ActionChainHelper helper)
    {
        helper.Deactivate();
        Destroy(helper);
    }

    private void DestroyHelper(ulong guid)
    {
        if (!_helpers.TryGetValue(guid, out var helper))
        {
            return;
        }

        _helpers.Remove(guid);
        DestroyHelper(helper);
    }

    private void DestroyHelpers()
    {
        var helpers = _helpers.Values.ToArray();
        _helpers.Clear();
        helpers.Do(DestroyHelper);
    }

    // beams: how many times the move is made (Eldritch Blast with several beams on one creature). It is worked
    // out as one longer move from where the creature stands; the real moves are made one beam at a time, each from
    // where the last one left it, which can end differently next to an obstacle.
    private int3 GetTargetShift(GameLocationCharacter target, int beams = 1)
    {
        if (_info == null) { return int3.zero; }

        var src = _info.Type switch
        {
            DirectionType.Down => (target.locationPosition + new int3(0, 10, 0)).ToVector3() + Center,
            _ when _info.FromOrigin && _isGravityFissure => GetPositionForGravityFissure(target),
            _ when _info.FromOrigin => _aimedPosition.ToVector3() + Center,
            _ => _actingCharacterCenter
        };

        var reverse = _info.Type == DirectionType.Pull;
        var distance = _info.Distance * Math.Max(1, beams);

        if (_envService.ComputePushDestination(src, target, distance, reverse, _positioningService, out var dst, out _))
        {
            return dst - target.LocationPosition;
        }

        return int3.zero;
    }

    private void HoverChanged(GameLocationCharacterSelection.HoverChangeMode mode, GameLocationCharacter character)
    {
        if (character?.RulesetCharacter == null) { return; }

        // which creature the pointer is on, for the per-beam preview
        var wasHovered = _hovered == character.Guid;

        if (mode == GameLocationCharacterSelection.HoverChangeMode.Add)
        {
            _hovered = character.Guid;
        }
        else if (wasHovered)
        {
            _hovered = 0;
        }

        if (HasTarget(character.Guid))
        {
            // already a target: its preview stays, but with a per-beam move it grows while hovered and shrinks back
            if (_info is { PerBeam: true })
            {
                UpdateHelper(character);
            }

            return;
        }

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (mode)
        {
            case GameLocationCharacterSelection.HoverChangeMode.Add:
                UpdateHelper(character);
                break;
            case GameLocationCharacterSelection.HoverChangeMode.Remove:
                DestroyHelper(character.Guid);
                break;
        }
    }

    private void RefreshAoETargets(CursorLocationGeometricShape cursor)
    {
        if (_info == null) { return; }

        var key = PosKey(cursor);
        var moved = _isGravityFissure
            ? _positionsKey != key
            : _aimedPosition != cursor.aimedPosition;

        _aimedPosition = cursor.aimedPosition;
        _positionsKey = key;

        if (!moved) { return; }

        var old = _helpers.Keys.ToList();
        var targets = GetAoETargets(cursor);
        foreach (var target in targets
                     .Where(target => target.RulesetCharacter != null)
                     .Where(target => !old.Remove(target.Guid) || _info.FromOrigin))
        {
            UpdateHelper(target);
        }

        old.ForEach(DestroyHelper);
    }

    private static string PosKey(CursorLocationGeometricShape cursor)
    {
        return string.Join(", ", cursor.coveredPlanePositions);
    }

    private List<GameLocationCharacter> GetAoETargets(CursorLocationGeometricShape cursor)
    {
        var affectedTargets = cursor.affectedCharacters.ToList();

        if (!_isGravityFissure)
        {
            return affectedTargets;
        }

        var caster = ActingCharacter;
        _gravityFissureTiles.SetRange(cursor.coveredPlanePositions);

        var targets = GravityFissure.GetPullTargets(caster, _gravityFissureTiles, _characterService);

        return targets;
    }

    private Vector3 GetPositionForGravityFissure(GameLocationCharacter target)
    {
        return GravityFissure.GetPositionForGravityFissure(target, _gravityFissureTiles, _positioningService)
            .ToVector3() + Center;
    }

    private void TargetsChanged(
        GameLocationCharacterSelection.TargetChangeMode mode,
        GameLocationCharacter character,
        int count)
    {
        if (character?.RulesetCharacter == null) { return; }

        switch (mode)
        {
            case GameLocationCharacterSelection.TargetChangeMode.Select:
                UpdateHelper(character);
                break;
            case GameLocationCharacterSelection.TargetChangeMode.Increase:
                // another beam on the same creature: its preview grows (issue #37)
                UpdateHelper(character);
                break;
            case GameLocationCharacterSelection.TargetChangeMode.Deselect:
                // one of several beams taken off: the creature is still a target, with a shorter preview
                if (_selectionService.SelectedTargets.Any(t => t.Guid == character.Guid))
                {
                    UpdateHelper(character);
                }
                else
                {
                    DestroyHelper(character.Guid);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    [CanBeNull]
    private MotionInfo BuildInfo()
    {
        _isGravityFissure = false;

        var character = ActingCharacter.RulesetCharacter;
        var attackMode = ActionParams.AttackMode;
        var effect = ActionParams.RulesetEffect;

        if (attackMode != null)
        {
            //Process 'Push' weapon mastery
            if (character.IsToggleEnabled((ActionDefinitions.Id)ExtraActionId.WeaponMasteryToggle)
                && character.GetMastery(attackMode) == Tabletop2024Context.MasteryProperty.Push)
            {
                return new MotionInfo { Distance = 2, Type = DirectionType.Push, FromOrigin = false };
            }

            //Check Tempest Domain's Thunderous Strike
            if (DomainTempest.ValidForPush(ActingCharacter, attackMode.EffectDescription.effectForms))
            {
                return new MotionInfo { Distance = 2, Type = DirectionType.Push, FromOrigin = false };
            }
        }

        if (effect == null) { return null; }

        _isGravityFissure = effect.SourceDefinition == SpellBuilders.GravityFissure;

        //Process Gravity Fissure
        if (_isGravityFissure)
        {
            return new MotionInfo
            {
                Distance = MotionContext.PullOntoCaster, Type = DirectionType.Pull, FromOrigin = true
            };
        }

        //Process Eldritch Blast
        if (effect.Name == SpellDefinitions.EldritchBlast.Name)
        {
            var pushes = character.HasActiveInvocation(InvocationDefinitions.RepellingBlast);
            var pulls = character.HasActiveInvocation(InvocationsBuilders.GraspingBlast);

            if (!pushes && !pulls) { return null; }

            // Each beam that hits moves the creature (issue #37). With one invocation the preview shows the
            // furthest it could go for the beams aimed at it; with both, the push and the pull side by side.
            return new MotionInfo
            {
                Distance = EldritchBlastMoveContext.MaxCells,
                Type = pushes ? DirectionType.Push : DirectionType.Pull,
                FromOrigin = false,
                PerBeam = !(pushes && pulls),
                PushThenPull = pushes && pulls
            };
        }

        //Check Tempest Domain's Thunderous Strike
        if (DomainTempest.ValidForPush(ActingCharacter, effect.EffectDescription.effectForms))
        {
            return new MotionInfo { Distance = 2, Type = DirectionType.Push, FromOrigin = false };
        }

        //TODO: check MotionForm.MotionType.PushFromWall
        var motion = effect.EffectDescription.effectForms
            .Where(f => f.FormType == EffectForm.EffectFormType.Motion)
            .Select(f => f.MotionForm)
            .FirstOrDefault(m => m.Type
                is MotionType.DragToOrigin or MotionType.PushFromOrigin or (MotionType)ExtraMotionType.PushDown);

        if (motion == null) { return null; }

        var fromOrigin = effect.SourceDefinition.HasSubFeatureOfType<ForcePushOrDragFromEffectPoint>();

        var type = motion.Type switch
        {
            (MotionType)ExtraMotionType.PushDown => DirectionType.Down,
            MotionType.DragToOrigin => DirectionType.Pull,
            _ => DirectionType.Push
        };

        return new MotionInfo { Distance = motion.Distance, Type = type, FromOrigin = fromOrigin };
    }
}

internal class MotionInfo
{
    public int Distance;
    public bool FromOrigin;

    // the move is made once for each beam aimed at the creature, so the preview is that many times as long
    public bool PerBeam;

    // a push followed by a pull, both drawn (Eldritch Blast with Repelling Blast and Grasp of Hadar)
    public bool PushThenPull;
    public DirectionType Type;
}

internal enum DirectionType
{
    Push, Pull, Down
}
