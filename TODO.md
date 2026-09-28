# TODO

## PrefabInstance override diffing — mostly implemented

`PrefabInstance:` documents (`m_Modification.m_Modifications`, `m_RemovedComponents`,
`m_RemovedGameObjects`) now show up in the tree/table: a changed PrefabInstance gets its own node
(parented the same way a stripped placeholder's virtual position already was, via
`m_Modification.m_TransformParent` — see `UnityYamlDiffBuilder.BuildPrefabInstanceSubtree`), grouped
by the object each override targets (resolved to a readable name via `GlobalObjectId`, same
machinery a stripped placeholder's own name already used — see
`UnityYamlValueConverter.ResolvePrefabSourceObjectName`), with full write-back (Take A/B, Apply) via
new per-entry span tracking in `UnityYamlParser` (`m_Modification.m_Modifications::<targetId>::<propertyPath>`
keys) and a small anchor fix in `UnityYamlWriter.BuildTakeBEdit` for a brand-new override entry that
doesn't exist in the working tree yet.

Remaining gaps from the original write-up:

- **`m_AddedGameObjects`/`m_AddedComponents`: display-only, no write-back.** Shown as a row (resolved
  member names, via `UnityYamlDiffBuilder.AddAddedListDiffRow`) but not editable — taking an "added"
  override from one side into the other means synthesizing whole new GameObject/component/stripped-
  placeholder documents that may not exist on the other side at all, a materially different write-back
  path (`UnityYamlWriter.BuildRestoreEdits`-shaped: copy whole documents + relink owner lists, not a
  single-field splice) that deserves its own pass. `MockRow.Key` is left `null` on these rows
  specifically so `UnityYamlWriter` never touches them.
- **Deleting the last remaining override in `m_Modifications` doesn't collapse to `m_Modifications: []`.**
  Removing one entry (Take A on a row that only exists in B, or vice versa) just removes that entry's
  own lines — if it was the only one, the file's left with a bare `m_Modifications:` key and nothing
  under it (a null list) rather than Unity's own canonical empty form. Probably harmless (Unity reads
  it the same as an empty list either way) but unverified — `BuildReferenceRemovalEdits` already does
  the equivalent collapse for `m_Component`/`m_Children`; the same treatment could be added here if it
  turns out to matter.
- **Stripped-block-removal re-check not done.** The original investigation found 5 of 19 hunks were a
  whole document disappearing — `HasGameObjectChanged`'s "a stripped placeholder is never changed"
  rule (still unchanged) means those never show up either way. Whether any of those 5 are actually a
  `PrefabInstance`'s own `m_RemovedComponents`/`m_RemovedGameObjects` entry (now visible, separately,
  as of this change) rather than a meaningless stripped-placeholder disappearance hasn't been
  re-checked against a real repo — needs running this against `Assets/Project/Modules/SCN_Main.unity`
  again to confirm.
- Header rows for a modification target group don't carry their own `HeaderStateA`/`HeaderStateB` (no
  Added/Modified/Deleted label at that level, unlike a component header) — cosmetic only, every
  property row underneath still shows its own A/B status correctly.
