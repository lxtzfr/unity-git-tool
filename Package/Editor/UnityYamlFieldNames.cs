using System.Text.RegularExpressions;

namespace UnityGitTool
{
    /// <summary>
    /// Turns a raw serialized field name (`m_LocalRotation`, `m_IsActive`, an auto-property's
    /// `&lt;Foo&gt;k__BackingField`) into the kind of label Unity's own Inspector shows for it
    /// (`Rotation`... well, `Local Rotation`, `Is Active`, `Foo`) — the property table should read
    /// like an Inspector, not a YAML dump. Best-effort word-splitting on case changes, not a lookup
    /// table, so it also degrades reasonably for fields this has never seen (custom script fields).
    /// </summary>
    internal static class UnityYamlFieldNames
    {
        private static readonly Regex BackingField = new(@"^<(.+)>k__BackingField$", RegexOptions.Compiled);
        private static readonly Regex WordBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        public static string Humanize(string rawKey)
        {
            var name = rawKey;

            var backing = BackingField.Match(name);
            if (backing.Success) name = backing.Groups[1].Value;

            if (name.StartsWith("m_")) name = name.Substring(2);
            else if (name.StartsWith("_")) name = name.Substring(1);

            name = WordBoundary.Replace(name, " ").Trim();
            return name.Length == 0 ? rawKey : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
