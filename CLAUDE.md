# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ComfyTyped is a class library that produces a strongly-typed C# binding layer for ComfyUI workflow JSON. Consumer extensions vendor the resulting `ComfyTyped.dll` and use it to replace stringly-typed `JObject` walking with compile-time-checked node classes, typed graph queries, and a bridge that keeps an untyped `JObject` workflow in sync with a typed `ComfyGraph`.

The library lives inside the SwarmUI source tree at `src/Extensions/ComfyTyped/` and takes a build-time `ProjectReference` to `SwarmUI.csproj`, but consumers do **not** reference this project source. They reference the built DLL only.

## Common commands

Run from the repo root (`src/Extensions/ComfyTyped/`).

Build the library:
```
dotnet build ComfyTyped.csproj
```

Run all tests:
```
dotnet test Tests/ComfyTyped.Tests.csproj
```

Run a single test (xUnit `--filter`):
```
dotnet test Tests/ComfyTyped.Tests.csproj --filter "FullyQualifiedName~RoundTripTests.SomeTest"
```

Regenerate ComfyTyped's own node bindings (writes to `src/Generated/`) from a local `object_info.json` dump. The dump is **not committed** (it's a ~4MB generated artifact) — point this at your own dump, or pull a fresh one from a running ComfyUI (below):
```
dotnet run --project tools/ComfyTyped.CodeGen -- --root --comfy-json object_info.json --families families.json
```

Or pull a fresh dump from a running ComfyUI (SwarmUI proxies its backend at `<host>:7801/ComfyBackendDirect/api/object_info`):
```
dotnet run --project tools/ComfyTyped.CodeGen -- --root --comfy-json http://127.0.0.1:8188/object_info --families families.json
```

Always pass `--families families.json` on a root regen, or the codegen drops the hand-written family interfaces (e.g. `IVaeDecode`) from the generated class declarations.

Generate diff bindings into a *consumer* extension (only nodes/types not already in `ComfyTyped.dll`):
```
dotnet run --project tools/ComfyTyped.CodeGen -- \
  --comfy-json http://127.0.0.1:8188/object_info \
  --output /path/to/your-extension/src/Generated \
  --namespace YourExt.Generated \
  --core-assembly /path/to/your-extension/lib/ComfyTyped.dll
```

Prune unreferenced generated files in a consumer (run before committing the consumer's typed bindings):
```
dotnet run --project tools/ComfyTyped.CodeGen -- prune \
  --generated-dir /path/to/ext/src/Generated \
  --source /path/to/ext/src \
  [--dry-run]
```

To group heterogeneous nodes that share a slot shape (e.g. `VAEDecode` + `VAEDecodeTiled`, which have no common base class) under one typed surface, hand-write an interface under `src/Families/` and list its members in `families.json` (`{ "ComfyTyped.Families.IVaeDecode": ["VAEDecode", "VAEDecodeTiled"] }`; `_`-prefixed keys are comments). Codegen appends the interface to each member node's base list. The member nodes must already expose the interface's exact slot properties (the codegen adds the name only, no glue) — a mismatch is a compile error after regen, by design. Family interfaces derive from `IComfyNode` (the read-only `ComfyNode` surface: `Id`, `Outputs`, `FindInput`, …) so consumers get node identity without casting. Graph queries accept them because `ComfyGraph`'s generic query methods (`GetNode<T>`, `NodesOfType<T>`, `FindNearestUpstream<T>`, `FindNearestDownstream<T>`) are constrained to `where T : class`, not `ComfyNode`. `--families` works in both root and diff mode.

To ship typed bindings for nodes the consumer code does not reference yet (custom-node packs that are part of the extension's supported surface area), pass `--keep-list <json>` at gen time. The JSON shape is `{ "keep_modules": [...], "keep_class_types": [...] }`; codegen force-includes matching nodes past `--native-only` and writes `PruneManifest.g.cs` listing the resolved C# class names. The `prune` subcommand reads that manifest automatically — no extra flag — so listed classes survive even with no consumer-source reference. Regenerate after editing the keep-list; the manifest is a derived artifact.

`tools/ComfyTyped.CodeGen -- --help` lists every flag.

## Architecture

The library is laid out as four conceptual layers under `src/`:

- **`src/Core/`** — generic graph machinery. `ComfyNode` is the base class for every node; `NodeSlot.cs` holds `NodeInput<T>`/`NodeOutput<T>` (statically type-checked connections); `ComfyGraph` is the typed graph (nodes by ID, traversal helpers, `RetargetConnections`); `WorkflowBridge` keeps a `ComfyGraph` and the original `JObject` workflow in sync (`AddNode`, `RemoveNode`, `SyncNode`, `SyncAll`, `ResolvePath`, `ToPath`); `NodeRegistry` maps `class_type` → `Type` so `ComfyGraph.FromWorkflow` can deserialize; `UnknownNode` is the lossless fallback for unrecognized `class_type` strings (preserves raw inputs so unknown nodes round-trip cleanly).

- **`src/Types/`** — hand-written `IComfyType` marker classes (`ModelType`, `LatentType`, `VaeType`, `ConditioningType`, primitives, etc.) used as the generic parameters on `NodeInput<T>`/`NodeOutput<T>`. Connecting a `LatentType` output to a `ModelType` input fails at compile time, not at ComfyUI runtime.

- **`src/Generated/`** — ~700 `*.g.cs` files emitted by `tools/ComfyTyped.CodeGen` from `object_info.json`. Each file declares one `ComfyNode` subclass plus its inputs/outputs typed with `IComfyType` markers, and a fluent `With(...)` method exposing **every singular input** as a nullable named parameter — `new KSamplerNode().With(Seed: 42, Steps: 20, Model: ckpt.MODEL, LatentImage: empty.LATENT)` returns `this` for chaining. Each parameter takes an *input binding* (`src/Core/InputBindings.cs`): primitive inputs (INT/FLOAT/STRING/BOOL) accept a literal **or** a same-typed output via `IntArg`/`FloatArg`/`StringArg`/`BoolArg`, and connection inputs accept a same-typed output via `In<T>`. Type-mismatch stays a compile error — there is no implicit conversion from a wrong-typed `NodeOutput<T>` to the binding, so `With(Model: ckpt.LATENT)` won't compile. You never name the binding types yourself (`int → IntArg → IntArg?` lifts automatically at the call site). `.ConnectTo(...)` / `.ConnectToUntyped(...)` / `.Set(...)` remain the low-level primitives `With(...)` builds on (and what the bridge/deserialization use directly). **Input lists (`COMFY_AUTOGROW_V3`) are not in `With(...)`** — use `Add`/`AddRange`. `NodeRegistrations.g.cs` is the codegen's registration entry point — call `ComfyTyped.Generated.NodeRegistrations.EnsureRegistered()` once at process startup. **Never hand-edit these files**; regenerate.

- **`src/SwarmUI/`** — the SwarmUI integration layer. Lives in namespace `ComfyTyped.SwarmUI` (deliberately separate from `ComfyTyped.Core` to keep SwarmUI-coupled types visually distinct). `MediaRef` is the typed equivalent of SwarmUI's `WGNodeData` (typed `INodeOutput` plus media metadata: dimensions, FPS, `T2IModelCompatClass`); converts to/from `WGNodeData` at the boundary, and `input.ConnectFrom(mediaRef)` (in `MediaRefExtensions`) connects a slot straight from one. `BridgeSync.SyncLastId(g)` advances `WorkflowGenerator.LastID` past any IDs the typed bridge assigned — the manual primitive. **Prefer `using SyncingWorkflowBridge bridge = BridgeSync.For(g);`** for self-contained helpers: it auto-calls `SyncLastId` on dispose and implicitly converts to `WorkflowBridge`, so the `WorkflowBridge.Create` + per-node `SyncNode` + trailing `SyncLastId` ritual collapses to one `using` (the bridge already auto-syncs typed mutations on subscribed nodes; an explicit `SyncNode` is only needed after `ExtraInputs`/`RawInputs` edits). `SyncingWorkflowBridge` is the **one sanctioned auto-syncer** — its sync boundary is the explicit `using` scope; do not make `AddNode`/`SyncNode` implicitly sync `LastID` outside that boundary.

- **`src/Families/`** — hand-written family interfaces (e.g. `IVaeDecode`) that give a shared typed surface to heterogeneous nodes (see the `--families` codegen note above). They derive from `Core.IComfyNode` (the read-only `ComfyNode` surface) so consumers get `Id`/`Outputs`/etc. without casting. Core path/lookup helpers that pair with these: `bridge.NodeAt(path)` / `NodeAt<T>(path)` resolve a `[nodeId, slot]` JArray straight to a (typed) node; `NodeRef.From(path)` / `.ToJArray()` replace hand-destructuring of those JArrays; `input.SetFromToken(bridge, token)` sets an INT input from a literal-or-connection `JToken`.

The codegen tool itself is at `tools/ComfyTyped.CodeGen/`, a separate `dotnet run` console program — not a Roslyn source generator. Two modes: root mode (regenerates ComfyTyped's own bindings) and diff mode (emits only nodes/types missing from a `--core-assembly`, used by consumer extensions).

## Consumer integration contract

Extensions consume ComfyTyped through `lib/ComfyTyped.dll` only:

```xml
<Reference Include="ComfyTyped">
  <HintPath>lib/ComfyTyped.dll</HintPath>
</Reference>
```

This is intentional. Extensions should have **no source-level dependency** on this repo — an extension author can run codegen, vendor the DLL, and discard the ComfyTyped source. Do not "fix" consumer csprojs to use `<ProjectReference>` against this project. The DLL is the API surface.

The DLL takes a transitive build-time dependency on `SwarmUI.csproj` (because `ComfyTyped.SwarmUI.MediaRef`/`BridgeSync` reference SwarmUI types in their public API). Consumer extensions already reference SwarmUI, so this resolves cleanly. ComfyTyped's csproj uses a `<ProjectReference>` to SwarmUI under `Condition="Exists('../../SwarmUI.csproj')"`.

`ComfyTyped.csproj` lives at the **extension root** (`src/Extensions/ComfyTyped/ComfyTyped.csproj`), not inside `src/`. This is required: SwarmUI's `ExtensionsManager` scans each extension directory's root for any `*.csproj` and synthesizes `SwarmAutoGenExtensionProjectFile.csproj` if it finds none — and that synthesized csproj uses default SDK file inclusion which sweeps up `Tests/`, `tools/`, and inner `obj/` artifacts, breaking the build. The root csproj explicitly excludes `Tests/**`, `tools/**`, `bin/**`, and `obj/**` so the SwarmUI build matches the standalone build.

## Tests

`Tests/` is an xUnit project with three suites:

- `RoundTripTests.cs` — load `workflow.json`/`workflow_api.json`, deserialize via `ComfyGraph.FromWorkflow`, re-serialize via `ToWorkflow`, assert structural equality. The fixture JSONs are copied to test output via `<None Update="workflow_api.json" CopyToOutputDirectory="PreserveNewest" />`.
- `WorkflowBridgeTests.cs` — `WorkflowBridge` add/remove/retarget/sync semantics.
- `WorkflowFixtureTests.cs` — broader fixture-driven assertions.

`MediaRef` and `BridgeSync` are exercised by `TypedBoundaryTests.cs` in the *VideoStages* extension (`swarmui/src/Extensions/SwarmUI-VideoStages/Tests/`), since those types only have meaning when paired with a SwarmUI `WorkflowGenerator`.
