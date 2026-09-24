using System.Collections.Generic;
using UnityGitTool.Yaml;

namespace UnityGitTool.Diff
{
    /// <summary>Which section of the object list a group belongs to — keeps scene-level
    /// singletons (RenderSettings, NavMeshSettings, ...) from being visually indistinguishable
    /// from actual GameObject hierarchy roots, since both sit at Depth 0.</summary>
    internal enum GroupCategory
    {
        GameObjects,
        SceneSettings,
        Other,
    }

    /// <summary>All documents belonging to one logical Inspector object (a GameObject and its
    /// Components, or a standalone document) across both revisions being compared.</summary>
    internal class ObjectGroup
    {
        public long OwnerFileId;
        public string DisplayName;
        public DocumentChangeType Status;
        public List<UnityYamlDocument> BeforeDocs = new();
        public List<UnityYamlDocument> AfterDocs = new();
        public List<DocumentDiff> DocDiffs = new();

        /// <summary>Owning GameObject's fileId, from this GameObject's Transform's
        /// m_Father — 0 for roots and for non-GameObject groups (materials, RenderSettings, ...).</summary>
        public long ParentFileId;

        /// <summary>Set when this group represents one specific nested-prefab-instance child
        /// object split out from its PrefabInstance (see ObjectGrouper.ResolveStrippedOwner) —
        /// the fileId of that owning PrefabInstance. Used both to resolve a friendlier display
        /// name (the child's own name in the source prefab, instead of a generic component-type
        /// fallback) and as a hierarchy-parent fallback when the child's own father chain can't
        /// be resolved locally.</summary>
        public long? NestedInstanceId;

        /// <summary>The source prefab guid and GameObject fileId this split-out child was
        /// resolved from (set alongside <see cref="NestedInstanceId"/>) — lets the hierarchy
        /// linker walk the child's real ancestor chain in the source prefab instead of flattening
        /// every override straight under the PrefabInstance's own row.</summary>
        public string SplitSourceGuid;
        public long? SplitSourceGoId;

        /// <summary>Depth in the GameObject hierarchy, for Hierarchy-window-style indentation.</summary>
        public int Depth;

        public GroupCategory Category;

        /// <summary>The document <see cref="ObjectGrouper"/> anchored this group's display name
        /// on (the owning GameObject, or the lone document for a non-GameObject group) — reused
        /// to pick the row's icon so it matches the same object the name refers to.</summary>
        public UnityYamlDocument Anchor;

        /// <summary>True when this GameObject's own fileId is listed in some PrefabInstance's
        /// <c>m_Modification.m_AddedGameObjects</c> — i.e. it's a genuine "added to this instance"
        /// override, the same thing Unity's own Hierarchy marks with a small "+" badge on the icon.
        /// Deliberately narrower than <c>Status == Added</c>: a plain new scene GameObject with no
        /// prefab involved, or an untouched GameObject inside a freshly-added whole instance (see
        /// VisitInstanceTree — its Status is Added too, inherited from the instance, but it was
        /// never individually added to anything), gets no badge in Unity either.</summary>
        public bool IsAddedGameObjectOverride;

        /// <summary>Same idea as <see cref="IsAddedGameObjectOverride"/> but for
        /// <c>m_RemovedGameObjects</c> — Unity's "-" badge.</summary>
        public bool IsRemovedGameObjectOverride;
    }
}
