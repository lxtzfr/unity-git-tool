using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGitTool
{
    /// <summary>A GameObject or component as the saved files define it, once prefab instances are expanded.
    /// <see cref="Doc"/> is the document that declares it, in <see cref="DocFile"/> (the source prefab for
    /// an object brought in by an instance); the hierarchy fields are already in the expanded file's ids.</summary>
    internal sealed class YamlObject
    {
        public long Id;
        public string TypeName;
        public GitYamlDocument Doc;
        public Dictionary<long, GitYamlDocument> DocFile;
        /// <summary>Set instead of <see cref="Doc"/> for an object of an imported model (FBX), which has no YAML.</summary>
        public Object ModelObject;

        public string Name;
        public List<long> Components = new();
        public long GameObject;
        public long Father;
        public List<long> Children = new();
        public int RootOrder;

        /// <summary>Property overrides of every instance level, innermost first (the order Unity applies them).</summary>
        public List<YamlOverride> Overrides = new();
        /// <summary>On an instance root GameObject: the source asset of the nearest instance (what the Hierarchy icon shows).</summary>
        public string InstanceGuid;
        /// <summary>The loaded object's GlobalObjectId: own fileID + 0, or fileID in the source prefab + scene instance fileID.</summary>
        public long GlobalObjectId;
        public long GlobalPrefabId;

        public bool IsTransform => TypeName is "Transform" or "RectTransform";
    }

    internal sealed class YamlOverride
    {
        public string PropertyPath;
        public Dictionary<string, object> Entry;
        /// <summary>The file holding the modification, which its objectReference values point into.</summary>
        public Dictionary<long, GitYamlDocument> File;
    }

    /// <summary>A scene or prefab file with every prefab instance expanded from its source, recursively
    /// (nested prefabs, variants, models). An object of instance P whose fileID in the source is C gets
    /// (C ^ P) &amp; long.MaxValue, the id Unity gives it inside a prefab file (override targets and
    /// stripped documents of prefabs use it); a scene's stripped documents use other ids, mapped through
    /// their m_CorrespondingSourceObject.</summary>
    internal sealed class YamlHierarchy
    {
        public Dictionary<long, YamlObject> Objects = new();
        public Dictionary<long, GitYamlDocument> ById;
        public long RootGameObject;
        /// <summary>PrefabInstance fileID -> its root GameObject in this file's ids.</summary>
        public Dictionary<long, long> InstanceRoots = new();
        private Dictionary<long, long> _strippedIds = new();
        private Dictionary<long, List<long>> _childrenByFather;
        private Dictionary<long, List<long>> _componentsByGameObject;

        public long MapLocalId(long id) => _strippedIds.TryGetValue(id, out var mapped) ? mapped : id;

        /// <summary>Parsed and expanded sources of one Build, by asset guid: a prefab placed many times is read once.</summary>
        internal sealed class SourceCache
        {
            private readonly Dictionary<string, YamlHierarchy> _byGuid = new();
            private readonly HashSet<string> _resolving = new();
            public int FilesRead;

            public YamlHierarchy Get(string guid)
            {
                if (string.IsNullOrEmpty(guid)) return null;
                if (_byGuid.TryGetValue(guid, out var cached)) return cached;
                if (!_resolving.Add(guid)) return null; // a prefab containing itself through a broken reference

                YamlHierarchy result = null;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // A package's "Packages/..." path is virtual: it lives in Library/PackageCache or a local folder.
                var physicalPath = string.IsNullOrEmpty(path) ? null : FileUtil.GetPhysicalPath(path);
                if (path.EndsWith(".prefab") && File.Exists(physicalPath))
                {
                    FilesRead++;
                    result = Resolve(UnityYamlDiffBuilder.IndexByFileId(UnityYamlParser.Parse(File.ReadAllText(physicalPath))), this);
                }
                else if (!string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path) is { } model)
                    result = FromModel(model);

                _resolving.Remove(guid);
                return _byGuid[guid] = result;
            }
        }

        public static YamlHierarchy Resolve(Dictionary<long, GitYamlDocument> byId, SourceCache cache)
        {
            var hierarchy = new YamlHierarchy { ById = byId };
            foreach (var doc in byId.Values.Where(d => d.Stripped))
            {
                var instanceId = Ref(doc.Fields, "m_PrefabInstance");
                var sourceId = Ref(doc.Fields, "m_CorrespondingSourceObject");
                if (instanceId != 0 && sourceId != 0) hierarchy._strippedIds[doc.FileId] = Combine(sourceId, instanceId);
            }

            foreach (var doc in byId.Values)
                if (!doc.Stripped && doc.TypeName is not null and not "PrefabInstance")
                    hierarchy.Objects[doc.FileId] = hierarchy.FromDoc(doc);

            foreach (var instance in byId.Values.Where(d => !d.Stripped && d.TypeName == "PrefabInstance"))
                hierarchy.Expand(instance, cache);

            hierarchy.RootGameObject = hierarchy.Objects.Values
                .Where(o => o.IsTransform && o.Father == 0)
                .Select(o => o.GameObject)
                .FirstOrDefault();
            return hierarchy;
        }

        private YamlObject FromDoc(GitYamlDocument doc)
        {
            var obj = new YamlObject
            {
                Id = doc.FileId,
                TypeName = doc.TypeName,
                Doc = doc,
                DocFile = ById,
                GameObject = MapLocalId(Ref(doc.Fields, "m_GameObject")),
                GlobalObjectId = doc.FileId,
            };
            if (doc.TypeName == "GameObject")
            {
                obj.Name = doc.Fields.GetValueOrDefault("m_Name") as string ?? "";
                obj.Components = UnityYamlDiffBuilder.CollectComponentIds(doc).Select(MapLocalId).ToList();
            }
            if (doc.Fields.ContainsKey("m_Father"))
            {
                obj.Father = MapLocalId(Ref(doc.Fields, "m_Father"));
                obj.Children = Refs(doc.Fields, "m_Children").Select(MapLocalId).ToList();
                obj.RootOrder = int.TryParse(doc.Fields.GetValueOrDefault("m_RootOrder") as string, out var order) ? order : 0;
            }
            return obj;
        }

        /// <summary>Copies the source's objects into this file under the instance's ids, minus the removed
        /// ones, with this instance's overrides stacked on top of the source's own.</summary>
        private void Expand(GitYamlDocument instance, SourceCache cache)
        {
            var guid = UnityYamlDiffBuilder.GetPrefabSourceGuid(instance);
            var source = cache.Get(guid);
            if (source == null) return; // missing asset: the instance has nothing to show

            var p = instance.FileId;
            long Map(long id) => id == 0 ? 0 : Combine(id, p);

            var removed = new HashSet<long>(
                UnityYamlDiffBuilder.GetModificationList(instance, "m_RemovedComponents")
                    .Concat(UnityYamlDiffBuilder.GetModificationList(instance, "m_RemovedGameObjects"))
                    .OfType<Dictionary<string, object>>()
                    .Select(m => UnityYamlDiffBuilder.TryGetFileId(m, out var id) ? id : 0));

            var overridesByTarget = new Dictionary<long, List<YamlOverride>>();
            foreach (var (key, entry) in UnityYamlDiffBuilder.GetModificationEntries(instance))
            {
                var separator = key.IndexOf("::", System.StringComparison.Ordinal);
                if (!long.TryParse(key.Substring(0, separator), out var target)) continue;
                if (!overridesByTarget.TryGetValue(target, out var list)) overridesByTarget[target] = list = new List<YamlOverride>();
                list.Add(new YamlOverride { PropertyPath = key.Substring(separator + 2), Entry = entry, File = ById });
            }

            var modification = instance.Fields.GetValueOrDefault("m_Modification") as Dictionary<string, object>;
            var transformParent = modification != null ? MapLocalId(Ref(modification, "m_TransformParent")) : 0;

            foreach (var src in source.Objects.Values)
            {
                if (removed.Contains(src.Id) || (src.GameObject != 0 && removed.Contains(src.GameObject))) continue;

                var clone = new YamlObject
                {
                    Id = Map(src.Id),
                    TypeName = src.TypeName,
                    Doc = src.Doc,
                    DocFile = src.DocFile,
                    ModelObject = src.ModelObject,
                    Name = src.Name,
                    Components = src.Components.Select(Map).ToList(),
                    GameObject = Map(src.GameObject),
                    Father = Map(src.Father),
                    Children = src.Children.Select(Map).ToList(),
                    RootOrder = src.RootOrder,
                    Overrides = new List<YamlOverride>(src.Overrides),
                    InstanceGuid = src.InstanceGuid,
                    GlobalObjectId = src.Id,
                    GlobalPrefabId = p,
                };
                if (overridesByTarget.TryGetValue(src.Id, out var overrides))
                {
                    clone.Overrides.AddRange(overrides);
                    if (overrides.LastOrDefault(o => o.PropertyPath == "m_Name")?.Entry.GetValueOrDefault("value") is string name) clone.Name = name;
                    if (overrides.LastOrDefault(o => o.PropertyPath == "m_RootOrder")?.Entry.GetValueOrDefault("value") is string order && int.TryParse(order, out var rootOrder)) clone.RootOrder = rootOrder;
                }
                if (src.IsTransform && src.Father == 0) clone.Father = transformParent;
                // The outer instance wins on a shared root (a variant's root is also its base's instance root).
                if (src.Id == source.RootGameObject)
                {
                    clone.InstanceGuid = guid;
                    InstanceRoots[p] = clone.Id;
                }
                Objects[clone.Id] = clone;
            }
        }

        private static YamlHierarchy FromModel(UnityEngine.GameObject root)
        {
            var hierarchy = new YamlHierarchy();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var go = transform.gameObject;
                var goId = LocalId(go);
                var components = go.GetComponents<Component>().Where(c => c != null).ToList();
                hierarchy.Objects[goId] = new YamlObject
                {
                    Id = goId, TypeName = "GameObject", ModelObject = go, Name = go.name, GlobalObjectId = goId,
                    Components = components.Select(LocalId).ToList(),
                };
                foreach (var component in components)
                {
                    var obj = new YamlObject
                    {
                        Id = LocalId(component), TypeName = component.GetType().Name, ModelObject = component,
                        GameObject = goId, GlobalObjectId = LocalId(component),
                    };
                    if (component is Transform t)
                    {
                        obj.Father = t.parent != null ? LocalId(t.parent) : 0;
                        obj.Children = t.Cast<Transform>().Select(LocalId).ToList();
                        obj.RootOrder = t.GetSiblingIndex();
                    }
                    hierarchy.Objects[obj.Id] = obj;
                }
            }
            hierarchy.RootGameObject = LocalId(root);
            return hierarchy;
        }

        // ------------------------------------------------------------------- navigation --

        /// <summary>A transform's children in saved order, then those added by an instance override.</summary>
        public IEnumerable<YamlObject> ChildGameObjects(YamlObject gameObject)
        {
            var transform = TransformOf(gameObject);
            if (transform == null) return Enumerable.Empty<YamlObject>();
            _childrenByFather ??= Objects.Values.Where(o => o.IsTransform && o.Father != 0)
                .GroupBy(o => o.Father).ToDictionary(g => g.Key, g => g.OrderBy(o => o.RootOrder).Select(o => o.Id).ToList());

            var ids = transform.Children.Where(Objects.ContainsKey).ToList();
            if (_childrenByFather.TryGetValue(transform.Id, out var all)) ids.AddRange(all.Where(id => !ids.Contains(id)));
            return ids.Where(Objects.ContainsKey).Select(id => Objects[id]).Where(t => t.Father == transform.Id)
                .Select(t => Objects.GetValueOrDefault(t.GameObject)).Where(go => go != null);
        }

        /// <summary>The GameObject's components in saved order, then those added by an instance override.</summary>
        public IEnumerable<YamlObject> ComponentsOf(YamlObject gameObject)
        {
            _componentsByGameObject ??= Objects.Values.Where(o => o.GameObject != 0)
                .GroupBy(o => o.GameObject).ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList());
            var ids = gameObject.Components.Where(Objects.ContainsKey).ToList();
            if (_componentsByGameObject.TryGetValue(gameObject.Id, out var all)) ids.AddRange(all.Where(id => !ids.Contains(id)));
            return ids.Select(id => Objects[id]);
        }

        public YamlObject TransformOf(YamlObject gameObject) => ComponentsOf(gameObject).FirstOrDefault(c => c.IsTransform);

        // ---------------------------------------------------------------------- values --

        /// <summary>The object's fields with its overrides applied (a copy when there are any), and the
        /// top-level keys they touched.</summary>
        public static (Dictionary<string, object> Fields, HashSet<string> Overridden) EffectiveFields(YamlObject obj)
        {
            var overridden = new HashSet<string>();
            if (obj.Overrides.Count == 0) return (obj.Doc.Fields, overridden);
            var fields = (Dictionary<string, object>)DeepClone(obj.Doc.Fields);
            foreach (var o in obj.Overrides)
            {
                ApplyOverride(fields, o.PropertyPath, o.Entry);
                overridden.Add(o.PropertyPath.Split('.')[0]);
            }
            return (fields, overridden);
        }

        /// <summary>Writes an override into the parsed YAML tree along its SerializedProperty path
        /// (<c>a.b</c>, <c>list.Array.data[i]</c>, <c>list.Array.size</c>).</summary>
        internal static void ApplyOverride(Dictionary<string, object> root, string path, Dictionary<string, object> entry)
        {
            var value = entry.GetValueOrDefault("value");
            var reference = entry.GetValueOrDefault("objectReference") as Dictionary<string, object>;
            var parts = path.Split('.');
            object container = root;
            for (var i = 0; i < parts.Length; i++)
            {
                if (container is List<object> list)
                {
                    if (parts[i] != "Array" || i == parts.Length - 1) return;
                    var element = parts[++i];
                    if (element == "size")
                    {
                        if (int.TryParse(value as string, out var size) && size >= 0)
                        {
                            if (list.Count > size) list.RemoveRange(size, list.Count - size);
                            while (list.Count < size) list.Add(null);
                        }
                        return;
                    }
                    if (!element.StartsWith("data[") || !int.TryParse(element.Substring(5, element.Length - 6), out var index) || index < 0) return;
                    while (list.Count <= index) list.Add(null);
                    if (i == parts.Length - 1) { list[index] = Leaf(list[index], value, reference); return; }
                    list[index] ??= NewContainer(parts[i + 1]);
                    container = list[index];
                    continue;
                }
                if (container is not Dictionary<string, object> map) return;
                if (i == parts.Length - 1) { map[parts[i]] = Leaf(map.GetValueOrDefault(parts[i]), value, reference); return; }
                if (!map.TryGetValue(parts[i], out var child) || child == null) map[parts[i]] = child = NewContainer(parts[i + 1]);
                container = child;
            }
        }

        private static object NewContainer(string next) => next == "Array" ? new List<object>() : new Dictionary<string, object>();

        // A reference field keeps its {fileID, guid} shape even when overridden to None (fileID 0, empty value).
        private static object Leaf(object existing, object value, Dictionary<string, object> reference) =>
            reference != null && ((existing is Dictionary<string, object> m && m.ContainsKey("fileID")) ||
                                  (reference.GetValueOrDefault("fileID") is string fileId && fileId != "0"))
                ? reference
                : value;

        private static object DeepClone(object value) => value switch
        {
            Dictionary<string, object> map => map.ToDictionary(kv => kv.Key, kv => DeepClone(kv.Value)),
            List<object> list => list.Select(DeepClone).ToList(),
            _ => value,
        };

        // ----------------------------------------------------------------------- helpers --

        private static long Combine(long sourceId, long instanceId) => (sourceId ^ instanceId) & long.MaxValue;

        private static long Ref(Dictionary<string, object> fields, string key) =>
            fields.GetValueOrDefault(key) is Dictionary<string, object> map && UnityYamlDiffBuilder.TryGetFileId(map, out var id) ? id : 0;

        private static IEnumerable<long> Refs(Dictionary<string, object> fields, string key) =>
            fields.GetValueOrDefault(key) is List<object> list
                ? list.OfType<Dictionary<string, object>>().Select(m => UnityYamlDiffBuilder.TryGetFileId(m, out var id) ? id : 0).Where(id => id != 0)
                : Enumerable.Empty<long>();

        private static long LocalId(Object obj) =>
            obj != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long id) ? id : 0;
    }
}
