using UnityEditor;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// PC마다 다른 설정 (EditorPrefs에 저장, Git에 올라가지 않음): 파이썬·claude 명령 경로.
    /// </summary>
    /// <remarks>작성일: 2026-11-11</remarks>
    public static class GitSyncPrefs
    {
        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>파이썬 실행 파일 경로 (기본 <c>python</c>).</summary>
        public static string PythonPath
        {
            get => EditorPrefs.GetString(PYTHON_KEY, DEFAULT_PYTHON);
            set => EditorPrefs.SetString(PYTHON_KEY, string.IsNullOrWhiteSpace(value) ? DEFAULT_PYTHON : value.Trim());
        }

        /// <summary>claude 명령 경로. 비우면 PATH에서 찾습니다 (자동 요약에만 씀).</summary>
        public static string ClaudePath
        {
            get => EditorPrefs.GetString(CLAUDE_KEY, string.Empty);
            set => EditorPrefs.SetString(CLAUDE_KEY, value == null ? string.Empty : value.Trim());
        }

        #endregion

        #region [ 상수 (Constants) ]

        private const string PYTHON_KEY = "GitSync.PythonPath";
        private const string CLAUDE_KEY = "GitSync.ClaudePath";
        private const string DEFAULT_PYTHON = "python";

        #endregion
    }
}
