# TODO: automated tests for the nested-PrefabInstance fixes (2026-07-24 session)

No EditMode test suite exists yet for `ObjectGrouper`/`PrefabOverrideHydrator`.
The bugs below were all found and fixed by hand (reflection-based repro
against a sample scene, `main` vs. a feature branch adding a nested UI
prefab instance). Each one is easy to regress silently since the logic is
deep, recursive, and has no coverage — worth turning into real fixture-based
EditMode tests before touching `ObjectGrouper.cs` / `PrefabOverrideHydrator.cs`
again.

## 1. Hierarchy flattening — `ObjectGrouper.FindFatherTransformId`

**Bug:** a nested `PrefabInstance` whose own root RectTransform/Transform has
a `stripped` placeholder in the scene (written whenever something else
references that root directly) was picked up by `FindFatherTransformId`
*before* the real `PrefabInstance.m_Modification.m_TransformParent` — since
`PrefabOverrideHydrator.Hydrate` fills that placeholder's `m_Father` in from
the source prefab (always `0` there, since it's the source's own root), the
function returned `0` and the whole instance rendered as a flat root row
instead of nested under its real scene parent.

**Fix:** exclude `Stripped` docs from the Transform/RectTransform search in
`FindFatherTransformId`.

**Test to write:** a nested-instance whose root is referenced by another
component's serialized field (forcing Unity to emit the stripped placeholder)
must resolve `Depth`/`ParentFileId` to its real scene parent, not `0`.

## 2. Wrong A/M badge on a brand-new nested instance — `PrefabOverrideHydrator.SynthesizeMissingOverrides`

**Bug:** for a `PrefabInstance` that exists only on one side (`beforePi` or
`afterPi` null — the whole instance was added/removed), the method still
synthesized a virtual counterpart doc on the *other*, non-existent side,
seeded with the source prefab's default values. This made every overridden
target diff as `Modified` (default vs. override) instead of `Added`/`Removed`.

**Fix:** only synthesize the `before` counterpart when `beforePi != null`,
and the `after` counterpart when `afterPi != null`.

**Test to write:** a newly-added nested instance with at least one overridden
property must show `Status == Added` for both the instance's own group and
its overridden sub-object groups — not `Modified`.

## 3. Same wrong badge, for untouched children — `ObjectGrouper.VisitInstanceTree`

**Bug:** a synthesized child with no override of its own (e.g. an untouched
component deep inside a newly-added instance) had `Status` hardcoded to
`Unchanged` regardless of context — it can never derive Added/Removed on its
own since it has no Before/AfterDocs to diff.

**Fix:** propagate the owning instance's `Status` down through
`VisitInstanceTree`/`Visit` (and through recursion into a deeper-nested
instance) — inherit `Added`/`Removed`, but never `Modified` (an untouched
child under a merely-modified instance must stay `Unchanged`).

**Test to write:** every synthesized descendant of a newly-added instance
must read `Added`, including ones several levels deep and across a
deeper-nested-instance recursion boundary. A descendant of an *unchanged*
instance must stay `Unchanged`; a descendant of a *modified* instance whose
own target has no override must also stay `Unchanged` (not inherit
`Modified`).

## 4. Content still rendered on the wrong side — `ObjectGrouper.FillMissingOwnedDocuments`

**Bug:** even after fix #3 made the `Added`/`Removed` badge correct,
`FillMissingOwnedDocuments` still copied the same doc into *both*
`BeforeDocs` and `AfterDocs` unconditionally — so an `Added` row's "before"
diff column still rendered real content instead of being empty.

**Fix:** only add to `BeforeDocs` when `g.Status != Added`, only add to
`AfterDocs` when `g.Status != Removed`.

**Test to write:** for an `Added` group, `BeforeDocs` must be empty and
`AfterDocs` must contain the full owned-component list (and symmetrically for
`Removed`). For `Unchanged`/`Modified`, both sides must still match.

## 5. Empty row for a nested-instance-root-of-a-nested-instance — `ObjectGrouper.Visit`

**Bug:** when a synthesized node is itself the root of a *deeper* nested
instance (e.g. `Background` inside a button template turning out to be an
instance of `PFB_UIBackground`), `FillMissingOwnedDocuments` was only ever
called against the *outer* stripped placeholder's identity — which is never
a `GameObject` document, so it always yielded zero owned docs. The node's own
row rendered with nothing in either diff column even though it's a real,
newly-added object, and the misleading "removed at this revision" message
showed on the (empty) after side of a supposedly `Added` row.

**Fix:** also call `FillMissingOwnedDocuments(g, nestedGuid, nestedRootGoId)`
— the deeper instance's own real guid/root — before recursing into its
children.

**Test to write:** a synthesized node that is itself a deeper-nested-instance
root must have its own real component list (GameObject, Transform, ...)
filled in from the deeper prefab, not just its children. Also worth a
regression test on `RenderColumn`'s empty-side message (`VisualGitDiffWindow.cs`)
so a genuinely-empty side never shows a status-contradicting message (e.g.
"removed" text on the after-side of an `Added` row) — not fixed this session,
only worked around by fix #5 removing the empty-both-sides case that
triggered it for this specific scenario.

## Suggested fixture

A minimal repro needs: a scene with a `PrefabInstance` of some
`.prefab` that itself contains an untouched component, an overridden
component, and a nested instance of a *second* prefab as one of its
(possibly `m_AddedGameObjects`) children — covering all 5 cases above in one
before/after pair. Could be built directly under `ProjetTest/Assets/POC/`
following the existing `PrefabParent.prefab`/`PrefabChild.prefab` pattern
already used for the parser/diff prototypes (see `NOTES.md` step 2).
