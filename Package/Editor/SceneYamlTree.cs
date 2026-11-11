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
        public YamlObject GameObject;
        public string Label;
        public string Icon;
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
    }

    /// <summary>The scene file's Hierarchy from its YAML, prefab instances expanded down to their
    /// innermost objects (<see cref="YamlHierarchy"/>). Rows are built per node on demand.</summary>
    internal static class SceneYamlTree
    {
        public static List<TreeViewItemData<SceneYamlNode>> Build(YamlHierarchy hierarchy, Func<int> nextId)
        {
            var icons = new Dictionary<string, string>();
            return SceneRoots(hierarchy).Select(go => BuildNode(go, hierarchy, icons, nextId)).ToList();
        }

        private static TreeViewItemData<SceneYamlNode> BuildNode(YamlObject go, YamlHierarchy hierarchy, Dictionary<string, string> icons, Func<int> nextId)
        {
            var node = new SceneYamlNode
            {
                GameObject = go,
                Label = go.Name,
                Icon = go.InstanceGuid != null ? PrefabIcon(go.InstanceGuid, icons) : "GameObject Icon",
            };
            var children = hierarchy.ChildGameObjects(go).Select(child => BuildNode(child, hierarchy, icons, nextId)).ToList();
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

        public static List<SceneYamlRow> BuildRows(SceneYamlNode node, YamlHierarchy hierarchy)
        {
            var go = node.GameObject;
            var rows = new List<SceneYamlRow> { new() { IsHeader = true, Property = node.Label, HeaderIcon = node.Icon } };
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
