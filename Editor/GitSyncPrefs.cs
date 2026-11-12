using UnityEditor;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// PC마다 다른 설정 (EditorPrefs에 저장, Git에 올라가지 않음): 파이썬·claude 명령 경로.
    /// </summary>
    /// <remarks>작성일: 2026-11-11<br/>수정일: 2026-11-12 (Python 경로를 비우면 자동 탐색)</remarks>
    public static class GitSyncPrefs
    {
        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>실제로 실행할 Python. 직접 적은 경로가 있으면 그것, 없으면 자동으로 찾은 것.</summary>
        public static string PythonPath => IsPythonAuto ? GitSyncTools.FindPython() : PythonPathSetting;

        /// <summary>직접 적은 Python 경로 (비우면 자동 탐색: Windows는 python → py → python3, 그 밖은 python3 → python).</summary>
        public static string PythonPathSetting
        {
            get => EditorPrefs.GetString(PYTHON_KEY, string.Empty);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    EditorPrefs.DeleteKey(PYTHON_KEY);
                else
                    EditorPrefs.SetString(PYTHON_KEY, value.Trim().Trim('"', '\'').Trim());
            }
        }

        /// <summary>
        /// Python 경로를 자동으로 찾는지 (직접 적은 경로가 없음).
        /// 1.1.x 이하는 빈 값을 <c>python</c>으로 저장했으므로 <c>python</c>도 자동으로 봄 (자동 탐색의 첫 후보와 같음).
        /// </summary>
        public static bool IsPythonAuto
        {
            get
            {
                string setting = PythonPathSetting.Trim();
                return setting.Length == 0 || string.Equals(setting, LEGACY_DEFAULT_PYTHON, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>claude 명령 경로. 비우면 PATH에서 찾습니다 (자동 요약에만 씀).</summary>
        public static string ClaudePath
        {
            get => EditorPrefs.GetString(CLAUDE_KEY, string.Empty);
            set => EditorPrefs.SetString(CLAUDE_KEY, value == null ? string.Empty : value.Trim().Trim('"', '\'').Trim());
        }

        #endregion

        #region [ 상수 (Constants) ]

        private const string PYTHON_KEY = "GitSync.PythonPath";
        private const string CLAUDE_KEY = "GitSync.ClaudePath";
        private const string LEGACY_DEFAULT_PYTHON = "python";

        #endregion
    }
}
