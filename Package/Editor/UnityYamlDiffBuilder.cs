using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    /// <summary>
    /// Builds the left tree's GameObject/component nodes (and the property rows behind each one)
    /// from two parsed revisions of the same scene/prefab file — the piece that used to be
    /// <c>MockDataSource</c>'s hardcoded scenario. Matching is by Unity's own per-object
    /// <see cref="GitYamlDocument.FileId"/>, which is stable across edits, so a GameObject or
    /// component that only changed fields (not identity) is recognized as "the same one, modified"
    /// rather than a delete+add pair.
    ///
    /// This is a two-revision diff, not a three-way merge — there's no common-ancestor revision to
    /// tell an auto-mergeable change from a real conflict on its own, which is why
    /// <see cref="BuildFileNode"/> takes <c>fileIsConflicted</c> (<see cref="GitFileReader.GetConflictedFiles"/>,
    /// git's own answer to that question): a field both sides have but disagree on is only surfaced
    /// as <see cref="MockRow.IsConflict"/> (unresolved, needs a pick) when its file is one git itself
    /// hasn't resolved yet — otherwise git's real 3-way merge already picked a winner (or resolved it
    /// outright) and A already holds it, so it's auto-mergeable like a single-side change. A field
    /// only one side has is always pre-filled into <see cref="MockRow.Result"/> as auto-mergeable,
    /// conflicted file or not, since there's nothing to choose between.
    /// </summary>
    internal static class UnityYamlDiffBuilder
    {
        // Present on every document type, or purely structural (hierarchy/identity plumbing) —
        // never meaningful to show as a "changed field" row.
        private static readonly HashSet<string> CommonIgnoredKeys = new()
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "serializedVersion",
        };

        private static readonly HashSet<string> GameObjectIgnoredKeys = new(CommonIgnoredKeys) { "m_Component" };

        // m_GameObject/m_Father/m_Children/m_RootOrder are hierarchy structure, not a component's
        // own data; m_Script identifies which MonoBehaviour class this is, not a value to diff.
        private static readonly HashSet<string> ComponentIgnoredKeys = new(CommonIgnoredKeys)
        {
            "m_GameObject", "m_Father", "m_Children", "m_RootOrder", "m_Script",
        };

        /// <summary>One file's worth of tree — the file node, then one node per changed GameObject
        /// nested under its real parent chain (via each GameObject's Transform.m_Father — see
        /// <see cref="GetParentGameObjectId"/>), same shape a Hierarchy window would show, not a flat
        /// list. An ancestor that isn't itself changed still gets a node when a descendant is (a
        /// "passthrough" — see <see cref="BuildGameObjectSubtree"/>) purely so the nesting reads
        /// correctly; components stay flattened onto their owning GameObject's row list rather than
        /// becoming their own nodes (see that method for why). <paramref name="contentA"/>/
        /// <paramref name="contentB"/> may be null (file added/removed on that side).
        /// <paramref name="bIsOlderBaseline"/> flips which side missing a document reads as Added vs
        /// Removed (see <see cref="ResolveBadge"/>): false (the normal case — A is the reference/base,
        /// B the side introducing a change, e.g. Yours vs Theirs in a real conflict, or A=HEAD/B=Working
        /// Tree in a plain browse) means "missing from A" reads as Added; true (only ever passed when B
        /// is a HEAD fallback for a non-conflicted file — see UnityGitToolWindow.Tree.cs — where B is
        /// actually the OLDER side despite being on the same "B" side of every other A/B convention in
        /// this tool) flips that, since there A is now the newer state and B the older one it's being
        /// compared against.</summary>
        public static TreeViewItemData<MockNode> BuildFileNode(
            Func<int> nextId,
            Dictionary<int, List<MockRow>> rowsByNodeId,
            Dictionary<int, List<MockRow>> ownRowsByNodeId,
            string label,
            string filePath,
            MockNodeKind fileKind,
            MockBadge fileBadge,
            string contentA,
            string contentB,
            bool fileIsConflicted,
            bool bIsOlderBaseline = false)
        {
            var byIdA = IndexByFileId(UnityYamlParser.Parse(contentA));
            var byIdB = IndexByFileId(UnityYamlParser.Parse(contentB));

            // Only GameObjects that actually changed (own fields or a component's) start a walk — a
            // scene can have thousands of untouched ones, and walking every single one up to the root
            // just to throw most of the result away isn't worth it. A "stripped" placeholder (see
            // GitYamlDocument.Stripped) never counts as changed regardless of add/remove status: it
            // has no real content of its own, only bookkeeping fields pointing at a nested prefab.
            var changedIds = byIdA.Values.Concat(byIdB.Values)
                .Where(d => d.TypeName == "GameObject")
                .Select(d => d.FileId)
                .Distinct()
                .Where(id =>
                {
                    byIdA.TryGetValue(id, out var a);
                    byIdB.TryGetValue(id, out var b);
                    return !(b ?? a).Stripped && HasGameObjectChanged(id, byIdA, byIdB);
                });

            // Each changed GameObject's full ancestor chain (via Transform.m_Father), unioned, so it
            // nests under its real parent all the way to the scene root instead of flattening onto the
            // file directly. An ancestor that isn't itself changed still ends up in this set — that's
            // deliberate (see BuildGameObjectSubtree) — its own diff just comes up empty, which is what
            // marks it as a structural passthrough rather than a real change.
            var included = new HashSet<long>();
            var parentOf = new Dictionary<long, long>();
            foreach (var id in changedIds)
            {
                var current = id;
                while (included.Add(current))
                {
                    var parentId = GetParentGameObjectId(current, byIdA, byIdB);
                    parentOf[current] = parentId;
                    if (parentId == 0) break;
                    current = parentId;
                }
            }

            var childrenOf = new Dictionary<long, List<long>>();
            foreach (var id in included)
            {
                var parentId = parentOf.GetValueOrDefault(id, 0);
                if (parentId == 0) continue;
                if (!childrenOf.TryGetValue(parentId, out var list)) childrenOf[parentId] = list = new List<long>();
                list.Add(id);
            }

            var children = included
                .Where(id => parentOf.GetValueOrDefault(id, 0) == 0)
                .Select(rootId => BuildGameObjectSubtree(rootId, childrenOf, nextId, rowsByNodeId, ownRowsByNodeId, byIdA, byIdB, fileIsConflicted, bIsOlderBaseline))
                .ToList();

            var nodeId = nextId();
            // The file node's own row list is every child's AGGREGATED rows concatenated (each already
            // includes its own descendants' — see BuildGameObjectSubtree) — lets
            // UnityGitToolWindow.ApplyResolution act on every change in the file at once. Has no
            // OwnRowsByNodeId entry of its own: a file is never shown in the table directly (see
            // UnityGitToolWindow.Tree.cs), only acted on via Apply.
            rowsByNodeId[nodeId] = children.SelectMany(c => rowsByNodeId.GetValueOrDefault(c.id) ?? new List<MockRow>()).ToList();
            var fileNode = new MockNode
            {
                Id = nodeId,
                Label = label,
                FilePath = filePath,
                Kind = fileKind,
                Badge = fileBadge,
                HasConflict = children.Any(c => c.data.HasConflict),
            };
            return new TreeViewItemData<MockNode>(nodeId, fileNode, children);
        }

        /// <summary>Resolves <paramref name="gameObjectId"/>'s parent GameObject's FileId via its own
        /// Transform/RectTransform component's <c>m_Father</c> — 0 (Unity's own "no reference"
        /// sentinel) at the scene root or when anything along the chain can't be resolved. Prefers B
        /// (the incoming/current side) for structure, falling back to A only where B doesn't have the
        /// object at all (added-in-A-only, i.e. removed on B).</summary>
        private static long GetParentGameObjectId(long gameObjectId, Dictionary<long, GitYamlDocument> byIdA, Dictionary<long, GitYamlDocument> byIdB)
        {
            var transformId = FindTransformId(gameObjectId, byIdB) ?? FindTransformId(gameObjectId, byIdA);
            if (transformId == null) return 0;

            var transform = byIdB.GetValueOrDefault(transformId.Value) ?? byIdA.GetValueOrDefault(transformId.Value);
            if (transform == null) return 0;
            if (!transform.Fields.TryGetValue("m_Father", out var fatherRef) || fatherRef is not Dictionary<string, object> fatherMap) return 0;
            if (!fatherMap.TryGetValue("fileID", out var rawFatherId) || rawFatherId is not string fatherIdStr ||
                !long.TryParse(fatherIdStr, out var fatherTransformId) || fatherTransformId == 0) return 0;

            var fatherTransform = byIdB.GetValueOrDefault(fatherTransformId) ?? byIdA.GetValueOrDefault(fatherTransformId);
            if (fatherTransform == null || !fatherTransform.Fields.TryGetValue("m_GameObject", out var ownerRef) ||
                ownerRef is not Dictionary<string, object> ownerMap) return 0;
            if (!ownerMap.TryGetValue("fileID", out var rawOwnerId) || rawOwnerId is not string ownerIdStr ||
                !long.TryParse(ownerIdStr, out var ownerId)) return 0;
            return ownerId;
        }

        private static long? FindTransformId(long gameObjectId, Dictionary<long, GitYamlDocument> byId)
        {
            if (!byId.TryGetValue(gameObjectId, out var gameObject)) return null;
            foreach (var compId in CollectComponentIds(gameObject))
                if (byId.TryGetValue(compId, out var comp) && comp.TypeName is "Transform" or "RectTransform")
                    return compId;
            return null;
        }

        /// <summary>Cheap "did this change at all" check — added/removed entirely, an own field
        /// differs, or a component was added/removed/differs — reusing <see cref="DiffFields"/> itself
        /// (with a throwaway <c>fileIsConflicted</c>, irrelevant to a field simply existing) rather
        /// than a second hand-rolled equality pass that could drift from what <see cref="BuildGameObjectSubtree"/>
        /// actually builds later. <see cref="GameObjectIgnoredKeys"/> already covers everything m_Component's
        /// own presence (as opposed to its target's content) is about — see that field's comment.</summary>
        private static bool HasGameObjectChanged(long id, Dictionary<long, GitYamlDocument> byIdA, Dictionary<long, GitYamlDocument> byIdB)
        {
            byIdA.TryGetValue(id, out var goA);
            byIdB.TryGetValue(id, out var goB);
            if (goA == null || goB == null) return true;
            if (DiffFields(0, goA.Fields, goB.Fields, GameObjectIgnoredKeys, byIdA, byIdB, false).Count > 0) return true;

            foreach (var compId in CollectComponentIds(goA).Concat(CollectComponentIds(goB)).Distinct())
            {
                byIdA.TryGetValue(compId, out var compA);
                byIdB.TryGetValue(compId, out var compB);
                if ((compB ?? compA)?.Stripped != false) continue;
                if (compA == null || compB == null) return true;
                if (DiffFields(0, compA.Fields, compB.Fields, ComponentIgnoredKeys, byIdA, byIdB, false).Count > 0) return true;
            }
            return false;
        }

        /// <summary>One GameObject's node, plus every one of its own components' rows flattened onto
        /// it (component identity itself was never useful as a separate tree node — see the header
        /// row <see cref="BuildComponentRows"/> already prepends), plus, now, its actual child
        /// GameObjects nested underneath as their own subtrees. An unchanged GameObject with a changed
        /// descendant still gets a node here — same "passthrough" idea as a folder in a file tree —
        /// so nesting reads correctly instead of flattening every changed object straight onto the
        /// file; its own <c>ownRows</c> stays empty and its <see cref="MockNode.Badge"/> stays
        /// <see cref="MockBadge.None"/>, distinguishing it from a real change at a glance. Registers
        /// two different row lists for this id — see <see cref="MockDataSource"/> for why a child
        /// GameObject's own components must never be attributed to its parent's table view just
        /// because the parent happens to be selected.</summary>
        private static TreeViewItemData<MockNode> BuildGameObjectSubtree(
            long id,
            Dictionary<long, List<long>> childrenOf,
            Func<int> nextId,
            Dictionary<int, List<MockRow>> rowsByNodeId,
            Dictionary<int, List<MockRow>> ownRowsByNodeId,
            Dictionary<long, GitYamlDocument> byIdA,
            Dictionary<long, GitYamlDocument> byIdB,
            bool fileIsConflicted,
            bool bIsOlderBaseline)
        {
            byIdA.TryGetValue(id, out var goA);
            byIdB.TryGetValue(id, out var goB);

            var childItems = new List<TreeViewItemData<MockNode>>();
            if (childrenOf.TryGetValue(id, out var childIds))
                foreach (var childId in childIds)
                    childItems.Add(BuildGameObjectSubtree(childId, childrenOf, nextId, rowsByNodeId, ownRowsByNodeId, byIdA, byIdB, fileIsConflicted, bIsOlderBaseline));

            // Write-back only ever patches revision A's own file (see UnityYamlWriter) — a row whose
            // object doesn't exist on the A side at all (FileId 0, Unity's own "no reference" sentinel,
            // safe to reuse since a real document never has it) has nothing to splice into; it's
            // filtered out at apply time instead of guessed at.
            var ownRows = DiffFields(goA?.FileId ?? 0, goA?.Fields, goB?.Fields, GameObjectIgnoredKeys, byIdA, byIdB, fileIsConflicted, bIsOlderBaseline);

            var componentIds = CollectComponentIds(goA).Concat(CollectComponentIds(goB)).Distinct();
            var anyComponentChanged = false;
            foreach (var compId in componentIds)
            {
                byIdA.TryGetValue(compId, out var compA);
                byIdB.TryGetValue(compId, out var compB);
                if ((compB ?? compA)?.Stripped != false) continue;

                var componentRows = BuildComponentRows(compA, compB, byIdA, byIdB, fileIsConflicted, bIsOlderBaseline, out var changed);
                anyComponentChanged |= changed;
                ownRows.AddRange(componentRows);
            }

            var badge = ResolveBadge(goA, goB, ownRows.Count > 0 || anyComponentChanged, bIsOlderBaseline);
            var label = (goB ?? goA)?.Fields.GetValueOrDefault("m_Name") as string ?? "GameObject";

            var nodeId = nextId();
            ownRowsByNodeId[nodeId] = ownRows;
            // Aggregated the same way BuildFileNode aggregates its own children: an unchanged
            // (passthrough) node's ownRows is empty, so this is purely its descendants'; a changed
            // node's is its own rows plus whatever's further down. Only ApplyResolution (via a file
            // node) ever reads this one — the table reads OwnRowsByNodeId instead.
            rowsByNodeId[nodeId] = ownRows.Concat(childItems.SelectMany(c => rowsByNodeId.GetValueOrDefault(c.id) ?? new List<MockRow>())).ToList();
            var node = new MockNode
            {
                Id = nodeId,
                Label = label,
                Kind = MockNodeKind.GameObject,
                Badge = badge,
                HasConflict = ownRows.Exists(r => r.IsConflict) || childItems.Any(c => c.data.HasConflict),
            };
            return new TreeViewItemData<MockNode>(nodeId, node, childItems);
        }

        /// <summary>Diffs one component's fields and prepends an Inspector-style header row (icon +
        /// name) ahead of them, so rows merged from several components into one GameObject's flat
        /// list still read as separate sections instead of a wall of unrelated properties.
        /// <paramref name="changed"/> is true whenever this component itself was added, removed, or
        /// has any differing field — used by the caller to badge the owning GameObject even when the
        /// field diff comes up empty (e.g. every field is in <see cref="ComponentIgnoredKeys"/>).</summary>
        private static List<MockRow> BuildComponentRows(
            GitYamlDocument compA,
            GitYamlDocument compB,
            Dictionary<long, GitYamlDocument> byIdA,
            Dictionary<long, GitYamlDocument> byIdB,
            bool fileIsConflicted,
            bool bIsOlderBaseline,
            out bool changed)
        {
            var typeName = compB?.TypeName ?? compA?.TypeName ?? "Component";
            // "MonoBehaviour" is just the YAML document type for every script component — the actual
            // class name lives in m_Script (a MonoScript asset reference), same as Unity's own
            // Inspector title bar resolves it, not the generic type tag.
            var label = typeName == "MonoBehaviour" ? ResolveScriptLabel(compB ?? compA) : typeName;

            var fieldRows = DiffFields(compA?.FileId ?? 0, compA?.Fields, compB?.Fields, ComponentIgnoredKeys, byIdA, byIdB, fileIsConflicted, bIsOlderBaseline);
            changed = compA == null || compB == null || fieldRows.Count > 0;
            if (!changed) return fieldRows;

            // Added/removed components mark themselves in the header rather than needing a synthetic
            // field row below it (which had nothing real to show when every field was ignored anyway).
            // See BuildFileNode's doc comment for bIsOlderBaseline — same Added/Removed direction flip.
            var isAdded = bIsOlderBaseline ? compB == null : compA == null;
            var isRemoved = bIsOlderBaseline ? compA == null : compB == null;
            var headerLabel = isAdded ? $"{label} (added)" : isRemoved ? $"{label} (removed)" : label;
            var rows = new List<MockRow> { new() { IsHeader = true, Property = headerLabel, HeaderIcon = IconForComponentType(typeName) } };
            rows.AddRange(fieldRows);
            return rows;
        }

        private static string IconForComponentType(string typeName) => typeName switch
        {
            "Transform" or "RectTransform" => "Transform Icon",
            "MeshRenderer" or "SkinnedMeshRenderer" => "MeshRenderer Icon",
            "MeshFilter" => "MeshFilter Icon",
            "BoxCollider" or "SphereCollider" or "CapsuleCollider" or "MeshCollider" => "BoxCollider Icon",
            "Rigidbody" => "Rigidbody Icon",
            "Canvas" => "Canvas Icon",
            "CanvasRenderer" => "CanvasRenderer Icon",
            "MonoBehaviour" => "cs Script Icon",
            _ => "cs Script Icon",
        };

        /// <summary>Resolves a MonoBehaviour document's m_Script reference to its actual class name
        /// (e.g. "KioskManager") via the referenced MonoScript asset — falls back to the script
        /// asset's filename if the class can't be loaded (e.g. a compile error), and to the bare
        /// "MonoBehaviour" tag if m_Script itself can't be resolved at all (missing script).</summary>
        private static string ResolveScriptLabel(GitYamlDocument component)
        {
            if (component != null &&
                component.Fields.TryGetValue("m_Script", out var scriptRef) &&
                scriptRef is Dictionary<string, object> scriptMap &&
                scriptMap.TryGetValue("guid", out var guidValue) && guidValue is string guid)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    var scriptClass = monoScript != null ? monoScript.GetClass() : null;
                    return scriptClass != null ? scriptClass.Name : Path.GetFileNameWithoutExtension(path);
                }
            }
            return "MonoBehaviour";
        }

        /// <summary>See <see cref="BuildFileNode"/>'s doc comment for what <paramref name="bIsOlderBaseline"/>
        /// means and when it's true.</summary>
        private static MockBadge ResolveBadge(GitYamlDocument a, GitYamlDocument b, bool hasChanges, bool bIsOlderBaseline)
        {
            if (a == null) return bIsOlderBaseline ? MockBadge.Removed : MockBadge.Added;
            if (b == null) return bIsOlderBaseline ? MockBadge.Added : MockBadge.Removed;
            return hasChanges ? MockBadge.Modified : MockBadge.None;
        }

        private static Dictionary<long, GitYamlDocument> IndexByFileId(List<GitYamlDocument> documents)
        {
            var byId = new Dictionary<long, GitYamlDocument>();
            foreach (var document in documents)
                if (document.TypeName != null)
                    byId[document.FileId] = document;
            return byId;
        }

        private static IEnumerable<long> CollectComponentIds(GitYamlDocument gameObject)
        {
            if (gameObject == null || !gameObject.Fields.TryGetValue("m_Component", out var raw) || raw is not List<object> entries)
                yield break;

            foreach (var entry in entries)
            {
                if (entry is not Dictionary<string, object> entryMap) continue;
                if (!entryMap.TryGetValue("component", out var reference) || reference is not Dictionary<string, object> refMap) continue;
                if (refMap.TryGetValue("fileID", out var fileId) && fileId is string s && long.TryParse(s, out var id))
                    yield return id;
            }
        }

        private static List<MockRow> DiffFields(
            long fileId,
            Dictionary<string, object> fieldsA,
            Dictionary<string, object> fieldsB,
            HashSet<string> ignoredKeys,
            Dictionary<long, GitYamlDocument> byIdA,
            Dictionary<long, GitYamlDocument> byIdB,
            bool fileIsConflicted,
            bool bIsOlderBaseline = false)
        {
            var rows = new List<MockRow>();
            var keys = (fieldsA?.Keys ?? Enumerable.Empty<string>()).Concat(fieldsB?.Keys ?? Enumerable.Empty<string>()).Distinct();

            foreach (var key in keys)
            {
                if (ignoredKeys.Contains(key)) continue;

                object rawA = null;
                var hasA = fieldsA != null && fieldsA.TryGetValue(key, out rawA);
                object rawB = null;
                var hasB = fieldsB != null && fieldsB.TryGetValue(key, out rawB);
                if (YamlValuesEqual(rawA, rawB)) continue;

                // Without fileIsConflicted, "both sides have it and it differs" alone would flag every
                // field a rebase/merge touched at all, on every file it touched — see the class
                // header for why that's wrong for a file git already resolved cleanly.
                var isConflict = fileIsConflicted && hasA && hasB;
                rows.Add(new MockRow
                {
                    Property = UnityYamlFieldNames.Humanize(key),
                    FileId = fileId,
                    Key = key,
                    BIsOlderBaseline = bIsOlderBaseline,
                    ValueA = hasA ? UnityYamlValueConverter.ToDisplayValue(rawA, byIdA) : null,
                    ValueB = hasB ? UnityYamlValueConverter.ToDisplayValue(rawB, byIdB) : null,
                    IsConflict = isConflict,
                    // Only one side to pick when it's not a conflict — auto-resolves to whichever side
                    // has the field, same value MockRow.Result gets below. See MockResolution: this is
                    // what UnityGitToolWindow.Table.cs's Take A/B/Revert buttons and the Result field's
                    // own inline edit callback (Manual) go on to overwrite.
                    Resolution = isConflict ? MockResolution.Unresolved : hasA ? MockResolution.A : MockResolution.B,
                    Result = isConflict ? null : UnityYamlValueConverter.ToDisplayValue(hasA ? rawA : rawB, hasA ? byIdA : byIdB),
                });
            }
            return rows;
        }

        private static bool YamlValuesEqual(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is string sa && b is string sb) return sa == sb;

            if (a is Dictionary<string, object> da && b is Dictionary<string, object> db)
            {
                if (da.Count != db.Count) return false;
                foreach (var (key, value) in da)
                    if (!db.TryGetValue(key, out var otherValue) || !YamlValuesEqual(value, otherValue))
                        return false;
                return true;
            }

            if (a is List<object> la && b is List<object> lb)
            {
                if (la.Count != lb.Count) return false;
                for (var i = 0; i < la.Count; i++)
                    if (!YamlValuesEqual(la[i], lb[i]))
                        return false;
                return true;
            }

            return false;
        }
    }
}
