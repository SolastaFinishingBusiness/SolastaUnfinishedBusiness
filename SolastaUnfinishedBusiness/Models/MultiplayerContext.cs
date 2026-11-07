using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Multiplayer fixes. Every decision here must depend only on state that is identical on all peers
// (game state, scene data, shared settings) so the lockstep simulation stays in sync.
internal static class MultiplayerContext
{
    // vanilla NetworkTick uses 6 as initial buffer, 4 as floor, 12 as ceiling and waits 10 checks before lowering
    internal const int DefaultInitialTickBuffer = 4;
    internal const int DefaultMinimumTickBuffer = 3;
    internal const int DefaultSecondsBeforeLowerTickBuffer = 3;
    internal const int MinTickBuffer = 2;
    internal const int MaxTickBuffer = 12;

    private const int IncreaseTickBufferStep = 2;
    private const float IncreaseTickBufferWaitingTime = 3f;
    private const float LowerTickBufferMaxWaitingTime = 0.05f;
    private const float TickDuration = 0.05f;

    // extra time allowed for network jitter when sizing the action delay from ping
    private const float PingJitterMarginMs = 30f;

    private const ulong DesyncCheckTickInterval = 100;
    private const int DesyncCheckHistory = 32;

    private static readonly Dictionary<ulong, ulong> EntityGuidByTick = [];
    private static ulong _lastDesyncCheckTickSent = ulong.MaxValue;
    private static bool _desyncReported;

    #region Tick Buffer

    internal static int InitialTickBuffer =>
        Mathf.Clamp(Main.Settings.MultiplayerInitialTickBuffer, MinimumTickBuffer, MaxTickBuffer);

    private static int MinimumTickBuffer =>
        Mathf.Clamp(Main.Settings.MultiplayerMinimumTickBuffer, MinTickBuffer, MaxTickBuffer);

    private static int ChecksBeforeLowerTickBuffer =>
        Mathf.Max(1, Main.Settings.MultiplayerSecondsBeforeLowerTickBuffer);

    internal static void ResetTickBuffer(NetworkTick tick)
    {
        var value = (byte)InitialTickBuffer;

        tick.currentSendTickBuffer = value;
        tick.currentWaitTickBuffer = value;
        tick.targetTickBuffer = value;
        tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;
    }

    // same shape as vanilla NetworkTick.UpdateTickBufferValue (called once per second by every peer) but with a
    // configurable floor and patience, and lowering one tick at a time instead of two
    internal static bool UpdateTickBufferValue(NetworkTick tick, out byte wantedTickBuffer)
    {
        wantedTickBuffer = tick.targetTickBuffer;

        if (tick.targetTickBuffer != tick.currentSendTickBuffer ||
            tick.targetTickBuffer != tick.currentWaitTickBuffer ||
            tick.waitingTimePerActor.Count <= 0 ||
            tick.networkPingPerActor.Count <= 0)
        {
            return false;
        }

        var maxWaitingTime = tick.waitingTimePerActor.Values.Max();
        var pingBased = Main.Settings.MultiplayerPingBasedTickBuffer;
        var floor = pingBased ? Mathf.Max(MinimumTickBuffer, PingTickBuffer(tick)) : MinimumTickBuffer;

        // a slower connection raises the delay as soon as the reported pings need it, before anyone waits
        if (pingBased && tick.targetTickBuffer < floor)
        {
            wantedTickBuffer = (byte)floor;
            tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;

            return true;
        }

        if (tick.targetTickBuffer < MaxTickBuffer && maxWaitingTime >= IncreaseTickBufferWaitingTime)
        {
            wantedTickBuffer = (byte)Mathf.Min(tick.targetTickBuffer + IncreaseTickBufferStep, MaxTickBuffer);
            tick.waitingTimePerActor.Clear();
            tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;

            return true;
        }

        if (tick.targetTickBuffer <= floor)
        {
            return false;
        }

        if (maxWaitingTime >= LowerTickBufferMaxWaitingTime)
        {
            tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;

            return false;
        }

        var lowerTickBuffer = Mathf.Max(tick.targetTickBuffer - 1, floor);
        var tickSpread = tick.MaxTick - tick.MinTick;
        var maxPingInSeconds = tick.networkPingPerActor.Values.Max() * 0.001f;
        // a command must reach every peer (round trip through the relay) before its scheduled tick; the ping based
        // floor already holds the delay the pings need
        var pingFits = pingBased ||
                       (maxPingInSeconds > 0 &&
                        maxPingInSeconds * 2 < (lowerTickBuffer * TickDuration) - TickDuration);

        if (!pingFits || tickSpread >= (ulong)lowerTickBuffer)
        {
            tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;

            return false;
        }

        tick.counterToAllowLowerBuffer--;

        if (tick.counterToAllowLowerBuffer > 0)
        {
            return false;
        }

        wantedTickBuffer = (byte)lowerTickBuffer;
        tick.counterToAllowLowerBuffer = ChecksBeforeLowerTickBuffer;

        return true;
    }

    // ticks a command needs to reach every computer before it runs: from the slowest sender through the server to
    // the slowest receiver (half of each one's round trip), plus jitter, plus one tick for peers' ticks to spread,
    // plus one more when commands wait for the next tick's flush
    private static int PingTickBuffer(NetworkTick tick)
    {
        var pings = tick.networkPingPerActor.Values.Where(x => x > 0).OrderByDescending(x => x).ToList();

        if (pings.Count == 0)
        {
            return MinimumTickBuffer;
        }

        var slowestPathMs = (pings[0] + (pings.Count > 1 ? pings[1] : pings[0])) / 2f;
        var flushTicks = Main.Settings.MultiplayerFlushCommandsImmediately ? 0 : 1;
        var ticks = Mathf.CeilToInt((slowestPathMs + PingJitterMarginMs) / (TickDuration * 1000f)) + 1 + flushTicks;

        return Mathf.Clamp(ticks, MinTickBuffer, MaxTickBuffer);
    }

    internal static int CurrentTickBuffer =>
        ServiceRepository.GetService<INetworkingService>() is NetworkingManager { tick: { } tick }
            ? tick.currentSendTickBuffer
            : 0;

    // game commands are only sent after the game accepted the action; sending them at once instead of at the next
    // tick's flush saves up to one tick. Same messages, same order, same scheduled tick: only the send time changes.
    internal static void FlushCommandIfNeeded()
    {
        if (Main.Settings.MultiplayerFlushCommandsImmediately &&
            PhotonNetwork.InRoom &&
            !PhotonNetwork.OfflineMode)
        {
            PhotonNetwork.SendAllOutgoingCommands();
        }
    }

    #endregion

    #region Desync Detection

    // the game ships the receiving side of an entity guid comparison but never sends it
    internal static void SendDesyncCheckIfNeeded(NetworkingManager networkingManager)
    {
        if (!Main.Settings.MultiplayerEnableDesyncDetection ||
            !networkingManager.IsMultiplayerGame ||
            networkingManager.frozenGameplay ||
            !networkingManager.ValidGameplayContext() ||
            networkingManager.view == null)
        {
            return;
        }

        var localTick = networkingManager.tick.LocalTick;

        if (localTick == 0 || localTick % DesyncCheckTickInterval != 0 || localTick == _lastDesyncCheckTickSent)
        {
            return;
        }

        if (ServiceRepository.GetService<IRulesetEntityService>() is not { } entityService)
        {
            return;
        }

        _lastDesyncCheckTickSent = localTick;
        networkingManager.view.RPC(nameof(NetworkingManager.NotifyCurEntityGuidByTickRPC), RpcTarget.AllViaServer,
            (long)localTick, (long)entityService.CurrentGuid);
    }

    // compares every peer's value with the first one received for that tick, so it also works with 3+ players
    internal static void CheckCurEntityGuidByTick(ulong localTick, ulong incomingGuid)
    {
        if (!EntityGuidByTick.TryGetValue(localTick, out var firstGuid))
        {
            EntityGuidByTick.Add(localTick, incomingGuid);

            foreach (var oldTick in EntityGuidByTick.Keys
                         .Where(x => x + (DesyncCheckTickInterval * DesyncCheckHistory) < localTick)
                         .ToList())
            {
                EntityGuidByTick.Remove(oldTick);
            }

            return;
        }

        if (firstGuid == incomingGuid || _desyncReported)
        {
            return;
        }

        _desyncReported = true;
        Main.Error($"Multiplayer desync detected at tick {localTick}: entity guid {firstGuid} vs {incomingGuid}");
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected("id-desync", localTick);
        Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncDetected", Gui.ColorFailure, 10f);
        MultiplayerDesyncContext.OfferResyncForIdGap();
    }

    internal static void ResetDesyncDetection()
    {
        EntityGuidByTick.Clear();
        _lastDesyncCheckTickSent = ulong.MaxValue;
        _desyncReported = false;
    }

    #endregion

    #region Shift Key State

    // SHIFT on a spell, reaction or slot conversion picks which slot pool a multiclass warlock spends. The key is only
    // pressed on one computer, so in multiplayer the choice travels inside the command and every computer applies it
    // when the command executes:
    // - cast: an extra element at the end of CharacterActionParams, only on the networking serializers (not saves)
    // - reaction: a flag added to subOption
    // - slot to sorcery points conversion: a flag added to slotLevel
    private const string ShiftKeyStateElement = "UbShiftKeyState";
    private const int ShiftKeyStateFlag = 1 << 20;

    private static readonly Dictionary<GameLocationCharacter, bool> LocalShiftKeyState = [];
    private static readonly WeakKeyTable<CharacterActionParams, object> ReceivedShiftKeyState = new();
    private static readonly object ShiftKeyPressed = new();

    // the local key state is kept off the character so game state stays identical on every computer
    internal static void RegisterLocalShiftKeyState(GameLocationCharacter character, bool isShiftPressed)
    {
        LocalShiftKeyState[character] = isShiftPressed;
    }

    private static bool GetLocalShiftKeyState(GameLocationCharacter character)
    {
        return character != null && LocalShiftKeyState.TryGetValue(character, out var value) && value;
    }

    internal static void ClearLocalShiftKeyState()
    {
        LocalShiftKeyState.Clear();
    }

    private static bool IsNetworkingSerializer(IElementsSerializer serializer)
    {
        return ServiceRepository.GetService<INetworkingService>() is NetworkingManager networkingManager &&
               (ReferenceEquals(serializer, networkingManager.reader) ||
                ReferenceEquals(serializer, networkingManager.writer));
    }

    // always written by the networking serializers, so the binary layout is the same on every computer
    internal static void SerializeShiftKeyState(CharacterActionParams actionParams, IElementsSerializer serializer)
    {
        if (!IsNetworkingSerializer(serializer))
        {
            return;
        }

        if (serializer.Mode == Serializer.SerializationMode.Write)
        {
            serializer.SerializeElement(ShiftKeyStateElement,
                Global.IsMultiplayer &&
                actionParams.RulesetEffect is RulesetEffectSpell &&
                GetLocalShiftKeyState(actionParams.ActingCharacter));

            return;
        }

        var isShiftPressed = serializer.SerializeElement(ShiftKeyStateElement, false);

        ReceivedShiftKeyState.Remove(actionParams);

        if (isShiftPressed)
        {
            ReceivedShiftKeyState.Add(actionParams, ShiftKeyPressed);
        }
    }

    internal static void ApplyShiftKeyState(CharacterActionParams actionParams)
    {
        if (!Global.IsMultiplayer ||
            actionParams?.ActingCharacter == null ||
            actionParams.RulesetEffect is not RulesetEffectSpell)
        {
            return;
        }

        actionParams.ActingCharacter.SetShiftState(ReceivedShiftKeyState.TryGetValue(actionParams, out _));
    }

    internal static int EncodeShiftKeyState(int value, GameLocationCharacter character)
    {
        return Global.IsMultiplayer && GetLocalShiftKeyState(character) ? value + ShiftKeyStateFlag : value;
    }

    internal static int DecodeShiftKeyState(int value, GameLocationCharacter character)
    {
        if (!Global.IsMultiplayer)
        {
            return value;
        }

        // values are small (slot levels, sub options, -1), so anything above half the flag carries it
        var isShiftPressed = value >= ShiftKeyStateFlag / 2;

        character?.SetShiftState(isShiftPressed);

        return isShiftPressed ? value - ShiftKeyStateFlag : value;
    }

    #endregion

    #region Movement

    private static bool IsBattleInProgress =>
        ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true };

    private static List<GameLocationCharacter> GetOtherOccupants(GameLocationCharacter character, int3 position)
    {
        return GridAccessor.Default.Occupants_TryGet(position, out var occupants) && occupants != null
            ? occupants.Where(x => x != null && x != character).ToList()
            : [];
    }

    // multiplayer only: out of combat any non-hostile characters may share cells, so the party no longer queues
    // behind each other in crawl spaces and doorways. In combat only allies that are themselves moving, if enabled.
    internal static bool CanIgnoreOccupantsAt(GameLocationCharacter mover, int3 position)
    {
        if (mover == null || !Global.IsMultiplayer)
        {
            return false;
        }

        var others = GetOtherOccupants(mover, position);

        if (others.Count == 0)
        {
            return false;
        }

        if (!IsBattleInProgress)
        {
            return Main.Settings.MultiplayerAllowCharactersToOverlapOutOfCombat &&
                   others.All(x => !x.IsOppositeSide(mover.Side));
        }

        return Main.Settings.MultiplayerAllowAlliesToPassThroughInCombat &&
               others.All(x =>
                   x.Side == mover.Side &&
                   x.MoveInProgress &&
                   x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false });
    }

    // multiplayer only: when a battle starts with characters sharing a cell, every one except the cell keeper (see CellKeeper) steps
    // to the closest safe cell: free, not dangerous, and not perceived by any enemy if the character is sneaking.
    // This runs on every peer from the same game state at the same point of the simulation, which is how lockstep
    // keeps it identical everywhere (the host's result and every client's result are the same by construction).
    internal static void ResolveOverlapOnBattleStart(GameLocationCharacter character)
    {
        if (!Global.IsMultiplayer ||
            !Main.Settings.MultiplayerResolveOverlapsOnBattleStart ||
            character?.RulesetCharacter is not { IsDeadOrUnconscious: false })
        {
            return;
        }

        var others = GetOtherOccupants(character, character.LocationPosition);

        if (others.Count == 0 || CellKeeper(character, others) == character)
        {
            return;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null)
        {
            return;
        }

        if (!TryFindClosestSafeCell(character, positioningService, out var destination))
        {
            Main.Info($"No safe cell found to separate {character.Name} at battle start.");

            return;
        }

        positioningService.TeleportCharacter(character, destination, character.Orientation);
    }

    // who stays in a shared cell: a lone sneaker when no enemy can see the cell yet (moving would risk being found),
    // otherwise (several sneakers, or the cell already seen) the occupant with the lowest guid. Only game state is
    // used, so every computer picks the same one.
    private static GameLocationCharacter CellKeeper(GameLocationCharacter character, List<GameLocationCharacter> others)
    {
        var occupants = others.Append(character).ToList();
        var sneakers = occupants.Where(x => x.Stealthy).ToList();

        if (sneakers.Count == 1 && !IsCellSeenByEnemy(sneakers[0], character.LocationPosition))
        {
            return sneakers[0];
        }

        return occupants.OrderBy(x => x.Guid).First();
    }

    private static bool IsCellSeenByEnemy(GameLocationCharacter character, int3 cell)
    {
        var visibilityService = ServiceRepository.GetService<IGameLocationVisibilityService>();

        return visibilityService != null &&
               (ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [])
               .Any(x => x.IsOppositeSide(character.Side) &&
                         x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                         visibilityService.IsCellPerceivedByCharacter(cell, x));
    }

    private const int SafeCellSearchRadius = 6;

    private static bool TryFindClosestSafeCell(
        GameLocationCharacter character,
        IGameLocationPositioningService positioningService,
        out int3 destination)
    {
        var origin = character.LocationPosition;
        var enemies = character.Stealthy
            ? (ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [])
            .Where(x => x.IsOppositeSide(character.Side) &&
                        x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false })
            .ToList()
            : [];
        var visibilityService = ServiceRepository.GetService<IGameLocationVisibilityService>();

        // deterministic order: horizontal distance, then same floor first, then coordinates
        var candidates = new List<int3>();

        for (var x = -SafeCellSearchRadius; x <= SafeCellSearchRadius; x++)
        {
            for (var z = -SafeCellSearchRadius; z <= SafeCellSearchRadius; z++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || z != 0 || y != 0)
                    {
                        candidates.Add(new int3(origin.x + x, origin.y + y, origin.z + z));
                    }
                }
            }
        }

        foreach (var cell in candidates
                     .OrderBy(c => ((c.x - origin.x) * (c.x - origin.x)) + ((c.z - origin.z) * (c.z - origin.z)))
                     .ThenBy(c => Mathf.Abs(c.y - origin.y))
                     .ThenBy(c => c.x)
                     .ThenBy(c => c.z)
                     .ThenBy(c => c.y))
        {
            if (!positioningService.CanPlaceCharacter(
                    character, cell, CellHelpers.PlacementMode.Station, null, null) ||
                positioningService.IsDangerousPosition(character, cell) ||
                (visibilityService != null &&
                 enemies.Any(enemy => visibilityService.IsCellPerceivedByCharacter(cell, enemy))))
            {
                continue;
            }

            destination = cell;

            return true;
        }

        destination = origin;

        return false;
    }

    // Characters still sharing a cell (out of combat, or when no safe cell was found) are drawn slightly apart so each
    // can be pointed at. Only the model moves: in multiplayer the game takes target and bone positions from the
    // logical position and the model's initial pose, so line of sight and everything simulated stay the same.
    // Two stand side by side, three in a triangle, more in a ring that widens with their number, always inside the cell.
    private const float SpreadSmoothing = 8f;
    private const float SpreadEpsilon = 0.001f;

    private static readonly HashSet<WorldLocationCharacter> SpreadCharacters = [];

    internal static void SpreadOverlappedCharacter(WorldLocationCharacter world)
    {
        SpreadCharacters.RemoveWhere(x => !x);

        var graphics = world.graphicsCharacter;
        var character = world.gameCharacter;

        if (!graphics || character == null)
        {
            return;
        }

        var target = Vector3.zero;

        if (Global.IsMultiplayer &&
            Main.Settings.MultiplayerSpreadOverlappedCharacters &&
            // climbing, jumping and falling only happen during a move
            !character.MoveInProgress)
        {
            var occupants = GetOtherOccupants(character, character.LocationPosition)
                .Where(x => !x.MoveInProgress)
                .Append(character)
                .OrderBy(x => x.Guid)
                .ToList();

            if (occupants.Count > 1)
            {
                target = world.transform.InverseTransformDirection(
                    SpreadOffset(occupants.IndexOf(character), occupants.Count));
            }
        }

        if (target == Vector3.zero && !SpreadCharacters.Contains(world))
        {
            return;
        }

        var local = graphics.transform.localPosition;
        var wanted = new Vector3(target.x, local.y, target.z);
        var next = Vector3.Lerp(local, wanted, Mathf.Clamp01(Time.deltaTime * SpreadSmoothing));

        if (target == Vector3.zero && new Vector2(next.x, next.z).sqrMagnitude < SpreadEpsilon)
        {
            next = new Vector3(0, local.y, 0);
            SpreadCharacters.Remove(world);
        }
        else
        {
            SpreadCharacters.Add(world);
        }

        graphics.transform.localPosition = next;
    }

    private static float _cellSize;

    // world width of one grid cell, read once from the game's grid
    private static float CellSize
    {
        get
        {
            if (_cellSize > 0 ||
                ServiceRepository.GetService<IGameLocationPositioningService>() is not { } positioning)
            {
                return _cellSize > 0 ? _cellSize : 1f;
            }

            var size = (positioning.GetWorldPositionFromGridPosition(new int3(1, 0, 0)) -
                        positioning.GetWorldPositionFromGridPosition(new int3(0, 0, 0))).magnitude;

            _cellSize = size > 0.01f ? size : 1f;

            return _cellSize;
        }
    }

    // world-space offset of the index-th of count characters sharing a cell, as a share of the cell width
    private static Vector3 SpreadOffset(int index, int count)
    {
        var radius = CellSize * count switch
        {
            2 => 0.22f,
            3 => 0.26f,
            _ => Mathf.Min(0.3f + (0.02f * (count - 4)), 0.38f)
        };
        var angle = (Mathf.PI * 2f * index / count) + (count == 2 ? 0f : Mathf.PI / 2f);

        return new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
    }

    #endregion

    #region Narrative Sequences

    // with EnableHeroWithBestProficiencyToRollChoice the same hero can win several roles of a state, so lines
    // addressed between those roles make the hero talk to themselves. Give each new role its own hero when possible.
    internal static void EnsureUniqueHeroPerRole(
        NarrativeStatePlayerRoleAssignment state, HashSet<string> rolesBefore)
    {
        var rolesMap = state.PlayerRolesMap;
        var description = state.NarrativeStateDescription;

        if (rolesMap == null || description?.DialogLines == null)
        {
            return;
        }

        var isGameplaySequence = state.NarrativeSequence.IsGameplaySequence;
        var actors = state.NarrativeSequence.PlayerActors
            .Where(x => x != null &&
                        (!isGameplaySequence ||
                         x.GameLocationCharacter.RulesetCharacter is { IsDeadOrUnconscious: false }))
            .ToList();
        var used = new HashSet<WorldLocationCharacter>();

        foreach (var line in description.DialogLines)
        {
            var role = line.TextLine;

            if (rolesBefore.Contains(role) || !rolesMap.TryGetValue(role, out var actor) || actor == null)
            {
                continue;
            }

            if (used.Contains(actor))
            {
                var candidates = actors.Where(x => !used.Contains(x)).ToList();

                if (line.FlavorFlags is { Count: > 0 } && candidates.Count > 1)
                {
                    state.SortByPersonalityFlagRelevance(candidates, line.FlavorFlags);
                }

                if (candidates.Count > 0)
                {
                    actor = candidates[0];
                    rolesMap[role] = actor;
                }
            }

            used.Add(actor);
        }
    }

    private static NarrativeSequence _lastSpeechSequence;
    private static GameLocationCharacter _lastSpeaker;
    private static string _lastSpeakerRole;

    internal static void RecordSpeaker(NarrativeStateBaseSpeech state)
    {
        _lastSpeechSequence = state.NarrativeSequence;
        _lastSpeaker = state.speaker;
        _lastSpeakerRole = state.NarrativeStateDescription?.Role;
    }

    // a role is written to be spoken by someone other than the previous line's role, so never let the same hero
    // say two consecutive lines of different roles: hand the role to another hero (preferably one without a role)
    // and rebuild the line so voice, lip sync and listeners match the new speaker
    internal static void AvoidConsecutiveLinesBySameHero(NarrativeStateCharacterSpeech state)
    {
        var sequence = state.NarrativeSequence;
        var role = state.NarrativeStateDescription?.Role;

        if (sequence == null || sequence != _lastSpeechSequence || string.IsNullOrEmpty(role) ||
            role == _lastSpeakerRole || state.speaker == null || state.speaker != _lastSpeaker)
        {
            return;
        }

        var rolesMap = state.PlayerRolesMap;

        if (rolesMap == null || !rolesMap.ContainsKey(role))
        {
            return;
        }

        var isGameplaySequence = sequence.IsGameplaySequence;
        var heroesWithRole = new HashSet<WorldLocationCharacter>(rolesMap.Values);
        var candidates = sequence.PlayerActors
            .Where(x => x != null &&
                        x.GameLocationCharacter != _lastSpeaker &&
                        (!isGameplaySequence ||
                         x.GameLocationCharacter.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false }))
            .ToList();
        var withoutRole = candidates.Where(x => !heroesWithRole.Contains(x)).ToList();
        var pool = withoutRole.Count > 0 ? withoutRole : candidates;

        if (pool.Count == 0)
        {
            return;
        }

        var flavorFlags = state.NarrativeStateDescription.DialogLines?.FirstOrDefault()?.FlavorFlags;

        if (flavorFlags is { Count: > 0 } && pool.Count > 1)
        {
            state.SortByPersonalityFlagRelevance(pool, flavorFlags);
        }

        rolesMap[role] = pool[0];

        if (sequence.PlayerRolesMap != null && sequence.PlayerRolesMap != rolesMap &&
            sequence.PlayerRolesMap.ContainsKey(role))
        {
            sequence.PlayerRolesMap[role] = pool[0];
        }

        state.BuildHook(rolesMap, state.NpcRolesMap);
    }

    // runs a room callback without letting an exception escape into Photon's event dispatch
    internal static void Guarded(string what, System.Action action)
    {
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            Main.Error($"error while handling {what}: {ex}");
        }
    }

    // Official scenes are authored for 4 heroes: the first 4 player markers are their seats. Any further markers are not
    // seats. They are where the scene's scripted TeleportCharacter states send the 4 role holders later on: of the 891
    // such states in the game's narrative trees, 653 use markers 0-3 and 39 use markers 4-7, which can be anywhere (70 m
    // away in Abjuration_01_MeetKythaela, 12 m behind in Abjuration_03_MeetNikannen1).
    //
    // The game simply stands hero N on marker N, so with 8 markers heroes 5 and 6 begin on that later stage, out of the
    // scene; with 4 they stay wherever they were on the map. So the number of markers doesn't say who needs a place: with
    // more than 4 heroes the whole party is staged around the 4 seats.
    //
    // Parties of up to 4 are put in the same height order on the seats the game used (user direction, 2026-11-07: "implement
    // it for 4 or lower as well"), unless Gameplay > Campaigns > "Keep the game's order for parties of 4 or fewer" is on
    // (DisableCutsceneHeightOrderSmallParties, off by default). Off the grid only: there it changes what's shown, not game
    // state, so computers with different settings stay in sync. Map-played sequences keep the game's placement.
    internal static void PlaceExtraHeroes(NarrativeSequence sequence)
    {
        var markers = sequence.PlayerPlacementMarkers;
        var actors = sequence.PlayerActors;

        LogCutsceneParty("before", sequence, markers, actors);

        if (sequence.NarrativeTreeDefinition.IsUserContent || markers == null || markers.Length == 0 || actors == null)
        {
            return;
        }

        var seatCount = Mathf.Min(markers.Length, ToolsContext.GamePartySize);
        var seats = markers.Take(seatCount).Where(x => x != null).ToList();
        var seated = actors.Take(seatCount).ToList();
        // parties under 4 are padded with repeats (NarrativeSequenceBuilderPatcher), and a repeat already has its seat
        var extras = actors.Skip(seatCount).Where(x => x != null && !seated.Contains(x)).Distinct().ToList();

        if (seats.Count == 0)
        {
            return;
        }

        var party = actors.Where(x => x != null).Distinct().ToList();
        var isTopDown = sequence.NarrativeTreeDefinition.IsTopDownNarrativeSequence;

        if (extras.Count == 0)
        {
            // a party of up to 4: the game stood hero N on marker N, so the heroes hold the first party.Count markers (a
            // padded repeat moves its hero to a later marker, which leaves an earlier one free; these are refilled here)
            if (!isTopDown && SortsSmallParties(party) && party.Count <= seats.Count)
            {
                CutsceneStagingContext.Begin(sequence);
                StageParty(seats.Take(party.Count).ToList(), party);
                LogCutsceneParty("after", sequence, markers, actors);
            }

            return;
        }

        if (isTopDown || party.Count <= seats.Count)
        {
            StandHeroesBehindSeats(seats, extras, isTopDown);
        }
        else
        {
            CutsceneStagingContext.Begin(sequence);
            StageParty(seats, party);
        }

        LogCutsceneParty("after", sequence, markers, actors);
    }

    // A scripted teleport moves only the 4 heroes holding its roles, to markers chosen by role. So in a bigger party the others
    // would be left on the previous stage, and may stand on a seat a role holder was just sent to (653 of these states send
    // roles to markers 0-3, often right at the start). After a teleport of the whole party, stage everyone around the seats it
    // used. Height decides the places, so a teleport to the same seats moves nobody, whoever holds which role.
    //
    // A state moving fewer roles moves part of the party (Level_01_CaerLem_20_Final sends 3 away and leaves the 4th), so
    // nobody follows it. A hero already on a marker it sends someone to swaps places with them.
    //
    // Parties of up to 4 have nobody to move, which keeps the game's own behaviour for them. Off the grid all of this is only
    // what's shown; on it, the state's marker positions and the role map are the same on all computers.
    internal static void PlaceHeroesLeftByTeleport(
        NarrativeStateTeleportCharacterBase state, Dictionary<WorldLocationCharacter, Vector3> before)
    {
        var sequence = state.NarrativeSequence;
        var markers = sequence.PlayerPlacementMarkers;
        var actors = sequence.PlayerActors;

        if (sequence.NarrativeTreeDefinition.IsUserContent || markers == null || markers.Length == 0 || actors == null)
        {
            return;
        }

        if (actors.Count <= ToolsContext.GamePartySize)
        {
            RestageSmallPartyAfterTeleport(state, markers, actors);

            return;
        }

        var seats = new List<Transform>();
        var moved = new List<WorldLocationCharacter>();

        // the same conditions under which NarrativeStateTeleportCharacterBase.Begin has just moved a role
        foreach (var teleport in state.NarrativeStateDescription.CharacterTeleportDescriptions)
        {
            var index = teleport.PlayerMarkerIndex;

            if (index < 0 || index >= markers.Length || !state.PlayerRolesMap.TryGetValue(teleport.Role, out var actor))
            {
                continue;
            }

            moved.Add(actor);

            if (markers[index] != null)
            {
                seats.Add(markers[index]);
            }
        }

        var onGrid = state is NarrativeStateTeleportCharacterGameplay;

        if (seats.Count < ToolsContext.GamePartySize)
        {
            if (!onGrid)
            {
                SwapHeroesOffSeats(actors, moved, before);
            }

            return;
        }

        var party = actors.Where(x => x != null).Distinct().ToList();

        if (onGrid || party.Count <= seats.Count)
        {
            var left = party.Where(x => !moved.Contains(x)).ToList();

            if (left.Count > 0)
            {
                StandHeroesBehindSeats(seats, left, onGrid);
            }
        }
        else
        {
            StageParty(seats, party);
        }

        LogCutsceneParty("after teleport", sequence, markers, actors);
    }

    // a hero standing where a partial teleport has put someone takes that someone's old place
    private static void SwapHeroesOffSeats(
        List<WorldLocationCharacter> actors,
        List<WorldLocationCharacter> moved,
        Dictionary<WorldLocationCharacter, Vector3> before)
    {
        var placement = new GameObject("FB_CutsceneHeroPlacement").transform;

        foreach (var mover in moved.Where(x => x != null && before.ContainsKey(x)))
        {
            foreach (var other in actors.Where(x => x != null && !moved.Contains(x)).Distinct())
            {
                var gap = other.transform.position - mover.transform.position;

                gap.y = 0f;

                if (gap.magnitude > 0.3f)
                {
                    continue;
                }

                placement.SetPositionAndRotation(before[mover], other.transform.rotation);
                other.PlaceFreely(placement);
                break;
            }
        }

        Object.Destroy(placement.gameObject);
    }

    // A party of up to 4 sorted by height (PlaceExtraHeroes) is mixed up again by a whole-party teleport, which sends each role
    // holder to its role's marker. When every hero was moved, off the grid, put the party back in height order on the markers
    // they were sent to. A hero holding two roles (a party under 4 is padded with repeats) ends on the later marker, as in
    // NarrativeStateTeleportCharacterBase.Begin. A teleport of only some heroes moves part of the party, so it's left alone.
    private static void RestageSmallPartyAfterTeleport(
        NarrativeStateTeleportCharacterBase state, Transform[] markers, List<WorldLocationCharacter> actors)
    {
        var party = actors.Where(x => x != null).Distinct().ToList();

        if (state is NarrativeStateTeleportCharacterGameplay ||
            state.NarrativeSequence.NarrativeTreeDefinition.IsTopDownNarrativeSequence || !SortsSmallParties(party))
        {
            return;
        }

        var sentTo = new Dictionary<WorldLocationCharacter, Transform>();

        foreach (var teleport in state.NarrativeStateDescription.CharacterTeleportDescriptions)
        {
            var index = teleport.PlayerMarkerIndex;

            if (index >= 0 && index < markers.Length && markers[index] != null &&
                state.PlayerRolesMap.TryGetValue(teleport.Role, out var actor) && actor != null)
            {
                sentTo[actor] = markers[index];
            }
        }

        var seats = sentTo.Values.Distinct().ToList();

        if (sentTo.Count != party.Count || seats.Count != party.Count)
        {
            return;
        }

        StageParty(seats, party);
        LogCutsceneParty("after teleport", state.NarrativeSequence, markers, actors);
    }

    // whether a party of up to 4 is put in height order: two or more heroes, and the player hasn't asked to keep the game's order
    private static bool SortsSmallParties(List<WorldLocationCharacter> party)
    {
        return party.Count >= 2 && !Main.Settings.DisableCutsceneHeightOrderSmallParties;
    }

    // Places the whole party off the grid: the shortest heroes in front (shorter at the centre, taller on the flanks), the
    // tallest behind the gaps (CutsceneFormation.AssignByHeight). PlayerActors isn't touched, because it decides roles, speakers and shots;
    // CutsceneStagingContext moves a close-up to wherever its speaker ended up. Everything is measured from the seats given,
    // so the formation turns with the party at each whole-party teleport.
    private static void StageParty(List<Transform> seats, List<WorldLocationCharacter> party)
    {
        var seatPoints = seats.Select(x => CutsceneStagingContext.ToPoint(x.position)).ToList();
        var seatForwards = seats.Select(x => CutsceneStagingContext.ToPoint(x.forward)).ToList();
        var (seatHeroes, spotHeroes) = CutsceneFormation.AssignByHeight(
            party.Select(HeightOf).ToList(), CutsceneFormation.SeatsFromCentre(seatPoints, seatForwards));
        var formation = CutsceneFormation.Compute(
            seatPoints, seatForwards, spotHeroes.Length);
        var forward = ToVector(formation.Forward);
        var placement = new GameObject("FB_CutsceneHeroPlacement").transform;

        placement.rotation = Quaternion.LookRotation(forward, Vector3.up);

        for (var i = 0; i < seats.Count; i++)
        {
            party[seatHeroes[i]].PlaceFreely(seats[i]);
        }

        for (var i = 0; i < spotHeroes.Length; i++)
        {
            placement.position = ToVector(formation.Spots[i]);
            party[spotHeroes[i]].PlaceFreely(placement);
        }

        Object.Destroy(placement.gameObject);
        CutsceneStagingContext.SetStage(spotHeroes.Select(x => party[x]).ToList(), forward);
    }

    // For map-played sequences, and scenes with as many seats as heroes. Here only the heroes without a seat move. onGrid: the
    // hero also has to take up a real cell, as the game's TeleportCharacterToStartingPosition does, and the result has to be
    // identical on all computers, so these don't look at the scene's cameras, whose transforms can move while a shot is live.
    private static void StandHeroesBehindSeats(List<Transform> seats, List<WorldLocationCharacter> unordered, bool onGrid)
    {
        var heroes = unordered.OrderBy(HeightOf).ToList();
        var formation = CutsceneFormation.Compute(
            seats.Select(x => CutsceneStagingContext.ToPoint(x.position)).ToList(),
            seats.Select(x => CutsceneStagingContext.ToPoint(x.forward)).ToList(),
            heroes.Count);
        var forward = ToVector(formation.Forward);
        var right = ToVector(formation.Right);
        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        var placement = new GameObject("FB_CutsceneHeroPlacement").transform;

        placement.rotation = Quaternion.LookRotation(forward, Vector3.up);

        for (var i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            var spot = ToVector(formation.Spots[i]);

            if (!onGrid)
            {
                placement.position = spot;
                hero.PlaceFreely(placement);
                continue;
            }

            if (!TryFindFreeCell(positioningService, hero.GameLocationCharacter, placement, spot, right, forward))
            {
                // nothing to do but leave them where they are: the map's own rules forbid stacking them on someone
                Main.Info($"[CutsceneParty] no free cell near the party for {hero.GameLocationCharacter?.Name}");
                continue;
            }

            hero.PlaceFreely(placement);

            var cell = positioningService.GetGridPositionFromWorldPosition(placement);

            if (hero.GameLocationCharacter.LocationPosition != cell)
            {
                positioningService.TeleportCharacter(hero.GameLocationCharacter, cell,
                    positioningService.GetGridOrientationFromWorldOrientation(placement));
            }
        }

        Object.Destroy(placement.gameObject);
    }

    // offsets (along the row, further back) tried in turn when a spot's cell can't be used; cells are 1 m wide
    private static readonly (int Along, int Back)[] CellSearchOffsets =
        [(0, 0), (1, 0), (-1, 0), (0, 1), (1, 1), (-1, 1), (2, 0), (-2, 0), (2, 1), (-2, 1), (0, 2), (1, 2), (-1, 2)];

    private static bool TryFindFreeCell(
        IGameLocationPositioningService positioningService,
        GameLocationCharacter character,
        Transform placement,
        Vector3 spot,
        Vector3 right,
        Vector3 forward)
    {
        if (positioningService == null || character == null)
        {
            return false;
        }

        foreach (var (along, back) in CellSearchOffsets)
        {
            placement.position = spot + (right * along) - (forward * back);

            if (positioningService.CanPlaceCharacter(character,
                    positioningService.GetGridPositionFromWorldPosition(placement),
                    CellHelpers.PlacementMode.Station, null, null))
            {
                return true;
            }
        }

        return false;
    }

    private static int HeightOf(WorldLocationCharacter hero)
    {
        return hero.GameLocationCharacter?.RulesetCharacter is RulesetCharacterHero character
            ? character.HeightInches
            : int.MaxValue;
    }

    private static Vector3 ToVector(CutsceneFormation.Point point)
    {
        return new Vector3(point.X, point.Y, point.Z);
    }

    // TEMPORARY diagnostics (1.6.17.1): a 6-hero cutscene showed 5 heroes and the root cause isn't known yet. For the
    // first few sequences of each launch, log the party in the sequence with each hero's marker and position, and the
    // whole campaign party, so a missing hero can be told apart from a hidden one. Remove once the cause is known.
    private const int MaxCutsceneLogs = 16;
    private static int _cutsceneLogs;

    private static void LogCutsceneParty(
        string when, NarrativeSequence sequence, Transform[] markers, List<WorldLocationCharacter> actors)
    {
        if (_cutsceneLogs >= MaxCutsceneLogs)
        {
            return;
        }

        _cutsceneLogs++;

        var party = Gui.GameCampaign?.Party?.CharactersList;

        Main.Info($"[CutsceneParty] {when} {sequence.NarrativeTreeDefinition?.Name}: userContent=" +
                  $"{sequence.NarrativeTreeDefinition?.IsUserContent}, topDown=" +
                  $"{sequence.NarrativeTreeDefinition?.IsTopDownNarrativeSequence}, markers={markers?.Length ?? -1}, " +
                  $"actors={actors?.Count ?? -1}, campaignParty={party?.Count ?? -1}, " +
                  $"allowAll={Main.Settings.AllowAllPlayersOnNarrativeSequences}, " +
                  $"partySize={Main.Settings.OverridePartySize}");

        for (var i = 0; markers != null && i < markers.Length; i++)
        {
            Main.Info($"[CutsceneParty]   marker {i}: " +
                      (markers[i] ? $"{markers[i].position} forward {markers[i].forward}" : "null"));
        }

        for (var i = 0; actors != null && i < actors.Count; i++)
        {
            var actor = actors[i];

            Main.Info($"[CutsceneParty]   actor {i}: " + (actor
                ? $"{actor.GameLocationCharacter?.Name} at {actor.transform.position} " +
                  $"active={actor.gameObject.activeInHierarchy}"
                : "null"));
        }

        if (party == null || actors == null)
        {
            return;
        }

        foreach (var character in party.Where(x =>
                     actors.All(a => !a || a.GameLocationCharacter?.RulesetCharacter != x.RulesetCharacter)))
        {
            Main.Info($"[CutsceneParty]   not in the sequence: {character.RulesetCharacter?.Name}");
        }
    }

    #endregion
}
