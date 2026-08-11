namespace VisualGitDiff.Diff
{
    public enum FieldChangeType
    {
        Added,
        Removed,
        Modified,
    }

    /// <summary>
    /// One leaf-level change inside a document's field tree. <see cref="Path"/> uses the
    /// same slash/bracket convention as the rest of the toolchain, e.g.
    /// "m_SavedProperties/m_Colors/[0]/_BaseColor/r".
    /// </summary>
    public class FieldChange
    {
        public FieldChangeType Type;
        public string Path;
        public string OldValue;
        public string NewValue;

        public override string ToString()
        {
            return Type switch
            {
                FieldChangeType.Added => $"+ {Path} = {NewValue}",
                FieldChangeType.Removed => $"- {Path} = {OldValue}",
                _ => $"~ {Path}: {OldValue} -> {NewValue}",
            };
        }
    }
}
