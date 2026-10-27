using System.Collections;
using System.Reflection;

namespace CardRarityRandomizer;

// The mod is built without a public API, so every game member it touches is looked up here by
// name. One place to fix after a game update, and a missing member degrades to a logged error.
internal static class GameReflection
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static Type? FindType(string fullName) =>
        typeof(MegaCrit.Sts2.Core.Modding.ModInitializerAttribute).Assembly.GetType(fullName);

    public static object? Get(object target, string name)
    {
        var type = target.GetType();
        var prop = type.GetProperty(name, Any);
        if (prop != null) return prop.GetValue(target);
        return type.GetField(name, Any)?.GetValue(target);
    }

    public static object? GetStatic(Type type, string name)
    {
        var prop = type.GetProperty(name, Any);
        if (prop != null) return prop.GetValue(null);
        return type.GetField(name, Any)?.GetValue(null);
    }

    public static IEnumerable<object> Enumerate(object? source) =>
        source is IEnumerable e ? e.Cast<object>() : Enumerable.Empty<object>();

    // Breadth-first search for a string member whose name contains "seed", starting at the run manager.
    public static string? FindRunSeed(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Obj, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (obj, depth) = queue.Dequeue();
            if (!seen.Add(obj)) continue;
            foreach (var member in obj.GetType().GetMembers(Any))
            {
                object? value;
                try
                {
                    value = member switch
                    {
                        PropertyInfo p when p.GetIndexParameters().Length == 0 && p.CanRead => p.GetValue(obj),
                        FieldInfo f => f.GetValue(obj),
                        _ => null,
                    };
                }
                catch { continue; }

                if (value is string s && member.Name.Contains("seed", StringComparison.OrdinalIgnoreCase) && s.Length > 0)
                    return s;
                if (depth < 2 && value != null && member.Name is "State" or "RunState" or "Rng" or "_state" or "_rng")
                    queue.Enqueue((value, depth + 1));
            }
        }
        return null;
    }
}
