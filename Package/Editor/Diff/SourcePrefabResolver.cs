using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VisualGitDiff.Yaml;

namespace VisualGitDiff.Diff
{
    /// <summary>
    /// Reads a referenced prefab asset straight off disk (via its guid) and parses it the same
    /// way a diffed revision is, so a nested-prefab-instance override — which only carries a bare
    /// target fileId/guid, never the object's type or name — can be resolved back to something
    /// human-meaningful (a component's type, a GameObject's name, its position in the hierarchy).
    /// This is a best-effort convenience: it reads the asset's current working-copy content
    /// rather than any specific git revision, so a source prefab whose structure changed there
    /// could resolve stale info; callers must treat a null/failed resolution as "show a generic
    /// fallback", never as an error.
    /// <para/>
    /// Cached process-wide by guid (not scoped to one compare), since the same source prefab is
    /// typically referenced by many instances/overrides across a session and re-parsing it every
    /// time would add up.
    /// </summary>
    internal static class SourcePrefabResolver
    {
        private static readonly Dictionary<string, Dictionary<long, UnityYamlDocument>> DocsCache = new();
        private static readonly Dictionary<string, long> RootGameObjectCache = new();
        private static readonly Dictionary<string, Dictionary<long, long>> ParentGameObjectCache = new();
        private static readonly Dictionary<string, Dictionary<long, long>> CompositeParentCache = new();

        public static UnityYamlDocument ResolveDocument(string guid, long fileId) =>
            GetDocsByFileId(guid).TryGetValue(fileId, out var doc) ? doc : null;

        /// <summary>Whether a stripped/synthetic document's own <c>m_CorrespondingSourceObject</c>
        /// no longer resolves to anything in the referenced source prefab — a dangling override
        /// left behind after the target object was removed or restructured there (e.g. a nested
        /// prefab that got unpacked/flattened) without the referencing scene/prefab's own
        /// modifications ever being cleaned up. Unity doesn't prune these automatically, so they
        /// linger as real (if inert) data — never treat this as "the tool failed to parse
        /// something", it means the underlying project data itself is stale.</summary>
        public static bool IsUnresolvedReference(UnityYamlDocument doc)
        {
            if (!doc.Stripped) return false;
            if (!doc.Fields.TryGetValue("m_CorrespondingSourceObject", out var cso) || cso is not Dictionary<string, object> csoMap) return false;
            if (!csoMap.TryGetValue("fileID", out var fidObj) || !long.TryParse(fidObj?.ToString(), out var fileId) || fileId == 0) return false;
            if (!csoMap.TryGetValue("guid", out var guidObj) || guidObj?.ToString() is not { Length: > 0 } guid) return false;

            return ResolveDocument(guid, fileId) == null;
        }

        /// <summary>The fileId of the source prefab's own root GameObject (the one whose
        /// Transform has no <c>m_Father</c>) — lets a caller tell "this override targets the
        /// instance's own root" apart from "this override targets one of its nested children".
        /// <para/>
        /// A Prefab Variant (or any prefab built by combining several reusable sub-prefabs as its
        /// own top-level children) has no such plain root Transform at all — its whole structure
        /// is a set of top-level <c>PrefabInstance</c> documents, each embedding one sub-prefab, tied
        /// together purely via each one's own <c>m_Modification.m_TransformParent</c> (the same shape
        /// a SCENE uses to embed a nested prefab — see <see cref="ObjectGrouper.LinkHierarchy"/>).
        /// Falls back to that composite resolution (see <see cref="GetCompositeParentMap"/>) when no
        /// plain root is found, returning the one top-level instance whose own transform-parent is 0.</summary>
        public static long? ResolveRootGameObjectId(string guid)
        {
            if (RootGameObjectCache.TryGetValue(guid, out var cached))
            {
                return cached == 0 ? null : cached;
            }

            var result = 0L;
            var docsByFileId = GetDocsByFileId(guid);
            var rootTransform = docsByFileId.Values.FirstOrDefault(d =>
                d.RootKey is "Transform" or "RectTransform" && IsRootFather(d));

            if (rootTransform != null &&
                rootTransform.Fields.TryGetValue("m_GameObject", out var go) &&
                go is Dictionary<string, object> goMap &&
                goMap.TryGetValue("fileID", out var goFid) &&
                long.TryParse(goFid?.ToString(), out var goId))
            {
                result = goId;
            }
            else
            {
                var compositeParents = GetCompositeParentMap(guid);
                var compositeRoot = compositeParents.FirstOrDefault(kv => kv.Value == 0);
                if (!compositeRoot.Equals(default(KeyValuePair<long, long>)))
                {
                    result = compositeRoot.Key;
                }
            }

            RootGameObjectCache[guid] = result;
            return result == 0 ? null : result;
        }

        /// <summary>Whether <paramref name="id"/> identifies this guid's own root — either directly
        /// (<see cref="ResolveRootGameObjectId"/>'s own answer) or, for a composite/Variant guid, a
        /// stripped GameObject/Transform placeholder owned by that same root instance (an override
        /// targeting a component attached directly to the root resolves its owner via that stripped
        /// placeholder's own fileId, not the root instance's PrefabInstance fileId — the two are
        /// different numbers for a composite guid, unlike a plain prefab where the root IS a real
        /// GameObject and both checks trivially agree).</summary>
        public static bool IsRootIdentity(string guid, long id)
        {
            var rootId = ResolveRootGameObjectId(guid);
            if (rootId is not { } root) return false;
            if (root == id) return true;

            var doc = ResolveDocument(guid, id);
            return doc is { Stripped: true } && TryGetFileId(doc, "m_PrefabInstance", out var piId) && piId == root;
        }

        /// <summary>Maps each top-level <c>PrefabInstance</c> document in this guid to its resolved
        /// parent identity — either a real GameObject's fileId (its transform-parent is a plain,
        /// non-stripped Transform), another top-level instance's own fileId (its transform-parent is
        /// a stripped placeholder owned by that instance — see <c>m_PrefabInstance</c>), or 0 for the
        /// one unparented root instance. This is exactly <see cref="ObjectGrouper.LinkHierarchy"/>'s
        /// own algorithm for resolving a SCENE's embedded instances, applied instead to one prefab
        /// asset's own file — needed because a Prefab Variant (or any prefab composed from several
        /// reusable sub-prefabs at its top level) has no plain Transform.m_Children list tying its
        /// pieces together, only each piece's own m_TransformParent.</summary>
        private static Dictionary<long, long> GetCompositeParentMap(string guid)
        {
            if (CompositeParentCache.TryGetValue(guid, out var cached)) return cached;

            var docsByFileId = GetDocsByFileId(guid);
            var result = new Dictionary<long, long>();
            CompositeParentCache[guid] = result;

            foreach (var doc in docsByFileId.Values)
            {
                if (doc.RootKey != "PrefabInstance") continue;
                if (!TryGetTransformParentId(doc, out var transformParentId)) continue;

                if (transformParentId == 0)
                {
                    result[doc.FileId] = 0;
                    continue;
                }

                if (!docsByFileId.TryGetValue(transformParentId, out var parentTransformDoc)) continue;

                if (TryGetFileId(parentTransformDoc, "m_GameObject", out var parentGoId))
                {
                    result[doc.FileId] = parentGoId;
                }
                else if (parentTransformDoc.Stripped && TryGetFileId(parentTransformDoc, "m_PrefabInstance", out var parentPiId))
                {
                    result[doc.FileId] = parentPiId;
                }
            }

            return result;
        }

        private static bool TryGetTransformParentId(UnityYamlDocument prefabInstanceDoc, out long transformParentId)
        {
            transformParentId = 0;
            return prefabInstanceDoc.Fields.TryGetValue("m_Modification", out var modObj) &&
                   modObj is Dictionary<string, object> modMap &&
                   modMap.TryGetValue("m_TransformParent", out var tp) &&
                   tp is Dictionary<string, object> tpMap &&
                   tpMap.TryGetValue("fileID", out var tpFid) &&
                   long.TryParse(tpFid?.ToString(), out transformParentId);
        }

        /// <summary>The fileId of a GameObject's parent GameObject within the same source prefab
        /// asset — resolved by finding the child's own Transform, following its <c>m_Father</c>
        /// to the parent Transform, then reading that Transform's <c>m_GameObject</c>. Null for
        /// the prefab's root (no father) or anything that fails to resolve.</summary>
        public static long? ResolveParentGameObjectId(string guid, long goId)
        {
            var map = GetParentGameObjectMap(guid);
            return map.TryGetValue(goId, out var parentId) && parentId != 0 ? parentId : null;
        }

        private static Dictionary<long, long> GetParentGameObjectMap(string guid)
        {
            if (ParentGameObjectCache.TryGetValue(guid, out var cached))
            {
                return cached;
            }

            var docsByFileId = GetDocsByFileId(guid);
            var result = new Dictionary<long, long>();
            ParentGameObjectCache[guid] = result;

            foreach (var doc in docsByFileId.Values)
            {
                if (doc.RootKey is not ("Transform" or "RectTransform")) continue;
                if (!TryGetFileId(doc, "m_GameObject", out var childGoId)) continue;
                if (!TryGetFileId(doc, "m_Father", out var fatherTransformId) || fatherTransformId == 0) continue;
                if (!docsByFileId.TryGetValue(fatherTransformId, out var fatherTransformDoc)) continue;
                if (!TryGetFileId(fatherTransformDoc, "m_GameObject", out var parentGoId)) continue;

                result[childGoId] = parentGoId;
            }

            return result;
        }

        /// <summary>The GameObject's own document plus every component document whose
        /// <c>m_GameObject</c> points at it — i.e. everything a synthesized, untouched nested
        /// child (see ObjectGrouper.SynthesizeSourceHierarchy) needs to render its real, current
        /// component list and full property values, exactly as Unity's own Inspector would show
        /// them, instead of an empty "(no changes)" placeholder.</summary>
        public static List<UnityYamlDocument> GetOwnedDocuments(string guid, long goId)
        {
            var docsByFileId = GetDocsByFileId(guid);
            var result = new List<UnityYamlDocument>();

            if (docsByFileId.TryGetValue(goId, out var goDoc) && goDoc.RootKey == "GameObject")
            {
                result.Add(goDoc);
            }

            foreach (var doc in docsByFileId.Values)
            {
                if (doc.RootKey == "GameObject") continue;
                if (TryGetFileId(doc, "m_GameObject", out var ownerGoId) && ownerGoId == goId)
                {
                    result.Add(doc);
                }
            }

            return result;
        }

        /// <summary>The fileIds of a GameObject's direct children, in real sibling order — read
        /// straight off the parent's own Transform.m_Children array (Unity always keeps this in
        /// display order) and mapped from each child Transform back to its owning GameObject, PLUS
        /// any top-level nested-instance children a Prefab Variant ties together purely via
        /// <c>m_TransformParent</c> instead (see <see cref="GetCompositeParentMap"/> — those never
        /// appear in any m_Children list at all, so they're appended rather than replacing the
        /// plain-Transform result). Empty if the parent's Transform can't be found, has no children,
        /// and isn't a composite parent either.</summary>
        public static List<long> GetChildGameObjectIdsInOrder(string guid, long parentGoId)
        {
            var docsByFileId = GetDocsByFileId(guid);
            var parentTransform = docsByFileId.Values.FirstOrDefault(d =>
                d.RootKey is "Transform" or "RectTransform" && TryGetFileId(d, "m_GameObject", out var go) && go == parentGoId);

            var result = new List<long>();
            if (parentTransform != null && parentTransform.Fields.TryGetValue("m_Children", out var childrenObj) && childrenObj is List<object> children)
            {
                foreach (var childRef in children)
                {
                    if (childRef is not Dictionary<string, object> childMap ||
                        !childMap.TryGetValue("fileID", out var cfid) ||
                        !long.TryParse(cfid?.ToString(), out var childTransformId)) continue;

                    if (!docsByFileId.TryGetValue(childTransformId, out var childTransformDoc)) continue;

                    if (TryGetFileId(childTransformDoc, "m_GameObject", out var childGoId))
                    {
                        result.Add(childGoId);
                        continue;
                    }

                    // A stripped child Transform (no m_GameObject of its own) means this child is
                    // itself the root of a DEEPER nested prefab instance (e.g. an imported FBX model
                    // instantiated inside this prefab) — silently skipping it would drop it from the
                    // hierarchy entirely. Its own fileId is still a stable, unique identity within this
                    // guid's fileId space, so use that instead; ObjectGrouper.ResolveSourceObjectName
                    // resolves its real name from the nested instance's own name override, and nothing
                    // deeper inside that further-nested instance is modeled (out of scope here — Unity
                    // itself shows it collapsed too).
                    if (childTransformDoc.Stripped)
                    {
                        result.Add(childTransformId);
                    }
                }
            }

            // Composite children (a Prefab Variant's own top-level sub-prefabs) in file order —
            // there's no m_Children-style sibling list for these, but Unity serializes them in the
            // same order it displays them in the Hierarchy.
            foreach (var kv in GetCompositeParentMap(guid))
            {
                if (kv.Value == parentGoId) result.Add(kv.Key);
            }

            return result;
        }

        private static bool TryGetFileId(UnityYamlDocument doc, string field, out long fileId)
        {
            fileId = 0;
            return doc.Fields.TryGetValue(field, out var value) &&
                   value is Dictionary<string, object> map &&
                   map.TryGetValue("fileID", out var fid) &&
                   long.TryParse(fid?.ToString(), out fileId);
        }

        private static bool IsRootFather(UnityYamlDocument transformDoc)
        {
            if (!transformDoc.Fields.TryGetValue("m_Father", out var father) ||
                father is not Dictionary<string, object> fatherMap ||
                !fatherMap.TryGetValue("fileID", out var fid))
            {
                return true;
            }

            return fid?.ToString() is null or "0";
        }

        private static Dictionary<long, UnityYamlDocument> GetDocsByFileId(string guid)
        {
            if (DocsCache.TryGetValue(guid, out var cached))
            {
                return cached;
            }

            var docsByFileId = new Dictionary<long, UnityYamlDocument>();
            // Cache the (possibly empty) result too, so a guid that fails to resolve isn't
            // re-attempted (re-reading a missing/unparsable asset) on every subsequent call.
            DocsCache[guid] = docsByFileId;

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return docsByFileId;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var fullPath = Path.Combine(projectRoot, assetPath);
            if (!File.Exists(fullPath) || !UnityYamlParser.TryParse(File.ReadAllText(fullPath), out var sourceDocs, out _))
            {
                return docsByFileId;
            }

            foreach (var sourceDoc in sourceDocs)
            {
                docsByFileId[sourceDoc.FileId] = sourceDoc;
            }

            return docsByFileId;
        }
    }
}
