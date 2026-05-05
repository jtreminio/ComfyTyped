using System.Reflection;
using System.Runtime.Loader;
using SwarmUI.Core;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// SwarmUI extension stub. Exists so that SwarmUI's <see cref="ExtensionsManager"/>
/// instantiates this type — which triggers the static constructor below — before
/// it walks <c>GetTypes()</c> on later-loaded extensions that depend on the
/// <c>ComfyTyped</c> assembly.
///
/// Each extension is loaded via <see cref="Assembly.LoadFile"/>, which puts it in
/// its own <see cref="AssemblyLoadContext"/>. Those contexts do not probe sibling
/// directories. Without a resolver registered on the default context, a dependent
/// extension's <c>GetTypes()</c> throws <see cref="ReflectionTypeLoadException"/>
/// when its metadata references <c>ComfyTyped</c> types.
/// </summary>
public sealed class ComfyTypedExtension : Extension
{
    static ComfyTypedExtension()
    {
        Assembly self = typeof(ComfyTypedExtension).Assembly;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            name.Name == "ComfyTyped" ? self : null;
    }
}
