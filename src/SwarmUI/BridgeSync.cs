using ComfyTyped.Core;
using ComfyTyped.Types;
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
/// (<c>AddNode</c>, <c>AddStub</c>, <c>RemoveNode</c>, <c>SyncNode</c>,
/// <c>SyncAll</c>, <c>Graph</c>, <c>Workflow</c>, <c>ResolvePath</c>) so callers
/// don't have to dot through <see cref="Bridge"/>. For less common operations,
/// use the inner <see cref="Bridge"/> directly.</para>
///
/// <para>An <b>implicit conversion</b> to <see cref="WorkflowBridge"/> makes this
/// wrapper a drop-in anywhere a bare bridge is expected — e.g.
/// <see cref="NodeInputExtensions.ConnectFromPath{T}(NodeInput{T}, WorkflowBridge, JArray?, string?)"/>
/// or <see cref="MediaRef.FromWGNodeData"/> accept it directly with no
/// <c>.Bridge</c> hop. The conversion hands back the inner bridge (whose own
/// <see cref="WorkflowBridge.Dispose"/> is pure); the deferred
/// <see cref="BridgeSync.SyncLastId"/> still fires when <em>this</em> wrapper is
/// disposed, so converting does not skip the sync.</para>
///
/// <para>Typical use collapses the
/// <c>WorkflowBridge.Create</c> + <c>SyncNode</c> + <c>BridgeSync.SyncLastId</c>
/// ritual into a single <c>using</c>:
/// <code>
/// using var bridge = BridgeSync.For(g);
/// SwarmTrimFramesNode node = bridge.AddNode(new SwarmTrimFramesNode().With(TrimStart: a, TrimEnd: b));
/// node.Image.TryConnectFromPath(bridge, imagePath); // auto-syncs into the JObject
/// return node.Id;                                    // SyncLastId fires on dispose
/// </code>
/// The explicit <c>SyncNode</c> is unnecessary for pure typed mutations (the bridge
/// auto-syncs subscribed nodes); keep an explicit <c>SyncNode</c> only after editing
/// <see cref="ComfyNode.ExtraInputs"/> or <see cref="UnknownNode.RawInputs"/>.</para>
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

    /// <summary>Implicitly unwrap to the inner <see cref="WorkflowBridge"/> so this
    /// wrapper drops into any API typed against a bare bridge. Safe: the inner
    /// bridge's <see cref="WorkflowBridge.Dispose"/> is pure, and the deferred
    /// <see cref="BridgeSync.SyncLastId"/> still runs when the wrapper is disposed.</summary>
    public static implicit operator WorkflowBridge(SyncingWorkflowBridge syncing) => syncing.Bridge;

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

    /// <inheritdoc cref="WorkflowBridge.ResolvePath{T}(JArray?)"/>
    public NodeOutput<T>? ResolvePath<T>(JArray? path) where T : IComfyType => Bridge.ResolvePath<T>(path);

    /// <inheritdoc cref="WorkflowBridge.NodeAt(JArray?)"/>
    public ComfyNode? NodeAt(JArray? path) => Bridge.NodeAt(path);

    /// <inheritdoc cref="WorkflowBridge.NodeAt{T}(JArray?)"/>
    public T? NodeAt<T>(JArray? path) where T : class => Bridge.NodeAt<T>(path);

    /// <inheritdoc cref="WorkflowBridge.SyncNode(ComfyNode)"/>
    public void SyncNode(ComfyNode node) => Bridge.SyncNode(node);

    /// <inheritdoc cref="WorkflowBridge.SyncNode(string)"/>
    public void SyncNode(string id) => Bridge.SyncNode(id);

    /// <inheritdoc cref="WorkflowBridge.SyncAll"/>
    public void SyncAll() => Bridge.SyncAll();

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
