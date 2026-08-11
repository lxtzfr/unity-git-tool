# Planned feature: resolve GameObjects inside an imported-model (FBX) nested instance

## Problem

`ObjectGrouper`/`SourcePrefabResolver` can walk a nested `PrefabInstance`'s own
GameObject tree because a real `.prefab` is plain text YAML
(`SourcePrefabResolver.GetDocsByFileId` just reads the file and parses it).

An imported 3D model (e.g. `SM_A320.fbx`) is *also* referenced as a nested
`PrefabInstance` from a `.prefab`/scene, but the model file itself is binary —
its internal node hierarchy is never serialized as YAML, so
`UnityYamlParser.TryParse` has nothing to read. Today this means:

- `SourcePrefabResolver.ResolveRootGameObjectId(fbxGuid)` returns `null`.
- `VisitInstanceTree` (in `ObjectGrouper`) stops at the FBX instance's own row
  (e.g. "SM_A320") and never recurses into its children.
- This matches Unity's own default collapsed view, but there's no way to
  expand it in the diff tool even if the user wants to.

The `PrefabInstance.m_Modification.m_Modifications` list for such an instance
*is* readable (it lives in the referencing `.prefab`'s own YAML), but it only
carries `target.fileID` + `propertyPath` + `value` — no names, no hierarchy.
Unity auto-generates AABB/bounds-recalc overrides for nearly every mesh
renderer in an instantiated model, so building rows straight from that list
produces dozens of anonymous "Transform"/"MeshRenderer" entries with only
bounding-box floats — not the real names (`Antenne_1`, `Base`,
`cargo_door_aft`, ...). Confirmed not worth building — this is a dead end,
not the approach to take.

## The actual approach: load the model via Editor APIs

Unity exposes exactly the mapping needed to correlate an FBX's internal nodes
with the fileIDs that show up in override `target.fileID` fields:

1. `UnityEditor.AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath)` — loads every
   sub-object of the imported model (GameObjects, meshes, materials, ...) as
   real, live objects.
2. For each GameObject sub-asset, walk its real `Transform` (`.parent`,
   `.childCount` / `.GetChild(i)`) to get the actual parent/child structure
   and names — since these are loaded `UnityEngine.GameObject` instances (not
   scene instances, but real objects), `Transform` navigation works normally.
3. `UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid,
   out long localId)` — gives the exact fileID Unity uses to reference each
   sub-object. This is what needs to match `target.fileID` in a
   `PrefabInstance`'s modifications, and what `SourcePrefabResolver.ResolveDocument`
   would need to return an equivalent for.

This means a **parallel resolver**, not a JSON/YAML-shaped one:

- `SourcePrefabResolver` currently works by reading file text + parsing YAML
  (`GetDocsByFileId`, cached per guid). It has no dependency on `AssetDatabase`
  asset loading at all today.
- A new code path is needed for guids whose asset path doesn't parse as
  YAML (i.e. `UnityYamlParser.TryParse` fails, or more directly: check the
  asset's importer type / extension before even trying) — e.g.
  `SourcePrefabResolver.GetOrLoadModelHierarchy(assetPath)` that:
  - Calls `AssetDatabase.LoadAllAssetsAtPath`, filters to `GameObject`s.
  - Builds `fileId -> name`, `fileId -> parentFileId`, and
    `parentFileId -> orderedChildFileIds` maps via `TryGetGUIDAndLocalFileIdentifier`
    + real `Transform` traversal.
  - Caches the result per guid (same caching shape as `DocsCache` /
    `ParentGameObjectCache` / `RootGameObjectCache` already used for the
    YAML path).
- `ObjectGrouper.VisitInstanceTree` needs a branch: when
  `SourcePrefabResolver.ResolveRootGameObjectId(guid)` fails (YAML path),
  try the model-hierarchy path before giving up and treating the node as a
  leaf. Same for `ResolveSourceObjectName` (read the loaded GameObject's
  real name instead of an `m_Name` YAML field) and `GetOwnedDocuments`
  (there's no "document" for a loaded live object — this would need to
  either synthesize a minimal doc-like wrapper for `InspectorFieldView`, or
  the FBX-sourced rows stay structure-only with no component-section detail,
  which is probably fine given these are almost never individually
  overridden).

## Trade-offs to weigh before building

- **Real object loading, not text parsing.** This actually invokes Unity's
  asset load machinery (potentially triggering an import if not yet
  imported/cached) rather than just reading bytes off disk — slower per
  guid than the YAML path, though only paid once per session thanks to
  caching.
- **Editor-only, already true.** `VisualGitDiff` is an Editor package
  already, so this doesn't introduce a new constraint, but it does mean the
  feature is inherently tied to loading the actual referenced asset — if the
  asset is missing/broken/reimporting, this path degrades the same way
  `SourcePrefabResolver`'s existing "best-effort, current working copy"
  doc comment already warns about (see class-level XML doc).
- **No diffable field data for FBX-internal nodes.** Even with real
  names/hierarchy, there's no YAML document to seed `BeforeDocs`/`AfterDocs`
  from — clicking e.g. "Antenne_1" would show structure (icon, position in
  tree) but not a component-section breakdown the way a real `.prefab`
  child does. Worth deciding up front whether that's an acceptable
  half-feature or whether component details should be skipped/labeled for
  these rows specifically.

## Scope estimate

Medium — a new resolver code path (not a small patch), touching:
`SourcePrefabResolver` (new model-hierarchy resolution + caching),
`ObjectGrouper.VisitInstanceTree`/`ResolveSourceObjectName`/`ResolveSourceAnchorDoc`
(branch to the model path when the YAML path fails), and a decision on how
`InspectorFieldView`/`FillMissingOwnedDocuments` degrade for FBX-sourced rows
with no real document.
