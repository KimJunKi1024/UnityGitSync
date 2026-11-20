# -*- coding: utf-8 -*-
"""Git Sync 도구 시험 (커밋 도구·푸시 도구).

임시 폴더에 원격 저장소(bare)와 예시 프로젝트 MyGame을 만들어 실제 도구를 실행합니다.
사용자의 Git 설정·로그인은 쓰지 않습니다 (GIT_CONFIG_GLOBAL을 임시 파일로, 시스템 설정 끔).
C#으로 옮길 때 같은 결과가 나오는지 비교하는 기준으로도 씁니다.

실행 (패키지 폴더에서):
    python "Tests~/test_gitsync.py"
"""
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(os.path.dirname(HERE), "Tools~")
COMMIT_TOOL = os.path.join(TOOLS, "gitsync_commit.py")
PUSH_TOOL = os.path.join(TOOLS, "gitsync_push.py")
sys.path.insert(0, TOOLS)

import gitsync_common as common  # noqa: E402
import gitsync_push as push      # noqa: E402

MESSAGE_RE = re.compile(r"^\d{4}-\d{2}-\d{2} (AM|PM) \d{1,2}:\d{2}\n\n작업 내용\n(- .+\n)+\n파일: .+\n총 작업한 내용: \d+개\n$")


def _remove_readonly(func, path, _):
    os.chmod(path, stat.S_IWRITE)
    func(path)


class GitSyncTestCase(unittest.TestCase):
    """원격 저장소 + 예시 프로젝트 MyGame (원격 origin, 브랜치 main)."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="gitsync_test_")
        gitconfig = os.path.join(self.root, "gitconfig")
        with open(gitconfig, "w", encoding="utf-8") as f:
            f.write("[user]\n\tname = Tester\n\temail = tester@example.com\n"
                    "[init]\n\tdefaultBranch = main\n[core]\n\tautocrlf = false\n")
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        self.env.update(GIT_CONFIG_GLOBAL=gitconfig, GIT_CONFIG_NOSYSTEM="1",
                        PYTHONIOENCODING="utf-8", PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        self._saved_env = dict(os.environ)
        os.environ.clear()
        os.environ.update(self.env)   # 같은 프로세스에서 부르는 함수 시험용

        self.remote = os.path.join(self.root, "remote.git")
        self.git(self.root, "init", "-q", "--bare", "-b", "main", self.remote)
        self.project = os.path.join(self.root, "MyGame")
        self.git(self.root, "clone", "-q", self.remote, self.project)

    def tearDown(self):
        os.environ.clear()
        os.environ.update(self._saved_env)
        shutil.rmtree(self.root, onerror=_remove_readonly)

    # ── 도우미 ──

    def git(self, cwd, *args, check=True):
        r = subprocess.run(["git", *args], cwd=cwd, env=self.env, capture_output=True, text=True, encoding="utf-8")
        if check and r.returncode != 0:
            self.fail(f"git {' '.join(args)} 실패: {r.stderr}")
        return r.stdout.strip()

    def remote_git(self, *args):
        return self.git(self.root, "--git-dir", self.remote, *args)

    def tool(self, script, *args, project=None):
        return subprocess.run([sys.executable, script, "--project", project or self.project, *args],
                              cwd=self.root, env=self.env, capture_output=True, text=True, encoding="utf-8")

    def commit(self, files, title, project=None):
        """files: {경로: 내용}. 커밋 도구로 그 파일만 커밋."""
        project = project or self.project
        for path, text in files.items():
            full = os.path.join(project, path)
            os.makedirs(os.path.dirname(full), exist_ok=True)
            with open(full, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        listing = os.path.join(self.root, "files.txt")
        with open(listing, "w", encoding="utf-8") as f:
            f.write("\n".join(files) + "\n")
        r = self.tool(COMMIT_TOOL, "--files-from", listing, "--title", title, "--no-auto", project=project)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        return r

    def head(self, project=None):
        return self.git(project or self.project, "rev-parse", "HEAD")

    def remote_head(self):
        return self.remote_git("rev-parse", "main")

    def first_push(self):
        self.commit({"Assets/Base.cs": "base\n"}, "기본")
        r = self.tool(PUSH_TOOL)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def backups(self):
        return self.git(self.project, "for-each-ref", "--format=%(refname)", push.BACKUP_REF_PREFIX).split()


class CommitToolTests(GitSyncTestCase):

    def test_commit_message_format(self):
        self.commit({"Assets/A.cs": "a\n"}, "튜토리얼 대사 고치기")
        message = self.git(self.project, "log", "-1", "--format=%B") + "\n"
        self.assertRegex(message, MESSAGE_RE)
        self.assertIn("- 튜토리얼 대사 고치기\n", message)
        self.assertIn("파일: 추가 1\n총 작업한 내용: 1개", message)

    def test_commit_only_listed_files(self):
        with open(os.path.join(self.project, "Other.txt"), "w", encoding="utf-8") as f:
            f.write("not selected\n")
        self.commit({"Assets/A.cs": "a\n"}, "고른 파일만")
        files = self.git(self.project, "show", "--name-only", "--format=", "HEAD").split()
        self.assertEqual(files, ["Assets/A.cs"])
        self.assertIn("Other.txt", self.git(self.project, "status", "--porcelain"))


class PushToolTests(GitSyncTestCase):

    def test_first_push_keeps_history(self):
        self.commit({"Assets/A.cs": "a\n"}, "첫째")
        self.commit({"Assets/B.cs": "b\n"}, "둘째")
        r = self.tool(PUSH_TOOL)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("첫 푸시는 묶지 않고", r.stdout)
        self.assertEqual(self.remote_git("rev-list", "--count", "main"), "2")
        self.assertEqual(self.head(), self.remote_head())
        self.assertEqual(self.backups(), [])

    def test_dry_run_changes_nothing(self):
        self.first_push()
        self.commit({"Assets/C.cs": "c\n"}, "미리보기 시험")
        before_local, before_remote = self.head(), self.remote_head()
        r = self.tool(PUSH_TOOL, "--dry-run")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("(미리보기: 아무것도 바꾸지 않음)", r.stdout)
        self.assertIn("- 미리보기 시험", r.stdout)
        self.assertEqual((self.head(), self.remote_head()), (before_local, before_remote))

    def test_bundle_push_and_backup(self):
        self.first_push()
        base = self.remote_head()
        self.commit({"Assets/C.cs": "c\n"}, "작업 하나")
        self.commit({"Assets/D.cs": "d\n"}, "작업 둘")
        individual_head = self.head()

        r = self.tool(PUSH_TOOL)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

        # 원격에는 묶음 커밋 1개만 늘어남, 부모는 지난 원격
        self.assertEqual(self.remote_git("rev-list", "--count", f"{base}..main"), "1")
        self.assertEqual(self.remote_git("rev-parse", "main^"), base)
        message = self.remote_git("log", "-1", "--format=%B", "main") + "\n"
        self.assertRegex(message, MESSAGE_RE)
        self.assertIn("- 작업 하나\n- 작업 둘\n", message)
        self.assertIn("파일: 추가 2\n총 작업한 내용: 2개", message)
        # 로컬 브랜치도 묶음 커밋으로 옮겨짐, 내용은 개별 커밋 끝과 같음
        self.assertEqual(self.head(), self.remote_head())
        self.assertEqual(self.git(self.project, "rev-parse", "HEAD^{tree}"),
                         self.git(self.project, "rev-parse", individual_head + "^{tree}"))

        # 묶기 전 개별 커밋은 백업으로 남음
        refs = self.backups()
        self.assertEqual(len(refs), 1)
        self.assertEqual(self.git(self.project, "rev-parse", refs[0]), individual_head)
        self.assertIn("묶기 전 개별 커밋은 백업", r.stdout)

        listing = self.tool(PUSH_TOOL, "--backups")
        self.assertEqual(listing.returncode, 0, listing.stderr)
        self.assertIn("개별 커밋 2개", listing.stdout)

        name = refs[0][len(push.BACKUP_REF_PREFIX):]
        restored = self.tool(PUSH_TOOL, "--restore-backup", name)
        self.assertEqual(restored.returncode, 0, restored.stderr)
        branch = push.BACKUP_BRANCH_PREFIX + name
        self.assertEqual(self.git(self.project, "rev-parse", branch), individual_head)
        self.assertEqual(self.git(self.project, "branch", "--show-current"), "main")   # 지금 브랜치는 그대로
        again = self.tool(PUSH_TOOL, "--restore-backup", name)
        self.assertNotEqual(again.returncode, 0)

    def test_remote_ahead_blocks(self):
        self.first_push()
        other = os.path.join(self.root, "OtherPC")
        self.git(self.root, "clone", "-q", self.remote, other)
        self.commit({"Assets/Other.cs": "o\n"}, "다른 PC 작업", project=other)
        self.assertEqual(self.tool(PUSH_TOOL, project=other).returncode, 0)

        self.commit({"Assets/Mine.cs": "m\n"}, "내 작업")
        before_local, before_remote = self.head(), self.remote_head()
        r = self.tool(PUSH_TOOL)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("이 PC에 없는 커밋이 1개", r.stderr)
        self.assertEqual((self.head(), self.remote_head()), (before_local, before_remote))
        self.assertEqual(self.backups(), [])

    def test_rejected_push_keeps_local(self):
        self.first_push()
        hook = os.path.join(self.remote, "hooks", "pre-receive")
        with open(hook, "w", encoding="utf-8", newline="\n") as f:
            f.write("#!/bin/sh\necho 'rejected by test hook' >&2\nexit 1\n")
        os.chmod(hook, 0o755)
        state_before = push.load_state(self.project)

        self.commit({"Assets/C.cs": "c\n"}, "거부될 작업")
        before_local, before_remote = self.head(), self.remote_head()
        r = self.tool(PUSH_TOOL)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("원격(origin/main)에 올리지 못했습니다", r.stderr)
        self.assertIn("pre-receive hook declined", r.stderr)   # git 오류 마지막 줄들이 함께 나옴
        self.assertIn("종료 코드", r.stderr)
        self.assertEqual((self.head(), self.remote_head()), (before_local, before_remote))
        self.assertEqual(self.backups(), [])
        self.assertEqual(push.load_state(self.project)["last_push"], state_before["last_push"])

    def test_push_counted_as_done_when_remote_has_it(self):
        """push 응답이 시간 초과·오류여도 원격에 올라갔으면 성공으로 처리하고 로컬을 옮김."""
        self.first_push()
        self.commit({"Assets/C.cs": "c\n"}, "응답 끊김")
        real = common.run_quiet_with_error

        def flaky(args, timeout, allow_login=False):
            code, error = real(args, timeout, allow_login)
            if "push" in args:
                return None, ""   # 실제로는 올라갔지만 응답을 못 받은 것처럼
            return code, error

        common.run_quiet_with_error = flaky
        try:
            settings = common.load_settings(self.project)
            project, repo, _ = common.resolve_paths(self.project, settings)
            push.cmd_push(project, repo, settings, False)
        finally:
            common.run_quiet_with_error = real
        self.assertEqual(self.head(), self.remote_head())
        self.assertEqual(len(self.backups()), 1)

    def test_branch_mismatch_blocks(self):
        self.first_push()
        self.git(self.project, "checkout", "-q", "-b", "feature")
        self.commit({"Assets/F.cs": "f\n"}, "다른 브랜치")
        r = self.tool(PUSH_TOOL)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("팀이 올리는 브랜치는 'main'", r.stderr)
        self.assertIn("Current branch", r.stderr)

    def test_staged_changes_block(self):
        self.first_push()
        self.commit({"Assets/C.cs": "c\n"}, "작업")
        with open(os.path.join(self.project, "Staged.txt"), "w", encoding="utf-8") as f:
            f.write("s\n")
        self.git(self.project, "add", "Staged.txt")
        r = self.tool(PUSH_TOOL)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("스테이징", r.stderr)

    def test_missing_remote_explained(self):
        self.first_push()
        self.git(self.project, "remote", "set-url", "origin", os.path.join(self.root, "nowhere.git"))
        self.commit({"Assets/C.cs": "c\n"}, "작업")
        r = self.tool(PUSH_TOOL, "--dry-run")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("원격(origin)을 확인하지 못했습니다", r.stderr)
        self.assertIn("저장소를 찾지 못했습니다", r.stderr)
        self.assertIn("종료 코드", r.stderr)

    def test_backup_keeps_latest(self):
        self.first_push()
        head = self.head()
        for i in range(push.BACKUP_KEEP + 3):
            self.git(self.project, "update-ref", f"{push.BACKUP_REF_PREFIX}20000101-0000{i:02d}", head)
        push.save_backup(self.project, head, 1)
        names = [n for n, _ in push.backup_refs(self.project)]
        self.assertEqual(len(names), push.BACKUP_KEEP)
        self.assertEqual(names, sorted(names, reverse=True))
        self.assertNotIn("20000101-000000", names)   # 가장 오래된 것부터 지움


class MessageTests(unittest.TestCase):

    def test_explain_remote_error(self):
        cases = {
            "error: cannot lock ref 'refs/remotes/origin/main'": "GitHub Desktop",
            "fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com": "인터넷",
            "fatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 403": "로그인",
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled": "로그인",
            "remote: Repository not found.": "저장소를 찾지 못했습니다",
            " ! [rejected]        main -> main (fetch first)": "Pull",
        }
        for error, expected in cases.items():
            self.assertIn(expected, common.explain_remote_error("origin", error), error)
        self.assertEqual(common.explain_remote_error("origin", "something else"), "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
