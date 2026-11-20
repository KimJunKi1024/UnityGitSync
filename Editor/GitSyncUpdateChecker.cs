using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 새 버전 확인. Git 주소로 설치된 경우에만, 설치한 저장소의 <c>vX.Y.Z</c> 태그 중 가장 높은 버전을 지금 버전과 비교합니다.<br/>
    /// 에디터를 켤 때 마지막 확인에서 24시간이 지났으면 백그라운드로 확인하고, 새 버전이 있으면 콘솔에 남기고 알림 창을 띄웁니다.
    /// [업데이트]는 Package Manager로 <c>주소#새 태그</c>를 설치합니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-20 (Work Log 1.1.0의 업데이트 확인을 옮겨 옴)</remarks>
    [InitializeOnLoad]
    public static class GitSyncUpdateChecker
    {
        #region [ 확인 결과 (Check Result) ]

        /// <summary>
        /// 설치 정보와 확인 결과.
        /// </summary>
        public class UpdateInfo
        {
            /// <summary>패키지 표시 이름.</summary>
            public string DisplayName = string.Empty;

            /// <summary>지금 설치된 버전 (package.json의 version).</summary>
            public string CurrentVersion = string.Empty;

            /// <summary>Package Manager에 다시 넣을 설치 주소 (<c>#태그</c> 제외, <c>git+</c> 접두어 유지).</summary>
            public string InstallUrl = string.Empty;

            /// <summary><c>git ls-remote</c>에 쓰는 저장소 주소.</summary>
            public string RemoteUrl = string.Empty;

            /// <summary>실행할 git (메인 스레드에서 미리 찾아 둠).</summary>
            public string GitExe = "git";

            /// <summary>확인할 때 작업 폴더로 쓸 프로젝트 폴더.</summary>
            public string WorkingDir = string.Empty;

            /// <summary>저장소의 가장 높은 버전 태그 (예: v1.4.0). 못 찾으면 빈 문자열.</summary>
            public string LatestTag = string.Empty;

            /// <summary>확인 실패 이유. 성공이면 빈 문자열.</summary>
            public string Error = string.Empty;

            /// <summary>새 버전이 있는지.</summary>
            public bool HasUpdate => LatestTag.Length > 0 && IsNewer(LatestTag, CurrentVersion);
        }

        #endregion

        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>이번 에디터 세션에서 찾은 새 버전 태그 (없거나 아직 확인 전이면 빈 문자열). 패널 머리말에 표시.</summary>
        public static string KnownLatestTag => SessionState.GetString(SESSION_LATEST_KEY, string.Empty);

        /// <summary>확인이 진행 중인지.</summary>
        public static bool IsChecking => _runningTask != null;

        #endregion

        #region [ 상수 (Constants) ]

        private const string MENU_PATH = "Tools/Git/업데이트 확인";
        private const string DIALOG_TITLE = "Git Sync 업데이트 확인";
        private const string LAST_CHECK_KEY = "GitSync.Update.LastCheckUtc";
        private const string SKIPPED_KEY = "GitSync.Update.SkippedTag";
        private const string SESSION_KEY = "GitSync.Update.CheckedThisSession";
        private const string SESSION_LATEST_KEY = "GitSync.Update.LatestTag";
        private const double CHECK_INTERVAL_HOURS = 24d;
        private const int GIT_TIMEOUT_MS = 20000;
        private const string TAG_PREFIX = "refs/tags/";
        private const string GIT_PLUS = "git+";

        #endregion

        #region [ 내부 상태 (Internal State) ]

        private static Task<UpdateInfo> _runningTask;
        private static bool _isManual;

        #endregion

        #region [ 초기화 (Initialization) ]

        static GitSyncUpdateChecker()
        {
            if (Application.isBatchMode)
                return;
            EditorApplication.delayCall += CheckOnStartup;
        }

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 메뉴·패널에서 지금 바로 확인합니다. 결과(최신·실패 포함)를 항상 알려 줍니다.
        /// </summary>
        [MenuItem(MENU_PATH, false, 40)]
        public static void CheckNow()
        {
            StartCheck(true);
        }

        /// <summary>
        /// 이 버전을 다시 알리지 않도록 기억합니다.
        /// </summary>
        public static void SkipVersion(string tag)
        {
            EditorPrefs.SetString(SKIPPED_KEY, tag ?? string.Empty);
        }

        /// <summary>
        /// 패키지를 해당 태그로 다시 설치합니다 (Packages/manifest.json의 주소가 <c>주소#태그</c>로 바뀜).
        /// </summary>
        public static AddRequest Install(UpdateInfo info)
        {
            return Client.Add(info.InstallUrl + "#" + info.LatestTag);
        }

        /// <summary>
        /// 설치 정보를 읽습니다. Git 주소로 설치된 경우가 아니면 false.
        /// </summary>
        public static bool TryGetInstallInfo(out UpdateInfo info)
        {
            info = null;
            PackageInfo package = PackageInfo.FindForAssembly(typeof(GitSyncUpdateChecker).Assembly);
            if (package == null || package.source != PackageSource.Git)
                return false;

            string installUrl;
            if (!TryParseGitPackageId(package.packageId, package.name, out installUrl))
                return false;

            info = new UpdateInfo
            {
                DisplayName = string.IsNullOrEmpty(package.displayName) ? package.name : package.displayName,
                CurrentVersion = package.version,
                InstallUrl = installUrl,
                RemoteUrl = installUrl.StartsWith(GIT_PLUS, StringComparison.Ordinal) ? installUrl.Substring(GIT_PLUS.Length) : installUrl,
                GitExe = GitSyncTools.FindGit(),
                WorkingDir = GitSyncSettings.ProjectRoot,
            };
            return true;
        }

        /// <summary>
        /// <c>이름@주소#태그</c> 형식의 packageId에서 <c>#태그</c>를 뺀 설치 주소를 꺼냅니다.
        /// </summary>
        public static bool TryParseGitPackageId(string packageId, string packageName, out string installUrl)
        {
            installUrl = string.Empty;
            string prefix = packageName + "@";
            if (string.IsNullOrEmpty(packageId) || !packageId.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string url = packageId.Substring(prefix.Length);
            int hash = url.IndexOf('#');
            installUrl = hash >= 0 ? url.Substring(0, hash) : url;
            return installUrl.Length > 0;
        }

        /// <summary>
        /// <c>git ls-remote --tags</c> 출력에서 가장 높은 <c>vX.Y.Z</c> 태그를 찾습니다. 없으면 빈 문자열.
        /// </summary>
        public static string FindLatestTag(string lsRemoteOutput)
        {
            string best = string.Empty;
            Version bestVersion = null;
            foreach (string rawLine in (lsRemoteOutput ?? string.Empty).Split('\n'))
            {
                string line = rawLine.Trim();
                int at = line.IndexOf(TAG_PREFIX, StringComparison.Ordinal);
                if (at < 0 || line.EndsWith("^{}", StringComparison.Ordinal))
                    continue;

                string tag = line.Substring(at + TAG_PREFIX.Length);
                Version version;
                if (TryParseVersion(tag, out version) && (bestVersion == null || version > bestVersion))
                {
                    best = tag;
                    bestVersion = version;
                }
            }
            return best;
        }

        /// <summary>
        /// <c>v1.2.3</c> 또는 <c>1.2.3</c>을 버전으로 읽습니다. 숫자 세 자리가 아니거나 <c>-preview</c> 같은 꼬리가 있으면 false.
        /// </summary>
        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            string value = (text ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);
            if (value.Split('.').Length != 3)
                return false;
            return Version.TryParse(value, out version);
        }

        /// <summary>
        /// 태그 버전이 지금 버전보다 높은지.
        /// </summary>
        public static bool IsNewer(string latestTag, string currentVersion)
        {
            Version latest, current;
            if (!TryParseVersion(latestTag, out latest))
                return false;
            if (!TryParseVersion(currentVersion, out current))
                return true;
            return latest > current;
        }

        /// <summary>
        /// GitHub 저장소면 해당 태그의 변경 기록 주소를, 아니면 빈 문자열을 돌려줍니다.
        /// </summary>
        public static string GetChangelogUrl(UpdateInfo info)
        {
            string url = info.RemoteUrl;
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 4);
            return url.TrimEnd('/') + "/blob/" + info.LatestTag + "/CHANGELOG.md";
        }

        #endregion

        #region [ 확인 흐름 (Check Flow) ]

        // 에디터를 켤 때 한 번: 마지막 확인에서 24시간이 지났을 때만
        private static void CheckOnStartup()
        {
            if (SessionState.GetBool(SESSION_KEY, false))
                return;
            SessionState.SetBool(SESSION_KEY, true);

            long ticks;
            if (long.TryParse(EditorPrefs.GetString(LAST_CHECK_KEY, string.Empty), out ticks)
                && (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalHours < CHECK_INTERVAL_HOURS)
                return;

            StartCheck(false);
        }

        private static void StartCheck(bool isManual)
        {
            if (_runningTask != null)
                return;

            UpdateInfo info;
            if (!TryGetInstallInfo(out info))
            {
                if (isManual)
                    EditorUtility.DisplayDialog(DIALOG_TITLE, "Git 주소로 설치한 경우에만 새 버전을 확인할 수 있습니다.\n(로컬 폴더나 Assets 안에 넣은 경우는 확인하지 않습니다)", "확인");
                return;
            }

            _isManual = isManual;
            _runningTask = Task.Run(() => QueryLatestTag(info));
            EditorApplication.update += PollCheck;
        }

        private static void PollCheck()
        {
            if (_runningTask == null || !_runningTask.IsCompleted)
                return;

            EditorApplication.update -= PollCheck;
            UpdateInfo info = _runningTask.IsFaulted ? null : _runningTask.Result;
            string fault = _runningTask.IsFaulted ? _runningTask.Exception.GetBaseException().Message : string.Empty;
            _runningTask = null;
            EditorPrefs.SetString(LAST_CHECK_KEY, DateTime.UtcNow.Ticks.ToString());

            if (info == null || info.Error.Length > 0)
            {
                if (_isManual)
                    EditorUtility.DisplayDialog(DIALOG_TITLE, "새 버전을 확인하지 못했습니다.\n" + (info == null ? fault : info.Error), "확인");
                return;
            }

            SessionState.SetString(SESSION_LATEST_KEY, info.HasUpdate ? info.LatestTag : string.Empty);
            if (!info.HasUpdate)
            {
                if (_isManual)
                    EditorUtility.DisplayDialog(DIALOG_TITLE, $"최신 버전입니다 (지금 v{info.CurrentVersion}" + (info.LatestTag.Length > 0 ? $", 저장소 최신 {info.LatestTag})" : ")"), "확인");
                return;
            }

            if (!_isManual && EditorPrefs.GetString(SKIPPED_KEY, string.Empty) == info.LatestTag)
                return;

            Debug.Log($"[Git Sync] 새 버전 {info.LatestTag}이 있습니다 (지금 v{info.CurrentVersion}). Tools → Git → 업데이트 확인");
            GitSyncUpdateWindow.Open(info);
        }

        // 백그라운드 스레드에서 실행: git ls-remote로 태그 목록을 받아 가장 높은 버전을 찾음 (Unity API를 쓰지 않음)
        private static UpdateInfo QueryLatestTag(UpdateInfo info)
        {
            string output, error;
            string arguments = "-c credential.interactive=never ls-remote --tags --refs " + GitSyncTools.Quote(info.RemoteUrl);
            int code = GitSyncTools.RunProcess(info.GitExe, arguments, info.WorkingDir, out output, out error, GIT_TIMEOUT_MS);
            if (code == -1)
                info.Error = "git을 실행하지 못했습니다: " + error.Trim() + "\nTools → Git → Git Sync 패널에서 git 설치를 확인하세요.";
            else if (code == -2)
                info.Error = $"저장소가 {GIT_TIMEOUT_MS / 1000}초 안에 답하지 않았습니다.";
            else if (code != 0)
                info.Error = error.Trim();
            else
                info.LatestTag = FindLatestTag(output);
            return info;
        }

        #endregion
    }
}
