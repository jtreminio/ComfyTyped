using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Bridges between a JObject workflow (untyped, mutable) and a ComfyGraph (typed).
/// Enables gradual migration from manual JObject manipulation to typed graph operations.
///
/// Usage: create a bridge, do typed work (query, add nodes, retarget connections),
/// then discard. If you modify the JObject directly after creating the bridge,
/// the typed graph becomes stale — create a new bridge instead.
///
/// Not thread-safe. Designed for SwarmUI's single-threaded workflow generation.
/// </summary>
public sealed class WorkflowBridge(ComfyGraph graph, JObject workflow)
{
    /// <summary>The typed graph view, deserialized from the workflow at creation time.</summary>
    public ComfyGraph Graph => graph;

    /// <summary>The original JObject workflow (same reference, not a clone).</summary>
    public JObject Workflow => workflow;

    /// <summary>
    /// Create a bridge from an existing JObject workflow.
    /// The typed graph is deserialized as a snapshot. The JObject reference is retained.
    /// </summary>
    public static WorkflowBridge Create(JObject workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ComfyGraph graph = ComfyGraph.FromWorkflow(workflow);

        return new WorkflowBridge(graph, workflow);
    }

    // ── Add ─────────────────────────────────────────────────────────

    /// <summary>
    /// Add a typed node with an auto-assigned ID.
    /// Updates both the typed graph and the JObject workflow.
    /// </summary>
    public T AddNode<T>(T node) where T : ComfyNode
    {
        EnsureNextIdCoversWorkflow();
        Graph.AddNode(node);
        Workflow[node.Id] = node.ToWorkflowNode();

        return node;
    }

    /// <summary>
    /// Add a typed node with a specific ID.
    /// Updates both the typed graph and the JObject workflow.
    /// </summary>
    public T AddNode<T>(T node, string id) where T : ComfyNode
    {
        Graph.AddNode(node, id);
        Workflow[node.Id] = node.ToWorkflowNode();

        return node;
    }

    // ── Remove ──────────────────────────────────────────────────────

    /// <summary>Remove a node by ID from both the typed graph and the JObject workflow.</summary>
    public bool RemoveNode(string id)
    {
        bool removed = Graph.RemoveNode(id);
        if (removed)
        {
            Workflow.Remove(id);
        }

        return removed;
    }

    /// <summary>Remove a node from both the typed graph and the JObject workflow.</summary>
    public bool RemoveNode(ComfyNode node) => RemoveNode(node.Id);

    /// <summary>
    /// Remove every node from both the typed graph and the JObject workflow.
    /// Non-node JObject properties (those without <c>class_type</c>, e.g. <c>_meta</c>) are preserved.
    /// Returns the number of nodes removed.
    /// </summary>
    public int RemoveAllNodes()
    {
        List<string> toRemove = [];
        foreach (JProperty prop in Workflow.Properties())
        {
            if (prop.Value is JObject obj && obj["class_type"] is not null)
            {
                toRemove.Add(prop.Name);
            }
        }
        foreach (string key in toRemove)
        {
            Workflow.Remove(key);
        }

        return Graph.RemoveAllNodes();
    }

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

    /// <summary>
    /// Re-serialize all nodes from the typed graph to the JObject.
    /// Nodes removed from the graph are removed from the JObject.
    /// Non-node JObject properties (those without class_type) are preserved.
    /// </summary>
    public void SyncAll()
    {
        // Remove existing node entries from JObject (will be rewritten from graph)
        List<string> toRemove = [];
        foreach (JProperty prop in Workflow.Properties())
        {
            if (prop.Value is JObject obj && obj["class_type"] is not null)
            {
                toRemove.Add(prop.Name);
            }
        }
        foreach (string key in toRemove)
        {
            Workflow.Remove(key);
        }

        // Write all graph nodes
        foreach ((string id, ComfyNode node) in Graph.Nodes)
        {
            if (node is UnknownNode unknown)
            {
                unknown.RawInputs = null;
            }
            Workflow[id] = node.ToWorkflowNode();
        }
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
    /// For <see cref="UnknownNode"/> targets, the slot is materialized on demand if it
    /// has not been registered yet — <see cref="ComfyGraph.FromWorkflow"/> only registers
    /// UnknownNode outputs for slots that some other node references, so a freshly seeded
    /// node with no consumers would otherwise resolve to null even though the slot exists
    /// logically. The synthesized slot is typed as <see cref="Types.AnyType"/>.
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
        if (node is null)
        {
            return null;
        }
        INodeOutput? output = node.FindOutput(slotIndex);
        if (output is null && node is UnknownNode unknown)
        {
            output = unknown.GetOutput(slotIndex);
        }

        return output;
    }

    // ── Internal ────────────────────────────────────────────────────

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
