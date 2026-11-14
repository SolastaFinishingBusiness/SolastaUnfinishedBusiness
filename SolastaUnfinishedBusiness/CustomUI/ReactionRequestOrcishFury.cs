using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     Orcish Fury's reaction (#62): "Immediately after you use your Relentless Endurance trait, you can use your
///     reaction to make one weapon attack." The player picks the creature to attack from a list of the enemies a
///     weapon can reach (one button each, <see cref="OrcishFuryPrompt" />), or passes with the game's own Pass.
///     <para>
///         The choice travels as the one number the game sends with a reaction answer (the sub-option): an index into
///         <see cref="Targets" />. The list is built the same way on every computer from the battle (the creature
///         that hit first when a weapon reaches it, then the others by distance, then battle order), and the game
///         calls <see cref="SelectSubOption" /> with that number on every computer before the request is processed.
///         A validated request then runs <see cref="ReactionRequest.ReactionParams" /> as the game's opportunity
///         attack action, against the chosen target with that target's weapon mode. The game's AI answers only yes
///         or no, so an AI-controlled half-orc attacks the first target.
///     </para>
/// </summary>
internal sealed class ReactionRequestOrcishFury : ReactionRequest
{
    internal const string Name = "ReactionAttackOrcishFury";

    private readonly GuiCharacter _attacker;
    private int _selected;

    internal ReactionRequestOrcishFury(
        CharacterActionParams reactionParams,
        GameLocationCharacter attacker,
        List<Target> targets) : base(Name, reactionParams)
    {
        _attacker = new GuiCharacter(attacker);
        Targets = targets;
        Apply(0);
    }

    /// <summary>The creatures that can be attacked, each with the weapon mode that reaches it, in button order.</summary>
    internal List<Target> Targets { get; }

    public override int SelectedSubOption => _selected;

    public override bool IsStillValid
    {
        get
        {
            var target = ReactionParams.TargetCharacters.Count > 0 ? ReactionParams.TargetCharacters[0] : null;

            return target != null &&
                   target.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false } &&
                   ServiceRepository.GetService<IGameLocationCharacterService>().ValidCharacters.Contains(target);
        }
    }

    /// <summary>
    ///     Called by the game on every computer with the number sent with the answer. Anything out of range is
    ///     ignored, which keeps the first target.
    /// </summary>
    public override void SelectSubOption(int option)
    {
        if (option >= 0 && option < Targets.Count)
        {
            Apply(option);
        }
    }

    private void Apply(int index)
    {
        var target = Targets[index];

        _selected = index;
        ReactionParams.AttackMode = target.Mode;
        ReactionParams.TargetCharacters.SetRange(target.Character);
        ReactionParams.ActionModifiers.SetRange(target.Modifier);
    }

    public override string FormatTitle()
    {
        return Gui.Localize($"Reaction/&{Name}Title");
    }

    public override string FormatDescription()
    {
        return Gui.Format($"Reaction/&{Name}Description", _attacker.Name);
    }

    public override string FormatReactTitle()
    {
        return Gui.Localize($"Reaction/&{Name}ReactTitle");
    }

    public override string FormatReactDescription()
    {
        return Gui.Localize($"Reaction/&{Name}ReactDescription");
    }

    /// <summary>One creature the half-orc can attack, with the weapon mode (a copy) and modifier that reach it.</summary>
    internal sealed class Target(
        GameLocationCharacter character,
        RulesetAttackMode mode,
        ActionModifier modifier,
        int distanceCells)
    {
        internal GameLocationCharacter Character { get; } = character;
        internal RulesetAttackMode Mode { get; } = mode;
        internal ActionModifier Modifier { get; } = modifier;
        internal int DistanceCells { get; } = distanceCells;
    }
}
