using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGitTool
{
    internal sealed class SceneYamlNode
    {
        public YamlHierarchy Hierarchy;
        public string SceneGuid;
        public string ScenePath;
        /// <summary>Null on a scene's own node.</summary>
        public YamlObject GameObject;
        public string Label;
        public string Icon;
        /// <summary>On an instance root (or the scene node, for an instance not in the tree).</summary>
        public List<DeadPrefabEntry> DeadEntries = new();
        public Action<List<DeadPrefabEntry>> CleanDeadEntries;
    }

    internal sealed class SceneYamlRow
    {
        public string Property;
        /// <summary>A display value for <see cref="FieldFactory"/>, or a <see cref="SerializedProperty"/>
        /// of an imported model (no YAML to read it from).</summary>
        public object Value;
        public bool IsHeader;
        public string HeaderIcon;
        public bool Overridden;
        public string ActionLabel;
        public string ActionTooltip;
        public Action Action;
    }

    /// <summary>The scene file's Hierarchy from its YAML, prefab instances expanded down to their
    /// innermost objects (<see cref="YamlHierarchy"/>). Rows are built per node on demand.</summary>
    internal static class SceneYamlTree
    {
        /// <summary>A scene's node (as the Hierarchy shows each open scene) over its root GameObjects;
        /// <paramref name="hierarchy"/> is null for a scene with no file yet.</summary>
        public static TreeViewItemData<SceneYamlNode> BuildScene(string label, string scenePath, YamlHierarchy hierarchy, Func<int> nextId,
            List<DeadPrefabEntry> deadEntries = null, Action<List<DeadPrefabEntry>> cleanDeadEntries = null)
        {
            var scene = new SceneYamlNode
            {
                Hierarchy = hierarchy,
                SceneGuid = string.IsNullOrEmpty(scenePath) ? null : AssetDatabase.AssetPathToGUID(scenePath),
                ScenePath = scenePath,
                Label = label,
                Icon = "SceneAsset Icon",
                CleanDeadEntries = cleanDeadEntries,
            };
            var deadByRoot = new Dictionary<long, List<DeadPrefabEntry>>();
            foreach (var entry in deadEntries ?? new List<DeadPrefabEntry>())
            {
                var rootId = hierarchy?.InstanceRoots.GetValueOrDefault(entry.InstanceId) ?? 0;
                if (!deadByRoot.TryGetValue(rootId, out var list)) deadByRoot[rootId] = list = new List<DeadPrefabEntry>();
                list.Add(entry);
            }
            var icons = new Dictionary<string, string>();
            var children = hierarchy == null
                ? new List<TreeViewItemData<SceneYamlNode>>()
                : SceneRoots(hierarchy).Select(go => BuildNode(go, scene, icons, nextId, deadByRoot)).ToList();
            // An instance whose root isn't in the tree: listed on the scene itself.
            scene.DeadEntries = deadByRoot.Where(kv => kv.Key == 0 || !hierarchy.Objects.ContainsKey(kv.Key)).SelectMany(kv => kv.Value).ToList();
            return new TreeViewItemData<SceneYamlNode>(nextId(), scene, children);
        }

        private static TreeViewItemData<SceneYamlNode> BuildNode(YamlObject go, SceneYamlNode scene, Dictionary<string, string> icons, Func<int> nextId,
            Dictionary<long, List<DeadPrefabEntry>> deadByRoot)
        {
            var node = new SceneYamlNode
            {
                Hierarchy = scene.Hierarchy,
                SceneGuid = scene.SceneGuid,
                ScenePath = scene.ScenePath,
                GameObject = go,
                Label = go.Name,
                Icon = go.InstanceGuid != null ? PrefabIcon(go.InstanceGuid, icons) : "GameObject Icon",
                DeadEntries = deadByRoot.GetValueOrDefault(go.Id) ?? new List<DeadPrefabEntry>(),
                CleanDeadEntries = scene.CleanDeadEntries,
            };
            var children = scene.Hierarchy.ChildGameObjects(go).Select(child => BuildNode(child, scene, icons, nextId, deadByRoot)).ToList();
            return new TreeViewItemData<SceneYamlNode>(nextId(), node, children);
        }

        /// <summary>The SceneRoots document's order (Unity 2022+), else the root transforms' m_RootOrder.
        /// SceneRoots lists a root prefab instance by its PrefabInstance document, not by a transform.</summary>
        private static IEnumerable<YamlObject> SceneRoots(YamlHierarchy hierarchy)
        {
            var sceneRoots = hierarchy.ById.Values.FirstOrDefault(d => d.TypeName == "SceneRoots");
            var gameObjectIds = sceneRoots?.Fields.GetValueOrDefault("m_Roots") is List<object> roots
                ? roots.OfType<Dictionary<string, object>>()
                    .Select(m => UnityYamlDiffBuilder.TryGetFileId(m, out var id) ? id : 0)
                    .Select(id => hierarchy.InstanceRoots.TryGetValue(id, out var instanceRoot) ? instanceRoot
                        : hierarchy.Objects.GetValueOrDefault(hierarchy.MapLocalId(id))?.GameObject ?? 0)
                : hierarchy.Objects.Values.Where(o => o.IsTransform && o.Father == 0).OrderBy(t => t.RootOrder).Select(t => t.GameObject);
            return gameObjectIds.Select(id => hierarchy.Objects.GetValueOrDefault(id)).Where(go => go != null);
        }

        // ------------------------------------------------------------------------- rows --

        public static List<SceneYamlRow> BuildRows(SceneYamlNode node)
        {
            var go = node.GameObject;
            var hierarchy = node.Hierarchy;
            var rows = new List<SceneYamlRow> { new() { IsHeader = true, Property = node.Label, HeaderIcon = node.Icon } };
            AddDeadEntryRows(rows, node);
            if (go == null)
            {
                if (!string.IsNullOrEmpty(node.ScenePath))
                    rows.Add(new SceneYamlRow { Property = "Scene Asset", Value = AssetDatabase.LoadAssetAtPath<SceneAsset>(node.ScenePath) });
                return rows;
            }
            if (go.InstanceGuid != null)
            {
                var path = AssetDatabase.GUIDToAssetPath(go.InstanceGuid);
                rows.Add(new SceneYamlRow { Property = "Source Prefab", Value = string.IsNullOrEmpty(path) ? $"(missing) guid:{go.InstanceGuid}" : AssetDatabase.LoadMainAssetAtPath(path) });
            }
            AddFields(rows, go, UnityYamlDiffBuilder.GameObjectIgnoredKeys, typeof(GameObject));

            foreach (var component in hierarchy.ComponentsOf(go))
            {
                rows.Add(new SceneYamlRow
                {
                    IsHeader = true,
                    Property = component.ModelObject != null ? component.TypeName
                        : component.TypeName == "MonoBehaviour" ? UnityYamlDiffBuilder.ResolveScriptLabel(component.Doc)
                        : component.TypeName,
                    HeaderIcon = UnityYamlDiffBuilder.IconForComponentType(component.TypeName),
                });
                AddFields(rows, component, UnityYamlDiffBuilder.ComponentIgnoredKeys,
                    component.ModelObject != null ? component.ModelObject.GetType() : UnityYamlDiffBuilder.ResolveComponentType(component.TypeName, component.Doc));
            }
            return rows;
        }

        private static void AddDeadEntryRows(List<SceneYamlRow> rows, SceneYamlNode node)
        {
            if (node.DeadEntries.Count == 0) return;
            var entries = node.DeadEntries;
            rows.Add(new SceneYamlRow
            {
                IsHeader = true,
                Property = $"Dead prefab entries ({entries.Count})",
                HeaderIcon = "console.warnicon.sml",
                ActionLabel = "Clean",
                ActionTooltip = "These entries point at objects that no longer exist in their source asset: Unity ignores them " +
                                "on load, drops the added objects on the next save and keeps the rest. Clean removes them all from the scene file.",
                Action = node.CleanDeadEntries != null ? () => node.CleanDeadEntries(entries) : null,
            });
            rows.AddRange(entries.Select(e => new SceneYamlRow { Property = e.Label, Value = e.TargetLabel }));
        }

        private static void AddFields(List<SceneYamlRow> rows, YamlObject obj, HashSet<string> ignoredKeys, Type declaringType)
        {
            if (obj.ModelObject != null) { AddModelFields(rows, obj, ignoredKeys); return; }

            var (fields, overridden) = YamlHierarchy.EffectiveFields(obj);
            foreach (var (key, raw) in fields)
            {
                if (ignoredKeys.Contains(key)) continue;
                rows.Add(new SceneYamlRow
                {
                    Property = UnityYamlFieldNames.Humanize(key),
                    Value = UnityYamlValueConverter.ToDisplayValue(raw, obj.DocFile, key, declaringType),
                    Overridden = overridden.Contains(key),
                });
            }
        }

        /// <summary>An imported model's object: its serialized properties, an overridden one replaced by its
        /// override rows (the asset holds the unmodified value).</summary>
        private static void AddModelFields(List<SceneYamlRow> rows, YamlObject obj, HashSet<string> ignoredKeys)
        {
            var overridesByKey = obj.Overrides.GroupBy(o => o.PropertyPath.Split('.')[0]).ToDictionary(g => g.Key, g => g.ToList());
            var property = new SerializedObject(obj.ModelObject).GetIterator();
            for (var enter = true; property.NextVisible(enter); enter = false)
            {
                if (ignoredKeys.Contains(property.propertyPath) || property.propertyPath == "m_Script") continue;
                if (overridesByKey.TryGetValue(property.propertyPath, out var overrides))
                    rows.AddRange(overrides.Select(o => new SceneYamlRow
                    {
                        Property = UnityYamlDiffBuilder.HumanizePropertyPath(o.PropertyPath),
                        Value = UnityYamlDiffBuilder.ResolveModificationValue(o.Entry, o.File),
                        Overridden = true,
                    }));
                else
                    rows.Add(new SceneYamlRow { Property = property.displayName, Value = property.Copy() });
            }
        }

        /// <summary>The Hierarchy's icon for an instance root, typed from its own source asset (an FBX
        /// nested in a regular prefab is still a Model).</summary>
        private static string PrefabIcon(string guid, Dictionary<string, string> cache)
        {
            if (cache.TryGetValue(guid, out var icon)) return icon;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            return cache[guid] = root == null ? "Prefab Icon" : PrefabUtility.GetPrefabAssetType(root) switch
            {
                PrefabAssetType.Model => "PrefabModel Icon",
                PrefabAssetType.Variant => "PrefabVariant Icon",
                _ => "Prefab Icon",
            };
        }
    }
}
