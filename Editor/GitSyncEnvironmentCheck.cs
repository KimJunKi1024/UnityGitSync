using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 사용 전 환경 확인: 이 PC에 도구가 의존하는 프로그램(git·Python)과 설정이 갖춰졌는지 실제 실행으로 검사합니다.<br/>
    /// 검사 로직은 여기에, 화면은 <see cref="GitSyncEnvironmentWindow"/>에 있습니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-12</remarks>
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

            /// <summary>필수 항목인지. 필수 항목이 하나라도 실패하면 커밋·푸시를 막습니다.</summary>
            public bool IsRequired = true;

            /// <summary>한 줄 설명 (버전 또는 실패 이유).</summary>
            public string Detail;

            /// <summary>실패했을 때 해결 방법 한 줄.</summary>
            public string Fix;

            /// <summary>실패했을 때 보여 줄 버튼 (없으면 null).</summary>
            public FixAction Action;
        }

        /// <summary>실패 항목에 붙는 해결 버튼.</summary>
        public enum FixAction
        {
            None,
            OpenSettings,
            OpenGitDownload,
            OpenPythonDownload,
            RestartUnity,
        }

        /// <summary>
        /// 전체 검사 결과.
        /// </summary>
        public class Report
        {
            /// <summary>항목 목록 (표시 순서).</summary>
            public readonly List<Item> Items = new List<Item>();

            /// <summary>필수 항목이 모두 통과했는지.</summary>
            public bool IsReady
            {
                get
                {
                    foreach (Item item in Items)
                    {
                        if (item.IsRequired && !item.IsOk)
                            return false;
                    }
                    return true;
                }
            }

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
        }

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>도구가 쓰는 최소 Python 버전.</summary>
        public const int PYTHON_MIN_MAJOR = 3;

        /// <summary>도구가 쓰는 최소 Python 버전.</summary>
        public const int PYTHON_MIN_MINOR = 8;

        /// <summary>Git 다운로드 페이지.</summary>
        public const string GIT_DOWNLOAD_URL = "https://git-scm.com/downloads";

        /// <summary>Python 다운로드 페이지.</summary>
        public const string PYTHON_DOWNLOAD_URL = "https://www.python.org/downloads/";

        /// <summary><c>Library/GitSync/Tools/</c>에 있어야 하는 도구 파일.</summary>
        public static readonly string[] REQUIRED_TOOLS = { GitSyncTools.COMMIT_SCRIPT, GitSyncTools.PUSH_SCRIPT, "gitsync_common.py" };

        // 도구 스크립트가 import 하는 모듈 (모두 표준 라이브러리). Python에서 실제로 import 해 보고 추가 패키지 필요 여부를 판단
        private static readonly string[] PYTHON_MODULES = { "argparse", "datetime", "json", "os", "re", "shutil", "subprocess", "sys", "tempfile" };

        private const int PROCESS_TIMEOUT_MS = 10000;

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 모든 항목을 검사합니다. 몇 초 걸릴 수 있으니 메인 스레드에서 호출하되 자주 부르지 마세요.
        /// </summary>
        public static Report Run()
        {
            var report = new Report();
            GitSyncSettings settings = GitSyncSettings.Load();

            Item gitItem = Guard("git", CheckGit);
            report.Items.Add(gitItem);
            report.Items.Add(Guard("Python", CheckPython));
            report.Items.Add(Guard("도구 스크립트", CheckTools));

            string repo = null;
            report.Items.Add(Guard("Git 저장소", () => CheckRepository(settings, gitItem.IsOk, out repo)));
            report.Items.Add(Guard("설정 파일", () => CheckSettingsFile(settings, gitItem.IsOk, repo)));

            if (!string.IsNullOrEmpty(GitSyncPrefs.ClaudePath))
                report.Items.Add(Guard("claude (선택)", CheckClaude));

            return report;
        }

        /// <summary>
        /// 필수 항목이 모두 통과했는지만 빠르게 확인합니다. 아니면 환경 확인 창을 띄우고 false를 돌려줍니다.
        /// </summary>
        public static bool EnsureReadyOrShowWindow()
        {
            Report report = Run();
            if (report.IsReady)
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
                case FixAction.OpenGitDownload: Application.OpenURL(GIT_DOWNLOAD_URL); break;
                case FixAction.OpenPythonDownload: Application.OpenURL(PYTHON_DOWNLOAD_URL); break;
                case FixAction.RestartUnity:
                    if (EditorUtility.DisplayDialog("Unity 재시작", "도구를 다시 복사하려면 Unity를 다시 켜야 합니다. 지금 다시 복사를 시도해 볼까요?", "다시 복사", "취소"))
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
                case FixAction.OpenGitDownload: return "Git 다운로드";
                case FixAction.OpenPythonDownload: return "Python 다운로드";
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
            int code = RunProcess("git", "--version", GitSyncSettings.ProjectRoot, out output, out error);

            if (code == 0 && output.StartsWith("git version", StringComparison.OrdinalIgnoreCase))
            {
                item.IsOk = true;
                item.Detail = output.Trim();
            }
            else
            {
                item.Detail = code == -1 ? "PATH에서 git을 찾지 못했습니다 (Unity를 켠 뒤 설치했다면 Unity를 다시 켜세요)." : "git 실행에 실패했습니다: " + FirstLine(error + output);
                item.Fix = "Git for Windows를 설치하고 Unity를 다시 켜세요 (설치할 때 PATH 추가 옵션을 켜 두세요).";
                item.Action = FixAction.OpenGitDownload;
            }
            return item;
        }

        private static Item CheckPython()
        {
            string python = GitSyncPrefs.PythonPath;
            var item = new Item { Name = "Python" };

            // 버전과 표준 라이브러리 import를 한 번에 확인 (-I: 현재 폴더·환경 변수 영향을 받지 않게)
            string script = "import sys;" + string.Join(";", Array.ConvertAll(PYTHON_MODULES, m => "import " + m))
                            + ";print(sys.version_info[0],sys.version_info[1],sys.version_info[2])";
            string output, error;
            int code = RunProcess(python, "-I -c " + GitSyncTools.Quote(script), GitSyncSettings.ProjectRoot, out output, out error);

            if (code == -1)
            {
                item.Detail = $"'{python}' 을(를) 실행하지 못했습니다 (파일이 없거나 실행할 수 없음).";
                item.Fix = "Python 3을 설치하거나, 설정의 Python 경로에 python.exe 전체 경로를 적으세요.";
                item.Action = python == "python" ? FixAction.OpenPythonDownload : FixAction.OpenSettings;
                return item;
            }

            // Windows 스토어 자리표시자 python.exe는 실행되지만 설치 안내만 출력하고 비정상 종료
            if (code != 0)
            {
                string reason = FirstLine(error + output);
                item.Detail = string.IsNullOrEmpty(reason) ? $"'{python}' 실행이 실패했습니다 (종료 코드 {code})." : reason;
                item.Fix = reason.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "표준 라이브러리가 빠진 Python입니다. python.org에서 다시 설치하세요."
                    : "Python 3을 설치하거나, 설정의 Python 경로를 실제 python.exe로 바꾸세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            string[] parts = output.Trim().Split(' ');
            int major, minor;
            if (parts.Length < 2 || !int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor))
            {
                item.Detail = "Python 버전을 읽지 못했습니다: " + FirstLine(output);
                item.Fix = "설정의 Python 경로가 python.exe를 가리키는지 확인하세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            string version = string.Join(".", parts);
            if (major < PYTHON_MIN_MAJOR || (major == PYTHON_MIN_MAJOR && minor < PYTHON_MIN_MINOR))
            {
                item.Detail = $"Python {version} — {PYTHON_MIN_MAJOR}.{PYTHON_MIN_MINOR} 이상이 필요합니다.";
                item.Fix = "새 Python을 설치하고 설정의 Python 경로를 바꾸세요.";
                item.Action = FixAction.OpenPythonDownload;
                return item;
            }

            item.IsOk = true;
            item.Detail = $"Python {version} ({python}) · 표준 라이브러리만 사용, 추가 패키지 불필요";
            return item;
        }

        private static Item CheckTools()
        {
            var item = new Item { Name = "도구 스크립트" };
            var missing = new List<string>();
            foreach (string name in REQUIRED_TOOLS)
            {
                if (!File.Exists(Path.Combine(GitSyncTools.LibraryToolsDir, name)))
                    missing.Add(name);
            }

            if (missing.Count == 0)
            {
                item.IsOk = true;
                item.Detail = $"Library/GitSync/Tools/ 에 {REQUIRED_TOOLS.Length}개 모두 있음";
            }
            else
            {
                item.Detail = "Library/GitSync/Tools/ 에 없음: " + string.Join(", ", missing);
                item.Fix = string.IsNullOrEmpty(GitSyncTools.GetToolsSourceDir())
                    ? "패키지의 Tools~ 폴더를 찾지 못했습니다. Package Manager에서 패키지를 다시 설치하세요."
                    : "Unity를 다시 켜면 자동으로 복사됩니다. (또는 '다시 복사' 버튼)";
                item.Action = FixAction.RestartUnity;
            }
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
            int code = RunProcess("git", "rev-parse --show-toplevel", project, out output, out error);
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

        private static Item CheckSettingsFile(GitSyncSettings settings, bool hasGit, string repo)
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
                item.Detail = "설정 파일을 읽지 못했습니다 (기본값 origin / main으로 동작 중): " + e.Message;
                item.Fix = "설정 창에서 값을 확인하고 다시 '저장'하세요 (저장소 폴더에 따옴표가 들어가 있으면 빼세요).";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            if (!hasGit || string.IsNullOrEmpty(repo))
            {
                item.IsRequired = false;
                item.Detail = $"파일 읽음 ({settings.Remote} / {settings.Branch}) · 저장소를 확인하지 못해 원격·브랜치는 검사하지 않음";
                item.IsOk = true;
                return item;
            }

            string output, error;
            RunProcess("git", "remote", repo, out output, out error);
            var remotes = new HashSet<string>(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            if (!remotes.Contains(settings.Remote))
            {
                item.Detail = $"원격 '{settings.Remote}' 이(가) 저장소에 없습니다" + (remotes.Count > 0 ? " (있는 원격: " + string.Join(", ", remotes) + ")" : " (원격 없음)");
                item.Fix = "git remote add 로 원격을 추가하거나, 설정의 '원격 이름'을 바로잡으세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            int branchCode = RunProcess("git", "rev-parse --verify -q " + GitSyncTools.Quote("refs/heads/" + settings.Branch), repo, out output, out error);
            if (branchCode != 0)
            {
                // 로컬에 없어도 원격에 있으면 받아서 만들면 되니 경고로만
                int remoteCode = RunProcess("git", "rev-parse --verify -q " + GitSyncTools.Quote("refs/remotes/" + settings.Remote + "/" + settings.Branch), repo, out output, out error);
                item.Detail = remoteCode == 0
                    ? $"브랜치 '{settings.Branch}' 이(가) 로컬에 없습니다 (원격 {settings.Remote}에는 있음)."
                    : $"브랜치 '{settings.Branch}' 이(가) 저장소에 없습니다.";
                item.Fix = remoteCode == 0
                    ? $"git checkout {settings.Branch} 로 받아오세요."
                    : "브랜치를 만들거나, 설정의 '브랜치'를 바로잡으세요.";
                item.Action = FixAction.OpenSettings;
                return item;
            }

            item.IsOk = true;
            item.Detail = $"ProjectSettings/GitSyncSettings.json · 원격 {settings.Remote}, 브랜치 {settings.Branch} 확인";
            return item;
        }

        private static Item CheckClaude()
        {
            string claude = GitSyncPrefs.ClaudePath;
            var item = new Item { Name = "claude (선택)", IsRequired = false };
            string output, error;
            int code = RunProcess(claude, "--version", GitSyncSettings.ProjectRoot, out output, out error);

            if (code == 0)
            {
                item.IsOk = true;
                item.Detail = FirstLine(output) + " (" + claude + ")";
            }
            else
            {
                item.Detail = code == -1 ? $"'{claude}' 을(를) 실행하지 못했습니다." : "claude 실행이 실패했습니다: " + FirstLine(error + output);
                item.Fix = "자동 요약에만 씁니다. 설정의 claude 경로를 claude.exe 전체 경로로 바꾸거나 비우세요.";
                item.Action = FixAction.OpenSettings;
            }
            return item;
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        // 검사 하나가 예외를 내도 나머지 항목은 보여 주도록 감쌈
        private static Item Guard(string name, Func<Item> check)
        {
            try
            {
                return check();
            }
            catch (Exception e)
            {
                return new Item { Name = name, Detail = "검사 중 오류: " + e.Message, Fix = "다시 확인을 눌러도 같으면 Unity 콘솔의 오류와 함께 알려 주세요." };
            }
        }

        [Serializable]
        private class ParseProbe
        {
            public string remote;
        }

        private static bool IsInsideWorkTree(string dir)
        {
            string output, error;
            return RunProcess("git", "rev-parse --is-inside-work-tree", dir, out output, out error) == 0
                   && output.Trim() == "true";
        }

        // 프로세스를 실행하고 종료 코드를 돌려줍니다. 실행 자체가 안 되면 -1, 시간 초과면 -2.
        private static int RunProcess(string file, string arguments, string workingDir, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;
            var info = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            try
            {
                using (Process process = Process.Start(info))
                {
                    var outTask = process.StandardOutput.ReadToEndAsync();
                    var errTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(PROCESS_TIMEOUT_MS))
                    {
                        try { process.Kill(); } catch { /* 이미 끝났을 수 있음 */ }
                        error = "시간 초과";
                        return -2;
                    }
                    output = outTask.Result;
                    error = errTask.Result;
                    return process.ExitCode;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return -1;
            }
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
