using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

/// <summary>
///     Clean-ups a feature registers on a magic effect while it runs, done once the effect's action has finished
///     (CharacterActionMagicEffect, after every target was handled). They hold their own references to what they undo,
///     so they don't depend on the targets' state or features at that point.
/// </summary>
internal static class EffectFinishedCleanup
{
    private static readonly ConditionalWeakTable<RulesetEffect, List<Action>> Cleanups = new();

    internal static void Add(RulesetEffect effect, Action cleanup)
    {
        if (effect == null)
        {
            cleanup();

            return;
        }

        Cleanups.GetValue(effect, _ => []).Add(cleanup);
    }

    internal static void Run(RulesetEffect effect)
    {
        if (effect == null || !Cleanups.TryGetValue(effect, out var cleanups))
        {
            return;
        }

        Cleanups.Remove(effect);

        foreach (var cleanup in cleanups)
        {
            cleanup();
        }
    }
}
