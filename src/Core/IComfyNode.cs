namespace ComfyTyped.Core;

/// <summary>
/// The read-only <see cref="ComfyNode"/> surface, expressed as an interface so that family
/// interfaces (e.g. <c>ComfyTyped.Families.IVaeDecode</c>) can expose node identity and slot
/// collections without the consumer having to cast back to the concrete <see cref="ComfyNode"/>.
/// A family interface declares <c>: IComfyNode</c> and instantly gets <see cref="Id"/>,
/// <see cref="Outputs"/>, etc. alongside its own typed slots.
///
/// <para>Implemented by <see cref="ComfyNode"/> itself — every node, generated or
/// <see cref="UnknownNode"/>, is an <see cref="IComfyNode"/>.</para>
/// </summary>
public interface IComfyNode
{
    /// <inheritdoc cref="ComfyNode.Id"/>
    string Id { get; }

    /// <inheritdoc cref="ComfyNode.ClassTypeName"/>
    string ClassTypeName { get; }

    /// <inheritdoc cref="ComfyNode.Inputs"/>
    IReadOnlyList<INodeInput> Inputs { get; }

    /// <inheritdoc cref="ComfyNode.InputLists"/>
    IReadOnlyList<INodeInputList> InputLists { get; }

    /// <inheritdoc cref="ComfyNode.Outputs"/>
    IReadOnlyList<INodeOutput> Outputs { get; }

    /// <inheritdoc cref="ComfyNode.FindInput(string)"/>
    INodeInput? FindInput(string name);

    /// <inheritdoc cref="ComfyNode.FindOutput(int)"/>
    INodeOutput? FindOutput(int slotIndex);

    /// <inheritdoc cref="ComfyNode.FindOutput(string)"/>
    INodeOutput? FindOutput(string slotName);
}
