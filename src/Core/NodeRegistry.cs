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

    /// <summary>Register a node type with a custom factory.</summary>
    public static void Register(string classType, Func<ComfyNode> factory)
    {
        Factories[classType] = factory;
    }

    /// <summary>
    /// Discover and register every concrete <see cref="ComfyNode"/> subclass in
    /// <paramref name="asm"/>. Candidates must be non-abstract classes with a
    /// parameterless constructor; the constructor is invoked once on a probe
    /// instance to read <see cref="ComfyNode.ClassTypeName"/> as the registry key.
    /// Idempotent per assembly. Safe to call concurrently — readers (Create)
    /// block until the scan finishes the first time.
    /// </summary>
    public static void RegisterAssembly(Assembly asm)
    {
        lock (ScanLock)
        {
            if (!ScannedAssemblies.Add(asm))
            {
                return;
            }

            // Tolerate ReflectionTypeLoadException so the registry still works when the
            // assembly is reflected over without all of its referenced assemblies present
            // (e.g. the codegen tool loading ComfyTyped.dll without SwarmUI.dll alongside).
            // Generated nodes never reference SwarmUI; only the SwarmUI-coupled types do,
            // and they are not ComfyNode subclasses, so dropping them is harmless here.
            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (Type? t in types)
            {
                if (t is null || !t.IsClass || t.IsAbstract || !typeof(ComfyNode).IsAssignableFrom(t)
                    || t.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                ComfyNode probe = (ComfyNode)Activator.CreateInstance(t)!;
                Type captured = t;
                Factories[probe.ClassTypeName] = () => (ComfyNode)Activator.CreateInstance(captured)!;
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

    /// <summary>All registered class_type strings.</summary>
    public static IEnumerable<string> RegisteredTypes => Factories.Keys;
}
