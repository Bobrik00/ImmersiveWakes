using System;

namespace ImmersiveWakes.Config;

// What a source of one kind produces: the wake in the simulation, the effects at the bow, both or nothing. The four
// values of Wakes' enum are the four pairs of flags, so the rule is kept as the pair and the file holds the enum's
// names. In Wakes the planes flag shows the splash plane, whose tick spawns the bow clouds; the plane is not ported
// (DESIGN "Сокращение уровня", 2026-10-10), and the flag gates the clouds of T06.03.
readonly record struct EffectSpawningRule(bool SimulateWakes, bool RenderPlanes)
{
    public static readonly EffectSpawningRule SimulationAndPlanes = new(true, true);
    public static readonly EffectSpawningRule OnlySimulation = new(true, false);
    public static readonly EffectSpawningRule OnlyPlanes = new(false, true);
    public static readonly EffectSpawningRule Disabled = new(false, false);

    // In the order of Name's index below.
    public static readonly string[] Names = ["SIMULATION_AND_PLANES", "ONLY_SIMULATION", "ONLY_PLANES", "DISABLED"];

    public string Name => Names[(SimulateWakes ? 0 : 2) + (RenderPlanes ? 0 : 1)];

    // A boat that carries a remote player and not ours takes boatSpawning masked by otherPlayersSpawning. Wakes 0.7.1
    // takes the planes flag of the mask from rule.simulateWakes (EffectSpawningRule.mask), so at its defaults such a
    // boat shows planes and ONLY_PLANES of the other players turns them off; here each flag comes from its own.
    public EffectSpawningRule Mask(EffectSpawningRule other) =>
        new(SimulateWakes && other.SimulateWakes, RenderPlanes && other.RenderPlanes);

    // A name of Names in any case, spaces around it allowed.
    public static bool TryParse(string? text, out EffectSpawningRule rule)
    {
        rule = Disabled;
        if (text == null) return false;
        int i = Array.FindIndex(Names, n => n.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        rule = new EffectSpawningRule(i < 2, i % 2 == 0);
        return true;
    }

    public override string ToString() => Name;
}
