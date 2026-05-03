using System.Collections.Concurrent;
using System.Reflection;

namespace ComfyTyped.Core;

/// <summary>
/// Maps ComfyUI class_type strings to C# node factory functions.
/// The generated <c>NodeRegistrations.EnsureRegistered()</c> calls
/// <see cref="RegisterAssembly"/> on its own assembly to populate this registry.
/// </summary>
public static class NodeRegistry
{
    private static readonly ConcurrentDictionary<string, Func<ComfyNode>> Factories = new(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> ScannedAssemblies = [];
    private static readonly object ScanLock = new();

    /// <summary>Register a single node type. Available for callers that need explicit control.</summary>
    public static void Register<T>(string classType) where T : ComfyNode, new()
    {
        Factories[classType] = () => new T();
    }

    /// <summary>Register a node type with a custom factory.</summary>
    public static void Register(string classType, Func<ComfyNode> factory)
    {
        Factories[classType] = factory;
    }

    /// <summary>
    /// Discover and register every concrete <see cref="ComfyNode"/> subclass in
    /// <paramref name="asm"/>. Candidates must be non-abstract classes with a
    /// parameterless constructor; the constructor is invoked once on a probe
    /// instance to read <see cref="ComfyNode.ClassType"/> as the registry key.
    /// Idempotent per assembly. Safe to call concurrently — readers (Create,
    /// IsKnown) block until the scan finishes the first time.
    /// </summary>
    public static void RegisterAssembly(Assembly asm)
    {
        lock (ScanLock)
        {
            if (!ScannedAssemblies.Add(asm))
            {
                return;
            }

            foreach (Type t in asm.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract || !typeof(ComfyNode).IsAssignableFrom(t)
                    || t.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                ComfyNode probe = (ComfyNode)Activator.CreateInstance(t)!;
                Type captured = t;
                Factories[probe.ClassType] = () => (ComfyNode)Activator.CreateInstance(captured)!;
            }
        }
    }

    /// <summary>Create a typed node for the given class_type, or an <see cref="UnknownNode"/> if not registered.</summary>
    public static ComfyNode Create(string classType)
    {
        if (Factories.TryGetValue(classType, out Func<ComfyNode>? factory))
        {
            return factory();
        }

        return new UnknownNode(classType);
    }

    /// <summary>Check whether a class_type has a registered typed node.</summary>
    public static bool IsKnown(string classType) => Factories.ContainsKey(classType);

    /// <summary>All registered class_type strings.</summary>
    public static IEnumerable<string> RegisteredTypes => Factories.Keys;
}
