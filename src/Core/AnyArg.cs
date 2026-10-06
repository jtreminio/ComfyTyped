using ComfyTyped.Types;

namespace ComfyTyped.Core;

/// <summary>
/// A fluent argument for union sockets represented by <see cref="AnyType"/>.
/// Accepts primitive literals implicitly, or any connection through the constructor.
/// </summary>
/// <remarks>
/// This wrapper does not validate union membership. ComfyUI performs that validation.
/// Wrap concrete typed outputs explicitly, for example <c>new AnyArg(checkpoint.CLIP)</c>.
/// </remarks>
public readonly struct AnyArg
{
    private readonly INodeOutput? _connection;
    private readonly object? _literal;

    public AnyArg(INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _connection = output;
        _literal = null;
    }

    private AnyArg(object literal)
    {
        _connection = null;
        _literal = literal;
    }

    public static implicit operator AnyArg(long value) => new((object)value);
    public static implicit operator AnyArg(int value) => new((object)(long)value);
    public static implicit operator AnyArg(double value) => new((object)value);
    public static implicit operator AnyArg(float value) => new((object)(double)value);
    public static implicit operator AnyArg(string value) => new((object)value);
    public static implicit operator AnyArg(bool value) => new((object)value);
    public static implicit operator AnyArg(NodeOutput<AnyType> output) => new(output);

    /// <summary>Apply the literal or connection to a union input.</summary>
    public void ApplyTo(NodeInput<AnyType> slot)
    {
        if (_connection is not null)
        {
            slot.ConnectToUntyped(_connection);
        }
        else
        {
            slot.Set(_literal);
        }
    }
}
