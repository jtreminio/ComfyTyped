namespace ComfyTyped.Core;

/// <summary>
/// A host's node-ID counter that a <see cref="WorkflowBridge"/> keeps in lockstep with the IDs it
/// assigns, so bridge-added nodes never collide with IDs the host mints separately (e.g. SwarmUI's
/// <c>WorkflowGenerator.CreateNode()</c>).
/// <para>
/// This is the whole of ComfyTyped's host coupling for ID management: Core depends only on "something
/// with a <see cref="LastID"/>", never on a concrete host type. A host wires itself in by passing an
/// adapter to <see cref="WorkflowBridge.Create(Newtonsoft.Json.Linq.JObject, INodeIdCounter?)"/> — the
/// SwarmUI layer's <c>BridgeSync.For(g)</c> does exactly that over <c>WorkflowGenerator.LastID</c>.
/// When no counter is supplied (read-only or host-agnostic uses), the bridge simply does no ID
/// bookkeeping.
/// </para>
/// </summary>
public interface INodeIdCounter
{
    /// <summary>The host's next-free / last-assigned node ID. The bridge raises this past every node
    /// it adds, using <c>id &gt;= LastID → LastID = id + 1</c>.</summary>
    int LastID { get; set; }
}
