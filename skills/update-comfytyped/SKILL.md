---
name: update-comfytyped
description: Refresh ComfyTyped node bindings and type markers from ComfyUI object_info and update a SwarmUI extension's vendored library. Use when an extension encounters an unknown node or socket type, a missing generated C# class or member, or a stale ComfyTyped.dll.
---

# Update ComfyTyped

## 1. Identify the missing definition

Find the exact ComfyUI `class_type` or socket type and check the backend's `object_info` and ComfyTyped's `src/Generated`. Check the consumer's project reference, generated namespace, and generation instructions. The local core checkout is usually the sibling `ComfyTyped` (or similar) directory. From a consumer elsewhere, locate the configured checkout before running commands.

- If the checked-out core already has the correct binding, export a fresh build of that revision. If the consumer already loads that build, check its imports or node registration.
- If the schema has a new native ComfyUI or SwarmUI definition, regenerate core first. Third-party custom nodes come from consumer diff generation.
- If the backend schema lacks the definition, identify the missing node pack or backend version. Use an endpoint or complete schema dump from the intended backend before generating. Generate the bindings with CodeGen so they stay reproducible.

## 2. Regenerate and commit core

Run from the ComfyTyped checkout. Check both repos' working trees before generation, which rewrites the target directory. Use an isolated core checkout if existing core edits would be overwritten or included in the exported build.

```bash
COMFYTYPED_CONFIG=Release ./regen.sh gen
```

Use `COMFYTYPED_OBJECT_INFO_URL` for a different verified backend. If the local, gitignored helper is absent, save the intended backend's complete `object_info` response as `object_info.json`. For a supplied dump, pass its absolute path to `--comfy-json` below. Use that same snapshot for consumer generation.

```bash
dotnet run --project tools/ComfyTyped.CodeGen -- --root --comfy-json object_info.json --families families.json
dotnet build ComfyTyped.csproj -c Release
```

Verify the requested definition and review all generated changes, including adds and removals. Run `dotnet test Tests/ComfyTyped.Tests.csproj -c Release`. Commit the generated changes in ComfyTyped before export, staging only this refresh. Keep the local dump and build outputs out of the commit. If core needed no changes, use the existing commit.

## 3. Export to the requesting extension

Use the Release build from the committed core revision. Set `consumer_root` to the absolute consumer checkout path. For the usual `lib/` reference, copy the build with `cp bin/Release/net8.0/ComfyTyped.dll "$consumer_root/lib/ComfyTyped.dll"`. Refresh the matching PDB if the consumer ships it. Follow the consumer's actual reference path and skip diff generation for a library-only refresh.

If the consumer uses diff bindings or needs a new custom binding, regenerate with the exact schema used for core, or the intended backend's current schema when reusing unchanged core. Set `consumer_namespace` to the consumer's existing or documented namespace (for example, `VideoStages.Generated` or `Base2Edit.Generated`). Set `schema` to the dump path. This example uses the usual `src/Generated` layout.

```bash
families=families.json
if [[ -f "$consumer_root/comfytyped.families.json" ]]; then
    families="$consumer_root/comfytyped.families.json"
fi
keep_args=()
if [[ -f "$consumer_root/comfytyped.keep.json" ]]; then
    keep_args=(--keep-list "$consumer_root/comfytyped.keep.json")
fi
dotnet run --project tools/ComfyTyped.CodeGen -- \
    --comfy-json "$schema" --core-assembly "$consumer_root/lib/ComfyTyped.dll" \
    --output "$consumer_root/src/Generated" --namespace "$consumer_namespace" \
    --families "$families" "${keep_args[@]}"
```

Export from the built revision and its saved schema. The `./regen.sh gen <project>` shortcut fetches the schema again and defaults to `VideoStages.Generated`.

## 4. Prune and verify the consumer

Update call sites and imports before pruning. A binding that moved into core now uses the core namespace. Use the consumer's documented prune command, or run this from ComfyTyped for the usual layout.

```bash
./regen.sh prune "$consumer_root"
```

Without the helper, run `dotnet run --project tools/ComfyTyped.CodeGen -- prune --generated-dir "$consumer_root/src/Generated" --source "$consumer_root/src"`. Add `--source` for other source directories that must count toward retention. Keep deliberately shipped nodes with no source reference in `comfytyped.keep.json` and regenerate the manifest before pruning. Skip pruning for a library-only refresh.

Build and test the consumer with its normal commands. If the failure was in a running SwarmUI host, verify the affected workflow after the host loads the refreshed DLL. Report the core commit and the consumer files changed. Follow the user's authorization for any consumer commit or push.
