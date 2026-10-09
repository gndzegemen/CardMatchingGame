# CLAUDE.md

<!-- KitWright Unity managed project skills -->
<!-- KitWright Unity project skill versions: unity-mcp-workflow@1.0.1 -->

# KitWright MCP for Unity Project Guidance

This section is managed by KitWright MCP for Unity for Claude Code. Everything between the begin and end markers is regenerated on each sync; edit outside this block.

## Installed skills

- `unity-mcp-workflow` v1.0.1 - Efficient workflow for using Unity MCP to edit, import, compile, inspect, and test Unity projects.

## Preferred workflow

- Use KitWright Unity tools for Unity editor state and automation.
- Use `execute_code` for non-trivial Unity orchestration. For new snippets, include `using KitWright.Editor.Tools.Scripting;`, implement `IKitWrightCommand`, and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo and `ctx.Log` for traceable output.
- Confirm the Unity project root, active scene, and real object/prefab/asset path before edits. Treat user-provided object names as hints, not paths.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{success, message, data}` / `{success: false, code, error, data}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_properties` — it picks up `[SerializeField] private` fields and accepts Object references as `{"fileID": <instanceId>}` or `{"assetPath": "Assets/..."}`.
- Read editor state through `get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`; try `execute_menu_item` before writing ad-hoc `execute_code`.
- Never edit `.unity`, `.prefab`, or `.asset` files with shell text tools or patches; use Unity MCP / Editor APIs for scenes, prefabs, and ScriptableObject assets.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes assets and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- In `execute_code`, null-guard every lookup and return explicit missing path/object/component messages; do not run self-healing fallback loops.
- For Unity object references, do not use `??=` for lazy rebinding; use explicit `if (field == null) field = Resolve();`.
- After code or resource edits, exit Play Mode if needed and call `request_recompile` first; it returns the compile errors unless it reports that Unity started importing or recompiling. Only then call `wait_for_compilation(force_refresh=false)`, which returns them too. Do not follow either with `get_compilation_errors`, and never use `force_refresh=false` without a `request_recompile` before it: on its own it reads the state from before the edits.
- Editing several `.cs` files: write them with your own file tools, then call `request_recompile` once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database.
- Group independent reads into one `batch_execute` call instead of one round-trip each.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- `enter_play_mode` and a recompile reload the domain. In broker mode (the default) requests are held across the reload, so make the next call directly, with no poll loop. If the connection is refused instead (direct mode), poll the JSON-RPC `ping` method over HTTP (with native tools only, `get_reload_recovery_status`) until it answers. Never probe with `tools/list`: it returns the whole tool catalog.
- If domain reload interrupts a request, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm it.
- Additional installed skills are available under `.claude/skills/`.

## Project

- Project root: `C:\WORK\_GAME\Unity\MobileGame`
- Product name: `a`
<!-- /KitWright Unity managed project skills -->
