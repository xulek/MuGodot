using MuGodot.Objects.Worlds.Lorencia;

namespace MuGodot.Objects.Worlds;

public static class WorldObjectRulesResolver
{
    private static readonly IReadOnlyDictionary<int, IWorldObjectRules> RulesByWorld =
        new Dictionary<int, IWorldObjectRules>
        {
            [1] = LorenciaWorldObjectRules.Instance,
        };

    public static IWorldObjectRules Resolve(int worldIndex)
    {
        return RulesByWorld.TryGetValue(worldIndex, out var rules)
            ? rules
            : FallbackWorldObjectRules.Instance;
    }
}
