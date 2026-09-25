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
            if (restoredIds != null)
                edits.AddRange(BuildRestoreEdits(new HashSet<long>(restoredIds), baseById, otherById, otherLines, baseLines, skipped));
            edits.AddRange(BuildFieldEdits(rows, toDelete, baseById, otherById, otherLines, skipped));

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
        /// far more than the user actually clicked.</summary>
        private static IEnumerable<LineEdit> BuildRestoreEdits(
            HashSet<long> toRestore, Dictionary<long, GitYamlDocument> baseById, Dictionary<long, GitYamlDocument> otherById,
            List<string> otherLines, List<string> baseLines, List<string> skipped)
        {
            var missing = toRestore.Where(id => !baseById.ContainsKey(id)).ToList();
            if (missing.Count == 0) yield break;

            // Copy each missing document's raw block verbatim, all appended together at the very end
            // of the file — Unity doesn't care about document order, only fileID references (which is
            // exactly what the owner-list fixups below take care of), so this is always a safe
            // insertion point regardless of the surrounding structure.
            var toAppend = new List<string>();
            var ownerFixups = new Dictionary<(long OwnerDocId, string FieldKey), List<long>>();

            void AddFixup(long ownerDocId, string fieldKey, long targetId)
            {
                var key = (ownerDocId, fieldKey);
                if (!ownerFixups.TryGetValue(key, out var list)) ownerFixups[key] = list = new List<long>();
                list.Add(targetId);
            }

            foreach (var id in missing)
            {
                if (!otherById.TryGetValue(id, out var doc))
                {
                    skipped.Add($"object #{id}: marked for restore but not found on the other side — not applied");
                    continue;
                }
                toAppend.AddRange(otherLines.GetRange(doc.HeaderLine, doc.BodyEndLine - doc.HeaderLine));

                if (doc.TypeName == "GameObject") continue; // linked into its parent below, once every restored id's own doc is known

                var ownerId = doc.Fields.TryGetValue("m_GameObject", out var raw) && raw is Dictionary<string, object> map &&
                    map.TryGetValue("fileID", out var rawId) && rawId is string s && long.TryParse(s, out var owner) ? owner : 0;
                if (ownerId == 0) { skipped.Add($"{doc.TypeName} #{id}: couldn't resolve its owning GameObject — not linked back in"); continue; }
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

            if (toAppend.Count > 0)
                yield return new LineEdit(baseLines.Count, 0, toAppend);
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
            List<MockRow> rows, HashSet<long> toDelete, Dictionary<long, GitYamlDocument> baseById, Dictionary<long, GitYamlDocument> otherById,
            List<string> otherLines, List<string> skipped)
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
                        var edit = BuildTakeBEdit(baseDoc, hasBaseSpan, baseSpan, otherById, otherLines, row, skipped);
                        if (edit.HasValue) yield return edit.Value;
                        break;
                }
            }
        }

        private static LineEdit? BuildTakeBEdit(
            GitYamlDocument baseDoc, bool hasBaseSpan, LineSpan baseSpan, Dictionary<long, GitYamlDocument> otherById,
            List<string> otherLines, MockRow row, List<string> skipped)
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

            // Remove A's existing lines for this field (none, at BodyEndLine, when it doesn't exist
            // in A — a pure insert), then splice in B's, verbatim, if it has any (none = a deletion,
            // when B doesn't have the field either).
            var removeStart = hasBaseSpan ? baseSpan.Start : baseDoc.BodyEndLine;
            var removeCount = hasBaseSpan ? baseSpan.End - baseSpan.Start : 0;
            var insert = hasOtherSpan ? otherLines.GetRange(otherSpan.Start, otherSpan.End - otherSpan.Start) : new List<string>();
            return new LineEdit(removeStart, removeCount, insert);
        }
    }
}
