using System.Collections.Generic;
using System.Linq;

namespace UnityGitTool
{
    /// <summary>
    /// Applies changes back onto <paramref name="baseText"/> (the file actually being patched — always
    /// the working tree, whether that's a real merge resolution or a diff-mode rollback, see
    /// <see cref="UnityGitToolWindow.ApplyResolution"/>/<see cref="UnityGitToolWindow.RollbackField"/>).
    /// Three kinds of edit, all expressed as the same "remove this line range, insert these lines
    /// instead" <see cref="LineEdit"/> so they can be collected up front and applied in one
    /// back-to-front pass (removing/inserting earlier in the file would otherwise invalidate line
    /// numbers already recorded for a later edit):
    /// <list type="bullet">
    /// <item>Field-level: splices in the other side's raw lines for every field resolved to it,
    /// verbatim — never re-serializes a display value (<see cref="UnityYamlValueConverter.ToDisplayValue"/>
    /// is lossy, a resolved reference becomes a name, an array becomes a fresh instance). A row this
    /// can't handle safely (<see cref="MockResolution.Manual"/>/<see cref="MockResolution.Unresolved"/>,
    /// or missing span info) is left exactly as-is in base and reported back instead of guessed at.</item>
    /// <item>Deletion: removes a whole GameObject's or component's document outright (<see cref="MockNode.MarkedForDeletion"/>/
    /// <see cref="MockRow.MarkedForDeletion"/>) — cascading to a deleted GameObject's own components
    /// and every descendant GameObject, and cleaning up the one line in a surviving parent's
    /// <c>m_Component</c>/<c>m_Children</c> list that pointed at whatever got removed.</item>
    /// <item>Restore (<see cref="BuildRestoreEdits"/>): the inverse of deletion — brings back a
    /// document missing from base by copying it verbatim from the other side and re-linking it into
    /// whichever owner (GameObject's <c>m_Component</c>, parent's <c>m_Children</c>) still exists.
    /// Used by a diff-mode rollback on a component/GameObject that doesn't exist in the working tree
    /// at all, as opposed to the field-level case above (which only ever touches a document that's
    /// already there).</item>
    /// </list>
    /// </summary>
    internal static class UnityYamlWriter
    {
        /// <summary>A single "remove <see cref="RemoveCount"/> lines starting at <see cref="Start"/>,
        /// insert <see cref="Insert"/> there instead" operation — <see cref="Insert"/> empty means a
        /// pure deletion, <see cref="RemoveCount"/> 0 means a pure insertion.</summary>
        private readonly struct LineEdit
        {
            public readonly int Start;
            public readonly int RemoveCount;
            public readonly List<string> Insert;
            public LineEdit(int start, int removeCount, List<string> insert) { Start = start; RemoveCount = removeCount; Insert = insert; }
        }

        public static (string Text, List<string> Skipped) Apply(
            string baseText,
            List<GitYamlDocument> baseDocs,
            string otherText,
            List<GitYamlDocument> otherDocs,
            List<MockRow> rows,
            IEnumerable<long> deletedGameObjectIds,
            IEnumerable<long> deletedComponentIds,
            IEnumerable<long> restoredIds = null)
        {
            var baseById = UnityYamlDiffBuilder.IndexByFileId(baseDocs);
            var otherById = UnityYamlDiffBuilder.IndexByFileId(otherDocs);
            var baseLines = baseText.Replace("\r\n", "\n").Split('\n').ToList();
            var otherLines = otherText.Replace("\r\n", "\n").Split('\n').ToList();
            var skipped = new List<string>();

            var (toDelete, componentOwners, childParents) = CollectDeletionCascade(deletedGameObjectIds, deletedComponentIds, baseById);

            var edits = new List<LineEdit>();
            edits.AddRange(BuildDeletionEdits(toDelete, baseById, skipped));
            edits.AddRange(BuildReferenceCleanupEdits(toDelete, componentOwners, childParents, baseById, baseLines, skipped));
            edits.AddRange(BuildDanglingReferenceCleanupEdits(toDelete, baseById, baseLines));
            if (restoredIds != null)
                edits.AddRange(BuildRestoreEdits(new HashSet<long>(restoredIds), baseById, otherById, otherLines, baseLines, skipped));
            edits.AddRange(BuildFieldEdits(rows, toDelete, baseById, baseLines, otherById, otherLines, skipped));

            // Descending by start: an edit only ever touches lines at or after its own recorded start,
            // so applying furthest-down-the-file first never shifts an earlier edit's still-pending
            // position — same invariant a single splice already relied on, just now shared by every
            // edit kind at once so they can't invalidate each other either. Ties matter here in one
            // specific case: a deleted document's HeaderLine can equal a surviving document's
            // BodyEndLine (they're simply adjacent in the file) — a field-insert anchored at that
            // BodyEndLine must apply AFTER the deletion at that same index, or it inserts into a
            // range about to be removed out from under it. OrderByDescending is a stable sort (ties
            // keep their original relative order), and deletion edits are added to `edits` before
            // field edits above, so that ordering falls out for free — don't reorder those AddRange
            // calls without re-checking this.
            foreach (var edit in edits.OrderByDescending(e => e.Start))
            {
                baseLines.RemoveRange(edit.Start, edit.RemoveCount);
                if (edit.Insert.Count > 0) baseLines.InsertRange(edit.Start, edit.Insert);
            }

            return (string.Join("\n", baseLines), skipped);
        }

        /// <summary>Starting from the explicitly deleted GameObjects/components, walks out to
        /// everything that has to disappear with them: a deleted GameObject's own components (its
        /// Transform included — <see cref="UnityYamlDiffBuilder.CollectComponentIds"/> returns it like
        /// any other) and every descendant GameObject, recursively. Also records, for each thing being
        /// removed, which SURVIVING document references it and needs that one reference line cleaned
        /// up: <paramref name="componentOwners"/> maps a deleted component id to its owning GameObject
        /// id (skipped if that owner is itself being deleted — its whole document disappears anyway,
        /// list entry included); <paramref name="childParents"/> maps a deleted GameObject's own
        /// Transform id to its parent GameObject id, same "skip if parent's deleted too" rule.</summary>
        private static (HashSet<long> ToDelete, Dictionary<long, long> ComponentOwners, Dictionary<long, long> ChildParents)
            CollectDeletionCascade(IEnumerable<long> deletedGameObjectIds, IEnumerable<long> deletedComponentIds, Dictionary<long, GitYamlDocument> baseById)
        {
            var toDelete = new HashSet<long>();
            var componentOwners = new Dictionary<long, long>();
            var childParents = new Dictionary<long, long>();

            // Every GameObject's parent, computed once, so a deleted GameObject's own transform id can
            // be looked back up to its parent for the m_Children cleanup below.
            var parentOf = new Dictionary<long, long>();
            foreach (var doc in baseById.Values.Where(d => d.TypeName == "GameObject"))
                parentOf[doc.FileId] = UnityYamlDiffBuilder.GetParentGameObjectId(doc.FileId, baseById, baseById);

            var childrenOf = new Dictionary<long, List<long>>();
            foreach (var (childId, parentId) in parentOf)
            {
                if (parentId == 0) continue;
                if (!childrenOf.TryGetValue(parentId, out var list)) childrenOf[parentId] = list = new List<long>();
                list.Add(childId);
            }

            void DeleteComponent(long componentId, long ownerId)
            {
                if (!toDelete.Add(componentId)) return;
                if (!toDelete.Contains(ownerId)) componentOwners[componentId] = ownerId;
            }

            void DeleteGameObject(long goId)
            {
                if (!toDelete.Add(goId)) return;

                if (baseById.TryGetValue(goId, out var go))
                    foreach (var compId in UnityYamlDiffBuilder.CollectComponentIds(go))
                        DeleteComponent(compId, goId);

                var parentId = parentOf.GetValueOrDefault(goId, 0);
                if (parentId != 0 && !toDelete.Contains(parentId) &&
                    UnityYamlDiffBuilder.FindTransformId(goId, baseById) is { } transformId)
                    childParents[transformId] = parentId;

                if (childrenOf.TryGetValue(goId, out var kids))
                    foreach (var childId in kids)
                        DeleteGameObject(childId);
            }

            foreach (var id in deletedGameObjectIds) DeleteGameObject(id);
            foreach (var id in deletedComponentIds)
            {
                if (!baseById.TryGetValue(id, out var comp)) continue;
                var ownerId = comp.Fields.TryGetValue("m_GameObject", out var raw) && raw is Dictionary<string, object> map &&
                    map.TryGetValue("fileID", out var rawId) && rawId is string s && long.TryParse(s, out var owner) ? owner : 0;
                DeleteComponent(id, ownerId);
            }

            return (toDelete, componentOwners, childParents);
        }

        private static IEnumerable<LineEdit> BuildDeletionEdits(HashSet<long> toDelete, Dictionary<long, GitYamlDocument> baseById, List<string> skipped)
        {
            foreach (var id in toDelete)
            {
                if (!baseById.TryGetValue(id, out var doc))
                {
                    skipped.Add($"object #{id}: marked for deletion but not found in the working tree — not applied");
                    continue;
                }
                yield return new LineEdit(doc.HeaderLine, doc.BodyEndLine - doc.HeaderLine, new List<string>());
            }
        }

        /// <summary>The `- component: {fileID: X}` (in a surviving GameObject's m_Component) or
        /// `- {fileID: X}` (in a surviving parent's Transform.m_Children) lines pointing at whatever
        /// just got deleted — found by exact-text search within that field's already-known span
        /// rather than needing per-list-item span tracking, safe because Unity always emits each of
        /// these as one self-contained line. Grouped by owner+field first (<paramref name="componentOwners"/>/
        /// <paramref name="childParents"/> can each map several deleted ids onto the SAME owner, e.g.
        /// two sibling GameObjects both deleted at once) so a field losing ALL its entries in this one
        /// Apply collapses to Unity's canonical empty-list form exactly once — computing that per
        /// target id independently, against the same unedited span, would have every one of them see
        /// "at least one other item survives" even when none actually do.</summary>
        private static IEnumerable<LineEdit> BuildReferenceCleanupEdits(
            HashSet<long> toDelete, Dictionary<long, long> componentOwners, Dictionary<long, long> childParents,
            Dictionary<long, GitYamlDocument> baseById, List<string> baseLines, List<string> skipped)
        {
            var targetsByField = new Dictionary<(long OwnerDocId, string FieldKey), List<long>>();

            void AddTarget(long ownerDocId, string fieldKey, long targetId)
            {
                var key = (ownerDocId, fieldKey);
                if (!targetsByField.TryGetValue(key, out var list)) targetsByField[key] = list = new List<long>();
                list.Add(targetId);
            }

            foreach (var (componentId, ownerId) in componentOwners)
                AddTarget(ownerId, "m_Component", componentId);

            foreach (var (transformId, parentId) in childParents)
                if (UnityYamlDiffBuilder.FindTransformId(parentId, baseById) is { } parentTransformId)
                    AddTarget(parentTransformId, "m_Children", transformId);
                else
                    skipped.Add($"GameObject: deleted, but its parent #{parentId}'s own Transform wasn't found — file may need a manual cleanup pass");

            foreach (var ((ownerDocId, fieldKey), targetIds) in targetsByField)
                foreach (var edit in BuildReferenceRemovalEdits(baseById, ownerDocId, fieldKey, targetIds, baseLines, skipped))
                    yield return edit;
        }

        /// <summary>Removes every line in <paramref name="fieldKey"/>'s list pointing at one of
        /// <paramref name="targetIds"/>. If that empties the list entirely, collapses the whole field
        /// down to Unity's own canonical empty-list form (<c>m_Component: []</c>) instead of leaving a
        /// bare `m_Component:` key with nothing under it — technically still valid YAML (null), but
        /// not a shape Unity's own serializer would ever produce, and not worth risking on an import
        /// path this tool hasn't verified against.</summary>
        private static IEnumerable<LineEdit> BuildReferenceRemovalEdits(
            Dictionary<long, GitYamlDocument> baseById, long ownerDocId, string fieldKey, List<long> targetIds, List<string> baseLines, List<string> skipped)
        {
            if (!baseById.TryGetValue(ownerDocId, out var ownerDoc) || !ownerDoc.FieldSpans.TryGetValue(fieldKey, out var span))
            {
                foreach (var id in targetIds)
                    skipped.Add($"#{id}: deleted, but owner #{ownerDocId}'s {fieldKey} list wasn't found — file may need a manual cleanup pass");
                yield break;
            }

            var needles = targetIds.Select(id => $"{{fileID: {id}}}").ToList();
            var targetLines = new List<int>();
            var remainingItemCount = 0;
            for (var i = span.Start; i < span.End; i++)
            {
                var trimmed = baseLines[i].TrimStart();
                if (!trimmed.StartsWith("- ")) continue; // the "m_Component:"/"m_Children:" key line itself, not an item
                if (needles.Any(n => baseLines[i].Contains(n))) targetLines.Add(i);
                else remainingItemCount++;
            }

            if (targetLines.Count < targetIds.Count)
                skipped.Add($"{targetIds.Count - targetLines.Count} of {targetIds.Count} deleted id(s) not found in owner #{ownerDocId}'s {fieldKey} list — file may need a manual cleanup pass");
            if (targetLines.Count == 0) yield break;

            if (remainingItemCount > 0)
            {
                foreach (var line in targetLines) yield return new LineEdit(line, 1, new List<string>());
                yield break;
            }

            // Every item is going — collapse the whole field (key line through the last item) to one
            // canonical empty-list line, keeping the key line's own original indentation.
            var keyLine = baseLines[span.Start];
            var indent = keyLine.Substring(0, keyLine.Length - keyLine.TrimStart().Length);
            yield return new LineEdit(span.Start, span.End - span.Start, new List<string> { $"{indent}{fieldKey}: []" });
        }

        /// <summary>Matches a local `{fileID: N}` reference — same as <see cref="LocalFileIdPattern"/>
        /// (a plain scan of the raw text rather than a parsed value, so it catches a reference no
        /// matter how deep it sits — a scalar field, or an entry inside a list of references — without
        /// needing to walk the parsed structure).</summary>
        private static readonly System.Text.RegularExpressions.Regex FileIdValuePattern = new(@"fileID:\s*(-?\d+)");

        /// <summary><see cref="BuildReferenceCleanupEdits"/> only ever cleans up the two relationships
        /// <see cref="CollectDeletionCascade"/> explicitly tracks — a component's owning GameObject's
        /// `m_Component`, a child GameObject's parent's `m_Children`. Anything ELSE in the file that
        /// happens to reference one of <paramref name="toDelete"/> — a script field on some unrelated
        /// component (e.g. a `_canvas: {fileID: X}` pointing at the GameObject just deleted) — was
        /// never touched, left silently dangling until Unity's own loader found it later and either
        /// logged "Broken text PPtr ... doesn't exist!" or, for a duplicate list entry, "has multiple
        /// entries of the same Object component. Removing it!". This is the general sweep that catches
        /// those too: every surviving document's own fields (skipping <c>m_Component</c>/<c>m_Children</c>,
        /// already handled precisely above — scanning them again here would double-edit the same
        /// lines) are scanned for a local `{fileID: N}` with N in <paramref name="toDelete"/>, and that
        /// N is zeroed out to Unity's own "no reference" sentinel — valid on both a scalar reference
        /// field and a list entry (an array with a null slot is normal, unlike a bare fileID pointing
        /// at nothing), so this never needs to know which shape a given field is.</summary>
        private static IEnumerable<LineEdit> BuildDanglingReferenceCleanupEdits(
            HashSet<long> toDelete, Dictionary<long, GitYamlDocument> baseById, List<string> baseLines)
        {
            if (toDelete.Count == 0) yield break;

            foreach (var doc in baseById.Values)
            {
                if (doc.TypeName == null || toDelete.Contains(doc.FileId)) continue; // a document being deleted itself needs no field-level cleanup

                foreach (var (key, span) in doc.FieldSpans)
                {
                    if (key is "m_Component" or "m_Children") continue; // already handled precisely by BuildReferenceCleanupEdits

                    for (var i = span.Start; i < span.End; i++)
                    {
                        var line = baseLines[i];
                        if (line.Contains("guid:")) continue; // external asset reference — its fileID is a sub-asset index, not a local document id

                        var replaced = FileIdValuePattern.Replace(line, m =>
                            long.TryParse(m.Groups[1].Value, out var id) && toDelete.Contains(id) ? "fileID: 0" : m.Value);
                        if (replaced != line) yield return new LineEdit(i, 1, new List<string> { replaced });
                    }
                }
            }
        }

        /// <summary>The inverse of <see cref="CollectDeletionCascade"/>/<see cref="BuildReferenceCleanupEdits"/>
        /// — brings back a document (GameObject or component) that doesn't exist in <paramref name="baseById"/>
        /// yet, copying its raw block verbatim from <paramref name="otherLines"/> and re-linking it into
        /// whichever surviving owner it belongs to (a component into its GameObject's <c>m_Component</c>,
        /// a GameObject into its parent's <c>m_Children</c>). Deliberately does NOT auto-cascade into
        /// bringing back a component's siblings or a GameObject's descendants — <paramref name="toRestore"/>
        /// is exactly the set of ids the caller asked for (see UnityGitToolWindow.Table.cs's component vs
        /// GameObject rollback buttons, which build that set differently: one component, or a GameObject
        /// plus every one of its own components). An id whose owner is ALSO missing from the working tree
        /// and not itself in <paramref name="toRestore"/> is reported rather than left dangling or
        /// silently cascaded further — restoring an entire orphaned subtree automatically risks pulling in
        /// far more than the user actually clicked. The one exception is nested-prefab bookkeeping (a
        /// stripped placeholder or its anchoring PrefabInstance document) that a restored document's own
        /// lines point at — see <see cref="CollectStructuralDependencies"/> for why that specific case IS
        /// auto-pulled in: the user has no tree node to click-restore it from, and skipping it would just
        /// leave a dangling reference ("Broken text PPtr ... doesn't exist!" on next scene load) inside
        /// the very document this method just wrote back.</summary>
        private static IEnumerable<LineEdit> BuildRestoreEdits(
            HashSet<long> toRestore, Dictionary<long, GitYamlDocument> baseById, Dictionary<long, GitYamlDocument> otherById,
            List<string> otherLines, List<string> baseLines, List<string> skipped)
        {
            var missing = toRestore.Where(id => !baseById.ContainsKey(id)).ToList();
            if (missing.Count == 0) yield break;

            // A restored document's own raw lines can reference nested-prefab bookkeeping (a stripped
            // placeholder, or the PrefabInstance anchoring one) that isn't itself in `missing` — e.g.
            // restoring a GameObject whose Transform.m_Children lists a stripped placeholder for a
            // PrefabInstance's root, or whose MonoBehaviour fields point at further stripped
            // placeholders inside that same instance. See CollectStructuralDependencies for why these
            // are always safe to pull in automatically (unlike real content, which stays reported
            // instead — same call).
            var allRestored = missing.Concat(CollectStructuralDependencies(missing, baseById, otherById, otherLines, skipped)).ToList();

            // Each missing document's raw block, verbatim — re-inserted near where it used to sit (see
            // BuildRestoreInsertionEdits below) rather than always at the end of the file. Unity itself
            // doesn't care about document order (only fileID references, which the owner-list fixups
            // below take care of), but git and reviewers do: dumping everything at end-of-file turns a
            // one-GameObject rollback into a diff that looks like most of the scene changed.
            var blockById = new Dictionary<long, List<string>>();
            var ownerFixups = new Dictionary<(long OwnerDocId, string FieldKey), List<long>>();

            void AddFixup(long ownerDocId, string fieldKey, long targetId)
            {
                var key = (ownerDocId, fieldKey);
                if (!ownerFixups.TryGetValue(key, out var list)) ownerFixups[key] = list = new List<long>();
                list.Add(targetId);
            }

            foreach (var id in allRestored)
            {
                if (!otherById.TryGetValue(id, out var doc))
                {
                    skipped.Add($"object #{id}: marked for restore but not found on the other side — not applied");
                    continue;
                }
                blockById[id] = otherLines.GetRange(doc.HeaderLine, doc.BodyEndLine - doc.HeaderLine);

                if (doc.TypeName == "GameObject") continue; // linked into its parent below, once every restored id's own doc is known
                // A stripped placeholder (or the PrefabInstance anchoring one) is never listed in any
                // GameObject's m_Component — it's found purely by fileID from whichever document already
                // references it (already copied verbatim above), so it needs no owner-list fixup at all.
                if (doc.Stripped || doc.TypeName == "PrefabInstance") continue;

                var ownerId = doc.Fields.TryGetValue("m_GameObject", out var raw) && raw is Dictionary<string, object> map &&
                    map.TryGetValue("fileID", out var rawId) && rawId is string s && long.TryParse(s, out var owner) ? owner : 0;
                if (ownerId == 0) { skipped.Add($"{doc.TypeName} #{id}: couldn't resolve its owning GameObject — not linked back in"); continue; }
                // A stripped owner (Unity's PrefabInstance.m_AddedComponents case — see
                // IsPrefabBoundDependency) has no m_Component list to append into at all; Unity finds
                // this component via the owning PrefabInstance's own m_AddedComponents entry instead,
                // already copied verbatim once that PrefabInstance is restored — nothing to fix up here.
                if (otherById.TryGetValue(ownerId, out var ownerDoc) && ownerDoc.Stripped) continue;
                // The owner is being restored in this same batch — its copied m_Component list (raw,
                // from `other`) already references this component verbatim, nothing extra to add.
                if (missing.Contains(ownerId)) continue;
                if (!baseById.ContainsKey(ownerId))
                {
                    skipped.Add($"{doc.TypeName} #{id}: its owning GameObject #{ownerId} doesn't exist in the working tree either — restore that GameObject too");
                    continue;
                }
                AddFixup(ownerId, "m_Component", id);
            }

            foreach (var id in missing)
            {
                if (!otherById.TryGetValue(id, out var doc) || doc.TypeName != "GameObject") continue;

                var parentId = UnityYamlDiffBuilder.GetParentGameObjectId(id, otherById, otherById);
                if (parentId == 0) continue; // scene root — nothing to link into
                if (missing.Contains(parentId)) continue; // parent restored in this same batch, already correct

                if (!baseById.ContainsKey(parentId))
                {
                    skipped.Add($"GameObject #{id}: its parent #{parentId} doesn't exist in the working tree either — restore that GameObject too");
                    continue;
                }
                if (UnityYamlDiffBuilder.FindTransformId(parentId, baseById) is not { } parentTransformId)
                {
                    skipped.Add($"GameObject #{id}: parent #{parentId}'s own Transform wasn't found — file may need a manual cleanup pass");
                    continue;
                }
                if (UnityYamlDiffBuilder.FindTransformId(id, otherById) is not { } childTransformId)
                {
                    skipped.Add($"GameObject #{id}: its own Transform wasn't found on the other side — not linked back in");
                    continue;
                }
                AddFixup(parentTransformId, "m_Children", childTransformId);
            }

            foreach (var ((ownerDocId, fieldKey), targetIds) in ownerFixups)
                foreach (var edit in BuildReferenceAddEdits(baseById, ownerDocId, fieldKey, targetIds, baseLines, skipped))
                    yield return edit;

            foreach (var edit in BuildRestoreInsertionEdits(blockById, baseById, otherById, baseLines.Count))
                yield return edit;
        }

        /// <summary>Places each restored document's raw block back near where it used to sit in
        /// <paramref name="otherById"/>'s own file order, instead of always at end-of-file: walks that
        /// order to find the nearest SURVIVING neighbor (a document present in <paramref name="baseById"/>)
        /// immediately before it, and inserts right after that neighbor's own content
        /// (<see cref="GitYamlDocument.BodyEndLine"/>) — same place Unity itself would have written it
        /// back, had it not been deleted. Falls back to inserting right before the nearest surviving
        /// neighbor AFTER it when nothing survives before it (e.g. it was the very first document in the
        /// file), and to <paramref name="endOfFileLine"/> only when no surviving document exists on
        /// either side at all. Multiple restored documents that land on the very same anchor point are
        /// grouped into one insert, in their own original relative order, so two docs that used to be
        /// adjacent still come back adjacent instead of merely both "somewhere near" the anchor.</summary>
        private static IEnumerable<LineEdit> BuildRestoreInsertionEdits(
            Dictionary<long, List<string>> blockById, Dictionary<long, GitYamlDocument> baseById,
            Dictionary<long, GitYamlDocument> otherById, int endOfFileLine)
        {
            if (blockById.Count == 0) yield break;

            var groups = new Dictionary<int, List<long>>();
            foreach (var id in blockById.Keys)
            {
                var doc = otherById[id];
                GitYamlDocument precedingSurvivor = null;
                GitYamlDocument followingSurvivor = null;
                foreach (var candidate in otherById.Values)
                {
                    if (!baseById.ContainsKey(candidate.FileId)) continue;
                    if (candidate.HeaderLine < doc.HeaderLine)
                    {
                        if (precedingSurvivor == null || candidate.HeaderLine > precedingSurvivor.HeaderLine) precedingSurvivor = candidate;
                    }
                    else if (candidate.HeaderLine > doc.HeaderLine)
                    {
                        if (followingSurvivor == null || candidate.HeaderLine < followingSurvivor.HeaderLine) followingSurvivor = candidate;
                    }
                }

                var anchorLine = precedingSurvivor != null ? baseById[precedingSurvivor.FileId].BodyEndLine
                    : followingSurvivor != null ? baseById[followingSurvivor.FileId].HeaderLine
                    : endOfFileLine;

                if (!groups.TryGetValue(anchorLine, out var ids)) groups[anchorLine] = ids = new List<long>();
                ids.Add(id);
            }

            foreach (var (anchorLine, ids) in groups)
            {
                var lines = new List<string>();
                foreach (var id in ids.OrderBy(id => otherById[id].HeaderLine))
                    lines.AddRange(blockById[id]);
                yield return new LineEdit(anchorLine, 0, lines);
            }
        }

        /// <summary>Nested-prefab bookkeeping (a stripped placeholder — see <see cref="GitYamlDocument.Stripped"/>
        /// — or the PrefabInstance document anchoring one) that something in <paramref name="missing"/>
        /// references but that isn't itself being restored. Neither ever gets its own tree node (a
        /// stripped document is never shown as a GameObject — see <see cref="UnityYamlDiffBuilder"/> —
        /// and a PrefabInstance has no changed-GameObject identity of its own either), so the user has no
        /// way to click-restore them separately; leaving them out just recreates the exact "Broken text
        /// PPtr ... doesn't exist!" dangling reference this whole path exists to avoid (the same failure
        /// <see cref="FindDanglingLocalReference"/> guards a single field against for <see cref="BuildTakeBEdit"/>,
        /// which this whole-document copy never went through).
        ///
        /// Two directions feed this closure, because the ownership between a PrefabInstance and its
        /// stripped placeholders runs backwards from every other parent/child relationship in this file:
        /// <list type="bullet">
        /// <item>Forward — a restored document's own raw lines pointing AT a stripped placeholder or
        /// PrefabInstance (e.g. a Transform's <c>m_Children</c>, a script's own field). Found by
        /// <see cref="FindLocalReferences"/>, same as before.</item>
        /// <item>Backward — once a PrefabInstance is included, EVERY stripped placeholder that declares
        /// itself part of it via its own <c>m_PrefabInstance</c> field, even ones nothing we're restoring
        /// happens to reference by name. A PrefabInstance document never lists its own overridden objects
        /// (unlike a GameObject's <c>m_Component</c>) — the link only exists on each placeholder itself —
        /// so restoring the PrefabInstance without ALL of them is exactly what Unity's loader logs as a
        /// "broken PrefabInstance" and silently deletes, rather than erroring: the rollback bug this
        /// second direction fixes.</item>
        /// </list>
        /// Both are always safe to pull in automatically: a stripped document carries no real content of
        /// its own (only bookkeeping fields), and a PrefabInstance document is a self-contained blob of
        /// overrides with nothing else to accidentally over-restore. Any OTHER dangling local reference
        /// found this way — real content the user didn't ask for — is reported instead of auto-pulled in
        /// or left dangling, same as every other guard in this class.</summary>
        private static List<long> CollectStructuralDependencies(
            List<long> missing, Dictionary<long, GitYamlDocument> baseById, Dictionary<long, GitYamlDocument> otherById,
            List<string> otherLines, List<string> skipped)
        {
            // Every stripped placeholder in the WHOLE other-side file, grouped by the PrefabInstance it
            // declares itself part of — built once up front so the backward direction above is a plain
            // lookup instead of a full otherById scan per PrefabInstance encountered.
            var strippedByInstance = new Dictionary<long, List<long>>();
            foreach (var doc in otherById.Values)
            {
                if (!doc.Stripped || !TryGetReferenceFileId(doc, "m_PrefabInstance", out var ownerInstanceId)) continue;
                if (!strippedByInstance.TryGetValue(ownerInstanceId, out var list)) strippedByInstance[ownerInstanceId] = list = new List<long>();
                list.Add(doc.FileId);
            }

            var included = new HashSet<long>(missing);
            var added = new List<long>();
            var queue = new Queue<long>(missing);

            void Include(long id)
            {
                if (id == 0 || baseById.ContainsKey(id) || !included.Add(id)) return;
                added.Add(id);
                queue.Enqueue(id);
            }

            var originalMissing = new HashSet<long>(missing);

            // A GameObject pulled in only by this closure — never explicitly requested, so the caller
            // (RollbackHeader) never listed its own components the way it does for a top-level restore
            // click — needs its own real content cascaded WITH it: its own components, and its own real
            // child GameObjects, recursively. Otherwise its copied m_Component/m_Children list would
            // just dangle one level deeper, the exact failure this whole method exists to prevent —
            // symmetric with CollectDeletionCascade's own unconditional recursive walk on the way out.
            // Never applied to `originalMissing` itself: a top-level restore click deliberately does NOT
            // cascade into child GameObjects (see this file's BuildRestoreEdits doc comment) — only
            // content with no independent tree node of its own gets this treatment.
            void IncludeOwnedContent(GitYamlDocument gameObject)
            {
                foreach (var compId in UnityYamlDiffBuilder.CollectComponentIds(gameObject)) Include(compId);

                if (UnityYamlDiffBuilder.FindTransformId(gameObject.FileId, otherById) is not { } transformId ||
                    !otherById.TryGetValue(transformId, out var transform) ||
                    !transform.Fields.TryGetValue("m_Children", out var childrenRaw) || childrenRaw is not List<object> children) return;

                foreach (var entry in children)
                {
                    if (entry is not Dictionary<string, object> childRef || !TryGetFileId(childRef, out var childTransformId)) continue;
                    if (otherById.TryGetValue(childTransformId, out var childTransform) &&
                        TryGetReferenceFileId(childTransform, "m_GameObject", out var childGoId))
                        Include(childGoId);
                }
            }

            while (queue.Count > 0)
            {
                if (!otherById.TryGetValue(queue.Dequeue(), out var doc)) continue;

                if (doc.TypeName == "PrefabInstance")
                {
                    if (strippedByInstance.TryGetValue(doc.FileId, out var companions))
                        foreach (var companionId in companions)
                            Include(companionId);

                    // This instance's own override footprint — objects/components Unity created
                    // SPECIFICALLY for it (see CollectAddedObjectTargets) — is exactly as inseparable
                    // from restoring the instance as the stripped companions above, whether or not the
                    // target happens to be fully real content (m_AddedGameObjects) or stripped
                    // (m_AddedComponents' owner always is). An m_AddedGameObjects target is the new
                    // object's TRANSFORM id, not its GameObject — resolve to the owning GameObject too
                    // so IncludeOwnedContent below picks up its own components/children once dequeued.
                    foreach (var (targetId, isAddedGameObject) in CollectAddedObjectTargets(doc))
                    {
                        Include(targetId);
                        if (isAddedGameObject && otherById.TryGetValue(targetId, out var targetTransform) &&
                            TryGetReferenceFileId(targetTransform, "m_GameObject", out var addedGoId))
                            Include(addedGoId);
                    }
                }

                if (doc.TypeName == "GameObject" && !originalMissing.Contains(doc.FileId))
                    IncludeOwnedContent(doc);

                foreach (var refId in FindLocalReferences(otherLines, doc))
                {
                    if (refId == 0 || included.Contains(refId) || baseById.ContainsKey(refId)) continue;

                    if (!otherById.TryGetValue(refId, out var refDoc) || !IsPrefabBoundDependency(refDoc, otherById))
                    {
                        skipped.Add($"{doc.TypeName} #{doc.FileId}: references object #{refId}, which doesn't exist in the working tree either — restore that too");
                        continue;
                    }

                    Include(refId);
                }
            }

            return added;
        }

        /// <summary>A PrefabInstance's own <c>m_Modification.m_AddedGameObjects</c>/<c>m_AddedComponents</c>
        /// entries — each <c>addedObject</c> fileID it lists was created specifically for this instance's
        /// override, so it's always safe (and, per <see cref="CollectStructuralDependencies"/>, necessary)
        /// to restore alongside it. Distinguishes the two lists because an <c>m_AddedGameObjects</c>
        /// entry's <c>addedObject</c> is the new object's TRANSFORM id (Unity's own convention — a
        /// GameObject is identified by its Transform here, not its own fileID), while an
        /// <c>m_AddedComponents</c> entry's <c>addedObject</c> is the component's own id directly.</summary>
        private static IEnumerable<(long TargetId, bool IsAddedGameObject)> CollectAddedObjectTargets(GitYamlDocument prefabInstance)
        {
            if (!prefabInstance.Fields.TryGetValue("m_Modification", out var modRaw) || modRaw is not Dictionary<string, object> modMap)
                yield break;

            foreach (var (key, isAddedGameObject) in new[] { ("m_AddedGameObjects", true), ("m_AddedComponents", false) })
            {
                if (!modMap.TryGetValue(key, out var listRaw) || listRaw is not List<object> list) continue;
                foreach (var entry in list)
                    if (entry is Dictionary<string, object> entryMap && entryMap.TryGetValue("addedObject", out var addedRaw) &&
                        addedRaw is Dictionary<string, object> addedMap && TryGetFileId(addedMap, out var addedId))
                        yield return (addedId, isAddedGameObject);
            }
        }

        private static bool TryGetReferenceFileId(GitYamlDocument doc, string key, out long fileId)
        {
            fileId = 0;
            return doc.Fields.TryGetValue(key, out var raw) && raw is Dictionary<string, object> map && TryGetFileId(map, out fileId);
        }

        private static bool TryGetFileId(Dictionary<string, object> map, out long fileId)
        {
            fileId = 0;
            return map.TryGetValue("fileID", out var rawId) && rawId is string s && long.TryParse(s, out fileId);
        }

        /// <summary>A document is nested-prefab plumbing with no independent tree node reachable by a
        /// GENERIC field reference (a script field, a Transform's m_Father) — safe to auto-restore, same
        /// reasoning as <see cref="CollectStructuralDependencies"/>'s own doc comment — when it's
        /// Stripped, is the PrefabInstance anchoring one, OR is a fully real (non-stripped) COMPONENT
        /// whose own <c>m_GameObject</c> owner is Stripped (Unity's <c>PrefabInstance.m_AddedComponents</c>
        /// override — its owner is a stripped GameObject with no <c>m_Component</c> list of its own for
        /// anything to reference it from; Unity discovers it via the PrefabInstance's own
        /// <c>m_AddedComponents</c> list instead). A reference to an <c>m_AddedGameObjects</c> target
        /// (a whole new real GameObject) is NOT decided here — that one IS a real, independently
        /// diff-tree-visible GameObject in the general case, so a stray reference to one from unrelated
        /// content stays reported rather than silently pulled in; it's only ever auto-included via
        /// <see cref="CollectAddedObjectTargets"/>, scoped specifically to the PrefabInstance that owns
        /// it, not any reference found anywhere.</summary>
        private static bool IsPrefabBoundDependency(GitYamlDocument doc, Dictionary<long, GitYamlDocument> otherById)
        {
            if (doc.Stripped || doc.TypeName == "PrefabInstance") return true;
            return doc.TypeName != "GameObject" && TryGetReferenceFileId(doc, "m_GameObject", out var ownerId) &&
                otherById.TryGetValue(ownerId, out var ownerDoc) && ownerDoc.Stripped;
        }

        /// <summary>Every local (non-guid) <c>{fileID: N}</c> reference inside <paramref name="doc"/>'s
        /// own line span — same <see cref="LocalFileIdPattern"/> match <see cref="FindDanglingLocalReference"/>
        /// uses for a single field, applied here across a whole document's raw lines instead.</summary>
        private static IEnumerable<long> FindLocalReferences(List<string> lines, GitYamlDocument doc)
        {
            for (var i = doc.HeaderLine; i < doc.BodyEndLine && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Contains("guid:")) continue;
                foreach (System.Text.RegularExpressions.Match m in LocalFileIdPattern.Matches(line))
                    if (long.TryParse(m.Groups[1].Value, out var id)) yield return id;
            }
        }

        /// <summary>The inverse of <see cref="BuildReferenceRemovalEdits"/> — appends
        /// <paramref name="targetIds"/> as new entries in <paramref name="fieldKey"/>'s list, right after
        /// its key line (restored entries lead; existing ones keep their order and position). A field
        /// currently at Unity's canonical empty-list form (<c>fieldKey: []</c>) has that single line
        /// replaced by the key line plus the new items, same shape Unity's own serializer would produce
        /// for a populated list. An id already present in the list is skipped rather than duplicated —
        /// restoring something that turns out to still have a (possibly stale/dangling) list entry must
        /// never create a second one: Unity treats two entries pointing at the same component as
        /// corrupt and silently drops one on load ("has multiple entries of the same Object component.
        /// Removing it!"), which is worse than the merely-stale entry this is guarding against.</summary>
        private static IEnumerable<LineEdit> BuildReferenceAddEdits(
            Dictionary<long, GitYamlDocument> baseById, long ownerDocId, string fieldKey, List<long> targetIds, List<string> baseLines, List<string> skipped)
        {
            if (!baseById.TryGetValue(ownerDocId, out var ownerDoc) || !ownerDoc.FieldSpans.TryGetValue(fieldKey, out var span))
            {
                foreach (var id in targetIds)
                    skipped.Add($"#{id}: restored, but owner #{ownerDocId}'s {fieldKey} list wasn't found — file may need a manual cleanup pass");
                yield break;
            }

            var alreadyListed = new HashSet<long>();
            for (var i = span.Start; i < span.End; i++)
            {
                if (!baseLines[i].TrimStart().StartsWith("- ")) continue; // the key line itself, not an item
                foreach (var id in targetIds)
                    if (baseLines[i].Contains($"{{fileID: {id}}}"))
                        alreadyListed.Add(id);
            }
            var idsToAdd = targetIds.Where(id => !alreadyListed.Contains(id)).ToList();
            if (idsToAdd.Count == 0) yield break;

            var keyLine = baseLines[span.Start];
            var indent = keyLine.Substring(0, keyLine.Length - keyLine.TrimStart().Length);
            var newLines = fieldKey == "m_Component"
                ? idsToAdd.Select(id => $"{indent}- component: {{fileID: {id}}}").ToList()
                : idsToAdd.Select(id => $"{indent}- {{fileID: {id}}}").ToList();

            if (keyLine.TrimEnd().EndsWith("[]"))
                yield return new LineEdit(span.Start, 1, new List<string> { $"{indent}{fieldKey}:" }.Concat(newLines).ToList());
            else
                yield return new LineEdit(span.Start + 1, 0, newLines);
        }

        private static IEnumerable<LineEdit> BuildFieldEdits(
            List<MockRow> rows, HashSet<long> toDelete, Dictionary<long, GitYamlDocument> baseById, List<string> baseLines,
            Dictionary<long, GitYamlDocument> otherById, List<string> otherLines, List<string> skipped)
        {
            // A field belonging to a document that's being wholly deleted needs no edit of its own —
            // the whole document (and this field along with it) is already gone via BuildDeletionEdits.
            var candidates = rows.Where(r => !r.IsHeader && r.FileId != 0 && r.Key != null && !toDelete.Contains(r.FileId));

            foreach (var row in candidates)
            {
                if (!baseById.TryGetValue(row.FileId, out var baseDoc)) continue;
                var baseSpan = default(LineSpan);
                var hasBaseSpan = baseDoc.FieldSpans.TryGetValue(row.Key, out baseSpan);

                switch (row.Resolution)
                {
                    case MockResolution.A:
                        break; // baseText already reflects A — nothing to splice

                    case MockResolution.Unresolved:
                        skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: unresolved conflict — not applied");
                        break;

                    case MockResolution.Manual:
                        skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: manually edited value can't be written back — take A or B instead");
                        break;

                    case MockResolution.B:
                        var edit = BuildTakeBEdit(baseDoc, hasBaseSpan, baseSpan, baseById, baseLines, otherById, otherLines, row, skipped);
                        if (edit.HasValue) yield return edit.Value;
                        break;
                }
            }
        }

        /// <summary>Matches a local `{fileID: N}` reference — one with no `guid:` on the same line,
        /// meaning it points at another object inside this same file rather than a whole other asset
        /// (a guid reference is always resolvable independently of this file's own content, so it's
        /// never a dangling-reference risk the way a local one is).</summary>
        private static readonly System.Text.RegularExpressions.Regex LocalFileIdPattern =
            new(@"fileID:\s*(-?\d+)(?![^\n]*guid:)");

        /// <summary>Splicing in a field's raw lines verbatim (see <see cref="BuildTakeBEdit"/>) can
        /// silently create a dangling reference when that field itself IS (or contains, for an array)
        /// one or more local object references — a `{fileID: N}` naming another document inside this
        /// same file, not an external asset (see <see cref="LocalFileIdPattern"/>) — and N doesn't
        /// exist on the side actually being written. Unity doesn't reject this at save time; it just
        /// logs "Broken text PPtr ... doesn't exist!" and silently nulls the reference the next time
        /// anything loads the file. Returns the first such id found, or null if every local reference
        /// in <paramref name="lines"/> resolves.</summary>
        private static long? FindDanglingLocalReference(List<string> lines, Dictionary<long, GitYamlDocument> baseById)
        {
            foreach (var line in lines)
            {
                foreach (System.Text.RegularExpressions.Match m in LocalFileIdPattern.Matches(line))
                {
                    if (!long.TryParse(m.Groups[1].Value, out var id) || id == 0) continue; // 0 = Unity's own "no reference" sentinel, always valid
                    if (!baseById.ContainsKey(id)) return id;
                }
            }
            return null;
        }

        private static LineEdit? BuildTakeBEdit(
            GitYamlDocument baseDoc, bool hasBaseSpan, LineSpan baseSpan, Dictionary<long, GitYamlDocument> baseById, List<string> baseLines,
            Dictionary<long, GitYamlDocument> otherById, List<string> otherLines, MockRow row, List<string> skipped)
        {
            if (!otherById.TryGetValue(baseDoc.FileId, out var otherDoc))
            {
                skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: object not found on the other side — not applied");
                return null;
            }

            var hasOtherSpan = otherDoc.FieldSpans.TryGetValue(row.Key, out var otherSpan);
            if (!hasBaseSpan && !hasOtherSpan)
            {
                skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: no line info on either side — not applied");
                return null;
            }

            var insert = hasOtherSpan ? otherLines.GetRange(otherSpan.Start, otherSpan.End - otherSpan.Start) : new List<string>();
            if (insert.Count > 0 && FindDanglingLocalReference(insert, baseById) is { } danglingId)
            {
                skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: references object #{danglingId}, which doesn't exist in the working tree — not applied (would create a broken reference)");
                return null;
            }

            // A brand-new PrefabInstance override entry (see UnityYamlParser's per-entry
            // m_Modification.m_Modifications spans) has no span of its own in base to anchor on — the
            // generic BodyEndLine fallback below would append it after the WHOLE PrefabInstance
            // document instead of inside its existing m_Modifications list. Anchor inside that list
            // instead, same "expand [] to a real list" shape BuildReferenceAddEdits already uses for
            // m_Component/m_Children.
            if (!hasBaseSpan && row.Key != null && row.Key.StartsWith("m_Modification.m_Modifications::", System.StringComparison.Ordinal))
            {
                if (!baseDoc.FieldSpans.TryGetValue("m_Modification.m_Modifications", out var listSpan))
                {
                    skipped.Add($"{baseDoc.TypeName} #{baseDoc.FileId} / {row.Property}: no m_Modifications list found on this PrefabInstance — not applied");
                    return null;
                }

                var keyLine = baseLines[listSpan.Start];
                if (keyLine.TrimEnd().EndsWith("[]"))
                {
                    var indent = keyLine.Substring(0, keyLine.Length - keyLine.TrimStart().Length);
                    var expanded = new List<string> { $"{indent}m_Modifications:" };
                    expanded.AddRange(insert);
                    return new LineEdit(listSpan.Start, 1, expanded);
                }
                return new LineEdit(listSpan.End, 0, insert);
            }

            // Remove A's existing lines for this field (none, at BodyEndLine, when it doesn't exist
            // in A — a pure insert), then splice in B's, verbatim, if it has any (none = a deletion,
            // when B doesn't have the field either).
            var removeStart = hasBaseSpan ? baseSpan.Start : baseDoc.BodyEndLine;
            var removeCount = hasBaseSpan ? baseSpan.End - baseSpan.Start : 0;
            return new LineEdit(removeStart, removeCount, insert);
        }
    }
}
