using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using VisualGitDiff.Yaml;

namespace VisualGitDiff.Diff
{
    /// <summary>
    /// Groups parsed documents the way a Unity user thinks about them: every Component
    /// document (identified by a top-level <c>m_GameObject</c> reference) is folded
    /// under its owning GameObject. Anything without an owner (RenderSettings,
    /// PrefabInstance, a Material's root document, a stripped nested-prefab placeholder,
    /// ...) becomes its own top-level entry. Groups are matched across revisions by that
    /// owner fileId, mirroring how <see cref="UnityYamlDiff"/> matches documents.
    /// </summary>
    internal static class ObjectGrouper
    {
        public static List<ObjectGroup> BuildGroups(
            List<UnityYamlDocument> before, List<UnityYamlDocument> after, List<DocumentDiff> diffs)
        {
            var groups = new Dictionary<long, ObjectGroup>();
            var order = new List<long>();

            ObjectGroup GroupFor(long owner)
            {
                if (groups.TryGetValue(owner, out var g))
                {
                    return g;
                }

                g = new ObjectGroup { OwnerFileId = owner };
                groups[owner] = g;
                order.Add(owner);
                return g;
            }

            // Indexed by fileId so OwnerOf() can tell whether a component's m_GameObject target
            // is itself a stripped placeholder (see OwnerOf) and redirect accordingly. FileIds
            // are stable across revisions for the same object, so before/after can share one map.
            var docsByFileId = new Dictionary<long, UnityYamlDocument>();
            foreach (var d in before.Concat(after)) docsByFileId[d.FileId] = d;

            foreach (var d in before)
            {
                var (owner, nestedPiId, srcGuid, srcGoId) = OwnerOf(d, docsByFileId);
                var g = GroupFor(owner);
                if (nestedPiId.HasValue) { g.NestedInstanceId = nestedPiId; g.SplitSourceGuid = srcGuid; g.SplitSourceGoId = srcGoId; }
                g.BeforeDocs.Add(d);
            }

            foreach (var d in after)
            {
                var (owner, nestedPiId, srcGuid, srcGoId) = OwnerOf(d, docsByFileId);
                var g = GroupFor(owner);
                if (nestedPiId.HasValue) { g.NestedInstanceId = nestedPiId; g.SplitSourceGuid = srcGuid; g.SplitSourceGoId = srcGoId; }
                g.AfterDocs.Add(d);
            }

            var diffByFileId = diffs.ToDictionary(d => d.FileId);

            foreach (var owner in order)
            {
                var g = groups[owner];
                // A Stripped "GameObject" doc here is a placeholder for a nested instance's own
                // root (present only because something else in the file references that root
                // directly — see PrefabOverrideHydrator) — never the real thing. Picking it as the
                // anchor would misname/mis-icon the instance's own row as a plain GameObject
                // instead of a Prefab; prefer a real (non-stripped) GameObject first, then the
                // group's own PrefabInstance doc, before falling back to whatever else it has.
                var anchor = g.AfterDocs.Find(d => d.RootKey == "GameObject" && !d.Stripped)
                             ?? g.BeforeDocs.Find(d => d.RootKey == "GameObject" && !d.Stripped)
                             ?? g.AfterDocs.Find(d => d.RootKey == "PrefabInstance")
                             ?? g.BeforeDocs.Find(d => d.RootKey == "PrefabInstance")
                             ?? g.AfterDocs.FirstOrDefault()
                             ?? g.BeforeDocs.FirstOrDefault();

                g.DisplayName = ResolveDisplayName(g, anchor, before, after);
                g.Category = ClassifyCategory(g);

                // A split-out nested child's Anchor (drives the row's icon — see DiffIcons) must
                // point at its real GameObject/PrefabInstance identity in source space, not
                // whichever raw override doc it happens to carry here (often a bare MonoBehaviour/
                // RectTransform placeholder with no icon meaning of its own) — same resolution a
                // freshly-synthesized sibling of the same kind already gets via VisitInstanceTree.
                // This group may never be revisited by that walk at all (e.g. its owning instance's
                // source guid can't be resolved), so it can't be left to fix itself up later.
                g.Anchor = g.NestedInstanceId.HasValue && g.SplitSourceGuid != null && g.SplitSourceGoId.HasValue
                    ? ResolveSourceAnchorDoc(g.SplitSourceGuid, g.SplitSourceGoId.Value) ?? anchor
                    : anchor;

                var allFileIds = g.BeforeDocs.Select(d => d.FileId).Union(g.AfterDocs.Select(d => d.FileId));
                g.DocDiffs = allFileIds.Where(diffByFileId.ContainsKey).Select(id => diffByFileId[id]).ToList();

                if (g.BeforeDocs.Count == 0) g.Status = DocumentChangeType.Added;
                else if (g.AfterDocs.Count == 0) g.Status = DocumentChangeType.Removed;
                else g.Status = g.DocDiffs.Any(d => d.Type != DocumentChangeType.Unchanged)
                    ? DocumentChangeType.Modified
                    : DocumentChangeType.Unchanged;
            }

            LinkHierarchy(groups, order, before, after, docsByFileId);
            SynthesizeSourceHierarchy(groups, order, before, after);

            // Component sections render in doc order — FillMissingOwnedDocuments appends
            // GameObject/Transform at the END of whichever real override doc a split-child group
            // already had, so every group's own doc list needs re-sorting to match how Unity's
            // Inspector always lists a GameObject: itself first, then Transform, then every other
            // component in whatever order they happen to sit in.
            foreach (var owner in order)
            {
                var g = groups[owner];
                g.BeforeDocs = g.BeforeDocs.OrderBy(ComponentRank).ToList();
                g.AfterDocs = g.AfterDocs.OrderBy(ComponentRank).ToList();
            }

            var instanceTables = BuildInstanceRankTables(before, after, docsByFileId);

            // Unity's Hierarchy marks a GameObject with a small "+"/"-" badge only when it's a
            // genuine per-instance override (listed in some PrefabInstance's own m_AddedGameObjects/
            // m_RemovedGameObjects) — never merely because git shows it as Added/Removed (a whole
            // freshly-added instance's untouched, unmodified content reads Added too — see
            // VisitInstanceTree — but none of it gets a badge in Unity, since none of it was
            // individually added to anything). addedRank's keys are exactly the real, local
            // GameObject fileIds instanceTables already resolved this way for child ordering.
            var addedGoIds = new HashSet<long>();
            foreach (var (_, addedRank) in instanceTables.Values) addedGoIds.UnionWith(addedRank.Keys);
            var removedSourceRefs = CollectRemovedSourceRefs(before, after);

            foreach (var owner in order)
            {
                var g = groups[owner];
                g.IsAddedGameObjectOverride = addedGoIds.Contains(g.OwnerFileId);
                g.IsRemovedGameObjectOverride = g.SplitSourceGuid != null && g.SplitSourceGoId.HasValue &&
                                                removedSourceRefs.Contains((g.SplitSourceGuid, g.SplitSourceGoId.Value));
            }

            return OrderHierarchically(groups, order, instanceTables);
        }

        /// <summary>Every (guid, fileId) pair referenced by any PrefabInstance's own
        /// <c>m_Modification.m_RemovedGameObjects</c> — the SOURCE prefab's identity for an object
        /// this instance no longer has, used to flag <see cref="ObjectGroup.IsRemovedGameObjectOverride"/>
        /// on the matching split-out child group (see <see cref="ObjectGroup.SplitSourceGuid"/>/
        /// <see cref="ObjectGroup.SplitSourceGoId"/>).</summary>
        private static HashSet<(string guid, long fileId)> CollectRemovedSourceRefs(List<UnityYamlDocument> before, List<UnityYamlDocument> after)
        {
            var result = new HashSet<(string, long)>();

            foreach (var doc in before.Concat(after))
            {
                if (doc.RootKey != "PrefabInstance") continue;
                if (!doc.Fields.TryGetValue("m_Modification", out var modObj) || modObj is not Dictionary<string, object> modMap) continue;
                if (!modMap.TryGetValue("m_RemovedGameObjects", out var removedObj) || removedObj is not List<object> removedList) continue;

                foreach (var item in removedList)
                {
                    if (item is Dictionary<string, object> entry &&
                        entry.TryGetValue("fileID", out var fid) && long.TryParse(fid?.ToString(), out var fileId) &&
                        entry.TryGetValue("guid", out var guidObj) && guidObj?.ToString() is { Length: > 0 } guid)
                    {
                        result.Add((guid, fileId));
                    }
                }
            }

            return result;
        }

        /// <summary>Sort key for a group's component-section order — GameObject first, then
        /// Transform/RectTransform, then everything else in its existing relative order (OrderBy
        /// is stable).</summary>
        private static int ComponentRank(UnityYamlDocument doc) => doc.RootKey switch
        {
            "GameObject" => 0,
            "Transform" or "RectTransform" => 1,
            _ => 2,
        };

        // A prefab instantiating another instantiating another... in practice this bottoms out
        // in 2-3 levels (a vehicle prefab containing an imported FBX model, say); this only guards
        // against an unexpected reference cycle turning the walk into an infinite recursion.
        private const int MaxNestedInstanceDepth = 12;

        /// <summary>Fills in every GameObject a nested prefab instance actually has — including
        /// ones with no override at all (e.g. an untouched "WheelColliders" container) — by
        /// walking the SOURCE prefab's own GameObject tree, so the Objects column matches the real
        /// Unity Hierarchy exactly instead of only showing whichever objects happen to carry a
        /// diffable override. Runs after <see cref="LinkHierarchy"/>: an override-driven group
        /// that already exists for a given source GameObject (its combined id is deterministic —
        /// see <see cref="IdMixer"/>) keeps its own diff-derived name/status, only its
        /// parent/depth get corrected here (now that every intermediate ancestor has a row of its
        /// own, there's no need to skip past unmodified ones — see ResolveNestedParent for the
        /// fallback this supersedes when the source tree can't be read at all).</summary>
        private static void SynthesizeSourceHierarchy(
            Dictionary<long, ObjectGroup> groups, List<long> order, List<UnityYamlDocument> before, List<UnityYamlDocument> after)
        {
            var seenInstances = new HashSet<long>();

            foreach (var doc in before.Concat(after))
            {
                if (doc.RootKey != "PrefabInstance" || !seenInstances.Add(doc.FileId)) continue;

                var piId = doc.FileId;
                if (!groups.TryGetValue(piId, out var rootGroup)) continue;

                var guid = GetSourcePrefabGuid(doc);
                if (string.IsNullOrEmpty(guid) || SourcePrefabResolver.ResolveRootGameObjectId(guid) is not { } rootGoId) continue;

                VisitInstanceTree(groups, order, guid, rootGoId, piId, piId, rootGroup.Depth, 0, rootGroup.Status);
            }
        }

        /// <summary>Walks one instance's own GameObject tree (root's children onward), recursing
        /// into a DEEPER nested prefab instance (e.g. an imported FBX model like "SM_A320"
        /// instantiated inside "PBF_A320") wherever one is found, so the Objects column shows that
        /// instance's own real children too instead of stopping at its collapsed root row. Each
        /// recursion level switches to the nested instance's own guid/root and uses the parent
        /// node's own combined id as the new "instance identity" everything under it combines
        /// against — see IdMixer — keeping ids unique across arbitrarily many nesting levels
        /// without needing a wider identity scheme.</summary>
        private static void VisitInstanceTree(
            Dictionary<long, ObjectGroup> groups, List<long> order,
            string guid, long rootSourceGoId, long instanceIdentity, long rootParentGroupId, int rootDepth, int recursionDepth,
            DocumentChangeType instanceStatus)
        {
            if (recursionDepth >= MaxNestedInstanceDepth) return;

            var visited = new HashSet<long> { rootSourceGoId };

            void Visit(long sourceGoId, long parentGroupId, int depth)
            {
                var combinedId = IdMixer.Combine(instanceIdentity, sourceGoId);

                if (!groups.TryGetValue(combinedId, out var g))
                {
                    g = new ObjectGroup
                    {
                        OwnerFileId = combinedId,
                        NestedInstanceId = instanceIdentity,
                        SplitSourceGuid = guid,
                        SplitSourceGoId = sourceGoId,
                        DisplayName = ResolveSourceObjectName(guid, sourceGoId),
                        Category = GroupCategory.GameObjects,
                        // An untouched target (no override of its own) has no Before/AfterDocs to
                        // diff against, so it can never derive Added/Removed on its own — inherit it
                        // from the owning instance instead, since that's the only way it can know it
                        // never existed before (instance just Added) or won't exist after (instance
                        // just Removed) either. A merely Modified instance must NOT propagate here —
                        // this specific target genuinely carries no change of its own.
                        Status = instanceStatus is DocumentChangeType.Added or DocumentChangeType.Removed
                            ? instanceStatus
                            : DocumentChangeType.Unchanged,
                        Anchor = ResolveSourceAnchorDoc(guid, sourceGoId),
                    };
                    groups[combinedId] = g;
                    order.Add(combinedId);
                }

                // An override-driven group (e.g. "VehicleWheels", overridden via ITS OWN
                // MonoBehaviour only) still ends up with just that one component doc — nothing
                // referenced its GameObject/Transform directly, so no placeholder for THOSE
                // exists either. Fill in whatever the source prefab says this GameObject
                // actually owns but isn't already covered by a real doc here, so every group —
                // freshly synthesized or override-driven — shows its complete, real component
                // list (GameObject, Transform, ...) exactly like Unity's own Inspector, not
                // just whichever single component happened to carry an override.
                FillMissingOwnedDocuments(g, guid, sourceGoId);

                // An override-driven group (created earlier via OwnerOf/ResolveStrippedOwner, before
                // this walk ever ran) had its Anchor picked from whichever raw doc it happened to
                // have at that point — often a bare MonoBehaviour/RectTransform override placeholder,
                // never revisited since. That anchor drives the row's icon (see DiffIcons), so a
                // GameObject-category row must always end up anchored on the actual GameObject/
                // PrefabInstance, never a component — correct it here now that the real source
                // identity is known (a freshly-created group already got this right at construction,
                // so this is a no-op for those).
                if (g.Anchor is not { RootKey: "GameObject" or "PrefabInstance" })
                {
                    g.Anchor = ResolveSourceAnchorDoc(guid, sourceGoId);
                }

                g.ParentFileId = parentGroupId;
                g.Depth = depth;

                // This node IS the root of a deeper nested instance — its own "children" live in a
                // DIFFERENT guid entirely, resolved via its owning PrefabInstance's m_SourcePrefab,
                // not via m_Children/composite-parent in this guid. Two shapes: a stripped Transform
                // with no m_GameObject pointing at a PrefabInstance doc elsewhere in this guid (see
                // SourcePrefabResolver.GetChildGameObjectIdsInOrder — an imported FBX model, or a
                // scene-style nested reference), or sourceGoId resolving DIRECTLY to a real
                // "PrefabInstance" document (a Prefab Variant's own top-level sub-prefab — see
                // SourcePrefabResolver.GetCompositeParentMap).
                if (TryResolveNestedPrefabInstanceDoc(guid, sourceGoId, out var nestedPrefabInstanceDoc))
                {
                    var nestedGuid = GetSourcePrefabGuid(nestedPrefabInstanceDoc);
                    if (!string.IsNullOrEmpty(nestedGuid) && SourcePrefabResolver.ResolveRootGameObjectId(nestedGuid) is { } nestedRootGoId)
                    {
                        // The call above (FillMissingOwnedDocuments(g, guid, sourceGoId)) resolved
                        // against the OUTER stripped placeholder's identity, which is never itself a
                        // GameObject document — it always yields nothing. This node's real component
                        // list (GameObject, RectTransform, ...) lives one level down, at the DEEPER
                        // instance's own root — fill it in from there instead, or this row's own
                        // Inspector sections stay empty on both sides even though it's a real object.
                        FillMissingOwnedDocuments(g, nestedGuid, nestedRootGoId);
                        VisitInstanceTree(groups, order, nestedGuid, nestedRootGoId, combinedId, combinedId, depth, recursionDepth + 1, instanceStatus);
                    }
                    return;
                }

                foreach (var childGoId in SourcePrefabResolver.GetChildGameObjectIdsInOrder(guid, sourceGoId))
                {
                    if (visited.Add(childGoId)) Visit(childGoId, combinedId, depth + 1);
                }
            }

            foreach (var childGoId in SourcePrefabResolver.GetChildGameObjectIdsInOrder(guid, rootSourceGoId))
            {
                if (visited.Add(childGoId)) Visit(childGoId, rootParentGroupId, rootDepth + 1);
            }
        }

        /// <summary>Adds the source prefab's real, current document for every component this
        /// GameObject owns (per <see cref="SourcePrefabResolver.GetOwnedDocuments"/>) that isn't
        /// already represented in the group — matched by each existing doc's own
        /// <c>m_CorrespondingSourceObject</c> fileId (the source-space identity a stripped/
        /// synthetic doc always carries), so a component that DOES have a real override keeps that
        /// override's doc untouched (with its own diff highlighting) while every other
        /// component — including the GameObject's own entry and its Transform — gets filled in
        /// with the plain, unchanged values Unity would otherwise show.
        /// <para/>
        /// Filled in on BOTH sides only when the group itself is Modified/Unchanged (nothing about
        /// a filled-in component changed, so both sides show the exact same doc instance). A group
        /// that's Added or Removed (see <see cref="VisitInstanceTree"/> — inherited from the owning
        /// instance for a target with no override of its own) only exists on one side in reality;
        /// filling in the other too would render that side's column with real content instead of
        /// empty, contradicting the row's own Added/Removed badge.</summary>
        private static void FillMissingOwnedDocuments(ObjectGroup g, string guid, long sourceGoId)
        {
            var coveredSourceFileIds = g.BeforeDocs.Concat(g.AfterDocs)
                .Select(d => TryGetCorrespondingSource(d, out _, out var srcFileId) ? srcFileId : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet();

            foreach (var ownedDoc in SourcePrefabResolver.GetOwnedDocuments(guid, sourceGoId))
            {
                if (coveredSourceFileIds.Contains(ownedDoc.FileId)) continue;

                if (g.Status != DocumentChangeType.Added) g.BeforeDocs.Add(ownedDoc);
                if (g.Status != DocumentChangeType.Removed) g.AfterDocs.Add(ownedDoc);
            }
        }

        /// <summary>A synthesized node's name, read straight from the source prefab's own
        /// GameObject document (m_Name) — falls back to a nicified type name only if the source
        /// document is somehow missing, which shouldn't happen since the caller only visits ids
        /// <see cref="SourcePrefabResolver.GetChildGameObjectIdsInOrder"/> itself just returned.</summary>
        private static string ResolveSourceObjectName(string guid, long goId)
        {
            var doc = SourcePrefabResolver.ResolveDocument(guid, goId);
            if (doc != null && doc.Fields.TryGetValue("m_Name", out var n) && !string.IsNullOrEmpty(n?.ToString()))
            {
                return n.ToString();
            }

            // A DEEPER nested prefab instance's own root has no m_Name of its own on the plain
            // document above — it carries one instead as a name-override modification on the
            // PrefabInstance itself (Unity always lets an instance rename its own root), same shape
            // ObjectGrouper.ResolveDisplayName already reads for a top-level PrefabInstance. See
            // TryResolveNestedPrefabInstanceDoc for the two shapes this nested instance can take
            // (an imported FBX model / scene-style reference, or a Prefab Variant's own top-level
            // sub-prefab).
            if (TryResolveNestedPrefabInstanceDoc(guid, goId, out var nestedPrefabInstanceDoc))
            {
                var overrideName = TryGetPrefabInstanceNameOverride(nestedPrefabInstanceDoc);
                if (overrideName != null) return overrideName;
            }

            return ObjectNames.NicifyVariableName(doc?.RootKey ?? "GameObject");
        }

        /// <summary>The document a synthesized node's row (icon, mainly) should anchor on — the
        /// resolved source document itself for a real GameObject, or the OWNING PrefabInstance doc
        /// for a deeper nested instance's root (see ResolveSourceObjectName), so it gets a Prefab
        /// icon instead of a bare Transform one.</summary>
        private static UnityYamlDocument ResolveSourceAnchorDoc(string guid, long goId) =>
            TryResolveNestedPrefabInstanceDoc(guid, goId, out var nestedPrefabInstanceDoc)
                ? nestedPrefabInstanceDoc
                : SourcePrefabResolver.ResolveDocument(guid, goId);

        /// <summary>Resolves whether (guid, goId) is itself the root of a DEEPER nested prefab
        /// instance, covering both shapes this can take: (a) a stripped Transform placeholder with
        /// no m_GameObject whose own m_PrefabInstance points at a real "PrefabInstance" document
        /// elsewhere in this guid (an imported FBX model, or any scene-style nested reference), or
        /// (b) goId resolving DIRECTLY to a real "PrefabInstance" document — a Prefab Variant's own
        /// top-level sub-prefab, tied to its siblings purely via m_TransformParent instead of a plain
        /// Transform.m_Children list (see <see cref="SourcePrefabResolver.GetCompositeParentMap"/>).
        /// False (with a null out param) for a plain GameObject.</summary>
        private static bool TryResolveNestedPrefabInstanceDoc(string guid, long goId, out UnityYamlDocument nestedPrefabInstanceDoc)
        {
            var doc = SourcePrefabResolver.ResolveDocument(guid, goId);

            if (doc is { RootKey: "PrefabInstance" })
            {
                nestedPrefabInstanceDoc = doc;
                return true;
            }

            if (doc is { Stripped: true } && TryGetPrefabInstanceId(doc, out var nestedPiId) &&
                SourcePrefabResolver.ResolveDocument(guid, nestedPiId) is { RootKey: "PrefabInstance" } resolved)
            {
                nestedPrefabInstanceDoc = resolved;
                return true;
            }

            nestedPrefabInstanceDoc = null;
            return false;
        }

        /// <summary>Per-PrefabInstance sibling-order tables, used by <see cref="OrderHierarchically"/>
        /// to sort a nested instance's children the way Unity's own Hierarchy would show them
        /// instead of the arbitrary order overrides happen to appear in the YAML file.
        /// <c>sourceRank</c> is a pre-order DFS index over the SOURCE prefab's own GameObject tree
        /// (root first, then each child's subtree, per <see cref="SourcePrefabResolver.GetChildGameObjectIdsInOrder"/>)
        /// — global across the whole instance, not just one level, so a split child that got
        /// flattened onto a different visible parent (see ResolveNestedParent) still sorts
        /// correctly relative to that parent's other children. <c>addedRank</c> is the position of
        /// an added GameObject within the instance's own <c>m_AddedGameObjects</c> list — Unity
        /// appends added children after the prefab's original ones, so added groups always sort
        /// after every source-ranked one.</summary>
        private static Dictionary<long, (Dictionary<long, int> sourceRank, Dictionary<long, int> addedRank)> BuildInstanceRankTables(
            List<UnityYamlDocument> before, List<UnityYamlDocument> after, Dictionary<long, UnityYamlDocument> docsByFileId)
        {
            var result = new Dictionary<long, (Dictionary<long, int>, Dictionary<long, int>)>();

            foreach (var doc in before.Concat(after))
            {
                if (doc.RootKey != "PrefabInstance" || result.ContainsKey(doc.FileId)) continue;

                var sourceRank = new Dictionary<long, int>();
                var guid = GetSourcePrefabGuid(doc);
                if (!string.IsNullOrEmpty(guid) && SourcePrefabResolver.ResolveRootGameObjectId(guid) is { } rootGoId)
                {
                    var counter = 0;
                    void Visit(long goId)
                    {
                        sourceRank[goId] = counter++;
                        foreach (var childGoId in SourcePrefabResolver.GetChildGameObjectIdsInOrder(guid, goId)) Visit(childGoId);
                    }
                    Visit(rootGoId);
                }

                var addedRank = new Dictionary<long, int>();
                if (doc.Fields.TryGetValue("m_Modification", out var modObj) &&
                    modObj is Dictionary<string, object> modMap &&
                    modMap.TryGetValue("m_AddedGameObjects", out var addedObj) &&
                    addedObj is List<object> addedList)
                {
                    for (var i = 0; i < addedList.Count; i++)
                    {
                        if (addedList[i] is not Dictionary<string, object> entry ||
                            !entry.TryGetValue("addedObject", out var ao) || ao is not Dictionary<string, object> aoMap ||
                            !aoMap.TryGetValue("fileID", out var aoFid) || !long.TryParse(aoFid?.ToString(), out var transformFileId) ||
                            !docsByFileId.TryGetValue(transformFileId, out var transformDoc))
                        {
                            continue;
                        }

                        if (transformDoc.Fields.TryGetValue("m_GameObject", out var goRef) &&
                            goRef is Dictionary<string, object> goMap &&
                            goMap.TryGetValue("fileID", out var goFid) && long.TryParse(goFid?.ToString(), out var goId))
                        {
                            addedRank[goId] = i;
                        }
                        // A stripped transform with no m_GameObject at all is itself the root of a
                        // nested PrefabInstance added directly as a child (e.g. "MainScreen"/
                        // "ConfirmScreen" — a reusable prefab dropped into this instance) rather
                        // than a plain added GameObject — record that nested instance's OWN fileId
                        // instead, the same identity ObjectGrouper.Visit's "deeper nested instance"
                        // branch uses for it elsewhere.
                        else if (transformDoc.Stripped &&
                                 transformDoc.Fields.TryGetValue("m_PrefabInstance", out var piRef) &&
                                 piRef is Dictionary<string, object> piMap &&
                                 piMap.TryGetValue("fileID", out var piFid) && long.TryParse(piFid?.ToString(), out var nestedPiId) && nestedPiId != 0)
                        {
                            addedRank[nestedPiId] = i;
                        }
                    }
                }

                result[doc.FileId] = (sourceRank, addedRank);
            }

            return result;
        }

        /// <summary>Resolves each GameObject group's parent (via its Transform's
        /// <c>m_Father</c>) and its depth, so the object list can indent like the Unity
        /// Hierarchy window.</summary>
        private static void LinkHierarchy(
            Dictionary<long, ObjectGroup> groups, List<long> order,
            List<UnityYamlDocument> before, List<UnityYamlDocument> after, Dictionary<long, UnityYamlDocument> docsByFileId)
        {
            // A Transform's own fileId -> the fileId of the group it belongs to, so a parent's
            // m_Father (a Transform fileId) can be resolved back to a GameObject/instance group.
            var transformOwner = new Dictionary<long, long>();
            foreach (var d in before.Concat(after))
            {
                if (d.RootKey is not ("Transform" or "RectTransform")) continue;

                if (d.Fields.TryGetValue("m_GameObject", out var go) &&
                    go is Dictionary<string, object> goMap &&
                    goMap.TryGetValue("fileID", out var goFid) &&
                    long.TryParse(goFid?.ToString(), out var ownerGoId))
                {
                    transformOwner[d.FileId] = ownerGoId;
                    continue;
                }

                // A stripped Transform placeholder (a nested prefab instance's root, or one of
                // its nested children, referenced here only because something outside the
                // instance parents off of it) has no m_GameObject of its own — it belongs to
                // whichever group OwnerOf() would resolve it to (the instance itself, or its own
                // split-out child group — see ResolveStrippedOwner).
                if (TryGetPrefabInstanceId(d, out _))
                {
                    var (owner, _, _, _) = ResolveStrippedOwner(d, docsByFileId);
                    transformOwner[d.FileId] = owner;
                }
            }

            foreach (var owner in order)
            {
                var g = groups[owner];
                var docs = g.AfterDocs.Concat(g.BeforeDocs).ToList();

                var fatherTransformId = FindFatherTransformId(docs);
                if (fatherTransformId is not (null or 0) &&
                    transformOwner.TryGetValue(fatherTransformId.Value, out var parentGoId) &&
                    groups.ContainsKey(parentGoId))
                {
                    g.ParentFileId = parentGoId;
                }
                // A split-out nested child (see ResolveStrippedOwner) has no m_Father of its own
                // to read (stripped placeholders carry no such field) — nest it under the nearest
                // ancestor that already has a row of its own by walking its real parent chain in
                // the SOURCE prefab (see ResolveNestedParent), falling all the way back to the
                // owning PrefabInstance's own row if no closer ancestor is being overridden too.
                else if (g.NestedInstanceId is { } nestedPiId && nestedPiId != owner && groups.ContainsKey(nestedPiId))
                {
                    g.ParentFileId = g.SplitSourceGuid != null && g.SplitSourceGoId.HasValue
                        ? ResolveNestedParent(g.SplitSourceGuid, g.SplitSourceGoId.Value, nestedPiId, groups)
                        : nestedPiId;
                }
            }

            foreach (var owner in order)
            {
                var depth = 0;
                var visited = new HashSet<long>();
                var current = groups[owner];
                while (current.ParentFileId != 0 && groups.TryGetValue(current.ParentFileId, out var parent) && visited.Add(current.OwnerFileId))
                {
                    depth++;
                    current = parent;
                }
                groups[owner].Depth = depth;
            }
        }

        /// <summary>Walks a split-out child's real ancestor chain in the SOURCE prefab (via
        /// <see cref="SourcePrefabResolver.ResolveParentGameObjectId"/>) looking for the nearest
        /// ancestor that also has its own group in this diff — an intermediate GameObject with no
        /// override of its own (e.g. a "WheelColliders" container between "VehicleWheels" and a
        /// "BL WheelCollider" override) never gets a row, so nesting has to skip past it to
        /// whichever ancestor further up DOES have one. Bottoms out at the instance's own root row
        /// once the walk reaches the prefab's root GameObject or a dead end.</summary>
        private static long ResolveNestedParent(string sourceGuid, long sourceGoId, long instancePiId, Dictionary<long, ObjectGroup> groups)
        {
            var rootGoId = SourcePrefabResolver.ResolveRootGameObjectId(sourceGuid);
            var current = sourceGoId;
            var visited = new HashSet<long>();

            while (visited.Add(current))
            {
                var parentGoId = SourcePrefabResolver.ResolveParentGameObjectId(sourceGuid, current);
                if (parentGoId is null || parentGoId == rootGoId) return instancePiId;

                var candidateGroupId = IdMixer.Combine(instancePiId, parentGoId.Value);
                if (groups.ContainsKey(candidateGroupId)) return candidateGroupId;

                current = parentGoId.Value;
            }

            return instancePiId;
        }

        /// <summary>
        /// Finds the fileId of the parent Transform for a group's documents. A regular
        /// GameObject reports this directly via its Transform's <c>m_Father</c>. A nested
        /// PrefabInstance has no real Transform of its own to read this from — its parent is
        /// recorded on the PrefabInstance document itself, as <c>m_Modification.m_TransformParent</c>
        /// — so a STRIPPED Transform/RectTransform placeholder is deliberately excluded from the
        /// search here even when one exists for the instance's own root (written whenever
        /// something else in the scene references that root directly): <see cref="PrefabOverrideHydrator"/>
        /// fills such a placeholder's <c>m_Father</c> in from the SOURCE prefab, where the root's
        /// father is always 0 — reading that back as "no parent" would incorrectly flatten the
        /// whole instance to a root row instead of falling through to the real m_TransformParent below.
        /// </summary>
        private static long? FindFatherTransformId(List<UnityYamlDocument> groupDocs)
        {
            var transformDoc = groupDocs.FirstOrDefault(d => d.RootKey is "Transform" or "RectTransform" && !d.Stripped);
            if (transformDoc != null &&
                transformDoc.Fields.TryGetValue("m_Father", out var father) &&
                father is Dictionary<string, object> fatherMap &&
                fatherMap.TryGetValue("fileID", out var fatherFid) &&
                long.TryParse(fatherFid?.ToString(), out var fatherTransformId))
            {
                return fatherTransformId;
            }

            var prefabInstanceDoc = groupDocs.FirstOrDefault(d => d.RootKey == "PrefabInstance");
            if (prefabInstanceDoc != null &&
                prefabInstanceDoc.Fields.TryGetValue("m_Modification", out var modObj) &&
                modObj is Dictionary<string, object> modMap &&
                modMap.TryGetValue("m_TransformParent", out var tp) &&
                tp is Dictionary<string, object> tpMap &&
                tpMap.TryGetValue("fileID", out var tpFid) &&
                long.TryParse(tpFid?.ToString(), out var transformParentId))
            {
                return transformParentId;
            }

            return null;
        }

        /// <summary>Reorders groups so each GameObject is immediately followed by its children
        /// (depth-first), matching how the Unity Hierarchy window lays things out, and clusters
        /// root-level entries by <see cref="GroupCategory"/> so scene-level singletons
        /// (RenderSettings, NavMeshSettings, ...) don't sit interleaved with real GameObject
        /// roots just because both happen to have no parent. Falls back to original document
        /// order for roots/siblings within a category.</summary>
        private static List<ObjectGroup> OrderHierarchically(
            Dictionary<long, ObjectGroup> groups, List<long> order,
            Dictionary<long, (Dictionary<long, int> sourceRank, Dictionary<long, int> addedRank)> instanceTables)
        {
            var childrenOf = new Dictionary<long, List<long>>();
            var roots = new List<long>();
            foreach (var owner in order)
            {
                var parentId = groups[owner].ParentFileId;
                if (parentId != 0 && groups.ContainsKey(parentId))
                {
                    if (!childrenOf.TryGetValue(parentId, out var kids)) childrenOf[parentId] = kids = new List<long>();
                    kids.Add(owner);
                }
                else
                {
                    roots.Add(owner);
                }
            }

            // A parent that's part of a nested prefab instance (either the instance's own root,
            // or a split child within it) has its children re-sorted to match the real Hierarchy
            // order instead of the arbitrary YAML appearance order — see BuildInstanceRankTables.
            foreach (var parentId in childrenOf.Keys.ToList())
            {
                var instanceId = groups[parentId].NestedInstanceId ?? (instanceTables.ContainsKey(parentId) ? parentId : (long?)null);
                if (instanceId is not { } piId || !instanceTables.TryGetValue(piId, out var tables)) continue;

                int RankOf(long kidId)
                {
                    var kid = groups[kidId];
                    if (kid.SplitSourceGoId.HasValue && tables.sourceRank.TryGetValue(kid.SplitSourceGoId.Value, out var sourceRank)) return sourceRank;
                    if (tables.addedRank.TryGetValue(kid.OwnerFileId, out var addedRank)) return 1_000_000 + addedRank;
                    return int.MaxValue;
                }

                // OrderBy is a stable sort, so kids with no rank (e.g. dangling/unresolved
                // overrides) keep their original relative order, pushed to the very end.
                childrenOf[parentId] = childrenOf[parentId].OrderBy(RankOf).ToList();
            }

            // OrderBy is a stable sort, so within each category roots keep their original
            // document order — only the category clustering itself reorders anything.
            var orderedRoots = roots.OrderBy(id => (int)groups[id].Category).ToList();

            var result = new List<ObjectGroup>();
            var visited = new HashSet<long>();

            void Visit(long id)
            {
                if (!visited.Add(id)) return;
                if (!IsDanglingSplitGroup(groups[id])) result.Add(groups[id]);

                if (childrenOf.TryGetValue(id, out var kids))
                {
                    foreach (var kid in kids) Visit(kid);
                }
            }

            foreach (var id in orderedRoots) Visit(id);
            foreach (var id in order) Visit(id); // defensive: pick up any cycle/orphan left out above

            return result;
        }

        /// <summary>A split-out nested-child group whose own identity (<see cref="ObjectGroup.SplitSourceGuid"/>
        /// + <see cref="ObjectGroup.SplitSourceGoId"/>) no longer resolves to anything real in the
        /// source prefab — a stale override left over after the target was removed or restructured
        /// there (see <see cref="SourcePrefabResolver.IsUnresolvedReference"/>). Since
        /// <see cref="SynthesizeSourceHierarchy"/> now fills in every GameObject the instance
        /// genuinely has, a group that fails this check isn't a real, currently-existing object —
        /// it would otherwise show up as an extra row with no counterpart in the real Hierarchy.
        /// Checked against the group's own identity (not per-document Stripped-ness) so a mixed
        /// group — a real virtual component override sitting alongside a dangling GameObject
        /// placeholder, e.g. one added directly to an otherwise-removed nested child — is still
        /// caught. Its diffs stay reachable from the owning PrefabInstance's own DocDiffs; only the
        /// standalone Objects-column row is dropped.
        /// <para/>
        /// A DEEPER nested prefab instance's own root (see ResolveSourceObjectName, e.g. "SM_A320"
        /// inside "PBF_A320") resolves to a stripped Transform rather than a GameObject too, but is
        /// a legitimate, still-existing object — distinguished from a genuinely dangling reference
        /// by whether its own m_PrefabInstance still points at a real PrefabInstance doc.</summary>
        private static bool IsDanglingSplitGroup(ObjectGroup g)
        {
            if (!g.NestedInstanceId.HasValue || g.SplitSourceGuid == null || !g.SplitSourceGoId.HasValue) return false;

            var doc = SourcePrefabResolver.ResolveDocument(g.SplitSourceGuid, g.SplitSourceGoId.Value);
            if (doc == null) return true;
            if (doc.RootKey == "GameObject") return false;

            // A Prefab Variant's own top-level sub-prefab (e.g. "Background"/"Avatar Image" inside
            // "User Item Horizontal Variant") resolves DIRECTLY to a real, non-stripped
            // "PrefabInstance" document — a legitimate, still-existing composite child (see
            // SourcePrefabResolver.GetCompositeParentMap), not a stale override.
            if (doc.RootKey == "PrefabInstance") return false;

            return !(doc.Stripped && TryGetPrefabInstanceId(doc, out var nestedPiId) &&
                      SourcePrefabResolver.ResolveDocument(g.SplitSourceGuid, nestedPiId) != null);
        }

        private static readonly HashSet<string> SceneSettingsRootKeys = new()
        {
            "RenderSettings", "LightmapSettings", "NavMeshSettings", "OcclusionCullingSettings",
            "SceneRoots",
        };

        private static GroupCategory ClassifyCategory(ObjectGroup g)
        {
            var docs = g.BeforeDocs.Concat(g.AfterDocs);
            if (g.NestedInstanceId.HasValue ||
                docs.Any(d => d.RootKey is "GameObject" or "PrefabInstance" || (d.Stripped && TryGetPrefabInstanceId(d, out _))))
            {
                return GroupCategory.GameObjects;
            }

            if (docs.Any(d => SceneSettingsRootKeys.Contains(d.RootKey)))
            {
                return GroupCategory.SceneSettings;
            }

            return GroupCategory.Other;
        }

        /// <summary>Resolves the group a document belongs to, plus (when it's a nested-prefab
        /// override split out to its own per-child group) the owning PrefabInstance's fileId and
        /// the split child's own identity in the source prefab (guid + GameObject fileId) — used
        /// by <see cref="LinkHierarchy"/> to nest it under the right ancestor instead of flattening
        /// it straight under the PrefabInstance's own row.</summary>
        private static (long owner, long? nestedPiId, string sourceGuid, long? sourceGoId) OwnerOf(UnityYamlDocument doc, Dictionary<long, UnityYamlDocument> docsByFileId)
        {
            if (doc.Fields.TryGetValue("m_GameObject", out var go) &&
                go is Dictionary<string, object> map &&
                map.TryGetValue("fileID", out var fid) &&
                long.TryParse(fid?.ToString(), out var ownerId) &&
                ownerId != 0)
            {
                // A component added to an existing child of a nested prefab instance (an
                // "added component" override) points m_GameObject at that child's own stripped
                // GameObject placeholder, not at the PrefabInstance directly. Redirect through it
                // the same way a stripped placeholder resolves its own group, or the component
                // ends up in a one-off group of its own (no GameObject/PrefabInstance doc inside
                // it) and gets misclassified as "Other".
                if (docsByFileId.TryGetValue(ownerId, out var ownerDoc) && ownerDoc.RootKey == "GameObject" && ownerDoc.Stripped)
                {
                    return ResolveStrippedOwner(ownerDoc, docsByFileId);
                }

                return (ownerId, null, null, null);
            }

            // Stripped placeholders (left behind for nested-prefab overrides) have no
            // m_GameObject of their own — their real owner lives in the source prefab, not this
            // file — so their group is resolved from there instead (see ResolveStrippedOwner).
            if (doc.RootKey != "PrefabInstance")
            {
                return ResolveStrippedOwner(doc, docsByFileId);
            }

            return (doc.FileId, null, null, null);
        }

        /// <summary>A stripped/synthetic document's group is, by default, its whole owning
        /// PrefabInstance (every override of one nested instance sharing one row) — except when
        /// its <c>m_CorrespondingSourceObject</c> resolves (via <see cref="SourcePrefabResolver"/>)
        /// to a nested child object other than the instance's own root, in which case it gets its
        /// own group instead, so selecting one nested child's row shows only that child's own
        /// overrides rather than every override in the whole instance dumped together. The
        /// instance's own root-level overrides intentionally stay merged with the PrefabInstance
        /// group itself (unchanged prior behavior) since that's what "select the instance" means.
        /// <para/>
        /// "The instance's own root" is judged against the PrefabInstance's own
        /// <c>m_SourcePrefab</c> guid specifically — not just any prefab's root. A prefab
        /// (PBF_A320) commonly contains several DIFFERENT nested sub-prefab instances of its own
        /// (a wheel, a door, ...); naively checking "is this the root of whichever prefab the
        /// target's guid points to" would call EVERY one of those sub-instances' own roots "the
        /// root" too, merging every unrelated nested child back into one row — exactly the bug
        /// this guid comparison avoids.</summary>
        private static (long owner, long? nestedPiId, string sourceGuid, long? sourceGoId) ResolveStrippedOwner(UnityYamlDocument strippedDoc, Dictionary<long, UnityYamlDocument> docsByFileId)
        {
            if (!TryGetPrefabInstanceId(strippedDoc, out var piId)) return (strippedDoc.FileId, null, null, null);

            if (TryResolveOwningSourceGameObjectId(strippedDoc, out var ownerGuid, out var ownerGoId))
            {
                var instanceSourceGuid = docsByFileId.TryGetValue(piId, out var prefabInstanceDoc) ? GetSourcePrefabGuid(prefabInstanceDoc) : null;
                var isInstanceRoot = ownerGuid == instanceSourceGuid && SourcePrefabResolver.IsRootIdentity(ownerGuid, ownerGoId);

                if (!isInstanceRoot)
                {
                    return (IdMixer.Combine(piId, ownerGoId), piId, ownerGuid, ownerGoId);
                }
            }

            return (piId, null, null, null);
        }

        private static string GetSourcePrefabGuid(UnityYamlDocument prefabInstanceDoc) =>
            prefabInstanceDoc.Fields.TryGetValue("m_SourcePrefab", out var sp) &&
            sp is Dictionary<string, object> spMap &&
            spMap.TryGetValue("guid", out var g)
                ? g?.ToString()
                : null;

        // Deep nesting is finite in practice; this only guards against an unexpected reference
        // cycle turning a lookup into an infinite loop.
        private const int MaxNestedPrefabDepth = 16;

        /// <summary>Resolves which GameObject a stripped/synthetic document's override actually
        /// targets — itself, if the target IS a GameObject (an override like m_Name/m_IsActive),
        /// or its owner via the target component's own <c>m_GameObject</c> otherwise — as a
        /// (source prefab guid, fileId) pair, since the same fileId number can be reused across
        /// different source prefabs (or even the same one instantiated twice).
        /// <para/>
        /// A prefab can itself contain another nested prefab instance (e.g. PBF_A320.prefab
        /// instantiating a "SM_A320" visual-mesh sub-object). An override on an object that deep
        /// resolves, one level in, to a STRIPPED placeholder — which carries no <c>m_GameObject</c>
        /// of its own, only its own <c>m_CorrespondingSourceObject</c> pointing one level further —
        /// so this follows that chain across as many nested levels as it takes. That chain can
        /// bottom out two ways without ever reaching a real GameObject: the referenced object is
        /// genuinely gone (a stale override — see <see cref="SourcePrefabResolver.IsUnresolvedReference"/>),
        /// or the next guid points at a non-YAML asset, e.g. an imported 3D model/FBX instanced
        /// like a prefab, whose internal node hierarchy is never text-serialized so it can never
        /// resolve here. Both cases still settle on the deepest (guid, fileId) pair reached rather
        /// than falling back to "no owner" — that pair is still a stable, unique identity for
        /// *this* object, letting it get its own group/row (generically labeled) instead of every
        /// such case collapsing into the outermost instance's row.</summary>
        private static bool TryResolveOwningSourceGameObjectId(UnityYamlDocument doc, out string ownerGuid, out long ownerGoId)
        {
            ownerGuid = null;
            ownerGoId = 0;

            if (!TryGetCorrespondingSource(doc, out var guid, out var sourceFileId)) return false;

            for (var depth = 0; depth < MaxNestedPrefabDepth; depth++)
            {
                var sourceDoc = SourcePrefabResolver.ResolveDocument(guid, sourceFileId);

                if (sourceDoc == null || sourceDoc.RootKey == "GameObject")
                {
                    ownerGuid = guid;
                    ownerGoId = sourceFileId;
                    return true;
                }

                if (!sourceDoc.Stripped)
                {
                    if (sourceDoc.Fields.TryGetValue("m_GameObject", out var goObj) &&
                        goObj is Dictionary<string, object> goMap &&
                        goMap.TryGetValue("fileID", out var goFid) &&
                        long.TryParse(goFid?.ToString(), out var ownerGo) &&
                        ownerGo != 0)
                    {
                        ownerGuid = guid;
                        ownerGoId = ownerGo;
                        return true;
                    }

                    // A real (non-stripped) document with no m_GameObject at all shouldn't
                    // normally happen for a Component — settle on it directly rather than failing.
                    ownerGuid = guid;
                    ownerGoId = sourceFileId;
                    return true;
                }

                // sourceDoc is a stripped placeholder that belongs to ANOTHER nested instance
                // WITHIN THIS SAME guid (m_PrefabInstance set) — e.g. a Prefab Variant's own
                // top-level sub-prefab, tied to its siblings via m_TransformParent instead of a
                // plain Transform (see SourcePrefabResolver.GetCompositeParentMap). That instance's
                // own fileId IS the identity to settle at: diving further via
                // m_CorrespondingSourceObject would walk PAST the level where a rename/override
                // specific to THIS instance actually lives (e.g. the Variant's own "Background"/
                // "Avatar Image" rename), landing on the deeper base prefab's generic name instead.
                if (TryGetPrefabInstanceId(sourceDoc, out var siblingPiId) &&
                    SourcePrefabResolver.ResolveDocument(guid, siblingPiId) is { RootKey: "PrefabInstance" })
                {
                    ownerGuid = guid;
                    ownerGoId = siblingPiId;
                    return true;
                }

                // Otherwise it's a deeper nested instance's root reached from OUTSIDE that
                // instance (an imported FBX model, or any scene-style nested reference) — follow
                // its own m_CorrespondingSourceObject one level further, or settle here if it has
                // none.
                if (!TryGetCorrespondingSource(sourceDoc, out var nextGuid, out var nextFileId))
                {
                    ownerGuid = guid;
                    ownerGoId = sourceFileId;
                    return true;
                }

                guid = nextGuid;
                sourceFileId = nextFileId;
            }

            // Extremely deep/cyclic chain — settle on the last identity reached rather than
            // looping forever or merging into the parent.
            ownerGuid = guid;
            ownerGoId = sourceFileId;
            return true;
        }

        private static bool TryGetCorrespondingSource(UnityYamlDocument doc, out string guid, out long fileId)
        {
            guid = null;
            fileId = 0;

            if (!doc.Fields.TryGetValue("m_CorrespondingSourceObject", out var cso) || cso is not Dictionary<string, object> csoMap) return false;
            if (!csoMap.TryGetValue("fileID", out var fidObj) || !long.TryParse(fidObj?.ToString(), out fileId) || fileId == 0) return false;
            if (!csoMap.TryGetValue("guid", out var guidObj) || guidObj?.ToString() is not { Length: > 0 } g) return false;

            guid = g;
            return true;
        }

        /// <summary>Reads a document's own <c>m_PrefabInstance</c> field (present on stripped
        /// placeholders), i.e. the fileId of the PrefabInstance document that owns it.</summary>
        private static bool TryGetPrefabInstanceId(UnityYamlDocument doc, out long piId)
        {
            piId = 0;
            return doc.Fields.TryGetValue("m_PrefabInstance", out var pi) &&
                   pi is Dictionary<string, object> piMap &&
                   piMap.TryGetValue("fileID", out var piFid) &&
                   long.TryParse(piFid?.ToString(), out piId) && piId != 0;
        }

        /// <summary>
        /// Named documents (GameObjects) use <c>m_Name</c> directly. Everything else that isn't
        /// owned by a GameObject — a PrefabInstance root, or a stripped placeholder left behind
        /// for a nested prefab override — has no name of its own, so this digs for the closest
        /// thing to one: a PrefabInstance's <c>m_Name</c> modification override (present when the
        /// instance renames its root), falling back through a stripped doc's owning PrefabInstance,
        /// and finally to the document's own type name.
        /// </summary>
        private static string ResolveDisplayName(ObjectGroup group, UnityYamlDocument anchor, List<UnityYamlDocument> before, List<UnityYamlDocument> after)
        {
            if (anchor.Fields.TryGetValue("m_Name", out var n) && !string.IsNullOrEmpty(n?.ToString()))
            {
                return n.ToString();
            }

            if (anchor.RootKey == "PrefabInstance")
            {
                return TryGetPrefabInstanceNameOverride(anchor) ?? "Prefab Instance";
            }

            // A split-out nested child (see ResolveStrippedOwner) has its real name sitting in
            // the source prefab, not this file — resolve it so the row reads "Wheel_FL" instead
            // of falling all the way through to a generic component-type-based fallback below.
            // The instance-level name-override fallback right after this is deliberately NOT
            // tried for these — it looks up ANY m_Name override recorded anywhere in the whole
            // PrefabInstance's modifications, which is the instance's OWN root name (e.g.
            // "PBF_A320") and would otherwise get misapplied to every one of its split-out
            // children too, making them all show the same wrong name.
            if (group.NestedInstanceId.HasValue)
            {
                var nestedName = TryResolveNestedChildName(anchor);
                if (nestedName != null) return nestedName;

                var genericName = ObjectNames.NicifyVariableName(anchor.RootKey);
                // A stale override whose target no longer exists anywhere in the source prefab
                // (see SourcePrefabResolver.IsUnresolvedReference) can't be named at all — flag it
                // plainly instead of showing a bare type name that reads as if it were a real,
                // still-present part of the hierarchy.
                return SourcePrefabResolver.IsUnresolvedReference(anchor)
                    ? $"{genericName} (unresolved override)"
                    : genericName;
            }

            if (anchor.Fields.TryGetValue("m_PrefabInstance", out var pi) &&
                pi is Dictionary<string, object> piMap &&
                piMap.TryGetValue("fileID", out var piFid) &&
                long.TryParse(piFid?.ToString(), out var piId) &&
                piId != 0)
            {
                var prefabInstanceDoc = after.Concat(before).FirstOrDefault(d => d.FileId == piId && d.RootKey == "PrefabInstance");
                var overrideName = prefabInstanceDoc != null ? TryGetPrefabInstanceNameOverride(prefabInstanceDoc) : null;
                if (overrideName != null)
                {
                    return overrideName;
                }
            }

            return ObjectNames.NicifyVariableName(anchor.RootKey);
        }

        private static string TryGetPrefabInstanceNameOverride(UnityYamlDocument prefabInstanceDoc)
        {
            if (prefabInstanceDoc.Fields.TryGetValue("m_Modification", out var modObj) &&
                modObj is Dictionary<string, object> modMap &&
                modMap.TryGetValue("m_Modifications", out var modsObj) &&
                modsObj is List<object> mods)
            {
                foreach (var item in mods)
                {
                    if (item is Dictionary<string, object> modEntry &&
                        modEntry.TryGetValue("propertyPath", out var pp) &&
                        pp?.ToString() == "m_Name" &&
                        modEntry.TryGetValue("value", out var val) &&
                        !string.IsNullOrEmpty(val?.ToString()))
                    {
                        return val.ToString();
                    }
                }
            }

            return null;
        }

        /// <summary>Resolves a split-out nested child's own name by reading its owning
        /// GameObject document straight out of the source prefab (see
        /// <see cref="TryResolveOwningSourceGameObjectId"/> / <see cref="SourcePrefabResolver"/>) —
        /// or, when that owner is itself a nested PrefabInstance (a Prefab Variant's own top-level
        /// sub-prefab), its own name-override modification instead, since a PrefabInstance document
        /// never carries a plain m_Name field of its own.</summary>
        private static string TryResolveNestedChildName(UnityYamlDocument doc)
        {
            if (!TryResolveOwningSourceGameObjectId(doc, out var guid, out var ownerGoId)) return null;

            var goDoc = SourcePrefabResolver.ResolveDocument(guid, ownerGoId);
            if (goDoc == null) return null;

            if (goDoc.RootKey == "PrefabInstance") return TryGetPrefabInstanceNameOverride(goDoc);

            return goDoc.Fields.TryGetValue("m_Name", out var nameVal) && !string.IsNullOrEmpty(nameVal?.ToString())
                ? nameVal.ToString()
                : null;
        }
    }
}
