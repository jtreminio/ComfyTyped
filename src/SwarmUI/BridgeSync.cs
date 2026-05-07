using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// Synchronizes SwarmUI's <see cref="WorkflowGenerator.LastID"/> counter
/// after bridge operations that may have added nodes with IDs beyond
/// the generator's current counter.
/// </summary>
public static class BridgeSync
{
    /// <summary>
    /// Ensure g.LastID is at least max(numeric JObject keys) + 1.
    /// Call this after bridge.SyncAll() or bridge.AddNode() to prevent
    /// subsequent g.CreateNode() calls from colliding with bridge-assigned IDs.
    /// </summary>
    public static void SyncLastId(WorkflowGenerator g)
    {
        foreach (JProperty prop in g.Workflow.Properties())
        {
            if (int.TryParse(prop.Name, out int n) && n >= g.LastID)
            {
                g.LastID = n + 1;
            }
        }
    }

    /// <summary>
    /// Create a <see cref="SyncingWorkflowBridge"/> over <c>g.Workflow</c> that
    /// calls <see cref="SyncLastId"/> when disposed. Use as the default bridge
    /// factory in SwarmUI seed steps so callers don't have to remember the
    /// trailing <c>BridgeSync.SyncLastId(g);</c> line.
    ///
    /// <para>Implemented as a wrapper, not a subclass —
    /// <see cref="WorkflowBridge.Dispose"/> remains pure (subscription teardown
    /// only). The wrapper's <see cref="SyncingWorkflowBridge.Dispose"/> calls
    /// <see cref="SyncLastId"/> first, then disposes the inner bridge.</para>
    /// </summary>
    public static SyncingWorkflowBridge For(WorkflowGenerator g) =>
        new(WorkflowBridge.Create(g.Workflow), g);
}

/// <summary>
/// A disposable wrapper around <see cref="WorkflowBridge"/> that calls
/// <see cref="BridgeSync.SyncLastId"/> on dispose, then forwards to the inner
/// bridge's own <see cref="WorkflowBridge.Dispose"/>. Construct via
/// <see cref="BridgeSync.For"/>.
///
/// <para>Forwards the most common <see cref="WorkflowBridge"/> surface
/// (<c>AddNode</c>, <c>AddStub</c>, <c>RemoveNode</c>, <c>Graph</c>,
/// <c>Workflow</c>, <c>ResolvePath</c>) so callers don't have to dot through
/// <see cref="Bridge"/>. For less common operations, use the inner
/// <see cref="Bridge"/> directly.</para>
/// </summary>
public sealed class SyncingWorkflowBridge : IDisposable
{
    private readonly WorkflowGenerator _g;
    private bool _disposed;

    /// <summary>The wrapped <see cref="WorkflowBridge"/>. Use for any operation
    /// not exposed directly on this wrapper.</summary>
    public WorkflowBridge Bridge { get; }

    internal SyncingWorkflowBridge(WorkflowBridge inner, WorkflowGenerator g)
    {
        Bridge = inner;
        _g = g;
    }

    /// <inheritdoc cref="WorkflowBridge.Graph"/>
    public ComfyGraph Graph => Bridge.Graph;

    /// <inheritdoc cref="WorkflowBridge.Workflow"/>
    public JObject Workflow => Bridge.Workflow;

    /// <inheritdoc cref="WorkflowBridge.AddNode{T}(T)"/>
    public T AddNode<T>(T node) where T : ComfyNode => Bridge.AddNode(node);

    /// <inheritdoc cref="WorkflowBridge.AddNode{T}(T, string)"/>
    public T AddNode<T>(T node, string id) where T : ComfyNode => Bridge.AddNode(node, id);

    /// <inheritdoc cref="WorkflowBridge.AddStub(string, string)"/>
    public UnknownNode AddStub(string classType, string id) => Bridge.AddStub(classType, id);

    /// <inheritdoc cref="WorkflowBridge.AddStub(string)"/>
    public UnknownNode AddStub(string classType) => Bridge.AddStub(classType);

    /// <inheritdoc cref="WorkflowBridge.RemoveNode(string)"/>
    public bool RemoveNode(string id) => Bridge.RemoveNode(id);

    /// <inheritdoc cref="WorkflowBridge.RemoveNode(ComfyNode)"/>
    public bool RemoveNode(ComfyNode node) => Bridge.RemoveNode(node);

    /// <inheritdoc cref="WorkflowBridge.ResolvePath(JArray?)"/>
    public INodeOutput? ResolvePath(JArray? path) => Bridge.ResolvePath(path);

    /// <summary>
    /// Calls <see cref="BridgeSync.SyncLastId"/> on the wrapped generator, then
    /// disposes the inner <see cref="WorkflowBridge"/>. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        BridgeSync.SyncLastId(_g);
        Bridge.Dispose();
    }
}
