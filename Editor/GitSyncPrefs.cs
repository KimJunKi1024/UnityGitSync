using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// PC마다 다른 설정 (EditorPrefs에 저장, Git에 올라가지 않음): 파이썬·claude 명령 경로, 자동 체크 제외 경로.
    /// </summary>
    /// <remarks>작성일: 2026-11-11<br/>수정일: 2026-11-12 (Python 경로를 비우면 자동 탐색)<br/>수정일: 2026-11-15 (개인·PC별 설정 자동 체크 제외 경로 추가)</remarks>
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
                return setting.Length == 0 || string.Equals(setting, LEGACY_DEFAULT_PYTHON, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>claude 명령 경로. 비우면 PATH에서 찾습니다 (자동 요약에만 씀).</summary>
        public static string ClaudePath
        {
            get => EditorPrefs.GetString(CLAUDE_KEY, string.Empty);
            set => EditorPrefs.SetString(CLAUDE_KEY, value == null ? string.Empty : value.Trim().Trim('"', '\'').Trim());
        }

        /// <summary>
        /// 커밋 창에서 자동 체크(처음 열기·새로 감지·모두 선택)에서 뺄 경로 목록 (한 줄에 하나, 프로젝트 기준 상대 경로).
        /// 한 번도 바꾸지 않았으면 기본 목록을 돌려줍니다.
        /// </summary>
        public static string UncheckedPathsText
        {
            get => EditorPrefs.HasKey(UNCHECKED_PATHS_KEY)
                ? EditorPrefs.GetString(UNCHECKED_PATHS_KEY, string.Empty)
                : GetDefaultUncheckedPathsText();
            set
            {
                // TextArea에서 줄바꿈 입력이 끊기지 않게 개행 형식만 \n으로 맞춤
                string normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                EditorPrefs.SetString(UNCHECKED_PATHS_KEY, normalized);
            }
        }

        /// <summary>자동 체크 제외 경로가 패키지 기본값과 같은지.</summary>
        public static bool IsDefaultUncheckedPaths
        {
            get
            {
                if (!EditorPrefs.HasKey(UNCHECKED_PATHS_KEY))
                    return true;
                return string.Equals(
                    string.Join("\n", GetUncheckedPatterns()),
                    GetDefaultUncheckedPathsText(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>기본 자동 체크 제외 경로 (유저별 레이아웃·에디터 도구 상태·Git Sync 설정).</summary>
        public static readonly string[] DEFAULT_UNCHECKED_PATHS =
        {
            "UserSettings/",
            "ProjectSettings/Packages/",
            "ProjectSettings/GitSyncSettings.json",
        };

        private const string PYTHON_KEY = "GitSync.PythonPath";
        private const string CLAUDE_KEY = "GitSync.ClaudePath";
        private const string UNCHECKED_PATHS_KEY = "GitSync.UncheckedPaths";
        private const string LEGACY_DEFAULT_PYTHON = "python";

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 자동 체크 제외 경로를 패키지 기본값으로 되돌립니다.
        /// </summary>
        public static void ResetUncheckedPaths()
        {
            EditorPrefs.DeleteKey(UNCHECKED_PATHS_KEY);
        }

        /// <summary>
        /// 정리된 자동 체크 제외 패턴 목록을 돌려줍니다.
        /// </summary>
        public static List<string> GetUncheckedPatterns()
        {
            var result = new List<string>();
            string[] lines = UncheckedPathsText.Split('\n');
            foreach (string rawLine in lines)
            {
                string pattern = NormalizePattern(rawLine);
                if (!string.IsNullOrEmpty(pattern))
                    result.Add(pattern);
            }
            return result;
        }

        /// <summary>
        /// 저장소 기준 경로가 자동 체크 제외 대상인지 판별합니다.
        /// 프로젝트가 저장소의 하위 폴더에 있을 때도 <paramref name="projectPrefix"/>를 떼고 함께 검사합니다.
        /// </summary>
        /// <param name="repoPath">Git 저장소 기준 파일 경로</param>
        /// <param name="projectPrefix">저장소 루트 대비 Unity 프로젝트 상대 경로 (같으면 빈 문자열)</param>
        public static bool IsAutoUncheckedPath(string repoPath, string projectPrefix)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                return false;

            List<string> patterns = GetUncheckedPatterns();
            if (patterns.Count == 0)
                return false;

            string normalizedRepo = NormalizePath(repoPath);
            string projectRel = StripProjectPrefix(normalizedRepo, projectPrefix);

            foreach (string pattern in patterns)
            {
                if (MatchesPattern(normalizedRepo, pattern))
                    return true;
                if (!string.Equals(projectRel, normalizedRepo, StringComparison.Ordinal) && MatchesPattern(projectRel, pattern))
                    return true;
            }
            return false;
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        private static string GetDefaultUncheckedPathsText()
        {
            return string.Join("\n", DEFAULT_UNCHECKED_PATHS);
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        }

        private static string NormalizePattern(string raw)
        {
            string pattern = (raw ?? string.Empty).Trim().Trim('"', '\'').Trim().Replace('\\', '/');
            while (pattern.StartsWith("./", StringComparison.Ordinal))
                pattern = pattern.Substring(2);
            return pattern.TrimStart('/');
        }

        private static string StripProjectPrefix(string normalizedRepoPath, string projectPrefix)
        {
            string prefix = NormalizePath(projectPrefix).TrimEnd('/');
            if (string.IsNullOrEmpty(prefix))
                return normalizedRepoPath;

            string withSlash = prefix + "/";
            return normalizedRepoPath.StartsWith(withSlash, StringComparison.OrdinalIgnoreCase)
                ? normalizedRepoPath.Substring(withSlash.Length)
                : normalizedRepoPath;
        }

        private static bool MatchesPattern(string path, string pattern)
        {
            string withoutMeta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(0, path.Length - 5)
                : path;

            if (pattern.IndexOf('*') >= 0)
            {
                string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase)
                       || Regex.IsMatch(withoutMeta, regex, RegexOptions.IgnoreCase);
            }

            if (pattern.EndsWith("/", StringComparison.Ordinal))
            {
                string folderName = pattern.TrimEnd('/');
                return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(withoutMeta, folderName, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(withoutMeta, pattern, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
