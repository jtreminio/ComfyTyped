using ComfyTyped.Core;
using ComfyTyped.Types;

namespace ComfyTyped.SwarmUI;

/// <summary>
/// Connection helpers that take a <see cref="MediaRef"/> directly, so call sites stop reaching
/// into <see cref="MediaRef.Output"/> and spelling <c>ConnectToUntyped</c> by hand.
///
/// <para><see cref="MediaRef.Output"/> is a non-generic <see cref="INodeOutput"/> (the type is
/// carried at runtime, not in the static type), so a same-typed wire still goes through
/// <see cref="INodeInput.TryConnectToUntyped"/> under the hood — but that is the only soft point:
/// a genuine type mismatch still throws, exactly as a direct <c>ConnectToUntyped</c> would.</para>
/// </summary>
public static class MediaRefExtensions
{
    /// <summary>
    /// Connect this input to <paramref name="media"/>'s output. Soft-fails (returns <c>false</c>,
    /// slot untouched) when <paramref name="media"/> or its <see cref="MediaRef.Output"/> is null
    /// — mirroring <see cref="INodeInput.TryConnectToUntyped"/>. Reads as
    /// <c>node.AudioVae.ConnectFrom(audioVae)</c> in place of
    /// <c>node.AudioVae.ConnectToUntyped(audioVae.Output)</c> /
    /// <c>node.AudioVae.TryConnectToUntyped(audioVae?.Output)</c>.
    /// </summary>
    /// <returns><c>true</c> if connected; <c>false</c> if <paramref name="media"/> had no output.</returns>
    public static bool ConnectFrom<T>(this NodeInput<T> input, MediaRef? media) where T : IComfyType
    {
        ArgumentNullException.ThrowIfNull(input);

        return input.TryConnectToUntyped(media?.Output);
    }
}
