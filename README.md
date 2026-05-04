# ComfyTyped

Strongly-typed C# bindings for ComfyUI workflow JSON. Replaces stringly-typed `JObject` walking with compile-time-checked node classes generated from a ComfyUI `object_info` dump.

## CodeGen

Generate the core assembly's nodes (native ComfyUI nodes + comfy_extras + SwarmUI bundled/installable packs):

```
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --comfy-json object_info.json \
  --output src/Generated \
  --namespace ComfyTyped.Generated \
  --registrations-class NodeRegistrations
```

`--comfy-json` accepts a local file or an HTTP URL — fetch live from a running ComfyUI:

```
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --comfy-json http://127.0.0.1:8188/object_info
```

Diff mode for extensions (emit every node and IComfyType marker that isn't
already in core's assembly):

```
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --comfy-json http://127.0.0.1:8188/object_info \
  --output Generated \
  --namespace MyExt.Generated \
  --core-assembly path/to/ComfyTyped.dll
```

The codegen scans `ComfyTyped.dll` for every `class_type` and every
`IComfyType` marker class core already defines, and only emits the diff. New
IO type names encountered in the comfy-json (e.g. an extension's custom
`SOME_CUSTOM_TYPE`) get a marker class generated automatically — mechanical
PascalCase + `Type` suffix, so `SOME_CUSTOM_TYPE` → `SomeCustomTypeType`.

Prune (remove generated files no longer referenced anywhere in source):

```
dotnet run --project tools/ComfyTyped.CodeGen -- prune \
  --generated-dir src/Generated \
  --source src \
  [--dry-run]
```

To generate only native ComfyUI + SwarmUI nodes:

```
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --comfy-json object_info.json \
  --root
```

See all flags: `dotnet run --project tools/ComfyTyped.CodeGen -- --help`.

## Usage

At process startup (idempotent, thread-safe):

```csharp
ComfyTyped.Generated.NodeRegistrations.EnsureRegistered();
```

### Build a workflow from scratch

```csharp
var graph = new ComfyGraph();

var ckpt = graph.AddNode(new CheckpointLoaderSimpleNode());
ckpt.CkptName.Set("model.safetensors");

var pos = graph.AddNode(new CLIPTextEncodeNode());
pos.Text.Set("a beautiful sunset");
pos.Clip.ConnectTo(ckpt.CLIP);

var latent = graph.AddNode(new EmptyLatentImageNode());
latent.Width.Set(1024L);
latent.Height.Set(1024L);

var sampler = graph.AddNode(new KSamplerNode());
sampler.Model.ConnectTo(ckpt.MODEL);
sampler.Positive.ConnectTo(pos.CONDITIONING);
sampler.LatentImage.ConnectTo(latent.LATENT);
sampler.Seed.Set(42L);
sampler.Steps.Set(20L);

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

### Rewire many connections at once

Replace every input that points at one output with a connection to another:

```csharp
// All consumers of oldDecode.IMAGE → newDecode.IMAGE, restricted to a predicate
int count = graph.RetargetConnections(
    oldDecode.IMAGE,
    newDecode.IMAGE,
    (node, input) => node is SwarmSaveImageWSNode && input.Name == "images");
```

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
