using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// Typed equivalent of <see cref="WGNodeData"/>.
/// Carries a typed <see cref="INodeOutput"/> plus the media metadata
/// (dimensions, FPS, compat) that WGNodeData holds but the graph does not.
///
/// Lives at the boundary between SwarmUI's untyped world and ComfyTyped's typed world.
/// Created from WGNodeData at stage entry, converted back at stage exit.
/// </summary>
public sealed class MediaRef
{
    public required INodeOutput Output { get; set; }
    public required string DataType { get; set; }
    public T2IModelCompatClass? Compat { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Frames { get; set; }
    public int? FPS { get; set; }
    public MediaRef? AttachedAudio { get; set; }

    /// <summary>
    /// Convert this typed reference back to a <see cref="WGNodeData"/> for SwarmUI consumption.
    /// </summary>
    public WGNodeData ToWGNodeData(WorkflowGenerator g)
    {
        WGNodeData data = new(WorkflowBridge.ToPath(Output), g, DataType, Compat)
        {
            Width = Width,
            Height = Height,
            Frames = Frames,
            FPS = FPS
        };
        if (AttachedAudio is not null)
        {
            data.AttachedAudio = AttachedAudio.ToWGNodeData(g);
        }

        return data;
    }

    /// <summary>
    /// Convert a <see cref="WGNodeData"/> to a typed <see cref="MediaRef"/> by resolving
    /// the JArray path against the bridge's typed graph.
    /// Returns null if the path is null, malformed, or unresolvable.
    /// </summary>
    public static MediaRef? FromWGNodeData(WGNodeData data, WorkflowBridge bridge)
    {
        if (data?.Path is not JArray path || path.Count != 2)
        {
            return null;
        }
        INodeOutput? output = bridge.ResolvePath(path);
        if (output is null)
        {
            return null;
        }

        return new MediaRef
        {
            Output = output,
            DataType = data.DataType,
            Compat = data.Compat,
            Width = data.Width,
            Height = data.Height,
            Frames = data.Frames,
            FPS = data.FPS,
            AttachedAudio = data.AttachedAudio is not null
                ? FromWGNodeData(data.AttachedAudio, bridge)
                : null
        };
    }

    /// <summary>
    /// Create a deep copy preserving the same <see cref="Output"/> reference.
    /// </summary>
    public MediaRef Clone()
    {
        return new MediaRef
        {
            Output = Output,
            DataType = DataType,
            Compat = Compat,
            Width = Width,
            Height = Height,
            Frames = Frames,
            FPS = FPS,
            AttachedAudio = AttachedAudio?.Clone()
        };
    }
}
