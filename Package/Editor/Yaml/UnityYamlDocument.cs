using System.Collections.Generic;

namespace UnityGitTool.Yaml
{
    /// <summary>
    /// One "--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;" document from a Unity YAML asset
    /// (a GameObject, Component, Material, etc.).
    /// </summary>
    public class UnityYamlDocument
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;

        /// <summary>The single root key of the document, e.g. "GameObject", "Transform", "Material".</summary>
        public string RootKey;

        /// <summary>Fields of the root object. Values are string, Dictionary&lt;string,object&gt; or List&lt;object&gt;.</summary>
        public Dictionary<string, object> Fields;
    }
}
