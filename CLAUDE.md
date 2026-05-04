# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ComfyTyped is a class library that produces a strongly-typed C# binding layer for ComfyUI workflow JSON. Consumer extensions vendor the resulting `ComfyTyped.dll` and use it to replace stringly-typed `JObject` walking with compile-time-checked node classes, typed graph queries, and a bridge that keeps an untyped `JObject` workflow in sync with a typed `ComfyGraph`.

The library lives inside the SwarmUI source tree at `src/Extensions/ComfyTyped/` and takes a build-time `ProjectReference` to `SwarmUI.csproj`, but consumers do **not** reference this project source. They reference the built DLL only.

## Common commands

Run from the repo root (`src/Extensions/ComfyTyped/`).

Build the library:
```
dotnet build src/ComfyTyped.csproj
```

Run all tests:
```
dotnet test Tests/ComfyTyped.Tests.csproj
```

Run a single test (xUnit `--filter`):
```
dotnet test Tests/ComfyTyped.Tests.csproj --filter "FullyQualifiedName~RoundTripTests.SomeTest"
```

Regenerate ComfyTyped's own node bindings (writes to `src/Generated/`, uses committed `object_info.json`):
```
dotnet run --project tools/ComfyTyped.CodeGen -- --root --comfy-json object_info.json
```

Or pull a fresh dump from a running ComfyUI:
```
dotnet run --project tools/ComfyTyped.CodeGen -- --root --comfy-json http://127.0.0.1:8188/object_info
```

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

`tools/ComfyTyped.CodeGen -- --help` lists every flag.

## Architecture

The library is laid out as four conceptual layers under `src/`:

- **`src/Core/`** — generic graph machinery. `ComfyNode` is the base class for every node; `NodeSlot.cs` holds `NodeInput<T>`/`NodeOutput<T>` (statically type-checked connections); `ComfyGraph` is the typed graph (nodes by ID, traversal helpers, `RetargetConnections`); `WorkflowBridge` keeps a `ComfyGraph` and the original `JObject` workflow in sync (`AddNode`, `RemoveNode`, `SyncNode`, `SyncAll`, `ResolvePath`, `ToPath`); `NodeRegistry` maps `class_type` → `Type` so `ComfyGraph.FromWorkflow` can deserialize; `UnknownNode` is the lossless fallback for unrecognized `class_type` strings (preserves raw inputs so unknown nodes round-trip cleanly).

- **`src/Types/`** — hand-written `IComfyType` marker classes (`ModelType`, `LatentType`, `VaeType`, `ConditioningType`, primitives, etc.) used as the generic parameters on `NodeInput<T>`/`NodeOutput<T>`. Connecting a `LatentType` output to a `ModelType` input fails at compile time, not at ComfyUI runtime.

- **`src/Generated/`** — ~700 `*.g.cs` files emitted by `tools/ComfyTyped.CodeGen` from `object_info.json`. Each file declares one `ComfyNode` subclass plus its inputs/outputs typed with `IComfyType` markers. `NodeRegistrations.g.cs` is the codegen's registration entry point — call `ComfyTyped.Generated.NodeRegistrations.EnsureRegistered()` once at process startup. **Never hand-edit these files**; regenerate.

- **`src/SwarmUI/`** — the SwarmUI integration layer. Lives in namespace `ComfyTyped.SwarmUI` (deliberately separate from `ComfyTyped.Core` to keep SwarmUI-coupled types visually distinct). `MediaRef` is the typed equivalent of SwarmUI's `WGNodeData` (typed `INodeOutput` plus media metadata: dimensions, FPS, `T2IModelCompatClass`); converts to/from `WGNodeData` at the boundary. `BridgeSync.SyncLastId(g)` advances `WorkflowGenerator.LastID` past any IDs the typed bridge assigned — call it explicitly after `bridge.AddNode` / `bridge.SyncAll` to prevent ID collisions with subsequent `g.CreateNode()` calls. **`SyncLastId` stays a manual call site by design**; do not invent auto-syncing factories.

The codegen tool itself is at `tools/ComfyTyped.CodeGen/`, a separate `dotnet run` console program — not a Roslyn source generator. Two modes: root mode (regenerates ComfyTyped's own bindings) and diff mode (emits only nodes/types missing from a `--core-assembly`, used by consumer extensions).

## Consumer integration contract

Extensions consume ComfyTyped through `lib/ComfyTyped.dll` only:

```xml
<Reference Include="ComfyTyped">
  <HintPath>lib/ComfyTyped.dll</HintPath>
</Reference>
```

This is intentional. Extensions should have **no source-level dependency** on this repo — an extension author can run codegen, vendor the DLL, and discard the ComfyTyped source. Do not "fix" consumer csprojs to use `<ProjectReference>` against this project. The DLL is the API surface.

The DLL takes a transitive build-time dependency on `SwarmUI.csproj` (because `ComfyTyped.SwarmUI.MediaRef`/`BridgeSync` reference SwarmUI types in their public API). Consumer extensions already reference SwarmUI, so this resolves cleanly. ComfyTyped's csproj uses a `<ProjectReference>` to SwarmUI under `Condition="Exists('../../../SwarmUI.csproj')"`.

## Tests

`Tests/` is an xUnit project with three suites:

- `RoundTripTests.cs` — load `workflow.json`/`workflow_api.json`, deserialize via `ComfyGraph.FromWorkflow`, re-serialize via `ToWorkflow`, assert structural equality. The fixture JSONs are copied to test output via `<None Update="workflow_api.json" CopyToOutputDirectory="PreserveNewest" />`.
- `WorkflowBridgeTests.cs` — `WorkflowBridge` add/remove/retarget/sync semantics.
- `WorkflowFixtureTests.cs` — broader fixture-driven assertions.

`MediaRef` and `BridgeSync` are exercised by `TypedBoundaryTests.cs` in the *VideoStages* extension (`swarmui/src/Extensions/SwarmUI-VideoStages/Tests/`), since those types only have meaning when paired with a SwarmUI `WorkflowGenerator`.
