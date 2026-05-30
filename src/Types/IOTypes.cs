namespace ComfyTyped.Types;

/// <summary>Marker interface for all ComfyUI IO types. Used as generic constraints on NodeInput/NodeOutput.</summary>
public interface IComfyType
{
    /// <summary>The ComfyUI type string this marker represents (e.g. "MODEL", "IMAGE").</summary>
    static abstract string TypeName { get; }
}

// ── Core types ──────────────────────────────────────────────────────

public sealed class ModelType : IComfyType { public static string TypeName => "MODEL"; }
public sealed class ClipType : IComfyType { public static string TypeName => "CLIP"; }
public sealed class VaeType : IComfyType { public static string TypeName => "VAE"; }
public sealed class LatentType : IComfyType { public static string TypeName => "LATENT"; }
public sealed class ImageType : IComfyType { public static string TypeName => "IMAGE"; }
public sealed class MaskType : IComfyType { public static string TypeName => "MASK"; }
public sealed class ConditioningType : IComfyType { public static string TypeName => "CONDITIONING"; }
public sealed class AudioType : IComfyType { public static string TypeName => "AUDIO"; }
public sealed class VideoType : IComfyType { public static string TypeName => "VIDEO"; }

// ── Primitive types (can be literals or connections) ─────────────────

public sealed class IntType : IComfyType { public static string TypeName => "INT"; }
public sealed class FloatType : IComfyType { public static string TypeName => "FLOAT"; }
public sealed class StringType : IComfyType { public static string TypeName => "STRING"; }
public sealed class BooleanType : IComfyType { public static string TypeName => "BOOLEAN"; }

// ── Sampling/scheduling types ───────────────────────────────────────

public sealed class SamplerType : IComfyType { public static string TypeName => "SAMPLER"; }
public sealed class SigmasType : IComfyType { public static string TypeName => "SIGMAS"; }
public sealed class GuiderType : IComfyType { public static string TypeName => "GUIDER"; }
public sealed class NoiseType : IComfyType { public static string TypeName => "NOISE"; }

// ── Vision/style types ──────────────────────────────────────────────

public sealed class ClipVisionType : IComfyType { public static string TypeName => "CLIP_VISION"; }
public sealed class ClipVisionOutputType : IComfyType { public static string TypeName => "CLIP_VISION_OUTPUT"; }
public sealed class StyleModelType : IComfyType { public static string TypeName => "STYLE_MODEL"; }

// ── Control types ───────────────────────────────────────────────────

public sealed class ControlNetType : IComfyType { public static string TypeName => "CONTROL_NET"; }
public sealed class GligenType : IComfyType { public static string TypeName => "GLIGEN"; }
public sealed class HooksType : IComfyType { public static string TypeName => "HOOKS"; }

// ── Model-related types ─────────────────────────────────────────────

public sealed class UpscaleModelType : IComfyType { public static string TypeName => "UPSCALE_MODEL"; }
public sealed class LatentUpscaleModelType : IComfyType { public static string TypeName => "LATENT_UPSCALE_MODEL"; }
public sealed class IpAdapterType : IComfyType { public static string TypeName => "IPADAPTER"; }
public sealed class ModelPatchType : IComfyType { public static string TypeName => "MODEL_PATCH"; }
public sealed class LoraModelType : IComfyType { public static string TypeName => "LORA_MODEL"; }

// ── Geometry types ──────────────────────────────────────────────────

public sealed class BboxType : IComfyType { public static string TypeName => "BBOX"; }

// ── Domain-specific types (custom nodes) ────────────────────────────

public sealed class TorchCompileArgsType : IComfyType { public static string TypeName => "TORCH_COMPILE_ARGS"; }

// ── Wildcard/any type ───────────────────────────────────────────────

/// <summary>Matches any ComfyUI type. Used for UnknownNode and wildcard inputs.</summary>
public sealed class AnyType : IComfyType { public static string TypeName => "*"; }

// ── V3 special types ────────────────────────────────────────────────

public sealed class ComfyMatchTypeV3 : IComfyType { public static string TypeName => "COMFY_MATCHTYPE_V3"; }
