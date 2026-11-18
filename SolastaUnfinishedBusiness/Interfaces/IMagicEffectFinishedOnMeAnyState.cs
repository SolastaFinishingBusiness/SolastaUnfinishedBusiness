using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

/// <summary>
///     Like IMagicEffectFinishedOnMe, but called for every target of the finished effect, including dead, dying and
///     unconscious ones, for clean-up that must happen whatever the effect did to the target.
/// </summary>
public interface IMagicEffectFinishedOnMeAnyState
{
    [UsedImplicitly]
    IEnumerator OnMagicEffectFinishedOnMeAnyState(
        CharacterAction action,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        List<GameLocationCharacter> targets);
}
