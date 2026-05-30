using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.Types;
using Xunit;

namespace ComfyTyped.Tests;

/// <summary>
/// Exercises the fluent <c>With(...)</c> API and the input-binding wrappers
/// (<see cref="In{T}"/>, <see cref="IntArg"/>, <see cref="FloatArg"/>,
/// <see cref="StringArg"/>, <see cref="BoolArg"/>). The key behaviors:
/// a literal sets a literal; a same-typed output sets a connection; mixing both
/// kinds in one call works; and a wrong-typed output has no implicit conversion
/// to the binding, so it won't compile (documented below — can't be asserted at
/// runtime). These are the call sites that prove <c>int → IntArg → IntArg?</c>
/// lifting resolves.
/// </summary>
public class WithBindingTests
{
    public WithBindingTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void With_PrimitiveLiterals_SetLiteralValues()
    {
        KSamplerNode k = new KSamplerNode().With(
            Seed: 42,            // int → IntArg
            Steps: 30L,          // long → IntArg
            Cfg: 7,              // int → double → FloatArg
            SamplerName: "euler",
            Scheduler: "normal",
            Denoise: 0.8);

        Assert.Equal(42L, ((INodeInput)k.Seed).LiteralValue);
        Assert.Equal(30L, ((INodeInput)k.Steps).LiteralValue);
        Assert.Equal(7d, ((INodeInput)k.Cfg).LiteralValue);
        Assert.Equal("euler", ((INodeInput)k.SamplerName).LiteralValue);
        Assert.Equal(0.8, ((INodeInput)k.Denoise).LiteralValue);
        Assert.False(k.Seed.IsConnected);
    }

    [Fact]
    public void With_OmittedArgs_LeaveExistingValuesUntouched()
    {
        // KSampler ships ctor defaults (Steps=20, Cfg=8, Denoise=1). A With() call
        // that omits them must not clobber those defaults.
        KSamplerNode k = new KSamplerNode().With(Seed: 5);

        Assert.Equal(5L, ((INodeInput)k.Seed).LiteralValue);
        Assert.Equal(20L, ((INodeInput)k.Steps).LiteralValue);
        Assert.Equal(8.0, ((INodeInput)k.Cfg).LiteralValue);
        Assert.Equal(1.0, ((INodeInput)k.Denoise).LiteralValue);
    }

    [Fact]
    public void With_ConnectionInputs_SetConnections()
    {
        EmptyLatentImageNode latent = new();
        KSamplerNode k = new KSamplerNode().With(LatentImage: latent.LATENT);

        Assert.True(k.LatentImage.IsConnected);
        Assert.Same(latent, k.LatentImage.Connection!.Node);
        Assert.Equal(latent.LATENT.SlotIndex, k.LatentImage.Connection!.SlotIndex);
    }

    [Fact]
    public void With_PrimitiveInput_AcceptsSameTypedConnection()
    {
        // An INT input can be wired from an INT output, not just given a literal.
        SwarmInputIntegerNode source = new();
        KSamplerNode k = new KSamplerNode().With(Seed: source.INT);

        Assert.True(k.Seed.IsConnected);
        Assert.Same(source, k.Seed.Connection!.Node);
        Assert.Null(((INodeInput)k.Seed).LiteralValue);
    }

    [Fact]
    public void With_MixedLiteralsAndConnections_AllApply()
    {
        EmptyLatentImageNode latent = new();
        KSamplerNode k = new KSamplerNode().With(
            Seed: 123,
            SamplerName: "dpmpp_2m",
            LatentImage: latent.LATENT);

        Assert.Equal(123L, ((INodeInput)k.Seed).LiteralValue);
        Assert.Equal("dpmpp_2m", ((INodeInput)k.SamplerName).LiteralValue);
        Assert.True(k.LatentImage.IsConnected);
    }

    [Fact]
    public void With_ReturnsSameInstance_ForChaining()
    {
        KSamplerNode k = new();
        Assert.Same(k, k.With(Seed: 1));
    }

    // Compile-time safety (cannot be a runtime assertion): a wrong-typed output has
    // no implicit conversion to the binding, so each of these is a build error:
    //
    //     new KSamplerNode().With(Model: new EmptyLatentImageNode().LATENT);
    //         // NodeOutput<LatentType> ↛ In<ModelType>
    //     new KSamplerNode().With(Seed: new EmptyLatentImageNode().LATENT);
    //         // NodeOutput<LatentType> ↛ IntArg
}
