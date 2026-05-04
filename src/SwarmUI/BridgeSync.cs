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
}
