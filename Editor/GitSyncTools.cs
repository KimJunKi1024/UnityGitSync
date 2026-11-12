using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 패키지의 파이썬 도구(<c>Tools~</c>)를 찾아 실행하고, 도구가 쓰는 프로그램(git·Python)의 위치를 찾습니다.<br/>
    /// 에디터가 켜질 때 도구를 <c>Library/GitSync/Tools/</c>에 복사해 두어, 명령줄(Claude Code 등)에서도 늘 같은 경로로 실행할 수 있게 합니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11<br/>수정일: 2026-11-12 (git·Python 표준 설치 위치 찾기, winget 설치, 패키지 정보)</remarks>
    [InitializeOnLoad]
    public static class GitSyncTools
    {
        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>명령줄용 도구 복사본 폴더 (<c>Library/GitSync/Tools</c>).</summary>
        public static string LibraryToolsDir => Path.Combine(GitSyncSettings.ProjectRoot, "Library", "GitSync", "Tools");

        /// <summary>실행할 git. PATH에 있으면 <c>git</c>, 없으면 표준 설치 위치의 전체 경로 (설치 직후 Unity를 다시 켜지 않아도 쓰도록).</summary>
        public static string GitExe => FindGit();

        /// <summary>설치된 패키지 폴더 (못 찾으면 빈 문자열).</summary>
        public static string PackageRoot
        {
            get
            {
                PackageInfo package = PackageInfo.FindForAssembly(typeof(GitSyncTools).Assembly);
                if (package != null)
                    return package.resolvedPath;

                string editorDir = Path.GetDirectoryName(GetThisFilePath());
                return string.IsNullOrEmpty(editorDir) ? string.Empty : Path.GetDirectoryName(editorDir);
            }
        }

        /// <summary>패키지 버전 (못 찾으면 빈 문자열).</summary>
        public static string PackageVersion
        {
            get
            {
                PackageInfo package = PackageInfo.FindForAssembly(typeof(GitSyncTools).Assembly);
                return package != null ? package.version : string.Empty;
            }
        }

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>커밋 도구 파일 이름.</summary>
        public const string COMMIT_SCRIPT = "gitsync_commit.py";

        /// <summary>푸시 도구 파일 이름.</summary>
        public const string PUSH_SCRIPT = "gitsync_push.py";

        /// <summary>도구가 쓰는 최소 Python 버전 (3.8 → 308).</summary>
        public const int PYTHON_MIN_VERSION = 308;

        /// <summary>짧은 확인용 프로세스의 제한 시간 (밀리초).</summary>
        public const int PROCESS_TIMEOUT_MS = 10000;

        /// <summary>winget으로 설치할 Git 패키지 ID.</summary>
        public const string WINGET_GIT_ID = "Git.Git";

        /// <summary>winget으로 설치할 Python 패키지 ID.</summary>
        public const string WINGET_PYTHON_ID = "Python.Python.3.12";

        private const string TOOLS_FOLDER = "Tools~";
        private const int PROBE_TIMEOUT_MS = 5000;
        private const int OUTPUT_DRAIN_TIMEOUT_MS = 2000;

        // Python 경로를 비워 두었을 때 차례로 실행해 보는 후보 (Windows 스토어 자리표시자 python.exe는 실행이 실패해 건너뜀)
        private static readonly string[] WINDOWS_PYTHON_NAMES = { "python", "py", "python3" };
        // macOS의 Unity는 셸 PATH를 물려받지 못하는 경우가 많아 Homebrew 경로도 직접 봄
        private static readonly string[] UNIX_PYTHON_CANDIDATES = { "python3", "python", "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" };
        private static readonly string[] UNIX_GIT_CANDIDATES = { "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git" };

        #endregion

        #region [ 내부 상태 (Internal State) ]

        // 찾은 결과는 못 찾은 경우까지 저장해 둠 (설정 화면처럼 자주 그리는 곳에서 매번 프로세스를 실행하지 않도록).
        // 도메인 리로드 때와 '다시 확인'(refresh) 때 다시 찾음
        private static string _detectedPython; // 자동으로 찾은 Python (못 찾았으면 null)
        private static string _detectedGit;    // 찾은 git (못 찾았으면 null)
        private static bool _isPythonSearched;
        private static bool _isGitSearched;
        private static int _wingetState;       // 0: 아직 모름, 1: 있음, -1: 없음

        #endregion

        #region [ 실행 결과 (Process Result) ]

        /// <summary>
        /// 파이썬 도구 실행 결과.
        /// </summary>
        public struct ProcessResult
        {
            /// <summary>종료 코드 (실행 자체가 실패하면 -1).</summary>
            public int ExitCode;

            /// <summary>표준 출력.</summary>
            public string Output;

            /// <summary>표준 오류.</summary>
            public string Error;
        }

        #endregion

        #region [ 초기화 (Initialization) ]

        static GitSyncTools()
        {
            EditorApplication.delayCall += CopyToolsToLibrary;
        }

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 도구 원본 폴더 (설치된 패키지 안의 <c>Tools~</c>). 못 찾으면 빈 문자열.
        /// </summary>
        public static string GetToolsSourceDir()
        {
            string root = PackageRoot;
            string dir = string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, TOOLS_FOLDER);
            return Directory.Exists(dir) ? dir : string.Empty;
        }

        /// <summary>
        /// 도구를 <c>Library/GitSync/Tools/</c>에 복사합니다. 내용이 같은 파일은 건너뜁니다.
        /// </summary>
        public static void CopyToolsToLibrary()
        {
            string source = GetToolsSourceDir();
            if (string.IsNullOrEmpty(source))
                return;

            try
            {
                Directory.CreateDirectory(LibraryToolsDir);
                foreach (string file in Directory.GetFiles(source, "*.py"))
                {
                    string target = Path.Combine(LibraryToolsDir, Path.GetFileName(file));
                    byte[] bytes = File.ReadAllBytes(file);
                    if (File.Exists(target) && AreSame(bytes, File.ReadAllBytes(target)))
                        continue;
                    File.WriteAllBytes(target, bytes);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Git Sync] 도구를 Library에 복사하지 못했습니다: " + e.Message);
            }
        }

        /// <summary>
        /// 파이썬 도구를 백그라운드에서 실행합니다. 프로젝트 폴더를 <c>--project</c>로 넘기고, 출력은 UTF-8로 받습니다.
        /// </summary>
        /// <param name="scriptName"><see cref="COMMIT_SCRIPT"/> 또는 <see cref="PUSH_SCRIPT"/></param>
        /// <param name="arguments">추가 인자 (이미 따옴표 처리된 문자열)</param>
        public static Task<ProcessResult> RunAsync(string scriptName, string arguments)
        {
            string projectRoot = GitSyncSettings.ProjectRoot;
            string toolsDir = GetToolsSourceDir();
            string python = GitSyncPrefs.PythonPath;
            string claude = GitSyncPrefs.ClaudePath;
            string childPath = GetChildPath();

            if (string.IsNullOrEmpty(toolsDir))
                return Task.FromResult(new ProcessResult { ExitCode = -1, Error = "패키지의 Tools~ 폴더를 찾지 못했습니다." });

            string script = Path.Combine(toolsDir, scriptName);
            string allArguments = Quote(script) + " --project " + Quote(projectRoot) + (string.IsNullOrEmpty(arguments) ? string.Empty : " " + arguments);

            return Task.Run(() =>
            {
                var info = new ProcessStartInfo(python, allArguments)
                {
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                SetChildEnvironment(info, childPath);
                info.EnvironmentVariables["PYTHONUTF8"] = "1";
                info.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1"; // 패키지 폴더에 __pycache__를 만들지 않음
                if (!string.IsNullOrEmpty(claude))
                    info.EnvironmentVariables["GITSYNC_CLAUDE"] = claude;

                try
                {
                    using (Process process = Process.Start(info))
                    {
                        Task<string> errorTask = process.StandardError.ReadToEndAsync();
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        return new ProcessResult { ExitCode = process.ExitCode, Output = output, Error = errorTask.Result };
                    }
                }
                catch (Exception e)
                {
                    return new ProcessResult { ExitCode = -1, Error = $"파이썬을 실행하지 못했습니다({python}): {e.Message}\nTools → Git → Git Sync 패널에서 Python을 확인하세요." };
                }
            });
        }

        /// <summary>
        /// git을 찾습니다: PATH의 <c>git</c> → 표준 설치 위치. 찾지 못하면 <c>git</c>을 돌려줍니다.
        /// </summary>
        /// <param name="refresh">true면 이전에 찾은 값을 버리고 다시 찾음 (git을 새로 설치한 뒤 '다시 확인')</param>
        public static string FindGit(bool refresh = false)
        {
            if (!refresh && _isGitSearched)
                return _detectedGit ?? "git";
            _isGitSearched = true;

            var candidates = new List<string> { "git" };
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                foreach (string root in GetWindowsProgramRoots())
                    candidates.Add(Path.Combine(root, "Git", "cmd", "git.exe"));
            }
            else
            {
                candidates.AddRange(UNIX_GIT_CANDIDATES);
            }

            foreach (string candidate in candidates)
            {
                if (candidate != "git" && !File.Exists(candidate))
                    continue;

                string output, error;
                if (RunProcess(candidate, "--version", GitSyncSettings.ProjectRoot, out output, out error, PROBE_TIMEOUT_MS) == 0
                    && output.StartsWith("git version", StringComparison.OrdinalIgnoreCase))
                {
                    _detectedGit = candidate;
                    return candidate;
                }
            }

            _detectedGit = null;
            return "git";
        }

        /// <summary>
        /// Python 경로를 비워 두었을 때 쓸 Python을 찾습니다: 후보를 차례로 실행해 3.8 이상이면 그것을 씁니다.
        /// 찾지 못하면 첫 후보 이름을 돌려줍니다 (실행할 때 '찾지 못함' 오류가 나도록).
        /// </summary>
        /// <param name="refresh">true면 이전에 찾은 값을 버리고 다시 찾음 (Python을 새로 설치한 뒤 '다시 확인')</param>
        public static string FindPython(bool refresh = false)
        {
            if (!refresh && _isPythonSearched)
                return _detectedPython ?? GetPythonCandidates()[0];
            _isPythonSearched = true;

            List<string> candidates = GetPythonCandidates();
            string script = Quote("import sys;print(sys.version_info[0]*100+sys.version_info[1])");
            foreach (string candidate in candidates)
            {
                string output, error;
                if (RunProcess(candidate, "-I -c " + script, GitSyncSettings.ProjectRoot, out output, out error, PROBE_TIMEOUT_MS) != 0)
                    continue;

                int version;
                if (int.TryParse(output.Trim(), out version) && version >= PYTHON_MIN_VERSION)
                {
                    _detectedPython = candidate;
                    return candidate;
                }
            }

            _detectedPython = null;
            return candidates[0];
        }

        /// <summary>
        /// 이 운영체제에서 자동 탐색할 Python 후보 목록 (이름 → 표준 설치 위치의 전체 경로).
        /// </summary>
        public static List<string> GetPythonCandidates()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return new List<string>(UNIX_PYTHON_CANDIDATES);

            var candidates = new List<string>(WINDOWS_PYTHON_NAMES);
            // python.org·winget 설치 위치 (새 버전 먼저). PATH에 없거나 Unity를 켠 뒤 설치한 경우를 위해
            string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            var roots = new List<string> { localPrograms };
            roots.AddRange(GetWindowsProgramRoots());
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                string[] dirs = Directory.GetDirectories(root, "Python3*");
                Array.Sort(dirs, (a, b) => string.CompareOrdinal(b, a));
                foreach (string dir in dirs)
                {
                    string exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe))
                        candidates.Add(exe);
                }
            }
            return candidates;
        }

        /// <summary>
        /// winget(Windows 패키지 관리자)을 쓸 수 있는지.
        /// </summary>
        public static bool IsWingetAvailable()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return false;
            if (_wingetState == 0)
            {
                string output, error;
                _wingetState = RunProcess("winget", "--version", GitSyncSettings.ProjectRoot, out output, out error, PROBE_TIMEOUT_MS) == 0 ? 1 : -1;
            }
            return _wingetState > 0;
        }

        /// <summary>
        /// winget 설치를 새 콘솔 창에서 시작합니다 (진행 상황과 동의 질문을 사용자가 직접 봄).
        /// </summary>
        /// <returns>창을 띄웠으면 true</returns>
        public static bool StartWingetInstall(string packageId)
        {
            try
            {
                var info = new ProcessStartInfo("winget", $"install --id {packageId} -e --source winget")
                {
                    UseShellExecute = true,
                };
                Process.Start(info);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Git Sync] winget 설치를 시작하지 못했습니다: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 프로세스를 실행하고 끝날 때까지 기다립니다 (확인용 짧은 명령).
        /// </summary>
        /// <returns>종료 코드. 실행 자체가 안 되면 -1, 시간 초과면 -2.</returns>
        public static int RunProcess(string file, string arguments, string workingDir, out string output, out string error, int timeoutMs = PROCESS_TIMEOUT_MS)
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
            SetChildEnvironment(info, GetChildPath(false));

            try
            {
                using (Process process = Process.Start(info))
                {
                    Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { /* 이미 끝났을 수 있음 */ }
                        error = "시간 초과";
                        return -2;
                    }
                    // 끝난 프로세스의 하위 프로세스가 출력 파이프를 쥐고 있으면 끝없이 기다리게 되므로 잠깐만 기다림
                    output = outTask.Wait(OUTPUT_DRAIN_TIMEOUT_MS) ? outTask.Result : string.Empty;
                    error = errTask.Wait(OUTPUT_DRAIN_TIMEOUT_MS) ? errTask.Result : string.Empty;
                    return process.ExitCode;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return -1;
            }
        }

        /// <summary>
        /// Windows 명령줄 규칙에 맞게 인자를 따옴표로 감쌉니다.
        /// </summary>
        public static string Quote(string value)
        {
            // 안쪽 따옴표는 \" , 끝의 \는 닫는 따옴표를 가리지 않게 두 번
            string escaped = (value ?? string.Empty).Replace("\"", "\\\"");
            if (escaped.EndsWith("\\", StringComparison.Ordinal))
                escaped += "\\";
            return "\"" + escaped + "\"";
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        private static string GetThisFilePath([CallerFilePath] string path = "")
        {
            return path;
        }

        private static IEnumerable<string> GetWindowsProgramRoots()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (!string.IsNullOrEmpty(root) && seen.Add(root))
                    yield return root;
            }
        }

        // 하위 프로세스(파이썬 도구가 부르는 git 포함)가 쓸 PATH: 찾은 git·Python 폴더를 앞에 붙임
        private static string GetChildPath(bool includeFound = true)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!includeFound)
                return path;

            var extra = new List<string>();
            foreach (string exe in new[] { FindGit(), GitSyncPrefs.PythonPath })
            {
                if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe))
                    continue;
                string dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                    extra.Add(dir);
            }
            return extra.Count == 0 ? path : string.Join(Path.PathSeparator.ToString(), extra) + Path.PathSeparator + path;
        }

        private static void SetChildEnvironment(ProcessStartInfo info, string childPath)
        {
            info.EnvironmentVariables["PATH"] = childPath;
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            // 창 없이 실행되므로 git이 터미널에서 로그인 정보를 묻고 멈춰 있지 않게 함 (자격 증명 관리자 창은 그대로 뜸)
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        }

        private static bool AreSame(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        #endregion
    }
}
