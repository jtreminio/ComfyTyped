using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// SwarmUI-side glue for keeping <see cref="WorkflowGenerator.LastID"/> in step with the node IDs a
/// <see cref="WorkflowBridge"/> assigns, so bridge-added nodes never collide with IDs the generator
/// mints separately (e.g. <c>g.CreateNode()</c>).
/// </summary>
public static class BridgeSync
{
    /// <summary>
    /// Create a <see cref="WorkflowBridge"/> over <c>g.Workflow</c> that keeps
    /// <see cref="WorkflowGenerator.LastID"/> advanced past every node it adds — automatically, on each
    /// <c>AddNode</c>/<c>AddStub</c> (and once up front for any pre-existing IDs), with no wrapper type
    /// and no trailing <see cref="SyncLastId"/> to remember. The default bridge factory for SwarmUI
    /// seed steps:
    /// <code>
    /// using var bridge = BridgeSync.For(g);
    /// var node = bridge.AddNode(new SwarmTrimFramesNode().With(TrimStart: a, TrimEnd: b));
    /// node.Image.TryConnectFromPath(bridge, imagePath);
    /// return node.Id;
    /// </code>
    /// The result is a plain <see cref="WorkflowBridge"/>, so it drops into any helper typed against one
    /// — passing it through a <c>(WorkflowBridge bridge, …)</c> parameter keeps the same instance (and
    /// its live counter), with none of the old wrapper's conversion caveats.
    /// </summary>
    public static WorkflowBridge For(WorkflowGenerator g) =>
        WorkflowBridge.Create(g.Workflow, new GeneratorIdCounter(g));

    /// <summary>
    /// Ensure <c>g.LastID</c> is at least <c>max(numeric workflow keys) + 1</c>. Rarely needed now that
    /// <see cref="For"/> keeps the counter live; reach for it only when nodes were added through a
    /// counter-less bridge (<see cref="WorkflowBridge.Create(JObject, INodeIdCounter?)"/> with no
    /// counter) and a later <c>g.CreateNode()</c> must not collide.
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

    /// <summary>Adapter exposing SwarmUI's <see cref="WorkflowGenerator.LastID"/> as the Core-side
    /// <see cref="INodeIdCounter"/> — the single point where the host ID-counter meets the bridge, so
    /// Core never names <see cref="WorkflowGenerator"/>.</summary>
    private sealed class GeneratorIdCounter(WorkflowGenerator g) : INodeIdCounter
    {
        public int LastID
        {
            get => g.LastID;
            set => g.LastID = value;
        }
    }
}
