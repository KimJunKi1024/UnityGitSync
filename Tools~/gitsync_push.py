# -*- coding: utf-8 -*-
"""Git Sync 푸시 도구 (시간 규칙 + 묶음 커밋).

푸시 규칙 (Documentation~/Git 운영 가이드.md)
- 작업 중에는 설정한 시간(pushIntervalMinutes, 기본 60분)마다 1번 푸시. 시간이 됐을 때 작업 중이면 그 작업을 끝낸 뒤 푸시.
- 시간은 푸시 직후가 아니라, 푸시 뒤 '다음 작업을 시작하는 순간'(--start)부터 셈.
- bundleCommit이 켜져 있으면(기본) 마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림.
  원격 기록에는 푸시마다 묶음 1개만 남고, 개별 커밋은 로컬에도 남기지 않음(푸시가 성공한 뒤에 로컬 브랜치를 옮김).
  커밋하지 않은 작업 폴더 변경은 그대로 둠. 이미 원격에 올라간 지난 커밋은 건드리지 않음(강제 푸시 없음).

사용법 (Unity 프로젝트 폴더에서, 또는 --project로 지정):
    python gitsync_push.py --start        작업을 시작할 때: 카운트가 멈춰 있으면 지금부터 셈
    python gitsync_push.py --status       카운트 시작 시각·지난 시간·푸시할 차례인지
    python gitsync_push.py --dry-run      묶음 커밋 설명 미리보기 (아무것도 바꾸지 않음)
    python gitsync_push.py                (묶음 커밋 →) git push → 카운트 멈춤
    python gitsync_push.py --status-json  상태를 JSON으로 (Unity 커밋 창이 사용)

상태 파일: <프로젝트>/Library/GitSync/push_state.json (PC마다 따로, Library라 Git에 안 올라감)
멈추는 경우: 원격에 이 PC에 없는 커밋이 있음, 이미 스테이징된 변경이 있음, 올릴 커밋이 없음. 강제 푸시는 하지 않음.
"""
import argparse
import datetime
import json
import os
import subprocess
import tempfile

import gitsync_common as common


def state_path(project):
    return os.path.join(project, "Library", "GitSync", "push_state.json")


def load_state(project):
    path = state_path(project)
    if os.path.isfile(path):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            return {"last_push": data.get("last_push"), "timer_start": data.get("timer_start")}
        except (OSError, ValueError):
            pass
    return {"last_push": None, "timer_start": None}


def save_state(project, state):
    path = state_path(project)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)


def now():
    return datetime.datetime.now().replace(microsecond=0)


def elapsed_minutes(state):
    if not state["timer_start"]:
        return None
    return int((now() - datetime.datetime.fromisoformat(state["timer_start"])).total_seconds() // 60)


def remote_ref(settings):
    return f"refs/remotes/{settings['remote']}/{settings['branch']}"


def ref_exists(repo, ref):
    return subprocess.run(["git", "-C", repo, "rev-parse", "--verify", "-q", ref], capture_output=True).returncode == 0


def empty_tree(repo):
    return common.run_git(repo, "hash-object", "-t", "tree", "--stdin", input_text="").strip()


def pending_commits(repo, settings):
    """(원격에 없는 커밋 목록(오래된 순), 비교 기준) — 원격 브랜치가 아직 없으면 기준은 빈 트리."""
    if not common.has_head(repo):
        return [], None
    ref = remote_ref(settings)
    if ref_exists(repo, ref):
        return common.run_git(repo, "rev-list", "--reverse", f"{ref}..HEAD").split(), ref
    return common.run_git(repo, "rev-list", "--reverse", "HEAD").split(), empty_tree(repo)


def bundle_message(repo, settings):
    """원격 확인 후 (묶음 커밋 설명, 올릴 커밋 수, 원격 브랜치가 있는지)."""
    remote, branch = settings["remote"], settings["branch"]
    if subprocess.run(["git", "-C", repo, "fetch", "-q", remote], capture_output=True).returncode != 0:
        common.fail(f"원격({remote})을 확인하지 못했습니다(네트워크·권한). 푸시하지 않습니다.")
    ref = remote_ref(settings)
    has_remote = ref_exists(repo, ref)
    if has_remote:
        behind = common.run_git(repo, "rev-list", "--count", f"HEAD..{ref}").strip() if common.has_head(repo) else "1"
        if behind != "0":
            common.fail(f"원격({remote}/{branch})에 이 PC에 없는 커밋이 {behind}개 있습니다. "
                        "먼저 받아서(Pull) 합친 뒤 푸시하세요. (푸시하지 않음)")
    commits, base = pending_commits(repo, settings)
    if not commits:
        common.fail("올릴 커밋이 없습니다.")

    items = []
    for c in commits:
        for item in common.parse_work_items(common.run_git(repo, "log", "-1", "--format=%B", c)):
            if item not in items:
                items.append(item)
    name_status = [r for r in common.run_git(repo, "diff", "--name-status", "-M", base, "HEAD").splitlines() if r.strip()]
    if not items:
        items = [common.rule_summary(name_status)]
    return common.build_message(items, name_status), len(commits), has_remote


def cmd_status(project, settings):
    s = load_state(project)
    interval = int(settings["pushIntervalMinutes"])
    print(f"마지막 푸시: {s['last_push'] or '-'}")
    minutes = elapsed_minutes(s)
    if minutes is None:
        print("카운트: 멈춤 (다음 작업을 시작할 때 --start)")
        return
    print(f"카운트 시작: {s['timer_start']} / 지난 시간: {minutes}분")
    print("푸시할 차례입니다 (지금 작업이 끝나면 푸시)" if minutes >= interval else f"푸시까지 {interval - minutes}분 남음")


def cmd_status_json(project, repo, settings):
    s = load_state(project)
    minutes = elapsed_minutes(s)
    interval = int(settings["pushIntervalMinutes"])
    commits, _ = pending_commits(repo, settings)
    data = {
        "lastPush": s["last_push"] or "",
        "timerStart": s["timer_start"] or "",
        "elapsedMinutes": -1 if minutes is None else minutes,
        "intervalMinutes": interval,
        "isDue": minutes is not None and minutes >= interval,
        "pendingCommits": len(commits),
        "remote": settings["remote"],
        "branch": settings["branch"],
        "bundleCommit": bool(settings["bundleCommit"]),
    }
    print(json.dumps(data, ensure_ascii=False))


def cmd_start(project, settings):
    s = load_state(project)
    if s["timer_start"]:
        print(f"이미 카운트 중: {s['timer_start']}")
    else:
        s["timer_start"] = now().isoformat()
        save_state(project, s)
        print(f"카운트 시작: {s['timer_start']}")
    cmd_status(project, settings)


def cmd_push(project, repo, settings, dry_run):
    message, count, has_remote = bundle_message(repo, settings)
    bundle = bool(settings["bundleCommit"])
    remote, branch = settings["remote"], settings["branch"]
    target = f"{remote}/{branch}"
    if bundle:
        print(f"개별 커밋 {count}개 → 묶음 커밋 1개로 합쳐 올림 → {target}\n--- 묶음 커밋 설명 ---\n{message}")
    else:
        print(f"올릴 커밋 {count}개 → {target} (묶음 커밋 끔, 개별 커밋 그대로)")
    if dry_run:
        print("(미리보기: 아무것도 바꾸지 않음)")
        return
    if common.run_git(repo, "diff", "--cached", "--name-only").strip():
        common.fail("이미 스테이징된 변경이 있습니다. 스테이징을 비운 뒤 다시 실행하세요. (푸시하지 않음)")

    upstream = [] if has_remote else ["-u"]
    if bundle:
        # 개별 커밋을 합친 묶음 커밋 1개: 내용은 지금 HEAD와 같고 부모는 원격 브랜치(첫 푸시면 부모 없음).
        # 먼저 묶음 커밋을 푸시하고, 성공한 뒤에만 로컬 브랜치를 옮김 → 푸시가 실패하면 로컬은 그대로.
        # 작업 폴더·스테이징은 건드리지 않음 (git reset --soft).
        with tempfile.NamedTemporaryFile("w", delete=False, encoding="utf-8", suffix=".txt") as f:
            f.write(message)
            path = f.name
        try:
            parent = ["-p", remote_ref(settings)] if has_remote else []
            squashed = common.run_git(repo, "commit-tree", "HEAD^{tree}", *parent, "-F", path).strip()
        finally:
            os.remove(path)
        common.run_git(repo, "push", *upstream, remote, f"{squashed}:refs/heads/{branch}")
        common.run_git(repo, "reset", "-q", "--soft", squashed)
        if not has_remote:
            common.run_git(repo, "branch", "-q", f"--set-upstream-to={remote}/{branch}", check=False)
    else:
        common.run_git(repo, "push", *upstream, remote, f"HEAD:{branch}")
    s = load_state(project)
    s["last_push"] = now().isoformat()
    s["timer_start"] = None
    save_state(project, s)
    print(f"푸시 완료: {common.run_git(repo, 'log', '-1', '--format=%h %s').strip()}\n카운트 멈춤 (다음 작업을 시작할 때 --start)")


def main():
    common.setup_console()
    p = argparse.ArgumentParser(description="Git Sync 푸시 도구 (시간 규칙, 묶음 커밋)")
    p.add_argument("--project", default="", help="Unity 프로젝트 폴더 (기본: 현재 폴더)")
    p.add_argument("--repo", default="", help="Git 저장소 폴더 (기본: 설정 repo → 프로젝트가 들어 있는 저장소)")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--start", action="store_true", help="작업 시작: 카운트가 멈춰 있으면 시작")
    g.add_argument("--status", action="store_true", help="카운트 상태 표시")
    g.add_argument("--status-json", action="store_true", help="상태를 JSON으로 출력")
    g.add_argument("--dry-run", action="store_true", help="묶음 커밋 설명 미리보기")
    a = p.parse_args()

    project_dir = os.path.abspath(a.project or os.getcwd())
    settings = common.load_settings(project_dir)
    if a.start:
        cmd_start(project_dir, settings)
        return
    if a.status:
        cmd_status(project_dir, settings)
        return
    project, repo, _ = common.resolve_paths(project_dir, settings, a.repo)
    if a.status_json:
        cmd_status_json(project, repo, settings)
    else:
        cmd_push(project, repo, settings, a.dry_run)


if __name__ == "__main__":
    main()
