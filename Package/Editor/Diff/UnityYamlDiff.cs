using System.Collections.Generic;
using System.Linq;

namespace UnityGitTool.Diff
{
    /// <summary>
    /// Compares two parsed revisions of the same Unity YAML asset. Documents are matched
    /// by <c>fileId</c> (stable across edits, unlike array position), then diffed field by
    /// field recursively.
    /// </summary>
    public static class UnityYamlDiff
    {
        /// <summary>Serialization bookkeeping fields that don't represent a meaningful content
        /// change on their own — e.g. <c>m_Component</c> always changes when a component is
        /// added/removed, but that's already conveyed by the component itself showing up as
        /// added/removed, so counting it here would flag the owning GameObject as "modified"
        /// with nothing to actually show.</summary>
        public static readonly HashSet<string> IgnoredKeys = new()
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
            "m_PrefabAsset", "m_Component", "m_GameObject",
        };

        public static List<DocumentDiff> Compare(
            IEnumerable<UnityGitTool.Yaml.UnityYamlDocument> before,
            IEnumerable<UnityGitTool.Yaml.UnityYamlDocument> after)
        {
            var beforeByFileId = before.ToDictionary(d => d.FileId);
            var afterByFileId = after.ToDictionary(d => d.FileId);
            var allFileIds = beforeByFileId.Keys.Union(afterByFileId.Keys).OrderBy(id => id);

            var result = new List<DocumentDiff>();

            foreach (var fileId in allFileIds)
            {
                var hasBefore = beforeByFileId.TryGetValue(fileId, out var beforeDoc);
                var hasAfter = afterByFileId.TryGetValue(fileId, out var afterDoc);

                if (hasBefore && !hasAfter)
                {
                    result.Add(new DocumentDiff
                    {
                        Type = DocumentChangeType.Removed,
                        ClassId = beforeDoc.ClassId,
                        FileId = fileId,
                        RootKey = beforeDoc.RootKey,
                    });
                    continue;
                }

                if (!hasBefore && hasAfter)
                {
                    result.Add(new DocumentDiff
                    {
                        Type = DocumentChangeType.Added,
                        ClassId = afterDoc.ClassId,
                        FileId = fileId,
                        RootKey = afterDoc.RootKey,
                    });
                    continue;
                }

                var fieldChanges = new List<FieldChange>();
                CompareValue(beforeDoc.Fields, afterDoc.Fields, "", fieldChanges);

                result.Add(new DocumentDiff
                {
                    Type = fieldChanges.Count > 0 ? DocumentChangeType.Modified : DocumentChangeType.Unchanged,
                    ClassId = afterDoc.ClassId,
                    FileId = fileId,
                    RootKey = afterDoc.RootKey,
                    FieldChanges = fieldChanges,
                });
            }

            return result;
        }

        private static void CompareValue(object before, object after, string path, List<FieldChange> changes)
        {
            if (before is Dictionary<string, object> beforeMap && after is Dictionary<string, object> afterMap)
            {
                CompareMapping(beforeMap, afterMap, path, changes);
                return;
            }

            if (before is List<object> beforeList && after is List<object> afterList)
            {
                CompareList(beforeList, afterList, path, changes);
                return;
            }

            // Leaf scalars (or a type change, e.g. a field switching between a plain
            // scalar and a flow mapping) both fall through to a plain string comparison.
            var beforeText = Stringify(before);
            var afterText = Stringify(after);
            if (beforeText != afterText)
            {
                changes.Add(new FieldChange
                {
                    Type = FieldChangeType.Modified,
                    Path = path,
                    OldValue = beforeText,
                    NewValue = afterText,
                });
            }
        }

        private static void CompareMapping(
            Dictionary<string, object> before,
            Dictionary<string, object> after,
            string path,
            List<FieldChange> changes)
        {
            foreach (var key in before.Keys.Union(after.Keys))
            {
                if (IgnoredKeys.Contains(key)) continue;

                var childPath = path.Length == 0 ? key : $"{path}/{key}";
                var hasBefore = before.TryGetValue(key, out var beforeValue);
                var hasAfter = after.TryGetValue(key, out var afterValue);

                if (hasBefore && !hasAfter)
                {
                    changes.Add(new FieldChange
                    {
                        Type = FieldChangeType.Removed,
                        Path = childPath,
                        OldValue = Stringify(beforeValue),
                    });
                }
                else if (!hasBefore && hasAfter)
                {
                    changes.Add(new FieldChange
                    {
                        Type = FieldChangeType.Added,
                        Path = childPath,
                        NewValue = Stringify(afterValue),
                    });
                }
                else
                {
                    CompareValue(beforeValue, afterValue, childPath, changes);
                }
            }
        }

        // Unity's serialized lists have no stable per-item id (unlike documents, which have
        // fileId), so items are matched by position. This reads fine for fixed-shape lists
        // (m_LocalPosition-style structs won't hit this path at all) but a reordered list —
        // e.g. moving a GameObject in m_Children — will show as N modified slots rather than
        // one move. Good enough for a first pass; positional matching is a known limitation.
        private static void CompareList(List<object> before, List<object> after, string path, List<FieldChange> changes)
        {
            var maxCount = System.Math.Max(before.Count, after.Count);
            for (var i = 0; i < maxCount; i++)
            {
                var childPath = $"{path}/[{i}]";
                if (i >= before.Count)
                {
                    changes.Add(new FieldChange
                    {
                        Type = FieldChangeType.Added,
                        Path = childPath,
                        NewValue = Stringify(after[i]),
                    });
                }
                else if (i >= after.Count)
                {
                    changes.Add(new FieldChange
                    {
                        Type = FieldChangeType.Removed,
                        Path = childPath,
                        OldValue = Stringify(before[i]),
                    });
                }
                else
                {
                    CompareValue(before[i], after[i], childPath, changes);
                }
            }
        }

        private static string Stringify(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case Dictionary<string, object> map:
                    return "{" + string.Join(", ", map.Keys) + "}";
                case List<object> list:
                    return $"[{list.Count} items]";
                default:
                    return value.ToString();
            }
        }
    }
}
