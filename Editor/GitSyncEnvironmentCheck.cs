using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 사용 전 환경 확인: 이 PC에 도구가 의존하는 프로그램(git·Python)과 설정이 갖춰졌는지 실제 실행으로 검사합니다.<br/>
    /// 항목은 '커밋에 필요', '푸시에만 필요', '선택'으로 나뉩니다. 원격이 없거나 첫 커밋 전인 저장소도 로컬 커밋은 할 수 있게 합니다.<br/>
    /// 검사 로직은 여기에, 화면은 <see cref="GitSyncEnvironmentWindow"/>에 있습니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-12<br/>수정일: 2026-11-12 (커밋용·푸시용 항목 분리, 지금 브랜치 검사, Git 버전·Python 자동 탐색, 프로그램/프로젝트 구분, winget 설치, claude·저장소 절대 경로 검사)</remarks>
    public static class GitSyncEnvironmentCheck
    {
        #region [ 검사 항목 (Check Item) ]

        /// <summary>
        /// 검사 항목 하나의 결과.
        /// </summary>
        public class Item
        {
            /// <summary>항목 이름 (예: "git").</summary>
            public string Name;

            /// <summary>통과 여부.</summary>
            public bool IsOk;

            /// <summary>필수 항목인지. 필수 항목이 실패하면 커밋(또는 푸시)을 막습니다.</summary>
            public bool IsRequired = true;

            /// <summary>푸시에만 필요한 항목인지 (원격·브랜치). 실패해도 로컬 커밋은 막지 않습니다.</summary>
            public bool IsPushOnly;

            /// <summary>PC에 설치하는 프로그램 항목인지 (git·Python·claude). 아니면 이 프로젝트의 항목.</summary>
            public bool IsProgram;

            /// <summary>한 줄 설명 (버전 또는 실패 이유).</summary>
            public string Detail;

            /// <summary>실패했을 때 해결 방법 한 줄.</summary>
            public string Fix;

            /// <summary>실패했을 때 보여 줄 버튼 (없으면 None).</summary>
            public FixAction Action;
        }

        /// <summary>실패 항목에 붙는 해결 버튼.</summary>
        public enum FixAction
        {
            None,
            OpenSettings,
            InstallGit,
            InstallPython,
            RestartUnity,
        }

        /// <summary>
        /// 전체 검사 결과.
        /// </summary>
        public class Report
        {
            /// <summary>항목 목록 (표시 순서).</summary>
            public readonly List<Item> Items = new List<Item>();

            /// <summary>로컬 커밋에 필요한 항목이 모두 통과했는지.</summary>
            public bool IsReadyForCommit => IsReady(false);

            /// <summary>푸시에 필요한 항목까지 모두 통과했는지.</summary>
            public bool IsReadyForPush => IsReady(true);

            /// <summary>실패한 항목 수 (선택 항목 포함).</summary>
            public int FailCount
            {
                get
                {
                    int count = 0;
                    foreach (Item item in Items)
                    {
                        if (!item.IsOk)
                            count++;
                    }
                    return count;
                }
            }

            /// <summary>
            /// 커밋(또는 푸시)에 필요한 항목이 모두 통과했는지.
            /// </summary>
            public bool IsReady(bool forPush)
            {
                foreach (Item item in Items)
                {
                    if (item.IsRequired && !item.IsOk && (forPush || !item.IsPushOnly))
                        return false;
                }
                return true;
            }
        }

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>도구가 쓰는 최소 Git 버전 (<c>git add --pathspec-from-file</c>이 2.25부터).</summary>
        public const int GIT_MIN_MAJOR = 2;

        /// <summary>도구가 쓰는 최소 Git 버전.</summary>
        public const int GIT_MIN_MINOR = 25;

        /// <summary>Git 다운로드 페이지.</summary>
        public const string GIT_DOWNLOAD_URL = "https://git-scm.com/downloads";

        /// <summary>Python 다운로드 페이지.</summary>
        public const string PYTHON_DOWNLOAD_URL = "https://www.python.org/downloads/";

        /// <summary>패키지의 <c>Tools~</c>와 <c>Library/GitSync/Tools/</c>에 있어야 하는 도구 파일.</summary>
        public static readonly string[] REQUIRED_TOOLS = { GitSyncTools.COMMIT_SCRIPT, GitSyncTools.PUSH_SCRIPT, "gitsync_common.py" };

        // 도구 스크립트가 import 하는 모듈 (모두 표준 라이브러리). Python에서 실제로 import 해 보고 추가 패키지 필요 여부를 판단
        private static readonly string[] PYTHON_MODULES = { "argparse", "datetime", "json", "os", "re", "shutil", "subprocess", "sys", "tempfile" };

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 모든 항목을 검사합니다. 몇 초 걸릴 수 있으니 메인 스레드에서 호출하되 자주 부르지 마세요.
        /// </summary>
        public static Report Run()
        {
            var report = new Report();
            GitSyncSettings settings = GitSyncSettings.Load();

            // PC에 설치하는 프로그램
            Item gitItem = Guard("git", CheckGit, isProgram: true);
            report.Items.Add(gitItem);
            report.Items.Add(Guard("Python", CheckPython, isProgram: true));
            report.Items.Add(Guard("claude", () => CheckClaude(settings), isProgram: true, isRequired: false));

            // 이 프로젝트
            report.Items.Add(Guard("도구 스크립트", CheckTools));
            report.Items.Add(Guard("명령줄 도구", CheckToolsCopy, isRequired: false));
            string repo = null;
            report.Items.Add(Guard("Git 저장소", () => CheckRepository(settings, gitItem.IsOk, out repo)));
            report.Items.Add(Guard("설정 파일", () => CheckSettingsFile(settings)));
            report.Items.Add(Guard("원격·브랜치", () => CheckRemoteAndBranch(settings, gitItem.IsOk, repo), isPushOnly: true));

            return report;
        }

        /// <summary>
        /// 커밋(또는 푸시)에 필요한 항목이 모두 통과했는지 확인합니다. 아니면 환경 확인 창을 띄우고 false를 돌려줍니다.
        /// </summary>
        /// <param name="forPush">true면 원격·브랜치 같은 푸시용 항목까지 봄</param>
        public static bool EnsureReadyOrShowWindow(bool forPush)
        {
            Report report = Run();
            if (report.IsReady(forPush))
                return true;

            GitSyncEnvironmentWindow.Open(report);
            return false;
        }

        /// <summary>
        /// 해결 버튼을 실행합니다.
        /// </summary>
        public static void Execute(FixAction action)
        {
            switch (action)
            {
                case FixAction.OpenSettings: GitSyncSettingsProvider.Open(); break;
                case FixAction.InstallGit: Install("Git", GitSyncTools.WINGET_GIT_ID, GIT_DOWNLOAD_URL); break;
                case FixAction.InstallPython: Install("Python 3.12", GitSyncTools.WINGET_PYTHON_ID, PYTHON_DOWNLOAD_URL); break;
                case FixAction.RestartUnity:
                    if (EditorUtility.DisplayDialog("도구 다시 복사", "도구를 Library/GitSync/Tools/에 다시 복사할까요? 계속 실패하면 Unity를 다시 켜 보세요.", "다시 복사", "취소"))
                        GitSyncTools.CopyToolsToLibrary();
                    break;
            }
        }

        /// <summary>
        /// 해결 버튼의 글자.
        /// </summary>
        public static string GetActionLabel(FixAction action)
        {
            switch (action)
            {
                case FixAction.OpenSettings: return "설정 열기";
                case FixAction.InstallGit: return GitSyncTools.IsWingetAvailable() ? "Git 설치" : "Git 다운로드";
                case FixAction.InstallPython: return GitSyncTools.IsWingetAvailable() ? "Python 설치" : "Python 다운로드";
                case FixAction.RestartUnity: return "다시 복사";
                default: return string.Empty;
            }
        }

        #endregion

        #region [ 항목별 검사 (Checks) ]

        private static Item CheckGit()
        {
            var item = new Item { Name = "git" };
            string output, error;
            string git = GitSyncTools.FindGit(true);
            int code = GitSyncTools.RunProcess(git, "--version", GitSyncSettings.ProjectRoot, out output, out error);

            if (code != 0 || !output.StartsWith("git version", StringComparison.OrdinalIgnoreCase))
            {
                item.Detail = code == -1 ? "git을 찾지 못했습니다 (PATH와 표준 설치 위치 모두 없음)." : "git 실행에 실패했습니다: " + FirstLine(error + output);
                item.Fix = "Git을 설치한 뒤 '다시 확인'을 누르세요 (표준 위치에 설치하면 Unity를 다시 켜지 않아도 찾습니다).";
                item.Action = FixAction.InstallGit;
                return item;
            }

            // "git version 2.55.0.windows.5" / "git version 2.39.3 (Apple Git-146)"
            string[] words = output.Trim().Split(' ');
            string[] numbers = words.Length > 2 ? words[2].Split('.') : new string[0];
            int major, minor;
            if (numbers.Length >= 2 && int.TryParse(numbers[0], out major) && int.TryParse(numbers[1], out minor)
                && (major < GIT_MIN_MAJOR || (major == GIT_MIN_MAJOR && minor < GIT_MIN_MINOR)))
            {
                item.Detail = $"{output.Trim()} — {GIT_MIN_MAJOR}.{GIT_MIN_MINOR} 이상이 필요합니다 (고른 파일만 커밋하는 기능).";
                item.Fix = "새 Git을 설치하고 Unity를 다시 켜세요.";
                item.Action = FixAction.InstallGit;
                return item;
            }

            item.IsOk = true;
            item.Detail = output.Trim() + (git == "git" ? string.Empty : " (" + git + ")");
            return item;
        }

        private static Item CheckPython()
        {
            bool isAuto = GitSyncPrefs.IsPythonAuto;
            string python = isAuto ? GitSyncTools.FindPython(true) : GitSyncPrefs.PythonPath;
            string shown = isAuto ? $"{python}, 자동으로 찾음" : python;
            var item = new Item { Name = "Python" };

            // 버전과 표준 라이브러리 import를 한 번에 확인 (-I: 현재 폴더·환경 변수 영향을 받지 않게)
            string script = "import sys;" + string.Join(";", Array.ConvertAll(PYTHON_MODULES, m => "import " + m))
                            + ";print(sys.version_info[0],sys.version_info[1],sys.version_info[2])";
            string output, error;
            int code = GitSyncTools.RunProcess(python, "-I -c " + GitSyncTools.Quote(script), GitSyncSettings.ProjectRoot, out output, out error);

            if (code == -1 || code == -2)
            {
                item.Detail = isAuto
                    ? $"Python을 찾지 못했습니다 ({string.Join(", ", GitSyncTools.GetPythonCandidates())} 모두 실행되지 않음)."
                    : $"'{python}' 을(를) 실행하지 못했습니다 ({(code == -2 ? "시간 초과" : "파일이 없거나 실행할 수 없음")}).";
                item.Fix = "Python 3.8 이상을 설치한 뒤 '다시 확인'을 누르거나, 설정의 Python 경로에 python 실행 파일 전체 경로를 적으세요.";
                item.Action = isAuto ? FixAction.InstallPython : FixAction.OpenSettings;
                return item;
            }

            // Windows 스토어 자리표시자 python.exe는 실행되지만 설치 안내만 출력하고 비정상 종료
            if (code != 0)
            {
                string reason = FirstLine(error + output);
                item.Detail = (isAuto ? "Python을 찾지 못했습니다. " : string.Empty)
                              + (string.IsNullOrEmpty(reason) ? $"'{python}' 실행이 실패했습니다 (종료 코드 {code})." : reason);
                item.Fix = reason.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "표준 라이브러리가 빠진 Python입니다. python.org에서 다시 설치하세요."
                    : "Python 3.8 이상을 설치하거나, 설정의 Python 경로를 실제 python 실행 파일로 바꾸세요.";
                item.Action = isAuto ? FixAction.InstallPython : FixAction.OpenSettings;
                return item;
            }

            string[] parts = output.Trim().Split(' ');
            int major, minor;
            if (parts.Length < 2 || !int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor))
            {
                item.Detail = "Python 버전을 읽지 못했습니다: " + FirstLine(output);
                item.Fix = "설정의 Python 경로가 python 실행 파일을 가리키는지 확인하세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            string version = string.Join(".", parts);
            if (major * 100 + minor < GitSyncTools.PYTHON_MIN_VERSION)
            {
                item.Detail = $"Python {version} ({shown}) — 3.8 이상이 필요합니다.";
                item.Fix = "새 Python을 설치하고, 필요하면 설정의 Python 경로를 바꾸세요.";
                item.Action = FixAction.InstallPython;
                return item;
            }

            item.IsOk = true;
            item.Detail = $"Python {version} ({shown}) · 표준 라이브러리만 사용, 추가 패키지 불필요";
            return item;
        }

        // 커밋 창이 실제로 실행하는 원본 (패키지의 Tools~)
        private static Item CheckTools()
        {
            var item = new Item { Name = "도구 스크립트" };
            string source = GitSyncTools.GetToolsSourceDir();
            List<string> missing = string.IsNullOrEmpty(source) ? new List<string>(REQUIRED_TOOLS) : FindMissing(source);

            if (missing.Count == 0)
            {
                item.IsOk = true;
                item.Detail = $"패키지의 Tools~ 에 {REQUIRED_TOOLS.Length}개 모두 있음";
                return item;
            }

            item.Detail = string.IsNullOrEmpty(source)
                ? "패키지의 Tools~ 폴더를 찾지 못했습니다."
                : "패키지의 Tools~ 에 없음: " + string.Join(", ", missing);
            item.Fix = "Package Manager에서 Git Sync 패키지를 지우고 다시 설치하세요.";
            return item;
        }

        // 명령줄(Claude Code 등)에서 쓰는 복사본. 커밋 창은 원본을 쓰므로 선택 항목
        private static Item CheckToolsCopy()
        {
            var item = new Item { Name = "명령줄 도구", IsRequired = false };
            if (FindMissing(GitSyncTools.LibraryToolsDir).Count > 0)
                GitSyncTools.CopyToolsToLibrary();

            List<string> missing = FindMissing(GitSyncTools.LibraryToolsDir);
            if (missing.Count == 0)
            {
                item.IsOk = true;
                item.Detail = $"Library/GitSync/Tools/ 에 {REQUIRED_TOOLS.Length}개 모두 있음";
                return item;
            }

            item.Detail = "Library/GitSync/Tools/ 에 없음: " + string.Join(", ", missing) + " (명령줄에서 쓸 때만 필요)";
            item.Fix = "'다시 복사'를 누르거나 Unity를 다시 켜세요.";
            item.Action = FixAction.RestartUnity;
            return item;
        }

        private static Item CheckRepository(GitSyncSettings settings, bool hasGit, out string repo)
        {
            var item = new Item { Name = "Git 저장소" };
            repo = null;
            string project = GitSyncSettings.ProjectRoot;

            if (!string.IsNullOrWhiteSpace(settings.Repo))
            {
                string configured = GitSyncSettings.CleanPath(settings.Repo);
                string path;
                try
                {
                    path = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(project, configured));
                }
                catch (Exception)
                {
                    // 따옴표 등 경로에 못 쓰는 글자가 들어 있음 (다른 PC에서 값을 통째로 붙여 넣은 경우)
                    item.Detail = "설정의 저장소 폴더 값이 경로가 아닙니다: " + configured;
                    item.Fix = "설정의 '저장소 폴더'에서 따옴표를 빼거나 비우세요 (비우면 프로젝트가 들어 있는 저장소를 씁니다).";
                    item.Action = FixAction.OpenSettings;
                    return item;
                }
                if (!Directory.Exists(path))
                {
                    item.Detail = "설정의 저장소 폴더가 없습니다: " + path;
                    item.Fix = "설정의 '저장소 폴더'를 바로잡거나 비우세요 (비우면 프로젝트가 들어 있는 저장소를 씁니다).";
                    item.Action = FixAction.OpenSettings;
                    return item;
                }
                if (hasGit && !IsInsideWorkTree(path))
                {
                    item.Detail = "설정의 저장소 폴더가 Git 저장소가 아닙니다: " + path;
                    item.Fix = "그 폴더에서 git init 또는 clone을 하거나, 설정의 '저장소 폴더'를 바로잡으세요.";
                    item.Action = FixAction.OpenSettings;
                    return item;
                }
                repo = path;
                item.IsOk = true;
                item.Detail = "설정의 저장소 폴더: " + path;
                return item;
            }

            if (!hasGit)
            {
                item.Detail = "git이 없어 확인하지 못했습니다.";
                item.Fix = "먼저 git 항목을 해결하세요.";
                return item;
            }

            string output, error;
            int code = GitSyncTools.RunProcess(GitSyncTools.GitExe, "rev-parse --show-toplevel", project, out output, out error);
            if (code != 0 || string.IsNullOrWhiteSpace(output))
            {
                item.Detail = "프로젝트 폴더가 Git 저장소 안에 있지 않습니다: " + project;
                item.Fix = "프로젝트를 저장소 안에 두거나(git init / clone), 설정의 '저장소 폴더'를 지정하세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            repo = output.Trim().Replace('/', Path.DirectorySeparatorChar);
            item.IsOk = true;
            item.Detail = "프로젝트가 들어 있는 저장소: " + repo;
            return item;
        }

        // 파일이 있는데 읽지 못하면 파이썬 도구가 커밋 단계에서 멈추므로 커밋에 필요한 항목
        private static Item CheckSettingsFile(GitSyncSettings settings)
        {
            var item = new Item { Name = "설정 파일" };
            string file = GitSyncSettings.FilePath;

            if (!File.Exists(file))
            {
                // 없어도 기본값으로 동작하므로 필수는 아님
                item.IsRequired = false;
                item.Detail = "ProjectSettings/GitSyncSettings.json 이 없어 기본값(origin / main)을 씁니다.";
                item.Fix = "설정 창에서 한 번 '저장'하면 파일이 만들어져 팀과 같이 쓸 수 있습니다.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            try
            {
                string json = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (!json.StartsWith("{", StringComparison.Ordinal) || JsonUtility.FromJson<ParseProbe>(json) == null)
                    throw new FormatException("JSON 객체가 아닙니다.");
            }
            catch (Exception e)
            {
                item.Detail = "설정 파일을 읽지 못했습니다: " + e.Message;
                item.Fix = "설정 창에서 값을 확인하고 다시 '저장'하세요 (저장소 폴더에 따옴표가 들어가 있으면 빼세요).";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            // 저장소 폴더가 이 PC의 절대 경로면 팀원 PC에서 저장소를 잘못 찾음 (설정 파일은 Git으로 공유됨)
            if (Path.IsPathRooted(settings.Repo))
            {
                item.IsRequired = false;
                item.Detail = "저장소 폴더에 이 PC의 경로가 들어 있습니다: " + settings.Repo + " (설정 파일은 팀과 공유되므로 다른 PC에서는 맞지 않음)";
                item.Fix = "설정의 '저장소 폴더'를 비우거나(프로젝트가 들어 있는 저장소를 자동으로 찾음) 프로젝트 기준 상대 경로로 바꾸세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            item.IsOk = true;
            item.Detail = "ProjectSettings/GitSyncSettings.json 읽음";
            return item;
        }

        // 푸시에만 필요: 원격이 있는지, 지금 브랜치가 설정의 푸시 브랜치인지 (다르면 다른 브랜치 작업이 푸시 브랜치로 올라감)
        private static Item CheckRemoteAndBranch(GitSyncSettings settings, bool hasGit, string repo)
        {
            var item = new Item { Name = "원격·브랜치", IsPushOnly = true };
            if (!hasGit || string.IsNullOrEmpty(repo))
            {
                item.Detail = "저장소를 확인하지 못해 원격·브랜치는 검사하지 않았습니다.";
                item.Fix = "먼저 git·Git 저장소 항목을 해결하세요.";
                return item;
            }

            string output, error;
            GitSyncTools.RunProcess(GitSyncTools.GitExe, "remote", repo, out output, out error);
            var remotes = new HashSet<string>(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            if (!remotes.Contains(settings.Remote))
            {
                item.Detail = $"원격 '{settings.Remote}' 이(가) 저장소에 없습니다" + (remotes.Count > 0 ? " (있는 원격: " + string.Join(", ", remotes) + ")" : " (원격 없음)") + ". 로컬 커밋은 할 수 있습니다.";
                item.Fix = "git remote add 로 원격을 추가하거나, 설정의 '원격 이름'을 바로잡으세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            int code = GitSyncTools.RunProcess(GitSyncTools.GitExe, "symbolic-ref --short -q HEAD", repo, out output, out error);
            string current = code == 0 ? output.Trim() : string.Empty;
            if (string.IsNullOrEmpty(current))
            {
                item.Detail = $"지금 브랜치가 아닌 커밋(detached HEAD)에 있습니다. 설정의 푸시 브랜치는 '{settings.Branch}'입니다.";
                item.Fix = $"git checkout {settings.Branch} 로 브랜치로 돌아오세요.";
                return item;
            }
            if (current != settings.Branch)
            {
                item.Detail = $"지금 브랜치는 '{current}'이고 설정의 푸시 브랜치는 '{settings.Branch}'입니다. 로컬 커밋은 할 수 있습니다.";
                item.Fix = $"git checkout {settings.Branch} 로 옮기거나, 이 브랜치를 올리려면 설정의 '브랜치'를 {current}로 바꾸세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            int remoteCode = GitSyncTools.RunProcess(GitSyncTools.GitExe, "rev-parse --verify -q " + GitSyncTools.Quote("refs/remotes/" + settings.Remote + "/" + settings.Branch), repo, out output, out error);
            item.IsOk = true;
            item.Detail = $"원격 {settings.Remote}, 브랜치 {settings.Branch}"
                          + (remoteCode == 0 ? string.Empty : " · 원격에 아직 이 브랜치가 없어 첫 푸시는 기록을 묶지 않고 그대로 올립니다");
            return item;
        }

        // 선택: 자동 요약을 켰을 때만 필요. 없으면 파일 이름 요약으로 대신하므로 커밋을 막지 않음
        private static Item CheckClaude(GitSyncSettings settings)
        {
            var item = new Item { Name = "claude", IsRequired = false };
            bool isAutoSummaryOn = settings.AutoSummary == GitSyncSettings.AUTO_SUMMARY_CLAUDE;
            string claude = string.IsNullOrEmpty(GitSyncPrefs.ClaudePath) ? "claude" : GitSyncPrefs.ClaudePath;
            if (!isAutoSummaryOn && string.IsNullOrEmpty(GitSyncPrefs.ClaudePath))
            {
                item.IsOk = true;
                item.Detail = "선택 · 작업 내용 자동 요약을 켤 때만 필요 (지금 꺼짐)";
                return item;
            }

            string output, error;
            int code = GitSyncTools.RunProcess(claude, "--version", GitSyncSettings.ProjectRoot, out output, out error);
            if (code == 0)
            {
                item.IsOk = true;
                item.Detail = FirstLine(output) + " (" + claude + ")" + (isAutoSummaryOn ? " · 자동 요약 사용" : string.Empty);
                return item;
            }

            item.Detail = (code == -1 ? $"'{claude}' 을(를) 찾지 못했습니다." : "claude 실행이 실패했습니다: " + FirstLine(error + output))
                          + (isAutoSummaryOn ? " 자동 요약 대신 파일 이름 요약을 씁니다." : string.Empty);
            item.Fix = "Claude Code를 설치하고, Unity가 PATH를 못 보면 설정의 claude 경로에 claude 실행 파일 전체 경로를 적으세요. 자동 요약을 끄면 필요 없습니다.";
            item.Action = FixAction.OpenSettings;
            return item;
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        // 검사 하나가 예외를 내도 나머지 항목은 보여 주도록 감쌈
        private static Item Guard(string name, Func<Item> check, bool isPushOnly = false, bool isProgram = false, bool isRequired = true)
        {
            try
            {
                Item item = check();
                item.IsProgram = isProgram;
                return item;
            }
            catch (Exception e)
            {
                return new Item { Name = name, IsRequired = isRequired, IsPushOnly = isPushOnly, IsProgram = isProgram, Detail = "검사 중 오류: " + e.Message, Fix = "다시 확인을 눌러도 같으면 Unity 콘솔의 오류와 함께 알려 주세요." };
            }
        }

        // winget이 있으면 확인 후 새 창에서 설치, 없으면(또는 macOS) 다운로드 페이지
        private static void Install(string name, string wingetId, string downloadUrl)
        {
            if (!GitSyncTools.IsWingetAvailable())
            {
                Application.OpenURL(downloadUrl);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex($"{name} 설치",
                $"winget(Windows 패키지 관리자)으로 {name}을(를) 설치합니다.\n새 창에서 진행 상황과 동의 질문이 나오면 직접 답해 주세요.\n설치가 끝나면 Git Sync 패널에서 '다시 확인'을 누르세요.",
                "설치", "취소", "다운로드 페이지 열기");
            if (choice == 0 && !GitSyncTools.StartWingetInstall(wingetId))
                Application.OpenURL(downloadUrl);
            else if (choice == 2)
                Application.OpenURL(downloadUrl);
        }

        [Serializable]
        private class ParseProbe
        {
            public string remote;
        }

        private static List<string> FindMissing(string dir)
        {
            var missing = new List<string>();
            foreach (string name in REQUIRED_TOOLS)
            {
                if (!File.Exists(Path.Combine(dir, name)))
                    missing.Add(name);
            }
            return missing;
        }

        private static bool IsInsideWorkTree(string dir)
        {
            string output, error;
            return GitSyncTools.RunProcess(GitSyncTools.GitExe, "rev-parse --is-inside-work-tree", dir, out output, out error) == 0
                   && output.Trim() == "true";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            string trimmed = text.Trim();
            int index = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return index < 0 ? trimmed : trimmed.Substring(0, index);
        }

        #endregion
    }
}
