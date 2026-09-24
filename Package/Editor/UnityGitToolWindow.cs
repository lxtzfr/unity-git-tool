using UnityEditor;

namespace UnityGitTool
{
    public class UnityGitToolWindow : EditorWindow
    {
        [MenuItem("Window/Unity Git Tool")]
        private static void Open()
        {
            GetWindow<UnityGitToolWindow>("Unity Git Tool");
        }
    }
}
