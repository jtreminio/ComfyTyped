# ComfyTyped

Strongly-typed C# bindings for ComfyUI workflow JSON. Replaces stringly-typed `JObject` walking with compile-time-checked node classes generated from a ComfyUI `object_info` dump.

## CodeGen

### Regenerating ComfyTyped's own nodes

`--root` is sugar for the repo defaults (`--output src/Generated --namespace
ComfyTyped.Generated --marker-namespace ComfyTyped.Types
--registrations-class NodeRegistrations --native-only`). Because those defaults
are relative paths, run from the ComfyTyped checkout:

```
cd /path/to/ComfyTyped
dotnet run --project /path/to/ComfyTyped/tools/ComfyTyped.CodeGen -- \
  --root \
  --comfy-json object_info.json
```

`--comfy-json` accepts a local file or an HTTP URL — fetch live from a running
ComfyUI instead of the committed `object_info.json`:

```
cd /path/to/ComfyTyped
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --root \
  --comfy-json http://127.0.0.1:8188/object_info
```

### Generating from another project (extension)

Diff mode emits every node and IComfyType marker that isn't already in the
core assembly. All paths absolute, run from anywhere:

```
dotnet run --project /path/to/ComfyTyped/tools/ComfyTyped.CodeGen -- \
  --comfy-json http://127.0.0.1:8188/object_info \
  --output /path/to/your-extension/src/Generated \
  --namespace YourExt.Generated \
  --core-assembly /path/to/your-extension/lib/ComfyTyped.dll
```

The codegen scans `ComfyTyped.dll` for every `class_type` and every
`IComfyType` marker class core already defines, and only emits the diff. New
IO type names encountered in the comfy-json (e.g. an extension's custom
`SOME_CUSTOM_TYPE`) get a marker class generated automatically — mechanical
PascalCase + `Type` suffix, so `SOME_CUSTOM_TYPE` → `SomeCustomTypeType`.

### Pruning unused generated files

Delete generated files whose class name isn't referenced anywhere under
`--source`. Run after writing the consumer code that uses the typed bindings,
before committing:

```
dotnet run --project /path/to/ComfyTyped/tools/ComfyTyped.CodeGen -- prune \
  --generated-dir /path/to/your-extension/src/Generated \
  --source /path/to/your-extension/src \
  [--dry-run]
```

`NodeRegistrations.g.cs` is always preserved.

See all flags: `dotnet run --project /path/to/ComfyTyped/tools/ComfyTyped.CodeGen -- --help`.

## Usage

At process startup (idempotent, thread-safe):

```csharp
ComfyTyped.Generated.NodeRegistrations.EnsureRegistered();
```

### Build a workflow from scratch

```csharp
var graph = new ComfyGraph();

var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode().With(CkptName: "model.safetensors"));

var pos = graph.AddNode(new CLIPTextEncodeNode().With(Text: "a beautiful sunset"));
pos.Clip.ConnectTo(ckpt.CLIP);

var latent = graph.AddNode(new EmptyLatentImageNode().With(Width: 1024, Height: 1024));

var sampler = graph.AddNode(new KSamplerNode().With(Seed: 42, Steps: 20));
sampler.Model.ConnectTo(ckpt.MODEL);
sampler.Positive.ConnectTo(pos.CONDITIONING);
sampler.LatentImage.ConnectTo(latent.LATENT);

var decode = graph.AddNode(new VAEDecodeNode());
decode.Samples.ConnectTo(sampler.LATENT);
decode.Vae.ConnectTo(ckpt.VAE);

var save = graph.AddNode(new SaveImageNode());
save.Images.ConnectTo(decode.IMAGE);

JObject workflow = graph.ToWorkflow();
// → submit to ComfyUI
```

The `ConnectTo` calls are statically type-checked — connecting a `LatentType`
output to a `ModelType` input will not compile.

`With(...)` is a generated fluent setter for primitive inputs (INT/FLOAT/STRING/BOOL).
Pass only what you want to set; `null` leaves the existing default untouched.
Connection inputs are intentionally not exposed via `With(...)` — they go through
`ConnectTo(...)` / `ConnectToUntyped(...)` so type-mismatch stays a compile error.

### Load and traverse an existing workflow

```csharp
ComfyGraph graph = ComfyGraph.FromWorkflow(workflowJson);

// Typed lookup by ID
var save = graph.GetNode<SwarmSaveAnimationWSNode>("53200");

// Walk upstream to the nearest node of a given type
var sampler = graph.FindNearestUpstream<SwarmKSamplerNode>(save);

// Read a literal directly off the typed slot
long steps = (long)sampler.Steps.LiteralValue!;

// Follow a typed connection
var separate = save.Images.TypedConnection?.Node as LTXVSeparateAVLatentNode;
```

Unknown node types fall back to `UnknownNode`, which preserves raw inputs for
lossless round-trips, so an old workflow with a custom node you don't have
generated bindings for still loads and re-serializes correctly.

### Mutate an existing workflow

`WorkflowBridge` keeps a typed `ComfyGraph` and the original `JObject` in sync
so you can reach for either side as needed:

```csharp
var bridge = WorkflowBridge.Create(workflow);

// Read via the typed graph
var sampler = bridge.Graph.GetNode<KSamplerNode>("10")!;

// Mutate via the typed graph
sampler.Steps.Set(40L);
sampler.Seed.Set(7L);

// Push the change back to the JObject
bridge.SyncNode(sampler);

// AddNode/RemoveNode write through automatically — no Sync needed
var newDecode = bridge.AddNode(new VAEDecodeNode());
newDecode.Samples.ConnectTo(sampler.LATENT);
```

If a downstream tool wants the legacy `[nodeId, slotIndex]` JArray form:

```csharp
JArray path = WorkflowBridge.ToPath(decode.IMAGE);
INodeOutput? output = bridge.ResolvePath(legacyJArrayPath);
```

### Wire inputs from a `JArray` path

When the source side of a connection is held as a JArray path (e.g. SwarmUI
`WorkflowGenerator` slots like `genInfo.PosCond` or `WGNodeData.Path`), reach
for one of the path-aware helpers instead of resolving by hand:

| You have…                      | You want…                          | Use                                       |
| ------------------------------ | ---------------------------------- | ----------------------------------------- |
| `NodeOutput<T>` reference      | wire it into `NodeInput<T>`        | `input.ConnectTo(output)`                 |
| `INodeOutput` (untyped)        | wire it into `NodeInput<T>`        | `input.ConnectToUntyped(output)`          |
| `JArray` path                  | wire it into `NodeInput<T>`        | `input.ConnectFromPath(bridge, path)`     |
| `JArray` path (may be missing) | wire if it resolves, else no-op    | `input.TryConnectFromPath(bridge, path)`  |
| `JArray` path                  | the typed output, for further use  | `bridge.ResolvePath<T>(path)`             |

```csharp
// Before:
cond.PositiveInput.ConnectToUntyped(bridge.ResolvePath(genInfo.PosCond));

// After — T inferred from the receiver, no manual <ConditioningType>:
cond.PositiveInput.ConnectFromPath(bridge, genInfo.PosCond);
```

`ConnectFromPath` throws `ArgumentException` on a null or unresolved path,
with diagnostics that name the source expression, slot, owning node, and
expected type:

```
Path 'genInfo.PosCond' did not resolve to an output for input 'positive'
on LTXVConditioningNode#5 (expected 'CONDITIONING').
```

`TryConnectFromPath` mirrors the existing `TryConnectToUntyped` contract:
returns `false` (no-op, slot state preserved) when the path is null or
doesn't resolve; type mismatches still throw — null tolerance is the only
soft failure.

#### Wildcard caveat

`ResolvePath<T>` is strict — it will not auto-coerce `AnyType` (UnknownNode
outputs) or `ComfyMatchTypeV3` (V3 wildcard outputs) into a concrete `T`,
because there's no honest way to return a `NodeOutput<T>` for a wildcard.
When the source may be a wildcard, route through the connection layer
instead — `ConnectFromPath` and `ConnectToUntyped` both accept wildcards
through their existing acceptance path:

```csharp
// Wildcard-tolerant: succeeds whether the resolved output is a concrete
// ImageType or a NodeOutput<AnyType> from an UnknownNode.
decode.Image.ConnectFromPath(bridge, somePath);

// Strict typed handle: throws InvalidOperationException if resolved
// output isn't NodeOutput<ImageType>. Use when you intend to hand the
// handle to typed `ConnectTo` or read its node for further work.
NodeOutput<ImageType>? typed = bridge.ResolvePath<ImageType>(somePath);
```

For cases that need to inspect the resolved node (graph navigation, type
checks, conditional rewiring), keep using the non-generic
`bridge.ResolvePath(path)` — it returns `INodeOutput?` and never throws on
type mismatch:

```csharp
if (bridge.ResolvePath(controlImage) is INodeOutput consumer
    && consumer.Node is ImageFromBatchNode batch)
{
    // …rewire / inspect / decide
}
```

### Rewire many connections at once

Replace every input that points at one output with a connection to another:

```csharp
// All consumers of oldDecode.IMAGE → newDecode.IMAGE, restricted to a predicate
int count = graph.RetargetConnections(
    oldDecode.IMAGE,
    newDecode.IMAGE,
    (node, input) => node is SwarmSaveImageWSNode && input.Name == "images");
```

### Removing a node that has consumers

`bridge.RemoveNode(node)` is a "dumb delete" — it drops the node from the graph and JObject but
does **not** clean up downstream inputs that pointed at its outputs. Those inputs keep a reference
to the now-removed node, and the `[id, slot]` JArrays in their serialized inputs still name the
deleted ID. Before deleting a middle node, rewire each output to its replacement and let auto-sync
flush the changes:

```csharp
var replacement = bridge.AddNode(new VAEDecodeNode());
// (copy any literal/connection inputs you want to carry over from `old` to `replacement`)

foreach (INodeOutput output in old.Outputs)
{
    INodeOutput? to = replacement.FindOutput(output.SlotIndex);
    if (to is not null) bridge.Graph.RetargetConnections(output, to);
}
bridge.RemoveNode(old);
```

The null guard matters when the replacement is a different class than the original — slot
indices that don't exist on the replacement leave those consumers dangling, and you'll need
to handle them yourself (rewire to a different output, or `Clear()` them). When the
replacement is the same class as `old`, every slot index lines up and the guard never trips.

To drop a node without a replacement, walk every output's consumers and `Clear()` them:

```csharp
foreach (INodeOutput output in old.Outputs)
{
    foreach (var (_, input) in bridge.Graph.FindInputsConnectedTo(output))
    {
        input.Clear();
    }
}
bridge.RemoveNode(old);
```

### SwarmUI integration: typed output → `WGNodeData`

`WorkflowGenerator.Current*` slots (`CurrentModel`, `CurrentVae`,
`CurrentMedia`, etc.) hold `WGNodeData`, a SwarmUI type that wraps a JArray
path plus media metadata. When the path is in your hands as a typed
`INodeOutput`, the `ToWGNodeData` extension on `ComfyTyped.SwarmUI`
projects it across the boundary without the manual path-and-fields spell:

```csharp
using ComfyTyped.SwarmUI;

var sampler = bridge.AddNode(new KSamplerNode());

// With explicit compat:
g.CurrentMedia = sampler.LATENT.ToWGNodeData(
    g, WGNodeData.DT_LATENT_IMAGE, g.CurrentCompat());

// Defaulted to g.CurrentCompat() — most call sites use this:
g.CurrentMedia = sampler.LATENT.ToWGNodeData(g, WGNodeData.DT_LATENT_IMAGE);

// With media metadata (Width/Height/Frames/FPS) inline — replaces the
// `new WGNodeData(...) { Width = 512, Height = 512, ... }` initializer block:
g.CurrentMedia = decode.IMAGE.ToWGMedia(
    g, WGNodeData.DT_VIDEO,
    width: 512, height: 512, frames: 16, fps: 24);

// Audio attachments — uses g.CurrentAudioVae?.Compat (g.CurrentCompat() is
// the wrong compat for audio output paths):
g.CurrentMedia.AttachedAudio = audioDecode.Audio.ToWGAttachedAudio(g);
```

`ToWGNodeData` / `ToWGMedia` are the typed-output peer of
`MediaRef.ToWGNodeData(g)` — the latter projects a typed `MediaRef` that
already carries dimensions / FPS / `AttachedAudio`; the former two are the
lightweight path for callers that just need the `[id, slot]` projection or
inline media metadata.

### SwarmUI integration: stub fixtures and auto-`SyncLastId`

For tests and round-tripping workflows whose `class_type` strings have no
typed bindings, `WorkflowBridge.AddStub(classType, id)` is shorthand for
`AddNode(new UnknownNode(classType), id)`. Pair with `WithOutputs(...)` to
declare the slot names at construction:

```csharp
var model = bridge.AddStub("UnitTest_Model", "4")
                  .WithOutputs("MODEL", "CLIP", "VAE");

g.CurrentModel = model.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
g.CurrentTextEnc = model.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
g.CurrentVae = model.GetOutput(2).ToWGNodeData(g, WGNodeData.DT_VAE);
```

`BridgeSync.For(g)` returns a disposable wrapper that calls `SyncLastId(g)`
on dispose, removing the trailing manual call from seed-step closures:

```csharp
new(g =>
{
    using var bridge = BridgeSync.For(g);

    var model = bridge.AddStub("UnitTest_Model", "4")
                      .WithOutputs("MODEL", "CLIP", "VAE");
    g.CurrentModel = model.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);

    // BridgeSync.SyncLastId(g) fires on dispose — no manual call.
}, priority);
```

The wrapper is intentionally a separate type so `WorkflowBridge.Dispose`
stays pure (subscription teardown only) — no surprise side-effects on
production-shaped bridges. Reach for `For(g)` when you want the
auto-sync; reach for `WorkflowBridge.Create(g.Workflow)` when you don't.

### Extending: registering nodes from another assembly

Once an extension generates its own `*.g.cs` files into its own assembly, it
self-registers in one call:

```csharp
NodeRegistry.RegisterAssembly(typeof(MyExtNode).Assembly);
```

The codegen auto-generates `IComfyType` marker classes for any new IO type
names the extension introduces, so no manual marker authoring is needed. If
you want runtime `IOTypeMap.Resolve(typeName)` to also know about them,
register the markers explicitly:

```csharp
IOTypeMap.Register<MyCustomMarkerType>("MY_CUSTOM_TYPE");
```
