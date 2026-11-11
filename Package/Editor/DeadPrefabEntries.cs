using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGitTool
{
    internal enum DeadEntryKind { Override, AddedComponent, AddedGameObject, RemovedComponent, RemovedGameObject }

    /// <summary>A prefab instance entry whose target no longer exists in the source asset (a model
    /// re-exported with new ids, a prefab object deleted). Unity skips it on load. Its next save drops an
    /// added object silently (a surprise in the next diff) but keeps a dead override or removed entry for
    /// good, until its own "Remove Unused Overrides".</summary>
    internal sealed class DeadPrefabEntry
    {
        public DeadEntryKind Kind;
        public long InstanceId;
        public string TargetGuid;
        public long TargetId;
        /// <summary><see cref="DeadEntryKind.Override"/> only.</summary>
        public string PropertyPath;
        /// <summary>Added component, or added GameObject's Transform: the document in this file.</summary>
        public long AddedObjectId;
        public string AddedTypeName;

        public string SourceFileName => Path.GetFileName(AssetDatabase.GUIDToAssetPath(TargetGuid));

        public string Label => Kind switch
        {
            DeadEntryKind.Override => $"Override {UnityYamlDiffBuilder.HumanizePropertyPath(PropertyPath)}",
            DeadEntryKind.AddedComponent => $"Added {AddedTypeName ?? "component"}",
            DeadEntryKind.AddedGameObject => "Added GameObject",
            DeadEntryKind.RemovedComponent => "Removed component",
            _ => "Removed GameObject",
        };

        public string TargetLabel => $"missing #{TargetId} ({SourceFileName})";

        public override string ToString() => $"{Label} on {TargetLabel}";
    }

    internal static class DeadPrefabEntries
    {
        /// <summary>Every dead entry of the file's own prefab instances (nested ones belong to their own prefab file).</summary>
        public static List<DeadPrefabEntry> Find(Dictionary<long, GitYamlDocument> byId, YamlHierarchy.SourceCache sources)
        {
            var dead = new List<DeadPrefabEntry>();
            foreach (var instance in byId.Values.Where(d => !d.Stripped && d.TypeName == "PrefabInstance"))
                dead.AddRange(Find(instance, byId, sources));
            return dead;
        }

        public static IEnumerable<DeadPrefabEntry> Find(GitYamlDocument instance, Dictionary<long, GitYamlDocument> byId, YamlHierarchy.SourceCache sources)
        {
            var sourceGuid = UnityYamlDiffBuilder.GetPrefabSourceGuid(instance);

            foreach (var entry in UnityYamlDiffBuilder.GetModificationEntries(instance).Values)
                if (IsDead(entry.GetValueOrDefault("target"), sourceGuid, sources, out var guid, out var id))
                    yield return new DeadPrefabEntry
                    {
                        Kind = DeadEntryKind.Override, InstanceId = instance.FileId, TargetGuid = guid, TargetId = id,
                        PropertyPath = entry.GetValueOrDefault("propertyPath") as string,
                    };

            foreach (var (listKey, kind) in new[] { ("m_AddedComponents", DeadEntryKind.AddedComponent), ("m_AddedGameObjects", DeadEntryKind.AddedGameObject) })
                foreach (var entry in UnityYamlDiffBuilder.GetModificationList(instance, listKey).OfType<Dictionary<string, object>>())
                    if (IsDead(entry.GetValueOrDefault("targetCorrespondingSourceObject"), sourceGuid, sources, out var guid, out var id) &&
                        entry.GetValueOrDefault("addedObject") is Dictionary<string, object> added && UnityYamlDiffBuilder.TryGetFileId(added, out var addedId))
                        yield return new DeadPrefabEntry
                        {
                            Kind = kind, InstanceId = instance.FileId, TargetGuid = guid, TargetId = id, AddedObjectId = addedId,
                            AddedTypeName = byId.GetValueOrDefault(addedId) is { } doc
                                ? doc.TypeName == "MonoBehaviour" ? UnityYamlDiffBuilder.ResolveScriptLabel(doc) : doc.TypeName
                                : null,
                        };

            foreach (var (listKey, kind) in new[] { ("m_RemovedComponents", DeadEntryKind.RemovedComponent), ("m_RemovedGameObjects", DeadEntryKind.RemovedGameObject) })
                foreach (var entry in UnityYamlDiffBuilder.GetModificationList(instance, listKey))
                    if (IsDead(entry, sourceGuid, sources, out var guid, out var id))
                        yield return new DeadPrefabEntry { Kind = kind, InstanceId = instance.FileId, TargetGuid = guid, TargetId = id };
        }

        /// <summary>Dead only when the source is fully known: a missing asset, or one holding an instance
        /// of a missing asset, is a different problem (and its absent ids prove nothing).</summary>
        private static bool IsDead(object reference, string sourceGuid, YamlHierarchy.SourceCache sources, out string guid, out long id)
        {
            guid = null;
            id = 0;
            if (reference is not Dictionary<string, object> map || !UnityYamlDiffBuilder.TryGetFileId(map, out id) || id == 0) return false;
            guid = map.GetValueOrDefault("guid") as string ?? sourceGuid;
            var source = sources.Get(guid);
            if (source == null || source.Incomplete) return false;
            return !source.Objects.ContainsKey(id) && !(source.ById?.ContainsKey(id) ?? false);
        }

        /// <summary>Removes the entries from the file on disk and reimports it. A scene open with unsaved
        /// changes is refused: Unity would ask to keep its in-memory copy over ours.</summary>
        public static bool Clean(string assetPath, IReadOnlyCollection<DeadPrefabEntry> entries, out List<string> skipped)
        {
            skipped = new List<string>();
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(assetPath);
            if (scene.IsValid() && scene.isDirty)
            {
                skipped.Add($"{assetPath}: has unsaved changes, save it first");
                return false;
            }
            var fullPath = Path.GetFullPath(assetPath);
            var text = File.ReadAllText(fullPath);
            var (patched, writeSkipped) = UnityYamlWriter.RemovePrefabEntries(text, UnityYamlParser.Parse(text), entries);
            skipped.AddRange(writeSkipped.Select(s => $"{assetPath}: {s}"));
            File.WriteAllText(fullPath, patched);
            AssetDatabase.ImportAsset(assetPath);
            Debug.Log($"[UnityGitTool] Cleaned {entries.Count} dead prefab entr(ies) in {assetPath}, {writeSkipped.Count} skipped");
            return true;
        }

        // ------------------------------------------------------------------ project scan --

        [MenuItem("Unity Git Tool/Find Dead Prefab Entries", priority = 20)]
        private static void FindInProject()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var sources = new YamlHierarchy.SourceCache();
            var paths = AssetDatabase.FindAssets("t:Prefab t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab") || p.EndsWith(".unity"))
                // Imported samples ship with their own dead entries; _Recovery holds Unity's crash backups.
                .Where(p => !p.StartsWith("Assets/Samples/") && !p.StartsWith("Assets/_Recovery/"))
                .Distinct().ToList();

            var byFile = new Dictionary<string, List<DeadPrefabEntry>>();
            try
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Unity Git Tool", $"Scanning {paths[i]}", (float)i / paths.Count)) return;
                    var byId = UnityYamlDiffBuilder.IndexByFileId(UnityYamlParser.Parse(File.ReadAllText(paths[i])));
                    var dead = Find(byId, sources);
                    if (dead.Count > 0) byFile[paths[i]] = dead;
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            var total = byFile.Values.Sum(l => l.Count);
            Debug.Log($"[UnityGitTool] Dead prefab entries: {total} in {byFile.Count} of {paths.Count} files ({watch.ElapsedMilliseconds} ms)");
            if (total == 0)
            {
                EditorUtility.DisplayDialog("Unity Git Tool", $"No dead prefab entry in {paths.Count} prefabs and scenes.", "OK");
                return;
            }
            foreach (var (path, entries) in byFile)
                Debug.LogWarning($"[UnityGitTool] {path}: {entries.Count} dead prefab entr(ies) — Unity ignores them on load\n" +
                                 string.Join("\n", entries.Select(e => $"  {e}")), AssetDatabase.LoadMainAssetAtPath(path));

            var list = string.Join("\n", byFile.Take(12).Select(kv => $"• {kv.Key} ({kv.Value.Count})")) + (byFile.Count > 12 ? $"\n… +{byFile.Count - 12} more" : "");
            if (!EditorUtility.DisplayDialog("Unity Git Tool",
                    $"{total} prefab instance entr(ies) point at objects that no longer exist in their source (listed in the Console). " +
                    $"Unity ignores them on load; its next save of the file drops the added objects and keeps the rest.\n\n{list}", "Clean them", "Keep")) return;

            var problems = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (path, entries) in byFile)
                {
                    Clean(path, entries, out var skipped);
                    problems.AddRange(skipped);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            foreach (var problem in problems) Debug.LogWarning($"[UnityGitTool] {problem}");
            EditorUtility.DisplayDialog("Unity Git Tool",
                problems.Count == 0 ? $"Cleaned {total} entr(ies) in {byFile.Count} file(s)." : $"Cleaned with {problems.Count} problem(s), see the Console.", "OK");
        }
    }
}
