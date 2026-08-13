using ComfyTyped.Types;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Bridges between a JObject workflow (untyped, mutable) and a ComfyGraph (typed).
/// Enables gradual migration from manual JObject manipulation to typed graph operations.
///
/// <para>
/// <b>Auto-sync.</b> Typed mutations on a node added through this bridge propagate into the
/// JObject: <see cref="NodeInput{T}.Set"/>, <see cref="NodeInput{T}.ConnectTo"/>,
/// <see cref="NodeInput{T}.ConnectToUntyped"/>, and <see cref="NodeInput{T}.Clear"/> update
/// <c>Workflow[id]["inputs"][name]</c> in place. The outer node JObject (<c>Workflow[id]</c>)
/// reference stays stable across typed mutations, so callers may hold it.
/// </para>
///
/// <para>
/// <b>Not auto-synced.</b> The following still require an explicit <see cref="SyncNode(ComfyNode)"/>:
/// <list type="bullet">
///   <item>Edits to <see cref="ComfyNode.ExtraInputs"/> (raw JObject; no events). A typed-slot mutation
///         on the same node also won't pick up new ExtraInputs keys — only the changed slot is written.</item>
///   <item>Edits to <see cref="UnknownNode.RawInputs"/> directly.</item>
///   <item>Mutations made directly against the JObject from outside the bridge.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Caveats:</b>
/// <list type="bullet">
///   <item>An <see cref="UnknownNode"/>'s first typed mutation replaces the inner <c>inputs</c> JObject
///         (it was a clone of <c>RawInputs</c>); the outer node JObject ref remains stable.</item>
///   <item><see cref="SyncNode(ComfyNode)"/> rebuilds from the typed graph and
///         <em>does</em> replace the outer <c>Workflow[id]</c> JObject — held references become detached.</item>
///   <item>A <see cref="NodeInput{T}.Clear"/> followed by <see cref="NodeInput{T}.Set"/> on the same input
///         appends the property to the end of <c>inputs</c> rather than restoring its original position.
///         Semantically irrelevant to ComfyUI but visible to anyone diffing/hashing serialized workflows.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Removing a node with consumers.</b> <see cref="RemoveNode(string)"/> is a "dumb delete": it
/// removes the node from the graph and the JObject but does <em>not</em> disconnect downstream inputs
/// that referenced its outputs — those <c>NodeInput&lt;T&gt;</c> retain a reference to the removed
/// node, and the corresponding <c>[id, slot]</c> JArrays in their serialized inputs still point at
/// the now-deleted ID. Before deleting a node with consumers, rewire each output:
/// <code>
/// foreach (var output in old.Outputs)
/// {
///     var to = replacement.FindOutput(output.SlotIndex);
///     if (to is not null) bridge.Graph.RetargetConnections(output, to);
/// }
/// bridge.RemoveNode(old);
/// </code>
/// Auto-sync flushes the rewires; the <c>RemoveNode</c> then has no surviving references to clean up.
/// The <c>FindOutput</c> null-guard matters when the replacement is a different node class —
/// slot indices that don't exist on the replacement leave consumers dangling and you'll need to
/// handle them explicitly (rewire elsewhere, or <c>Clear()</c>).
/// To drop a node without a replacement, iterate its outputs and clear every consumer:
/// <code>
/// foreach (var output in old.Outputs)
///     foreach (var (_, input) in bridge.Graph.FindInputsConnectedTo(output))
///         input.Clear();
/// bridge.RemoveNode(old);
/// </code>
/// </para>
///
/// <para>
/// <b>Lifetime.</b> The bridge subscribes to every node it tracks; while those subscriptions exist, a
/// reference to any tracked node keeps the bridge (and the entire workflow JObject and graph) reachable.
/// Call <see cref="Dispose"/> when done to drop the subscriptions, or use a <c>using</c> block.
/// </para>
///
/// Not thread-safe. Designed for SwarmUI's single-threaded workflow generation.
/// </summary>
public sealed class WorkflowBridge : IDisposable
{
    private readonly ComfyGraph _graph;
    private readonly JObject _workflow;
    private readonly Action<ComfyNode, INodeInput> _onInputChanged;
    private readonly Action<ComfyNode, INodeInputList> _onInputListChanged;
    /// <summary>Nodes currently subscribed-to by this bridge. Guards against
    /// double-subscribe when a caller adds the same node instance twice (e.g.
    /// via <c>bridge.AddNode(graph.GetNode(id))</c>).</summary>
    private readonly HashSet<ComfyNode> _subscribed = [];

    /// <summary>Optional host ID-counter kept in lockstep with bridge-assigned IDs; null in
    /// host-agnostic / read-only uses (then ID bookkeeping is skipped). The SwarmUI layer supplies an
    /// adapter over <c>WorkflowGenerator.LastID</c> — Core never references the host type.</summary>
    private readonly INodeIdCounter? _idCounter;
    private bool _disposed;

    /// <summary>The typed graph view, deserialized from the workflow at creation time.</summary>
    public ComfyGraph Graph => _graph;

    /// <summary>The original JObject workflow (same reference, not a clone).</summary>
    public JObject Workflow => _workflow;

    public WorkflowBridge(ComfyGraph graph, JObject workflow, INodeIdCounter? idCounter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(workflow);
        _graph = graph;
        _workflow = workflow;
        _idCounter = idCounter;
        _onInputChanged = OnInputChanged;
        _onInputListChanged = OnInputListChanged;
        // Subscribe only. The counter follows IDs this bridge mints, never IDs the host already
        // minted: SwarmUI allocates from banded ranges (LoRA loaders at 3000+, saves at 51000+) that
        // its own unbanded `LastID++` never checks, so raising LastID to cover a banded node makes
        // the very next host-minted node overwrite one.
        foreach (ComfyNode node in graph.Nodes.Values)
        {
            Subscribe(node);
        }
    }

    /// <summary>
    /// Create a bridge from an existing JObject workflow.
    /// The typed graph is deserialized from the JObject and kept in sync going forward.
    /// </summary>
    public static WorkflowBridge Create(JObject workflow, INodeIdCounter? idCounter = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);

        return new WorkflowBridge(graph, workflow, idCounter);
    }

    // ── Add ─────────────────────────────────────────────────────────

    /// <summary>
    /// Add a typed node with an auto-assigned ID.
    /// Updates both the typed graph and the JObject workflow, and starts auto-syncing
    /// future typed mutations on the node into the JObject.
    /// </summary>
    public T AddNode<T>(T node) where T : ComfyNode
    {
        EnsureNextIdCoversWorkflow();
        Graph.AddNode(node);
        Workflow[node.Id] = node.ToWorkflowNode();
        Subscribe(node);
        AdvanceIdCounter(node);

        return node;
    }

    /// <summary>As <see cref="AddNode{T}(T)"/>, but with a caller-supplied ID.</summary>
    public T AddNode<T>(T node, string id) where T : ComfyNode
    {
        Graph.AddNode(node, id);
        Workflow[node.Id] = node.ToWorkflowNode();
        Subscribe(node);
        AdvanceIdCounter(node);

        return node;
    }

    /// <summary>
    /// Add an <see cref="UnknownNode"/> for the given <paramref name="classType"/>
    /// at a specific <paramref name="id"/>. Convenience shorthand for
    /// <c>AddNode(new UnknownNode(classType), id)</c>. Use for stub fixtures and
    /// round-tripping classes without typed bindings.
    /// </summary>
    public UnknownNode AddStub(string classType, string id) =>
        AddNode(new UnknownNode(classType), id);

    /// <summary>Raise the host ID-counter (if any) past a node's ID, keeping it in lockstep so
    /// host-minted IDs (e.g. SwarmUI <c>g.CreateNode()</c>) never collide with bridge-assigned ones.
    /// No-op when the bridge has no counter or the ID is non-numeric.</summary>
    private void AdvanceIdCounter(ComfyNode node)
    {
        if (_idCounter is not null && int.TryParse(node.Id, out int n) && n >= _idCounter.LastID)
        {
            _idCounter.LastID = n + 1;
        }
    }

    // ── Remove ──────────────────────────────────────────────────────

    /// <summary>Remove a node by ID from both the typed graph and the JObject workflow.</summary>
    public bool RemoveNode(string id)
    {
        ComfyNode? node = Graph.GetNode(id);
        bool removed = Graph.RemoveNode(id);
        if (removed)
        {
            if (node is not null)
            {
                Unsubscribe(node);
            }
            Workflow.Remove(id);
        }

        return removed;
    }

    /// <inheritdoc cref="RemoveNode(string)"/>
    public bool RemoveNode(ComfyNode node) => RemoveNode(node.Id);

    // ── Sync ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-serialize a single node from the typed graph to the JObject.
    /// Use after mutating connections or literal values on the typed graph.
    /// For UnknownNodes, clears RawInputs so modifications are reflected.
    /// </summary>
    public void SyncNode(ComfyNode node) => SyncNode(node.Id);

    /// <summary>
    /// Re-serialize a single node (by ID) from the typed graph to the JObject.
    /// </summary>
    public void SyncNode(string id)
    {
        ComfyNode? node = Graph.GetNode(id);
        if (node is null)
        {
            throw new KeyNotFoundException($"Node '{id}' not found in graph.");
        }
        if (node is UnknownNode unknown)
        {
            unknown.RawInputs = null;
        }
        Workflow[id] = node.ToWorkflowNode();
    }

    // ── Path conversion ─────────────────────────────────────────────

    /// <summary>
    /// Convert a typed output to a JArray path [nodeId, slotIndex].
    /// Suitable for constructing WGNodeData or passing to old-style JObject code.
    /// </summary>
    public static JArray ToPath(INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrEmpty(output.Node.Id))
        {
            throw new InvalidOperationException(
                "Cannot create path for a node that has not been added to a graph (no ID assigned).");
        }

        return new JArray(output.Node.Id, output.SlotIndex);
    }

    /// <summary>
    /// Resolve a JArray path [nodeId, slotIndex] to a typed output in the graph.
    /// Returns null if the path is malformed or the node is not found.
    /// <para>
    /// For <see cref="UnknownNode"/> targets, <see cref="UnknownNode.FindOutput(int)"/> materializes
    /// the slot on demand (typed as <see cref="Types.AnyType"/>) — every slot index is logically valid
    /// on an UnknownNode, so this resolves cleanly even when <see cref="ComfyGraph.FromWorkflow"/>'s
    /// output-discovery scan didn't see the slot referenced by any in-JObject input.
    /// </para>
    /// </summary>
    public INodeOutput? ResolvePath(JArray? path)
    {
        if (path is null || path.Count != 2)
        {
            return null;
        }
        string? nodeId = path[0]?.ToString();
        if (nodeId is null || path[1] is not JValue slotVal || slotVal.Type != JTokenType.Integer)
        {
            return null;
        }
        int slotIndex = Convert.ToInt32(slotVal.Value!);
        ComfyNode? node = Graph.GetNode(nodeId);

        return node?.FindOutput(slotIndex);
    }

    /// <summary>
    /// Resolve the <em>node</em> at a connection path (the slot index in <paramref name="path"/>
    /// is ignored — use <see cref="ResolvePath(JArray?)"/> when you want the output slot). The
    /// typed shorthand for the ubiquitous <c>Graph.GetNode($"{path[0]}")</c> over a SwarmUI
    /// path (<c>WGNodeData.Path</c>, <c>genInfo.PosCond</c>, …). Returns <c>null</c> when
    /// <paramref name="path"/> is null/empty or the node is absent — no <c>$"{path[0]}"</c>
    /// interpolation or <c>Count</c> guard at the call site.
    /// </summary>
    public ComfyNode? NodeAt(JArray? path)
    {
        if (path is not { Count: >= 1 })
        {
            return null;
        }
        string? nodeId = path[0]?.ToString();

        return nodeId is null ? null : Graph.GetNode(nodeId);
    }

    /// <summary>
    /// <see cref="NodeAt(JArray?)"/> narrowed to <typeparamref name="T"/> — a node class, or
    /// (once family interfaces ship) a shared node interface. Returns <c>null</c> when the path
    /// does not resolve or the node is not a <typeparamref name="T"/>. Replaces
    /// <c>Graph.GetNode&lt;T&gt;($"{path[0]}")</c>.
    /// </summary>
    public T? NodeAt<T>(JArray? path) where T : class => NodeAt(path) as T;

    // ── Disposal ────────────────────────────────────────────────────

    /// <summary>
    /// Unsubscribe from all tracked nodes so the bridge no longer roots them — and they no longer root the bridge.
    /// Idempotent. Subsequent <see cref="AddNode{T}(T)"/> / <see cref="RemoveNode(string)"/> calls are no-ops on subscription.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (ComfyNode node in _subscribed)
        {
            node.InputChanged -= _onInputChanged;
            node.InputListChanged -= _onInputListChanged;
        }
        _subscribed.Clear();
    }

    // ── Internal ────────────────────────────────────────────────────

    private void Subscribe(ComfyNode node)
    {
        if (_disposed || !_subscribed.Add(node))
        {
            return;
        }
        node.InputChanged += _onInputChanged;
        node.InputListChanged += _onInputListChanged;
    }

    private void Unsubscribe(ComfyNode node)
    {
        if (!_subscribed.Remove(node))
        {
            return;
        }
        node.InputChanged -= _onInputChanged;
        node.InputListChanged -= _onInputListChanged;
    }

    /// <summary>
    /// Auto-sync handler. Mirrors a single typed-input change onto <c>Workflow[id]["inputs"]</c>
    /// in place, preserving the outer node JObject reference.
    /// </summary>
    private void OnInputChanged(ComfyNode node, INodeInput input)
    {
        if (!TryGetInPlaceInputs(node, out JObject inputs))
        {
            return;
        }
        inputs.Remove(input.Name);
        input.SerializeInto(inputs);
    }

    /// <summary>
    /// Auto-sync handler for input-list changes (Add/Remove/Clear or any list child's value
    /// change). Removes every wire key the list claims, then refans by calling
    /// <see cref="INodeInputList.SerializeInto"/>. Heavier than the singular path but correct
    /// under index renumbering.
    /// </summary>
    private void OnInputListChanged(ComfyNode node, INodeInputList list)
    {
        if (!TryGetInPlaceInputs(node, out JObject inputs))
        {
            return;
        }
        List<string> toRemove = [];
        foreach (JProperty prop in inputs.Properties())
        {
            if (list.TryParseKey(prop.Name) >= 0)
            {
                toRemove.Add(prop.Name);
            }
        }
        foreach (string key in toRemove)
        {
            inputs.Remove(key);
        }
        list.SerializeInto(inputs);
    }

    /// <summary>Get the <c>inputs</c> JObject for in-place mutation. Returns false (and skips
    /// the change handler) if the node isn't represented in the JObject yet, or rebuilds the
    /// inputs JObject from scratch when RawInputs/structure is stale.</summary>
    private bool TryGetInPlaceInputs(ComfyNode node, out JObject inputs)
    {
        inputs = null!;
        if (Workflow[node.Id] is not JObject existingNode)
        {
            // Node isn't represented in the JObject yet (e.g. mutation between Graph.AddNode and bridge AddNode).
            // The next AddNode call will write the full snapshot.
            return false;
        }

        // For UnknownNode, RawInputs (when set) wins over typed slots in ToWorkflowNode. The first typed
        // mutation must clear RawInputs and rebuild the inputs JObject from typed slots so subsequent
        // in-place edits land on a JObject that actually reflects the typed state.
        if (node is UnknownNode unknown && unknown.RawInputs is not null)
        {
            unknown.RawInputs = null;
            existingNode["inputs"] = (JObject)node.ToWorkflowNode()["inputs"]!;
            return false;
        }

        if (existingNode["inputs"] is not JObject existingInputs)
        {
            existingNode["inputs"] = (JObject)node.ToWorkflowNode()["inputs"]!;
            return false;
        }

        inputs = existingInputs;
        return true;
    }

    private void EnsureNextIdCoversWorkflow()
    {
        int maxId = 0;
        foreach (JProperty prop in Workflow.Properties())
        {
            if (int.TryParse(prop.Name, out int n) && n > maxId)
            {
                maxId = n;
            }
        }
        Graph.EnsureMinNextId(maxId + 1);
    }
}
