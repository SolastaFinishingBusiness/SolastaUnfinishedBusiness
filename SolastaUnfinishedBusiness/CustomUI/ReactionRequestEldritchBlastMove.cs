using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses.Builders;
using static SolastaUnfinishedBusiness.Models.EldritchBlastMoveContext;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     The prompt asking how far one beam of Eldritch Blast pushes (Repelling Blast) and pulls (Grasp of Hadar) the
///     creature it hit, and which goes first (issue #37). It only borrows the look of a reaction: it runs a free
///     "do nothing" action and never uses the caster's reaction.
///     <para>
///         The whole answer travels as the one number the game sends with a reaction answer (the sub-option), so it
///         reaches every computer: see <see cref="Pack" />. The game calls <see cref="SelectSubOption" /> with that
///         number on every computer before the request is processed; <see cref="Chosen" /> and <see cref="ForAll" />
///         are only read after that.
///     </para>
/// </summary>
internal sealed class ReactionRequestEldritchBlastMove : ReactionRequest
{
    internal const string Name = "EldritchBlastMove";

    internal const int ButtonSubmit = 0;
    internal const int ButtonSkip = 1;
    internal const int ButtonSubmitForAll = 2;
    internal const int ButtonSkipForAll = 3;

    private const int PackedCount = 72; // 3 push distances x 3 pull distances x 2 orders x 4 buttons

    private readonly GuiCharacter _caster;
    private readonly GuiCharacter _target;
    private int _packed;

    internal ReactionRequestEldritchBlastMove(
        CharacterActionParams reactionParams,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        bool hasPush,
        bool hasPull) : base(Name, reactionParams)
    {
        _caster = new GuiCharacter(caster);
        _target = new GuiCharacter(target);
        HasPush = hasPush;
        HasPull = hasPull;

        // What stands if the request is validated without a number, which only the game's own React path could do:
        // the full distance in the default order, for this beam only.
        Chosen = Limit(Decision.Default);
        _packed = Pack(Chosen.PushCells, Chosen.PullCells, true, ButtonSubmit);
    }

    /// <summary>Whether Repelling Blast takes part in this beam.</summary>
    internal bool HasPush { get; }

    /// <summary>Whether Grasp of Hadar takes part in this beam.</summary>
    internal bool HasPull { get; }

    /// <summary>The moves chosen for this beam.</summary>
    internal Decision Chosen { get; private set; }

    /// <summary>True when the caster chose the same for every remaining beam of the cast.</summary>
    internal bool ForAll { get; private set; }

    public override int SelectedSubOption => _packed;

    internal static string PushTitle => DatabaseHelper.InvocationDefinitions.RepellingBlast.FormatTitle();

    internal static string PullTitle => InvocationsBuilders.GraspingBlast.FormatTitle();

    /// <summary>Packs an answer into one small number (0 to 71), well below the mod's SHIFT-key flag.</summary>
    internal static int Pack(int pushCells, int pullCells, bool pushFirst, int button)
    {
        return pushCells + (3 * pullCells) + (9 * (pushFirst ? 0 : 1)) + (18 * button);
    }

    /// <summary>
    ///     Called by the game on every computer with the number sent with the answer. Anything out of range is
    ///     ignored, which leaves the default answer in place.
    /// </summary>
    public override void SelectSubOption(int option)
    {
        if (option is < 0 or >= PackedCount)
        {
            return;
        }

        _packed = option;

        var pushCells = option % 3;
        var pullCells = option / 3 % 3;
        var pushFirst = option / 9 % 2 == 0;
        var button = option / 18;

        ForAll = button is ButtonSubmitForAll or ButtonSkipForAll;
        Chosen = button is ButtonSkip or ButtonSkipForAll
            ? Decision.None
            : Limit(new Decision(pushCells, pullCells, pushFirst));
    }

    // an invocation that takes no part in the beam never moves the creature, whatever number arrived
    private Decision Limit(Decision decision)
    {
        return new Decision(HasPush ? decision.PushCells : 0, HasPull ? decision.PullCells : 0, decision.PushFirst);
    }

    public override string FormatTitle()
    {
        // the spell's own name, in the game's language
        return DatabaseHelper.SpellDefinitions.EldritchBlast.FormatTitle();
    }

    public override string FormatDescription()
    {
        return Gui.Format("Reaction/&EldritchBlastMoveDescription", _caster.Name, _target.Name,
            DatabaseHelper.SpellDefinitions.EldritchBlast.FormatTitle());
    }

    public override string FormatReactTitle()
    {
        return Gui.Localize("Reaction/&EldritchBlastMoveReactTitle");
    }

    public override string FormatReactDescription()
    {
        return Gui.Localize("Reaction/&EldritchBlastMoveReactDescription");
    }
}
