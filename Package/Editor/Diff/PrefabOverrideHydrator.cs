using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityGitTool.Yaml;

namespace UnityGitTool.Diff
{
    /// <summary>
    /// Stripped prefab-instance placeholders (a Transform/Component referenced from outside its
    /// source prefab, e.g. via <c>m_Father</c> or a script field) carry no field data of their
    /// own — Unity stores the actual overridden values on the owning <c>PrefabInstance</c>
    /// document's <c>m_Modification.m_Modifications</c> list instead, keyed by
    /// <c>target.fileID</c>. Left unresolved, every stripped placeholder renders as an empty
    /// component in the diff view, and several distinct overridden objects sharing one
    /// PrefabInstance (see <see cref="ObjectGrouper"/>) look like duplicate, content-less
    /// sections of the same component type. <see cref="Hydrate"/> copies each placeholder's own
    /// overrides back onto its <see cref="UnityYamlDocument.Fields"/>, so the existing field-row
    /// rendering (vectors, colors, references, ...) picks them up and each section shows what it
    /// actually overrides.
    /// <para/>
    /// Most overridden components (e.g. a Rigidbody whose mass changed) have no stripped
    /// placeholder at all — Unity only emits one when something in this file needs to *reference*
    /// the object (a parent's <c>m_Children</c>, a script field, ...), which a bare property
    /// override never requires. Those overrides would otherwise only be visible as raw
    /// target/propertyPath/value entries buried inside the PrefabInstance's own section.
    /// <see cref="SynthesizeMissingOverrides"/> gives each of those targets its own virtual
    /// component section too, resolving its real type name (e.g. "Rigidbody") by reading the
    /// referenced source prefab asset.
    /// </summary>
    internal static class PrefabOverrideHydrator
    {
        private static readonly Regex ArrayIndexRegex = new(@"^data\[(\d+)\]$", RegexOptions.Compiled);

        public static void Hydrate(List<UnityYamlDocument> docs)
        {
            var prefabInstancesByFileId = docs
                .Where(d => d.RootKey == "PrefabInstance")
                .ToDictionary(d => d.FileId);

            foreach (var doc in docs)
            {
                if (!doc.Stripped || doc.RootKey == "PrefabInstance") continue;
                if (!TryGetOwnerPrefabInstanceId(doc, out var piId)) continue;
                if (!prefabInstancesByFileId.TryGetValue(piId, out var prefabInstanceDoc)) continue;

                // A modification's target.fileID lives in the SOURCE prefab's fileId space, not
                // the local placeholder's own &fileId — the placeholder's m_CorrespondingSourceObject
                // is what maps it back to that source-side id, so that's what we match on.
                if (!TryGetCorrespondingSourceFileId(doc, out var sourceFileId)) continue;

                // A stripped placeholder often exists only because something ELSE references it
                // (e.g. a script field pointing at this Transform/Collider) — not because any of
                // its OWN properties were overridden. Left as-is it would carry none of its real
                // field values at all (only the bookkeeping m_CorrespondingSourceObject/
                // m_PrefabInstance/m_PrefabAsset keys), rendering as a component section with zero
                // visible properties. Seed it with the source's own current values first — same
                // idea as SynthesizeMissingOverrides' BuildSyntheticDoc — so it shows exactly what
                // Unity's Inspector would, then let any actual override win on top.
                var guid = GetSourcePrefabGuid(prefabInstanceDoc);
                var sourceDoc = guid != null ? SourcePrefabResolver.ResolveDocument(guid, sourceFileId) : null;
                if (sourceDoc != null)
                {
                    foreach (var kv in sourceDoc.Fields)
                    {
                        // Never seed these — they're the source's OWN (non-instance) bookkeeping
                        // values and would stomp this placeholder's real scene-relative ones (see
                        // ObjectGrouper.OwnerOf, which depends on m_GameObject staying absent here).
                        if (kv.Key is "m_GameObject" or "m_CorrespondingSourceObject" or "m_PrefabInstance" or "m_PrefabAsset") continue;
                        if (!doc.Fields.ContainsKey(kv.Key)) doc.Fields[kv.Key] = DeepCopyValue(kv.Value);
                    }
                }

                foreach (var modification in GetModificationsTargeting(prefabInstanceDoc, sourceFileId))
                {
                    ApplyModification(doc.Fields, modification);
                }
            }
        }

        private static string GetSourcePrefabGuid(UnityYamlDocument prefabInstanceDoc) =>
            prefabInstanceDoc.Fields.TryGetValue("m_SourcePrefab", out var sp) &&
            sp is Dictionary<string, object> spMap &&
            spMap.TryGetValue("guid", out var g)
                ? g?.ToString()
                : null;

        /// <summary>Adds one virtual, always-stripped document per overridden target that has no
        /// placeholder of its own, on BOTH sides at once, so <see cref="ObjectGrouper"/> and
        /// <see cref="InspectorFieldView"/> render it exactly like any other component section
        /// (it groups under the same PrefabInstance via the synthesized <c>m_PrefabInstance</c>
        /// field). Doing both sides together (rather than each independently) matters: the
        /// component itself always pre-exists in the source prefab — a modification can only
        /// target an existing object — so a property that's overridden on only one side (a brand
        /// new override, or one just removed) must still show a matching document on the OTHER
        /// side too, seeded with the source's own (inherited/default) field values, or the whole
        /// component would incorrectly read as freshly Added/Removed instead of Modified. The
        /// synthesized FileId combines the owning instance's own fileId with the target's, which
        /// stays stable across the two compared revisions for the same (instance, target) pair —
        /// the identity <see cref="UnityYamlDiff"/> already matches documents by.</summary>
        public static void SynthesizeMissingOverrides(List<UnityYamlDocument> before, List<UnityYamlDocument> after)
        {
            // Materialized eagerly: the loop below adds synthetic docs to before/after as it
            // goes, and a lazy query re-evaluated over those same lists would throw on the
            // resulting mutation-during-enumeration.
            var piIds = before.Concat(after)
                .Where(d => d.RootKey == "PrefabInstance")
                .Select(d => d.FileId)
                .Distinct()
                .ToList();

            foreach (var piId in piIds)
            {
                var beforePi = before.FirstOrDefault(d => d.RootKey == "PrefabInstance" && d.FileId == piId);
                var afterPi = after.FirstOrDefault(d => d.RootKey == "PrefabInstance" && d.FileId == piId);

                var coveredBefore = AlreadyCoveredTargets(before, piId);
                var coveredAfter = AlreadyCoveredTargets(after, piId);

                var targetFileIds = new HashSet<long>();
                if (beforePi != null) targetFileIds.UnionWith(GetAllModifications(beforePi).Select(GetTargetFileId).Where(id => id is > 0).Select(id => id!.Value));
                if (afterPi != null) targetFileIds.UnionWith(GetAllModifications(afterPi).Select(GetTargetFileId).Where(id => id is > 0).Select(id => id!.Value));

                foreach (var targetFileId in targetFileIds)
                {
                    if (coveredBefore.Contains(targetFileId) && coveredAfter.Contains(targetFileId)) continue;

                    var anyModification = (beforePi != null ? GetModificationsTargeting(beforePi, targetFileId) : Enumerable.Empty<Dictionary<string, object>>())
                        .Concat(afterPi != null ? GetModificationsTargeting(afterPi, targetFileId) : Enumerable.Empty<Dictionary<string, object>>())
                        .FirstOrDefault();
                    var guid = anyModification != null ? GetTargetGuid(anyModification) : null;
                    var sourceDoc = guid != null ? SourcePrefabResolver.ResolveDocument(guid, targetFileId) : null;

                    var combinedId = IdMixer.Combine(piId, targetFileId);

                    // Only synthesize a counterpart when the OWNING instance itself exists on that
                    // side too — if beforePi/afterPi is null, the whole instance was added/removed,
                    // and every one of its overridden targets must read the same way (no synthetic
                    // "default value" baseline to fake a Modified status against).
                    if (!coveredBefore.Contains(targetFileId) && beforePi != null)
                    {
                        before.Add(BuildSyntheticDoc(combinedId, piId, targetFileId, guid, sourceDoc,
                            GetModificationsTargeting(beforePi, targetFileId)));
                    }

                    if (!coveredAfter.Contains(targetFileId) && afterPi != null)
                    {
                        after.Add(BuildSyntheticDoc(combinedId, piId, targetFileId, guid, sourceDoc,
                            GetModificationsTargeting(afterPi, targetFileId)));
                    }
                }
            }
        }

        private static HashSet<long> AlreadyCoveredTargets(List<UnityYamlDocument> docs, long piId) => docs
            .Where(d => d.Stripped && TryGetOwnerPrefabInstanceId(d, out var owner) && owner == piId)
            .Select(d => TryGetCorrespondingSourceFileId(d, out var src) ? src : (long?)null)
            .Where(src => src.HasValue)
            .Select(src => src!.Value)
            .ToHashSet();

        private static UnityYamlDocument BuildSyntheticDoc(
            long fileId, long piId, long targetFileId, string guid, UnityYamlDocument sourceDoc, IEnumerable<Dictionary<string, object>> modifications)
        {
            var doc = new UnityYamlDocument
            {
                ClassId = 0,
                FileId = fileId,
                RootKey = sourceDoc?.RootKey ?? "Overridden Component",
                Stripped = true,
                // Seed with the source component's own (inherited/default) field values, if
                // resolved, so a property overridden on only one side still shows every other
                // field identically on both — and the overridden one as a genuine value change,
                // not an entire component appearing out of nowhere.
                Fields = sourceDoc != null ? DeepCopyFields(sourceDoc.Fields) : new Dictionary<string, object>(),
            };

            // Must come after the seed copy — the source doc's own m_PrefabInstance/
            // m_CorrespondingSourceObject (its own {fileID: 0}, since the source isn't itself an
            // instance) would otherwise overwrite the ones ObjectGrouper's per-nested-object
            // splitting depends on (see ObjectGrouper.TryResolveOwningSourceGameObjectId).
            //
            // A component that has one also deep-copies its own m_GameObject — a reference into
            // the SOURCE PREFAB's raw fileId space (e.g. the Rigidbody on PBF_A320's root pointing
            // at PBF_A320.prefab's own root GameObject fileId), meaningless in this diffed file.
            // ObjectGrouper.OwnerOf checks m_GameObject BEFORE m_CorrespondingSourceObject, so left
            // in place it resolves this synthetic doc to a bogus standalone group keyed by that raw
            // source fileId instead of going through ResolveStrippedOwner like every other stripped
            // placeholder — removing it lets the normal (correct) path take over.
            doc.Fields.Remove("m_GameObject");
            doc.Fields["m_PrefabInstance"] = new Dictionary<string, object> { ["fileID"] = piId.ToString() };
            if (guid != null)
            {
                doc.Fields["m_CorrespondingSourceObject"] = new Dictionary<string, object>
                {
                    ["fileID"] = targetFileId.ToString(), ["guid"] = guid, ["type"] = "3",
                };
            }

            if (modifications != null)
            {
                foreach (var modification in modifications)
                {
                    ApplyModification(doc.Fields, modification);
                }
            }

            return doc;
        }

        private static Dictionary<string, object> DeepCopyFields(Dictionary<string, object> source)
        {
            var copy = new Dictionary<string, object>();
            foreach (var kv in source) copy[kv.Key] = DeepCopyValue(kv.Value);
            return copy;
        }

        private static object DeepCopyValue(object value) => value switch
        {
            Dictionary<string, object> map => DeepCopyFields(map),
            List<object> list => list.Select(DeepCopyValue).ToList(),
            _ => value,
        };

        private static IEnumerable<Dictionary<string, object>> GetAllModifications(UnityYamlDocument prefabInstanceDoc)
        {
            if (!prefabInstanceDoc.Fields.TryGetValue("m_Modification", out var modObj) ||
                modObj is not Dictionary<string, object> modMap ||
                !modMap.TryGetValue("m_Modifications", out var modsObj) ||
                modsObj is not List<object> mods)
            {
                yield break;
            }

            foreach (var item in mods)
            {
                if (item is Dictionary<string, object> entry) yield return entry;
            }
        }

        private static long? GetTargetFileId(Dictionary<string, object> modification) =>
            modification.TryGetValue("target", out var target) &&
            target is Dictionary<string, object> targetMap &&
            targetMap.TryGetValue("fileID", out var fid) &&
            long.TryParse(fid?.ToString(), out var id)
                ? id
                : null;

        private static string GetTargetGuid(Dictionary<string, object> modification) =>
            modification.TryGetValue("target", out var target) &&
            target is Dictionary<string, object> targetMap &&
            targetMap.TryGetValue("guid", out var guid)
                ? guid?.ToString()
                : null;

        private static bool TryGetOwnerPrefabInstanceId(UnityYamlDocument doc, out long piId)
        {
            piId = 0;
            return doc.Fields.TryGetValue("m_PrefabInstance", out var pi) &&
                   pi is Dictionary<string, object> map &&
                   map.TryGetValue("fileID", out var fid) &&
                   long.TryParse(fid?.ToString(), out piId) && piId != 0;
        }

        private static bool TryGetCorrespondingSourceFileId(UnityYamlDocument doc, out long sourceFileId)
        {
            sourceFileId = 0;
            return doc.Fields.TryGetValue("m_CorrespondingSourceObject", out var cso) &&
                   cso is Dictionary<string, object> map &&
                   map.TryGetValue("fileID", out var fid) &&
                   long.TryParse(fid?.ToString(), out sourceFileId) && sourceFileId != 0;
        }

        private static IEnumerable<Dictionary<string, object>> GetModificationsTargeting(UnityYamlDocument prefabInstanceDoc, long sourceFileId)
        {
            if (!prefabInstanceDoc.Fields.TryGetValue("m_Modification", out var modObj) ||
                modObj is not Dictionary<string, object> modMap ||
                !modMap.TryGetValue("m_Modifications", out var modsObj) ||
                modsObj is not List<object> mods)
            {
                yield break;
            }

            foreach (var item in mods)
            {
                if (item is not Dictionary<string, object> entry) continue;
                if (!entry.TryGetValue("target", out var target) ||
                    target is not Dictionary<string, object> targetMap ||
                    !targetMap.TryGetValue("fileID", out var targetFid) ||
                    !long.TryParse(targetFid?.ToString(), out var targetId) ||
                    targetId != sourceFileId)
                {
                    continue;
                }

                yield return entry;
            }
        }

        private static void ApplyModification(Dictionary<string, object> fields, Dictionary<string, object> modification)
        {
            if (!modification.TryGetValue("propertyPath", out var ppObj) || string.IsNullOrEmpty(ppObj?.ToString()))
            {
                return;
            }

            var leaf = ResolveLeafValue(modification);
            if (leaf == null) return;

            SetPath(fields, ppObj.ToString().Split('.'), 0, leaf);
        }

        /// <summary>A modification carries either a scalar <c>value</c> or, for reference fields,
        /// an <c>objectReference</c> (fileID/guid/type) with an empty value — mirror that choice
        /// so reference-typed overrides render via the existing fileID row instead of blank text.</summary>
        private static object ResolveLeafValue(Dictionary<string, object> modification)
        {
            if (modification.TryGetValue("objectReference", out var objRef) &&
                objRef is Dictionary<string, object> refMap &&
                refMap.TryGetValue("fileID", out var refFid) &&
                refFid?.ToString() != "0")
            {
                return refMap;
            }

            return modification.TryGetValue("value", out var value) ? value?.ToString() ?? "" : null;
        }

        /// <summary>Recursively walks/creates the nested dictionary (and, for
        /// <c>Array.data[n]</c> segments, list) chain a dotted Unity propertyPath describes,
        /// then writes <paramref name="leaf"/> at the end. E.g. <c>m_LocalPosition.y</c> becomes
        /// <c>fields["m_LocalPosition"]["y"] = leaf</c>, matching the shape a real (non-stripped)
        /// Transform document already serializes, so <see cref="InspectorFieldView"/> renders it
        /// as an ordinary Vector3 row with no special-casing needed.</summary>
        private static void SetPath(object container, string[] segments, int index, object leaf)
        {
            if (index >= segments.Length || container is not Dictionary<string, object> map) return;

            var segment = segments[index];

            // "Foo.Array.data[n]" means Foo is a list; consume all three tokens as one step.
            if (index + 2 < segments.Length && segments[index + 1] == "Array" && ArrayIndexRegex.IsMatch(segments[index + 2]))
            {
                if (!map.TryGetValue(segment, out var listObj) || listObj is not List<object> list)
                {
                    map[segment] = list = new List<object>();
                }

                var arrayIndex = int.Parse(ArrayIndexRegex.Match(segments[index + 2]).Groups[1].Value);
                while (list.Count <= arrayIndex) list.Add(new Dictionary<string, object>());

                if (index + 2 == segments.Length - 1)
                {
                    list[arrayIndex] = leaf;
                    return;
                }

                if (list[arrayIndex] is not Dictionary<string, object>) list[arrayIndex] = new Dictionary<string, object>();
                SetPath(list[arrayIndex], segments, index + 3, leaf);
                return;
            }

            if (index == segments.Length - 1)
            {
                map[segment] = leaf;
                return;
            }

            if (!map.TryGetValue(segment, out var child) || child is not Dictionary<string, object>)
            {
                map[segment] = child = new Dictionary<string, object>();
            }

            SetPath(child, segments, index + 1, leaf);
        }
    }
}
