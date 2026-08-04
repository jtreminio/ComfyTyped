using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

/// <summary>
/// Merges nodes that compute the same thing. Two nodes are the same computation when they share a
/// class and their inputs are equal — including the connections, so an input naming a different
/// node makes them different however alike they look.
/// <para>
/// This exists because a workflow is often assembled by more than one author. A host builds its own
/// chain, an extension builds beside it, and neither can consult the other's bookkeeping; the graph
/// itself is the only thing they share. Collapsing afterwards needs no agreement between them.
/// </para>
/// </summary>
public static class DuplicateNodeCollapse
{
    /// <summary>
    /// Merges every duplicate in <paramref name="bridge"/>, keeping the survivor a caller names or
    /// else the lowest id, and returns the ids removed mapped to the ids that replaced them.
    /// <para>
    /// Runs to a fixed point: merging two nodes can make their consumers identical in turn, and
    /// that only becomes visible once the merge below them has happened. Each round merges
    /// everything it can see, so the number of rounds follows the depth of duplicated chains rather
    /// than the number of duplicates.
    /// </para>
    /// </summary>
    /// <param name="bridge">The workflow to collapse, typed graph and JSON alike.</param>
    /// <param name="prefer">
    /// Ranks candidates for which node survives a merge — lower wins. Use it to keep the id a
    /// consumer outside the graph depends on; the default keeps the lowest numeric id, which for a
    /// host that reserves low ids means the host's own node survives.
    /// </param>
    /// <param name="mergeable">
    /// Which nodes may be folded together at all. Everything, by default. A node wanted for an
    /// effect rather than for a value it hands on — a save, a preview — has to be excluded here:
    /// two of those are two effects however alike they look, and only the caller knows which
    /// classes those are.
    /// </param>
    public static IReadOnlyDictionary<string, string> Collapse(
        WorkflowBridge bridge,
        Func<ComfyNode, int>? prefer = null,
        Func<ComfyNode, bool>? mergeable = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Dictionary<string, string> merged = [];
        while (CollapseRound(bridge, prefer, mergeable, merged))
        {
        }

        return merged;
    }

    /// <summary>
    /// Merges every duplicate visible in one pass. Grouping by a serialized key costs one
    /// serialization per node rather than one per pair, which is what keeps this usable on a graph
    /// with hundreds of nodes.
    /// <para>
    /// Keys are read once, before any merge in this round, and retargeting can only invalidate them
    /// in the safe direction: two nodes equal at the start of the round read the same upstreams, so
    /// a retarget rewrites both alike and they stay equal. It can make two <em>unequal</em> nodes
    /// equal, which this round then misses and the next one catches.
    /// </para>
    /// </summary>
    private static bool CollapseRound(
        WorkflowBridge bridge,
        Func<ComfyNode, int>? prefer,
        Func<ComfyNode, bool>? mergeable,
        Dictionary<string, string> merged)
    {
        bool mergedAny = false;
        foreach (IGrouping<string, ComfyNode> duplicates in bridge.Graph.Nodes.Values
            .Where(node => mergeable?.Invoke(node) ?? true)
            .GroupBy(Key)
            .Where(group => group.Count() > 1)
            .ToList())
        {
            ComfyNode survivor = duplicates.MinBy(node => Rank(node, prefer))!;
            foreach (ComfyNode removed in duplicates)
            {
                // Reference equality, not id: one instance can be registered under several ids, and
                // merging it into itself would remove the survivor and never terminate.
                if (!ReferenceEquals(removed, survivor)
                    && Merge(bridge, survivor, removed, merged))
                {
                    mergedAny = true;
                }
            }
        }

        return mergedAny;
    }

    /// <summary>False when nothing was removed, which is what bounds the outer loop.</summary>
    private static bool Merge(
        WorkflowBridge bridge,
        ComfyNode survivor,
        ComfyNode removed,
        Dictionary<string, string> merged)
    {
        foreach (INodeOutput output in removed.Outputs)
        {
            // By slot index, not list position: an UnknownNode materializes its slots as they are
            // discovered, so its outputs sit in reference order rather than slot order.
            if (survivor.FindOutput(output.SlotIndex) is INodeOutput target)
            {
                bridge.Graph.RetargetConnections(output, target);
            }
        }
        RetargetExtraInputs(bridge, removed.Id, survivor.Id);
        if (!bridge.RemoveNode(removed.Id))
        {
            return false;
        }
        merged[removed.Id] = survivor.Id;
        // A node merged earlier may have been this one's survivor; keep those redirects pointing at
        // the node that is actually still in the graph.
        foreach (string key in merged.Where(entry => entry.Value == removed.Id)
            .Select(entry => entry.Key)
            .ToList())
        {
            merged[key] = survivor.Id;
        }

        return true;
    }

    /// <summary>
    /// Repoints connection references held in <see cref="ComfyNode.ExtraInputs"/>, which the typed
    /// graph does not model and so cannot retarget. Left alone they would name a node that is no
    /// longer in the workflow.
    /// </summary>
    private static void RetargetExtraInputs(WorkflowBridge bridge, string removedId, string toId)
    {
        foreach (ComfyNode node in bridge.Graph.Nodes.Values)
        {
            if (node.ExtraInputs.Count == 0)
            {
                continue;
            }
            List<JArray> references = [.. node.ExtraInputs.Descendants()
                .OfType<JArray>()
                .Where(token => IsConnectionTo(token, removedId))];
            if (references.Count == 0)
            {
                continue;
            }
            foreach (JArray reference in references)
            {
                reference[0] = toId;
            }
            // Extras are copied into the JSON on serialization rather than tracked, so the node has
            // to be written back over the workflow for the rewrite to ship.
            bridge.Workflow[node.Id] = node.ToWorkflowNode();
        }
    }

    private static bool IsConnectionTo(JArray token, string nodeId) =>
        token.Count == 2
        && token[0].Type == JTokenType.String
        && (string?)token[0] == nodeId
        && token[1].Type == JTokenType.Integer;

    /// <summary>
    /// What makes two nodes the same computation, as one comparable string. Numbers render through
    /// a single form, so a literal written as an integer and the same value written as a float do
    /// not read as different inputs.
    /// </summary>
    private static string Key(ComfyNode node) =>
        node.ClassTypeName + " " + Canonical(node.ToWorkflowNode()["inputs"] ?? new JObject());

    private static string Canonical(JToken token) => token switch
    {
        JObject obj => "{" + string.Concat(obj.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}:{Canonical(property.Value)};")) + "}",
        JArray array => "[" + string.Concat(array.Select(item => Canonical(item) + ";")) + "]",
        JValue { Type: JTokenType.Integer or JTokenType.Float } number => CanonicalNumber(number),
        _ => token.ToString(Formatting.None),
    };

    private static string CanonicalNumber(JValue number)
    {
        double value = number.Value<double>();
        return !double.IsInfinity(value) && !double.IsNaN(value) && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Ordering key for which of several equal nodes survives. Ties break on the id, by
    /// ordinal so the winner cannot depend on the running process's locale.</summary>
    private static (int Preference, int Numeric, string Id) Rank(
        ComfyNode node,
        Func<ComfyNode, int>? prefer) =>
        (prefer?.Invoke(node) ?? 0,
            int.TryParse(node.Id, out int numeric) ? numeric : int.MaxValue,
            node.Id);
}
