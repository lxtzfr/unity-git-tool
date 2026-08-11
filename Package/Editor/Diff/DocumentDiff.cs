using System.Collections.Generic;

namespace VisualGitDiff.Diff
{
    public enum DocumentChangeType
    {
        Added,
        Removed,
        Modified,
        Unchanged,
    }

    /// <summary>One Unity YAML document (a GameObject, Component, Material, ...) matched
    /// across two revisions by <c>fileId</c>.</summary>
    public class DocumentDiff
    {
        public DocumentChangeType Type;
        public int ClassId;
        public long FileId;
        public string RootKey;
        public List<FieldChange> FieldChanges = new();
    }
}
