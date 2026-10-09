---
name: unity-mcp-workflow
description: Efficient workflow for using Unity MCP to edit, import, compile, inspect, and test Unity projects.
version: 1.0.1
platform: claude
---
<!-- KitWright Unity managed project skills -->
<!-- KitWright Unity skill version: unity-mcp-workflow@1.0.1 -->

# Unity MCP Workflow

Use this skill when Codex or another AI agent is working in a Unity project and needs to verify code, prefabs, UI, Play Mode behavior, screenshots, scene hierarchy, console logs, domain reloads, or MCP connection issues.

## Operating Loop

1. Establish context.
   - Confirm the Unity project root and active scene.
   - Check that Unity MCP is reachable before assuming Editor state.
   - Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP.
   - If the user names an object, treat the name as a hint and verify the real Unity object path before editing.
   - Group independent reads into one `batch_execute` call instead of one round-trip each.
2. Choose the edit surface.
   - Edit source files with normal repo tools, then trigger Unity recompilation.
   - Edit scene objects through Unity APIs, mark the scene dirty, and save the scene.
   - Edit prefab assets with `PrefabUtility.LoadPrefabContents`, `PrefabUtility.SaveAsPrefabAsset`, and `PrefabUtility.UnloadPrefabContents`.
   - Edit ScriptableObject assets through `SerializedObject`, `EditorUtility.SetDirty`, and `AssetDatabase.SaveAssetIfDirty` / `SaveAssets`.
   - Never patch `.unity`, `.prefab`, or `.asset` YAML with shell text tools.
   - If the user is looking at an open scene instance, update the visible scene instance as well as the prefab asset when appropriate.
3. Execute changes.
   - Prefer one well-guarded `execute_code` batch over many fragile UI clicks.
   - Use null guards for every object, component, asset, and path lookup.
   - Return explicit missing-path/object/component messages that include the expected path and the scene or prefab searched.
   - Return concise before/after values from snippets.
   - Save only the assets or scenes intentionally modified.
   - Do not run self-healing fallback loops; if a reference, path, package, or tool is missing, report it once and stop or skip that item.
4. Validate.
   - Read back the changed objects through MCP.
   - For code or resource edits, exit Play Mode if needed and follow Recompile And Reload below: `request_recompile`, then `wait_for_compilation(force_refresh=false)` only if it reports a started import or compile. Both return the compile errors, so do not follow them with `get_compilation_errors`; inspect console errors.
   - For runtime behavior, enter Play Mode or inspect live objects when needed.
   - If MCP is unreachable, do not claim scene, prefab, asset, or runtime verification.
   - Report exactly what was verified and what still requires device, store, network, or manual validation.

## Unity Serialized Asset Safety

- Do not use shell text tools, scripts, or patches to modify `.unity`, `.prefab`, or `.asset` files. These are Unity-owned serialized assets; changing them outside Unity can corrupt file IDs, prefab overrides, references, import state, or scene dirtiness.
- Shell tools may inspect or locate serialized Unity assets, but scene, prefab, and ScriptableObject modifications must go through Unity MCP tools or Editor APIs.
- For scenes, modify live objects through Unity APIs, mark only the touched scene dirty, and save that scene.
- For prefabs, use Prefab Mode tools or `PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` / `UnloadPrefabContents`.
- For ScriptableObjects or other `.asset` files, load the asset with `AssetDatabase`, modify serialized properties through `SerializedObject` when possible, mark that asset dirty, and save only that asset.
- If Unity readback and raw file text disagree, trust Unity readback and investigate the asset path instead of hand-editing YAML.

## Tool Exposure

- With the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.
- With the default `full` profile, prefer specific MCP tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.
- If Tool Exposure is customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.

## MCP Call Pattern

If native MCP tools are not directly available, probe the local HTTP endpoint. Take its URL from the `kitwright` entry in this project's MCP client config (`.mcp.json`, `.codex/config.toml`, `.gemini/settings.json`, ...). It is not written here because it carries this editor's access token: a skill file gets committed, and the token must not be.

```bash
URL='<url of the kitwright entry>'
curl -sS -m 1 -X POST "${URL}mcp" \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"ping"}'
```

Probe with `ping`, never with `tools/list`: that returns the whole tool catalog, tens of KB per probe. Call `tools/list` once, only when you need the tool schemas.

That URL carries this project's pin and access token, and the editor rewrites it to the live
port on every start. A URL without the token is refused with 401. A pinless or stale-port URL
is answered by whichever editor happens to hold the port, which may be a different Unity project.

For multi-line `execute_code` calls over curl, generate JSON with a real encoder instead of hand-escaping C#:

```bash
node - <<'NODE'
const code = String.raw`
using UnityEngine;

public class InspectSomething
{
    public static string Run()
    {
        var obj = GameObject.Find("PracticeInGameUiRoot");
        return obj != null ? obj.name : "not found";
    }
}
`;
const payload = {
  jsonrpc: "2.0",
  id: 1,
  method: "tools/call",
  params: { name: "execute_code", arguments: { code } }
};
process.stdout.write(JSON.stringify(payload));
NODE
```

## Recommended `execute_code` Template

For non-trivial snippets, prefer `IKitWrightCommand` over the legacy `public static string Run()` template. `execute_code` auto-adds `using KitWright.Editor.Tools.Scripting;` when `IKitWrightCommand` is used, but include it explicitly in generated snippets for readability:

```csharp
using KitWright.Editor.Tools.Scripting;
using UnityEngine;

public class CommandScript : IKitWrightCommand
{
    public void Execute(ExecutionContext ctx)
    {
        var root = GameObject.Find("PracticeInGameUiRoot");
        if (root == null)
        {
            ctx.LogWarning("PracticeInGameUiRoot not found");
            ctx.ReturnValue = "missing root";
            return;
        }

        ctx.RegisterObjectModification(root);
        ctx.Log("Found {0}, active={1}", root.name, root.activeInHierarchy);
        ctx.ReturnValue = new
        {
            name = root.name,
            active = root.activeInHierarchy
        };
    }
}
```

Use `ctx.RegisterObjectCreation(obj)`, `ctx.RegisterObjectModification(obj)`, and `ctx.DestroyObject(obj)` instead of direct Undo calls when possible. Use `ctx.Log`, `ctx.LogWarning`, and `ctx.LogError` for output returned in the MCP response without polluting the Unity Console.

## Unity C# Patterns

Add explicit `using` directives or use fully qualified types for project code. `execute_code` does not auto-inject project namespaces by default:

```csharp
var root = UnityEngine.GameObject.Find("PracticeInGameUiRoot");
var rect = root.GetComponent<UnityEngine.RectTransform>();
```

Use Unity null semantics for `UnityEngine.Object` references:

```csharp
if (image == null)
{
    return "Image missing";
}
```

Do not use `??=` to lazily resolve or rebind `UnityEngine.Object` references. Unity's destroyed or unbound serialized references can be fake-null: `field == null` returns true through Unity's overloaded operator, while C# `??=` can still treat the managed wrapper as non-null and skip the fallback assignment. Use an explicit Unity-null check instead:

```csharp
if (_hud == null)
{
    _hud = GetComponentInChildren<MyHud>(true);
}
```

For prefab edits:

```csharp
var path = "Assets/MyGame/UI/Prefabs/PF_PracticeInGameUiRoot.prefab";
var prefab = UnityEditor.PrefabUtility.LoadPrefabContents(path);
try
{
    var target = prefab.transform.Find("SafeArea/SwingCancelZone");
    if (target == null)
    {
        return "SwingCancelZone not found in prefab";
    }

    var rect = target.GetComponent<UnityEngine.RectTransform>();
    var before = rect.anchoredPosition;
    rect.anchoredPosition = new UnityEngine.Vector2(-76f, 448f);

    UnityEditor.EditorUtility.SetDirty(rect);
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefab, path);
    UnityEditor.AssetDatabase.SaveAssets();
    return "Prefab saved: pos " + before + " -> " + rect.anchoredPosition;
}
finally
{
    UnityEditor.PrefabUtility.UnloadPrefabContents(prefab);
}
```

For scene edits:

```csharp
var obj = UnityEngine.GameObject.Find("PracticeInGameUiRoot/SafeArea/SwingCancelZone");
if (obj == null)
{
    return "Scene object not found";
}

var rect = obj.GetComponent<UnityEngine.RectTransform>();
var before = rect.sizeDelta;
UnityEditor.Undo.RecordObject(rect, "Update cancel zone");
rect.sizeDelta = new UnityEngine.Vector2(220f, 116f);
UnityEditor.EditorUtility.SetDirty(rect);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(obj.scene);
return "Scene dirtied: size " + before + " -> " + rect.sizeDelta + " (call save_scene to write it)";
```

## Recompile And Reload

After external C# or asset file edits:

1. If Unity is in Play Mode, call `exit_play_mode` first — `request_recompile` is rejected during play because Unity does not run script compilation or domain reloads while playing.
2. Call `request_recompile`, always first: it is the call that imports the edits. Unless it reports that Unity started importing or recompiling, its answer already carries the compile result (`COMPILATION_FAILED` with the issues, or no errors).
3. Only if it reported that Unity started importing or recompiling, call `wait_for_compilation` with `force_refresh=false`: the refresh already ran, and its answer carries the compile errors too. Never use `force_refresh=false` without step 2 before it — on its own it reads the state from before the edits.
4. Do not follow step 2 or 3 with `get_compilation_errors`. Read `get_console_logs` errors before continuing.
5. If a domain reload drops or interrupts the request, call `get_reload_recovery_status` when available, re-scan the MCP endpoint if needed, then continue from step 3.

Editing several `.cs` files: write all of them with your own file tools, then run this sequence once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database, so writing N files through them starts up to N imports and compiles.

Do not treat a disconnected, interrupted, or domain-reload-recovered request as a successful compile or edit. It only means the state is unknown until compilation checks and MCP readback confirm the final values.

`enter_play_mode` and a recompile both reload the domain. In broker mode (the default) the broker holds requests across the reload and delivers them once Unity is back, so make the next call directly — no poll loop is needed (over curl, give that call a timeout of a minute rather than `-m 1`). A call answered with "Unity is recompiling scripts, so this tool did not run" is simply repeated. If the connection is refused instead (direct mode), poll `ping` as in MCP Call Pattern (with native tools only, `get_reload_recovery_status`) until it answers.

## Verification Checklist

Use readback snippets that print exact values, not only `success`:

```csharp
var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
UnityEngine.Transform target = null;
for (int i = 0; i < all.Length; i++)
{
    if (all[i].name == "SwingCancelZone")
    {
        target = all[i];
        break;
    }
}

if (target == null)
{
    return "SwingCancelZone not found";
}

var rect = target.GetComponent<UnityEngine.RectTransform>();
return "path=" + target.name + "; pos=" + rect.anchoredPosition + "; size=" + rect.sizeDelta;
```

For UI work, verify prefab or scene hierarchy, sprite references, anchors, sorting order, active state, text fit, and button listeners. A populated `Content` hierarchy does not prove the user can see the UI.

For gameplay or network work, verify object identity, ownership, live instance existence, transform values, animation state, visibility, and whether client-side filters are discarding valid data.

## Failure Handling

- If MCP is unreachable, say so and fall back only to safe filesystem inspection or code edits. Do not claim scene, prefab, or runtime verification without Unity readback.
- If an object lookup fails, inspect hierarchy and prefab contents instead of inventing a path.
- If multiple matching objects exist, print their paths and choose the one matching the user-visible UI or current scene.
- If a reference, package, tool, or path is missing, return one clear error and stop or skip that item. Do not loop through guessed fallback paths, create replacement objects silently, or report success after a best-effort fallback.
- If compile errors appear after a change, fix them before Play Mode validation.
- When Unity and text files disagree for serialized scene or prefab state, trust Unity readback and inspect the asset path.

## Metadata

- Original skill id: `unity-mcp-workflow`
- Skill version: `1.0.1`
- Source repository: `https://github.com/kitwright/unity-mcp`
