using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// A typed representation of a ComfyUI workflow graph.
/// Nodes are connected via typed <see cref="NodeInput{T}"/> / <see cref="NodeOutput{T}"/> slots.
/// </summary>
public sealed class ComfyGraph
{
    private readonly Dictionary<string, ComfyNode> _nodes = new(StringComparer.Ordinal);
    private int _nextId = 100;

    /// <summary>All nodes in the graph, keyed by their string ID.</summary>
    public IReadOnlyDictionary<string, ComfyNode> Nodes => _nodes;

    /// <summary>Add a node to the graph with an auto-assigned ID.</summary>
    public T AddNode<T>(T node) where T : ComfyNode
    {
        string id = $"{_nextId++}";
        node.Id = id;
        _nodes[id] = node;

        return node;
    }

    /// <summary>Add a node to the graph with a specific ID.</summary>
    public T AddNode<T>(T node, string id) where T : ComfyNode
    {
        node.Id = id;
        _nodes[id] = node;
        int idNum = int.TryParse(id, out int n) ? n : 0;
        if (idNum >= _nextId)
        {
            _nextId = idNum + 1;
        }

        return node;
    }

    public ComfyNode? GetNode(string id) => _nodes.TryGetValue(id, out ComfyNode? node) ? node : null;

    /// <summary>Get a node by ID narrowed to <typeparamref name="T"/> — a node class, or a shared
    /// node interface (e.g. a codegen-emitted family interface like <c>IVaeDecode</c>). Returns
    /// null when absent or not a <typeparamref name="T"/>. The <c>class</c> (not <c>ComfyNode</c>)
    /// constraint is what permits interface targets.</summary>
    public T? GetNode<T>(string id) where T : class => _nodes.TryGetValue(id, out ComfyNode? node) ? node as T : null;

    /// <summary>Find all nodes assignable to <typeparamref name="T"/> — a node class or a shared
    /// node interface. The <c>class</c> constraint permits interface targets
    /// (e.g. <c>NodesOfType&lt;IVaeDecode&gt;()</c>).</summary>
    public IReadOnlyList<T> NodesOfType<T>() where T : class => [.. _nodes.Values.OfType<T>()];

    /// <summary>Find all nodes with a specific ComfyUI class_type string.</summary>
    public IReadOnlyList<ComfyNode> NodesOfType(string classType) => [.. _nodes.Values.Where(n => n.ClassTypeName == classType)];

    /// <summary>Remove a node from the graph. Does NOT disconnect it — callers should handle that.</summary>
    public bool RemoveNode(string id) => _nodes.Remove(id);

    /// <summary>Ensure the next auto-assigned ID is at least this value.</summary>
    internal void EnsureMinNextId(int minNextId)
    {
        if (minNextId > _nextId)
        {
            _nextId = minNextId;
        }
    }

    // ── Serialization ───────────────────────────────────────────────

    /// <summary>Serialize this graph to ComfyUI workflow JSON format.</summary>
    public JObject ToWorkflow()
    {
        JObject workflow = [];
        foreach ((string id, ComfyNode node) in _nodes)
        {
            workflow[id] = node.ToWorkflowNode();
        }

        return workflow;
    }

    // ── Deserialization ─────────────────────────────────────────────

    /// <summary>
    /// Parse a ComfyUI workflow JObject into a typed graph.
    /// Known node types become their generated C# class; unknown types become <see cref="UnknownNode"/>.
    /// </summary>
    public static ComfyGraph FromWorkflow(JObject workflow)
    {
        ComfyGraph graph = new();

        // Pass 1: Create all nodes
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is not JObject nodeObj)
            {
                continue;
            }
            string? classType = nodeObj["class_type"]?.ToString();
            if (classType is null)
            {
                continue;
            }

            ComfyNode node = NodeRegistry.Create(classType);
            graph.AddNode(node, prop.Name);

            // For UnknownNode, determine output slot count from how many other nodes reference it
            // (we'll handle this in pass 2)
            if (node is UnknownNode unknown)
            {
                unknown.RawInputs = nodeObj["inputs"] as JObject;
            }
        }

        // Pass 1.5: Scan all inputs to discover output slot indices for UnknownNodes
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is not JObject nodeObj || nodeObj["inputs"] is not JObject inputs)
            {
                continue;

            }
            foreach (JProperty inputProp in inputs.Properties())
            {
                if (IsConnectionRef(inputProp.Value, out string? targetId, out int slotIndex)
                    && targetId is not null
                    && graph.GetNode(targetId) is UnknownNode targetUnknown)
                {
                    targetUnknown.GetOutput(slotIndex);
                }
            }
        }

        // Pass 2: Wire up inputs — set literal values and connections
        foreach (JProperty prop in workflow.Properties())
        {
            if (prop.Value is not JObject nodeObj || nodeObj["inputs"] is not JObject inputs)
            {
                continue;
            }
            ComfyNode? node = graph.GetNode(prop.Name);
            if (node is null)
            {
                continue;
            }
            WireNodeInputs(graph, node, inputs);
        }

        return graph;
    }

    private static void WireNodeInputs(ComfyGraph graph, ComfyNode node, JObject inputs)
    {
        Dictionary<INodeInputList, SortedDictionary<int, JToken>>? listBuckets = null;

        foreach (JProperty inputProp in inputs.Properties())
        {
            INodeInput? input = node is UnknownNode unknown
                ? unknown.GetInput(inputProp.Name)
                : node.FindInput(inputProp.Name);
            if (input is not null)
            {
                WireSingleInput(graph, input, inputProp.Value);
                continue;
            }

            // Claim into a list when the value is either a literal (for primitive-element
            // lists like GLSLShader.Floats) or a connection ref whose source node actually
            // exists in the graph. A dangling connection ref (source id missing) is
            // explicitly NOT claimed — it would otherwise materialize a list slot that
            // fails to wire, then drop from serialization, silently mutating the list's
            // count/order. Falling through to ExtraInputs preserves the bad ref in-memory
            // (the serialize side may still drop it on key collision with the compacted
            // typed list output — full dangling-ref round-trip is not guaranteed).
            bool isValueClaimable = !IsConnectionRef(inputProp.Value, out string? srcId, out int _)
                || (srcId is not null && graph.GetNode(srcId) is not null);
            if (node is not UnknownNode && isValueClaimable)
            {
                bool claimed = false;
                foreach (INodeInputList list in node.InputLists)
                {
                    int idx = list.TryParseKey(inputProp.Name);
                    if (idx < 0)
                    {
                        continue;
                    }
                    listBuckets ??= [];
                    if (!listBuckets.TryGetValue(list, out SortedDictionary<int, JToken>? bucket))
                    {
                        bucket = [];
                        listBuckets[list] = bucket;
                    }
                    bucket[idx] = inputProp.Value;
                    claimed = true;
                    break;
                }
                if (claimed)
                {
                    continue;
                }
            }

            node.ExtraInputs[inputProp.Name] = inputProp.Value.DeepClone();
        }

        if (listBuckets is not null)
        {
            foreach ((INodeInputList list, SortedDictionary<int, JToken> bucket) in listBuckets)
            {
                foreach (JToken token in bucket.Values)
                {
                    INodeInput child = list.AppendUnsetSlot();
                    WireSingleInput(graph, child, token);
                }
            }
        }
    }

    private static void WireSingleInput(ComfyGraph graph, INodeInput input, JToken value)
    {
        if (IsConnectionRef(value, out string? sourceId, out int slotIndex)
            && sourceId is not null)
        {
            ComfyNode? sourceNode = graph.GetNode(sourceId);
            INodeOutput? sourceOutput = sourceNode?.FindOutput(slotIndex);
            if (sourceOutput is not null)
            {
                input.ConnectToUntyped(sourceOutput);
            }
        }
        else
        {
            input.SetUntyped(ExtractLiteralValue(value));
        }
    }

    /// <summary>Check if a JToken is a [nodeId, slotIndex] connection reference.</summary>
    private static bool IsConnectionRef(JToken token, out string? nodeId, out int slotIndex)
    {
        nodeId = null;
        slotIndex = 0;
        if (token is not JArray arr || arr.Count != 2)
        {
            return false;
        }
        // Connection refs are [string_or_int_nodeId, int_slotIndex]
        // The node ID is typically a string representation of an integer
        if (arr[1] is not JValue slotVal || slotVal.Type != JTokenType.Integer)
        {
            return false;
        }
        nodeId = arr[0]?.ToString();
        slotIndex = Convert.ToInt32(slotVal.Value!);

        return nodeId is not null;
    }

    /// <summary>Extract a literal value from a JToken for storage in a NodeInput.</summary>
    private static object? ExtractLiteralValue(JToken token) => token is JValue jval ? jval.Value : token;

    // ── Graph queries ───────────────────────────────────────────────

    /// <summary>Every singular input on the node plus every child of every input list,
    /// in declaration order. Use this anywhere you'd otherwise iterate <see cref="ComfyNode.Inputs"/>
    /// and want to include list children (AUTOGROW_V3 connections).</summary>
    private static IEnumerable<INodeInput> AllInputs(ComfyNode node)
    {
        foreach (INodeInput input in node.Inputs)
        {
            yield return input;
        }
        foreach (INodeInputList list in node.InputLists)
        {
            foreach (INodeInput child in list.Items)
            {
                yield return child;
            }
        }
    }

    /// <summary>Find all nodes that have an input connected to the given output.</summary>
    public IReadOnlyList<ComfyNode> FindDownstream(INodeOutput output)
    {
        List<ComfyNode> result = [];
        foreach (ComfyNode node in _nodes.Values)
        {
            foreach (INodeInput input in AllInputs(node))
            {
                if (input.Connection == output)
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Find all upstream nodes connected to the given node's inputs.</summary>
    public IReadOnlyList<ComfyNode> FindUpstream(ComfyNode node)
    {
        List<ComfyNode> result = [];
        foreach (INodeInput input in AllInputs(node))
        {
            if (input.Connection?.Node is ComfyNode upstream && !result.Contains(upstream))
            {
                result.Add(upstream);
            }
        }

        return result;
    }

    /// <summary>Walk upstream from a node to find the nearest node assignable to <typeparamref name="T"/>
    /// — a node class or a shared node interface (e.g. <c>FindNearestUpstream&lt;IVaeDecode&gt;()</c>
    /// matches both <c>VAEDecodeNode</c> and <c>VAEDecodeTiledNode</c>).</summary>
    public T? FindNearestUpstream<T>(ComfyNode startNode) where T : class
    {
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNode);
        visited.Add(startNode.Id);

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            foreach (INodeInput input in AllInputs(current))
            {
                if (input.Connection?.Node is not ComfyNode upstream || !visited.Add(upstream.Id))
                {
                    continue;
                }
                if (upstream is T match)
                {
                    return match;
                }
                pending.Enqueue(upstream);
            }
        }

        return null;
    }

    /// <summary>Walk upstream from a node to find the nearest node matching <paramref name="predicate"/>.
    /// Like <see cref="FindNearestUpstream{T}(ComfyNode)"/> this excludes <paramref name="startNode"/>
    /// itself — test it separately if you need start-inclusive behavior. There is no downstream peer:
    /// the downstream predicate overload takes a barrier, not a match.</summary>
    public ComfyNode? FindNearestUpstream(ComfyNode startNode, Func<ComfyNode, bool> predicate)
    {
        if (startNode is null || predicate is null)
        {
            return null;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNode);
        visited.Add(startNode.Id);

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            foreach (INodeInput input in AllInputs(current))
            {
                if (input.Connection?.Node is not ComfyNode upstream || !visited.Add(upstream.Id))
                {
                    continue;
                }
                if (predicate(upstream))
                {
                    return upstream;
                }
                pending.Enqueue(upstream);
            }
        }

        return null;
    }

    /// <summary>Check whether a node with the given ID is reachable by walking upstream from startNode.</summary>
    public bool IsReachableUpstream(ComfyNode startNode, string targetNodeId)
    {
        if (startNode is null || string.IsNullOrEmpty(targetNodeId) || startNode.Id == targetNodeId)
        {
            return false;
        }

        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(startNode);
        visited.Add(startNode.Id);

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            foreach (INodeInput input in AllInputs(current))
            {
                if (input.Connection?.Node is not ComfyNode upstream || !visited.Add(upstream.Id))
                {
                    continue;
                }
                if (upstream.Id == targetNodeId)
                {
                    return true;
                }
                pending.Enqueue(upstream);
            }
        }

        return false;
    }

    /// <summary>Find all (node, input) pairs where the input is connected to the given output.
    /// Includes connections wired through <see cref="NodeInputList{T}"/> children.</summary>
    public IReadOnlyList<(ComfyNode Node, INodeInput Input)> FindInputsConnectedTo(INodeOutput output)
    {
        List<(ComfyNode, INodeInput)> result = [];
        foreach (ComfyNode node in _nodes.Values)
        {
            foreach (INodeInput input in AllInputs(node))
            {
                if (input.Connection is INodeOutput conn
                    && conn.Node == output.Node
                    && conn.SlotIndex == output.SlotIndex)
                {
                    result.Add((node, input));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Rewire all inputs currently connected to <paramref name="from"/> so they connect to <paramref name="to"/> instead.
    /// Returns the number of connections retargeted. Works for both singular inputs and list children.
    /// </summary>
    public int RetargetConnections(
        INodeOutput from,
        INodeOutput to,
        Func<ComfyNode, INodeInput, bool>? predicate = null)
    {
        int count = 0;
        foreach ((ComfyNode node, INodeInput input) in FindInputsConnectedTo(from))
        {
            if (predicate is not null && !predicate(node, input))
            {
                continue;
            }
            // ConnectToUntyped fully replaces existing state; no pre-Clear needed.
            // Clear() would throw on list children, so this also keeps the path uniform.
            input.ConnectToUntyped(to);
            count++;
        }

        return count;
    }

    /// <summary>Walk downstream from a node's output to find the nearest node assignable to
    /// <typeparamref name="T"/> — a node class or a shared node interface.</summary>
    public T? FindNearestDownstream<T>(INodeOutput output) where T : class =>
        FindNearestDownstream<T>(output, stopAt: null);

    /// <summary>Walk downstream as <see cref="FindNearestDownstream{T}(INodeOutput)"/>, but treat
    /// nodes assignable to <typeparamref name="TStop"/> as barriers: they are neither returned nor
    /// traversed past.
    /// <para>
    /// Use this when "nearest" alone is the wrong question because something between the two ends
    /// rewrites the data. A latent reaches a decode both before and after a sampler, and the
    /// unbounded walk happily returns the far one — but that decode is of different pixels. Stopping
    /// at the sampler asks the question the caller meant: a decode of <em>this</em> latent.
    /// </para>
    /// <para>
    /// A node that is both <typeparamref name="T"/> and <typeparamref name="TStop"/> is returned —
    /// the match is tested first.
    /// </para></summary>
    public T? FindNearestDownstream<T, TStop>(INodeOutput output)
        where T : class
        where TStop : class =>
        FindNearestDownstream<T>(output, node => node is TStop);

    /// <summary>Walk downstream as <see cref="FindNearestDownstream{T}(INodeOutput)"/>, stopping at
    /// nodes matching <paramref name="stopAt"/>. Prefer
    /// <see cref="FindNearestDownstream{T, TStop}(INodeOutput)"/> when the barrier is a type;
    /// this overload is for barriers no type expresses (a widget value, a node's connectivity).</summary>
    public T? FindNearestDownstream<T>(INodeOutput output, Func<ComfyNode, bool>? stopAt)
        where T : class
    {
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        foreach (ComfyNode downstream in FindDownstream(output))
        {
            pending.Enqueue(downstream);
            visited.Add(downstream.Id);
        }

        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is T match)
            {
                return match;
            }
            if (stopAt is not null && stopAt(current))
            {
                continue;
            }
            foreach (INodeOutput currentOutput in current.Outputs)
            {
                foreach (ComfyNode downstream in FindDownstream(currentOutput))
                {
                    if (visited.Add(downstream.Id))
                    {
                        pending.Enqueue(downstream);
                    }
                }
            }
        }

        return null;
    }
}
