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
    /// tell an auto-mergeable change from a real conflict. Every field both sides have but disagree
    /// on is surfaced as <see cref="MockRow.IsConflict"/> (unresolved, needs a pick) the same way
    /// the old mock data did for its "both sides changed it" rows; a field only one side has is
    /// pre-filled into <see cref="MockRow.Result"/> as auto-mergeable, since there's nothing to
    /// choose between.
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

        /// <summary>One file's worth of tree — the file node itself plus one leaf child per changed
        /// GameObject that exists on either side (see <see cref="BuildGameObjectNode"/> for why
        /// components aren't separate nodes). <paramref name="contentA"/>/<paramref name="contentB"/>
        /// may be null (file added/removed on that side).</summary>
        public static TreeViewItemData<MockNode> BuildFileNode(
            Func<int> nextId,
            Dictionary<int, List<MockRow>> rowsByNodeId,
            string label,
            MockNodeKind fileKind,
            MockBadge fileBadge,
            string contentA,
            string contentB)
        {
            var byIdA = IndexByFileId(UnityYamlParser.Parse(contentA));
            var byIdB = IndexByFileId(UnityYamlParser.Parse(contentB));

            var gameObjectIds = byIdA.Values.Concat(byIdB.Values)
                .Where(d => d.TypeName == "GameObject")
                .Select(d => d.FileId)
                .Distinct();

            // Only GameObjects that actually changed (own fields or a component's) are worth a tree
            // node — a scene can have thousands of untouched ones, and this is a diff tool, not an
            // Inspector. A "stripped" placeholder (see GitYamlDocument.Stripped) is also excluded
            // regardless of add/remove status: it has no real content of its own to show, only
            // bookkeeping fields pointing at a nested prefab, so it'd otherwise show up as a bare,
            // unnamed "GameObject" node whenever the whole file is added/removed.
            var children = new List<TreeViewItemData<MockNode>>();
            foreach (var goId in gameObjectIds)
            {
                byIdA.TryGetValue(goId, out var goA);
                byIdB.TryGetValue(goId, out var goB);
                if ((goB ?? goA).Stripped) continue;

                var goNode = BuildGameObjectNode(nextId, rowsByNodeId, goA, goB, byIdA, byIdB);
                if (goNode.data.Badge != MockBadge.None) children.Add(goNode);
            }

            var fileId = nextId();
            rowsByNodeId[fileId] = new List<MockRow>();
            var fileNode = new MockNode
            {
                Id = fileId,
                Label = label,
                Kind = fileKind,
                Badge = fileBadge,
                HasConflict = children.Any(c => c.data.HasConflict),
            };
            return new TreeViewItemData<MockNode>(fileId, fileNode, children);
        }

        /// <summary>A GameObject is a tree LEAF — no separate Transform/MonoBehaviour/... children.
        /// Selecting it shows its own fields plus every one of its components' fields in one flat
        /// row list instead, each component row's <see cref="MockRow.Property"/> prefixed with that
        /// component's label so it's still clear where each row came from.</summary>
        private static TreeViewItemData<MockNode> BuildGameObjectNode(
            Func<int> nextId,
            Dictionary<int, List<MockRow>> rowsByNodeId,
            GitYamlDocument goA,
            GitYamlDocument goB,
            Dictionary<long, GitYamlDocument> byIdA,
            Dictionary<long, GitYamlDocument> byIdB)
        {
            var rows = DiffFields(goA?.Fields, goB?.Fields, GameObjectIgnoredKeys, byIdA, byIdB);

            var componentIds = CollectComponentIds(goA).Concat(CollectComponentIds(goB)).Distinct();
            var anyComponentChanged = false;
            foreach (var compId in componentIds)
            {
                byIdA.TryGetValue(compId, out var compA);
                byIdB.TryGetValue(compId, out var compB);
                if ((compB ?? compA)?.Stripped != false) continue;

                var componentRows = BuildComponentRows(compA, compB, byIdA, byIdB, out var changed);
                anyComponentChanged |= changed;
                rows.AddRange(componentRows);
            }

            var badge = ResolveBadge(goA, goB, rows.Count > 0 || anyComponentChanged);
            var label = (goB ?? goA)?.Fields.GetValueOrDefault("m_Name") as string ?? "GameObject";

            var id = nextId();
            rowsByNodeId[id] = rows;
            var node = new MockNode
            {
                Id = id,
                Label = label,
                Kind = MockNodeKind.GameObject,
                Badge = badge,
                HasConflict = rows.Exists(r => r.IsConflict),
            };
            return new TreeViewItemData<MockNode>(id, node);
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
            out bool changed)
        {
            var typeName = compB?.TypeName ?? compA?.TypeName ?? "Component";
            // "MonoBehaviour" is just the YAML document type for every script component — the actual
            // class name lives in m_Script (a MonoScript asset reference), same as Unity's own
            // Inspector title bar resolves it, not the generic type tag.
            var label = typeName == "MonoBehaviour" ? ResolveScriptLabel(compB ?? compA) : typeName;

            var fieldRows = DiffFields(compA?.Fields, compB?.Fields, ComponentIgnoredKeys, byIdA, byIdB);
            changed = compA == null || compB == null || fieldRows.Count > 0;
            if (!changed) return fieldRows;

            // Added/removed components mark themselves in the header rather than needing a synthetic
            // field row below it (which had nothing real to show when every field was ignored anyway).
            var headerLabel = compA == null ? $"{label} (added)" : compB == null ? $"{label} (removed)" : label;
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

        private static MockBadge ResolveBadge(GitYamlDocument a, GitYamlDocument b, bool hasChanges)
        {
            if (a == null) return MockBadge.Added;
            if (b == null) return MockBadge.Removed;
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
            Dictionary<string, object> fieldsA,
            Dictionary<string, object> fieldsB,
            HashSet<string> ignoredKeys,
            Dictionary<long, GitYamlDocument> byIdA,
            Dictionary<long, GitYamlDocument> byIdB)
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

                var isConflict = hasA && hasB;
                rows.Add(new MockRow
                {
                    Property = UnityYamlFieldNames.Humanize(key),
                    ValueA = hasA ? UnityYamlValueConverter.ToDisplayValue(rawA, byIdA) : null,
                    ValueB = hasB ? UnityYamlValueConverter.ToDisplayValue(rawB, byIdB) : null,
                    IsConflict = isConflict,
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
