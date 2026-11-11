using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    internal enum SceneYamlNodeKind { GameObject, PrefabInstance, PrefabInstanceObject }

    /// <summary>One Hierarchy entry read from the scene file. <see cref="FileId"/> is the scene document
    /// (GameObject or PrefabInstance); an overridden object also carries its fileID inside the source
    /// prefab (<see cref="SourceFileId"/>), which is how Unity identifies it in the loaded scene.</summary>
    internal sealed class SceneYamlNode
    {
        public SceneYamlNodeKind Kind;
        public string Label;
        public string Icon;
        public long FileId;
        public long PrefabInstanceId;
        public long SourceFileId;
        public string SourceGuid;
        /// <summary>Override target fileIDs (in the source prefab) an overridden-object node groups.</summary>
        public List<string> TargetIds;
    }

    internal sealed class SceneYamlRow
    {
        public string Property;
        public object Value;
        public bool IsHeader;
        public string HeaderIcon;
    }

    /// <summary>The scene file's Hierarchy, straight from its YAML (same parser and value conversion as
    /// the diff): GameObjects and PrefabInstances in their saved order, a PrefabInstance's overridden
    /// objects as its first children. Rows are built per node on demand: a scene holds thousands.</summary>
    internal static class SceneYamlTree
    {
        public static List<TreeViewItemData<SceneYamlNode>> Build(Dictionary<long, GitYamlDocument> byId, Func<int> nextId)
        {
            var nodeIds = byId.Values
                .Where(d => !d.Stripped && d.TypeName is "GameObject" or "PrefabInstance")
                .Select(d => d.FileId)
                .ToList();

            var childrenOf = new Dictionary<long, List<long>>();
            var roots = new List<long>();
            foreach (var id in nodeIds)
            {
                var parentId = UnityYamlDiffBuilder.GetParentGameObjectId(id, byId, byId);
                if (parentId == 0 || !byId.ContainsKey(parentId)) { roots.Add(id); continue; }
                if (!childrenOf.TryGetValue(parentId, out var list)) childrenOf[parentId] = list = new List<long>();
                list.Add(id);
            }

            var rootOrder = SiblingOrder(SceneRootTransforms(byId), byId);
            return Sort(roots, rootOrder).Select(id => BuildNode(id, byId, childrenOf, nextId)).ToList();
        }

        private static TreeViewItemData<SceneYamlNode> BuildNode(long id, Dictionary<long, GitYamlDocument> byId, Dictionary<long, List<long>> childrenOf, Func<int> nextId)
        {
            var doc = byId[id];
            var children = new List<TreeViewItemData<SceneYamlNode>>();
            SceneYamlNode node;

            if (doc.TypeName == "PrefabInstance")
            {
                var sourceGuid = UnityYamlDiffBuilder.GetPrefabSourceGuid(doc);
                var sourceRoot = LoadSourceRoot(sourceGuid);
                node = new SceneYamlNode
                {
                    Kind = SceneYamlNodeKind.PrefabInstance,
                    Label = UnityYamlDiffBuilder.ResolvePrefabInstanceName(doc, sourceGuid) ?? "Prefab Instance",
                    Icon = PrefabIcon(sourceRoot),
                    FileId = id,
                    PrefabInstanceId = id,
                    SourceFileId = LocalFileId(sourceRoot),
                    SourceGuid = sourceGuid,
                };
                children.AddRange(BuildOverriddenObjects(doc, sourceGuid, nextId));
            }
            else
            {
                node = new SceneYamlNode
                {
                    Kind = SceneYamlNodeKind.GameObject,
                    Label = doc.Fields.GetValueOrDefault("m_Name") as string ?? "GameObject",
                    Icon = "GameObject Icon",
                    FileId = id,
                };
            }

            if (childrenOf.TryGetValue(id, out var childIds))
            {
                var transformId = UnityYamlDiffBuilder.FindTransformId(id, byId);
                var order = transformId != null ? SiblingOrder(ChildTransforms(byId[transformId.Value]), byId) : new Dictionary<long, int>();
                children.AddRange(Sort(childIds, order).Select(childId => BuildNode(childId, byId, childrenOf, nextId)));
            }
            return new TreeViewItemData<SceneYamlNode>(nextId(), node, children);
        }

        /// <summary>One node per source-prefab GameObject the instance's property overrides touch, the
        /// same grouping as the diff tree.</summary>
        private static IEnumerable<TreeViewItemData<SceneYamlNode>> BuildOverriddenObjects(GitYamlDocument instance, string sourceGuid, Func<int> nextId)
        {
            var groups = new List<SceneYamlNode>();
            var groupByKey = new Dictionary<object, SceneYamlNode>();
            foreach (var key in UnityYamlDiffBuilder.GetModificationEntries(instance).Keys)
            {
                var targetId = key.Substring(0, key.IndexOf("::", StringComparison.Ordinal));
                var target = UnityYamlDiffBuilder.DescribeModificationTarget(sourceGuid, targetId);
                object groupKey = target.Owner != null ? target.Owner : targetId;
                if (!groupByKey.TryGetValue(groupKey, out var group))
                {
                    group = new SceneYamlNode
                    {
                        Kind = SceneYamlNodeKind.PrefabInstanceObject,
                        Label = target.OwnerLabel,
                        Icon = "GameObject Icon",
                        FileId = instance.FileId,
                        PrefabInstanceId = instance.FileId,
                        SourceFileId = LocalFileId(target.Owner),
                        SourceGuid = sourceGuid,
                        TargetIds = new List<string>(),
                    };
                    groupByKey[groupKey] = group;
                    groups.Add(group);
                }
                if (!group.TargetIds.Contains(targetId)) group.TargetIds.Add(targetId);
            }
            return groups.Select(g => new TreeViewItemData<SceneYamlNode>(nextId(), g));
        }

        // ------------------------------------------------------------------------- rows --

        public static List<SceneYamlRow> BuildRows(SceneYamlNode node, Dictionary<long, GitYamlDocument> byId)
        {
            var rows = new List<SceneYamlRow> { new() { IsHeader = true, Property = node.Label, HeaderIcon = node.Icon } };
            if (!byId.TryGetValue(node.FileId, out var doc)) return rows;

            switch (node.Kind)
            {
                case SceneYamlNodeKind.GameObject:
                    AddFields(rows, doc.Fields, UnityYamlDiffBuilder.GameObjectIgnoredKeys, byId, typeof(GameObject));
                    foreach (var componentId in UnityYamlDiffBuilder.CollectComponentIds(doc))
                    {
                        if (!byId.TryGetValue(componentId, out var component) || component.Stripped) continue;
                        var typeName = component.TypeName ?? "Component";
                        rows.Add(new SceneYamlRow
                        {
                            IsHeader = true,
                            Property = typeName == "MonoBehaviour" ? UnityYamlDiffBuilder.ResolveScriptLabel(component) : typeName,
                            HeaderIcon = UnityYamlDiffBuilder.IconForComponentType(typeName),
                        });
                        AddFields(rows, component.Fields, UnityYamlDiffBuilder.ComponentIgnoredKeys, byId, UnityYamlDiffBuilder.ResolveComponentType(typeName, component));
                    }
                    break;

                case SceneYamlNodeKind.PrefabInstance:
                    var sourcePath = AssetDatabase.GUIDToAssetPath(node.SourceGuid ?? "");
                    rows.Add(new SceneYamlRow { Property = "Source Prefab", Value = string.IsNullOrEmpty(sourcePath) ? $"(missing) guid:{node.SourceGuid}" : AssetDatabase.LoadMainAssetAtPath(sourcePath) });
                    AddList(rows, doc, "m_RemovedComponents", "Removed Components", node.SourceGuid);
                    AddList(rows, doc, "m_RemovedGameObjects", "Removed GameObjects", node.SourceGuid);
                    break;

                case SceneYamlNodeKind.PrefabInstanceObject:
                    rows.Clear(); // each target gets its own header below, the object's own included
                    var entries = UnityYamlDiffBuilder.GetModificationEntries(doc);
                    foreach (var targetId in node.TargetIds)
                    {
                        var target = UnityYamlDiffBuilder.DescribeModificationTarget(node.SourceGuid, targetId);
                        rows.Add(new SceneYamlRow { IsHeader = true, Property = target.Label, HeaderIcon = target.Icon });
                        foreach (var (key, entry) in entries)
                        {
                            if (!key.StartsWith(targetId + "::", StringComparison.Ordinal)) continue;
                            rows.Add(new SceneYamlRow
                            {
                                Property = UnityYamlDiffBuilder.HumanizePropertyPath(key.Substring(targetId.Length + 2)),
                                Value = UnityYamlDiffBuilder.ResolveModificationValue(entry, byId),
                            });
                        }
                    }
                    break;
            }
            return rows;
        }

        private static void AddFields(List<SceneYamlRow> rows, Dictionary<string, object> fields, HashSet<string> ignoredKeys, Dictionary<long, GitYamlDocument> byId, Type declaringType)
        {
            foreach (var (key, raw) in fields)
            {
                if (ignoredKeys.Contains(key)) continue;
                rows.Add(new SceneYamlRow
                {
                    Property = UnityYamlFieldNames.Humanize(key),
                    Value = UnityYamlValueConverter.ToDisplayValue(raw, byId, key, declaringType),
                });
            }
        }

        private static void AddList(List<SceneYamlRow> rows, GitYamlDocument instance, string fieldKey, string label, string sourceGuid)
        {
            var list = UnityYamlDiffBuilder.GetModificationList(instance, fieldKey);
            if (list.Count > 0) rows.Add(new SceneYamlRow { Property = label, Value = UnityYamlDiffBuilder.ResolveListDisplay(list, sourceGuid) });
        }

        // ------------------------------------------------------------------- ordering --

        /// <summary>The scene roots' saved order: the SceneRoots document (Unity 2022+), else each root
        /// transform's m_RootOrder.</summary>
        private static List<object> SceneRootTransforms(Dictionary<long, GitYamlDocument> byId)
        {
            var sceneRoots = byId.Values.FirstOrDefault(d => d.TypeName == "SceneRoots");
            if (sceneRoots != null && sceneRoots.Fields.TryGetValue("m_Roots", out var raw) && raw is List<object> list) return list;

            return byId.Values
                .Where(d => d.TypeName is "Transform" or "RectTransform" && d.Fields.GetValueOrDefault("m_Father") is Dictionary<string, object> father &&
                            UnityYamlDiffBuilder.TryGetFileId(father, out var fatherId) && fatherId == 0)
                .OrderBy(d => int.TryParse(d.Fields.GetValueOrDefault("m_RootOrder") as string, out var order) ? order : int.MaxValue)
                .Select(d => (object)new Dictionary<string, object> { ["fileID"] = d.FileId.ToString() })
                .ToList();
        }

        private static List<object> ChildTransforms(GitYamlDocument transform) =>
            transform.Fields.TryGetValue("m_Children", out var raw) && raw is List<object> list ? list : new List<object>();

        /// <summary>Node id -> index among its siblings, from an ordered list of transform references.
        /// A stripped transform stands for the PrefabInstance it belongs to.</summary>
        private static Dictionary<long, int> SiblingOrder(List<object> transformRefs, Dictionary<long, GitYamlDocument> byId)
        {
            var order = new Dictionary<long, int>();
            for (var i = 0; i < transformRefs.Count; i++)
            {
                if (transformRefs[i] is not Dictionary<string, object> map || !UnityYamlDiffBuilder.TryGetFileId(map, out var transformId)) continue;
                if (!byId.TryGetValue(transformId, out var transform)) continue;
                var ownerKey = transform.Stripped ? "m_PrefabInstance" : "m_GameObject";
                if (transform.Fields.GetValueOrDefault(ownerKey) is Dictionary<string, object> ownerMap &&
                    UnityYamlDiffBuilder.TryGetFileId(ownerMap, out var ownerId))
                    order.TryAdd(ownerId, i);
            }
            return order;
        }

        private static IEnumerable<long> Sort(List<long> ids, Dictionary<long, int> order) =>
            ids.OrderBy(id => order.TryGetValue(id, out var index) ? index : int.MaxValue);

        // ---------------------------------------------------------------------- assets --

        private static GameObject LoadSourceRoot(string sourceGuid)
        {
            var path = sourceGuid != null ? AssetDatabase.GUIDToAssetPath(sourceGuid) : null;
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static long LocalFileId(UnityEngine.Object obj) =>
            obj != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long fileId) ? fileId : 0;

        /// <summary>The Hierarchy's icon for an instance root: typed from its own source asset (an FBX
        /// nested in a regular prefab is still a Model).</summary>
        internal static string PrefabIcon(GameObject sourceRoot) => sourceRoot == null
            ? "Prefab Icon"
            : PrefabUtility.GetPrefabAssetType(sourceRoot) switch
            {
                PrefabAssetType.Model => "PrefabModel Icon",
                PrefabAssetType.Variant => "PrefabVariant Icon",
                _ => "Prefab Icon",
            };
    }
}
