# -*- coding: utf-8 -*-
"""Git Sync 공용 모듈: 설정 읽기, 저장소 찾기, git 실행, 커밋 설명 형식.

커밋 설명 형식 (고정, 이 밖의 내용은 적지 않음):

    2026-11-11 PM 5:24

    작업 내용
    - 334. 튜토리얼 대사 고치기

    파일: 추가 1, 수정 2
    총 작업한 내용: 3개
"""
import datetime
import json
import os
import subprocess
import sys

SETTINGS_FILE = os.path.join("ProjectSettings", "GitSyncSettings.json")
DEFAULT_SETTINGS = {
    "repo": "",                  # 비우면 프로젝트 폴더가 들어 있는 Git 저장소
    "branch": "main",
    "remote": "origin",
    "pushIntervalMinutes": 60,
    "bundleCommit": True,
    "workLogEntriesDir": "",     # 비우면 작업 기록 연동 끔 (프로젝트 폴더 기준 상대 경로)
    "autoSummary": "off",        # off | claude
}
WORK_HEADER = "작업 내용"


def fail(message):
    """오류 메시지를 표준 오류로 보내고 종료 코드 1로 끝냅니다."""
    sys.stdout.flush()
    print(message, file=sys.stderr)
    sys.exit(1)


def setup_console():
    """Windows 콘솔·파이프에서도 한글이 깨지지 않게 표준 출력을 UTF-8로 맞춥니다."""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def load_settings(project):
    """ProjectSettings/GitSyncSettings.json을 읽어 기본값과 합칩니다. 파일이 없으면 기본값."""
    settings = dict(DEFAULT_SETTINGS)
    path = os.path.join(project, SETTINGS_FILE)
    if os.path.isfile(path):
        try:
            with open(path, encoding="utf-8-sig") as f:
                data = json.load(f)
        except (OSError, ValueError) as e:
            fail(f"설정 파일을 읽지 못했습니다: {path}\n{e}")
        for key in DEFAULT_SETTINGS:
            if key in data and data[key] is not None:
                settings[key] = data[key]
    # 경로 값은 앞뒤 공백·따옴표를 뺌 (탐색기에서 '경로로 복사'한 값은 따옴표가 붙어 옴). C# GitSyncSettings.CleanPath와 같은 규칙
    for key in ("repo", "workLogEntriesDir"):
        settings[key] = str(settings.get(key) or "").strip().strip("\"'").strip()
    return settings


def run_git(repo, *args, check=True, input_text=None):
    """git 명령을 실행하고 표준 출력을 돌려줍니다. check면 실패 시 종료."""
    r = subprocess.run(["git", "-c", "core.quotepath=false", "-C", repo, *args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace",
                       input=input_text)
    if check and r.returncode != 0:
        fail(f"git {' '.join(args)} 실패:\n{r.stderr.strip()}")
    return r.stdout


def resolve_paths(project_arg, settings, repo_arg=""):
    """(프로젝트 폴더, 저장소 폴더, 저장소 안에서 프로젝트 폴더의 상대 경로)를 돌려줍니다.

    저장소 순서: --repo → 설정 repo(프로젝트 기준 상대 경로 가능) → 프로젝트 폴더가 들어 있는 저장소.
    """
    project = os.path.abspath(project_arg or os.getcwd())
    repo = repo_arg or settings.get("repo") or ""
    if repo:
        repo = repo if os.path.isabs(repo) else os.path.join(project, repo)
    else:
        r = subprocess.run(["git", "-C", project, "rev-parse", "--show-toplevel"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            fail(f"Git 저장소를 찾지 못했습니다: {project}\n프로젝트 폴더를 저장소 안에 두거나 설정의 repo를 지정하세요.")
        repo = r.stdout.strip()
    repo = os.path.abspath(repo)
    if not os.path.isdir(repo):
        fail(f"저장소 폴더가 없습니다: {repo}")
    prefix = os.path.relpath(project, repo).replace("\\", "/")
    if prefix == ".":
        prefix = ""
    elif prefix.startswith(".."):
        prefix = ""  # 프로젝트가 저장소 밖에 있음 (경로 핑만 안 됨)
    return project, repo, prefix


def has_head(repo):
    """첫 커밋이 있는지."""
    r = subprocess.run(["git", "-C", repo, "rev-parse", "--verify", "-q", "HEAD"], capture_output=True)
    return r.returncode == 0


def summary_line(now=None):
    """커밋 요약 줄: "2026-11-11 PM 5:24"."""
    now = now or datetime.datetime.now()
    hour = now.hour % 12 or 12
    return f"{now:%Y-%m-%d} {'AM' if now.hour < 12 else 'PM'} {hour}:{now:%M}"


def count_kinds(name_status):
    """git --name-status 줄 목록 → {"A": n, "M": n, "D": n, "R": n}."""
    counts = {}
    for row in name_status:
        kind = row.split("\t")[0][:1]
        if kind in ("C", "T", "U", "X", "B", ""):
            kind = "M"
        counts[kind] = counts.get(kind, 0) + 1
    return counts


def file_count_lines(name_status):
    """"파일: 추가 1, 수정 2\\n총 작업한 내용: 3개" (0인 종류는 뺌)."""
    order = [("A", "추가"), ("M", "수정"), ("D", "삭제"), ("R", "이동")]
    counts = count_kinds(name_status)
    parts = [f"{name} {counts[k]}" for k, name in order if counts.get(k)]
    return "파일: " + ", ".join(parts) + f"\n총 작업한 내용: {sum(counts.values())}개"


def build_message(work_items, name_status, now=None):
    """작업 내용 목록과 파일 수로 커밋 설명 전체를 만듭니다."""
    lines = [summary_line(now), "", WORK_HEADER]
    lines += [f"- {item}" for item in work_items]
    lines += ["", file_count_lines(name_status)]
    return "\n".join(lines) + "\n"


def parse_work_items(message):
    """커밋 설명에서 "작업 내용" 아래 "- " 줄들을 꺼냅니다(빈 줄에서 끝)."""
    items, inside = [], False
    for line in message.splitlines():
        text = line.strip()
        if text == WORK_HEADER:
            inside = True
            continue
        if inside:
            if not text:
                break
            if text.startswith("- "):
                items.append(text[2:].strip())
    return items


RULE_CATEGORIES = [
    ("스크립트", (".cs",)),
    ("씬", (".unity",)),
    ("프리팹", (".prefab",)),
    ("이미지", (".png", ".jpg", ".jpeg", ".psd", ".tga", ".gif", ".bmp", ".tif", ".tiff", ".svg", ".webp", ".exr", ".hdr")),
]
KIND_VERBS = {"A": "추가", "M": "수정", "D": "삭제", "R": "이동"}
MAX_NAMES = 3


def rule_summary(name_status):
    """파일 종류·이름으로 만든 한 줄 요약. 예: "스크립트 2개 수정: A, B / 씬 Village 수정"."""
    groups = {}   # 분류 이름 → [(종류, 표시 이름)]
    metas = []
    for row in name_status:
        cols = row.split("\t")
        kind = cols[0][:1]
        kind = kind if kind in KIND_VERBS else "M"
        path = cols[-1]
        base = os.path.basename(path)
        if base.endswith(".meta"):
            metas.append(kind)
            continue
        ext = os.path.splitext(base)[1].lower()
        category = next((name for name, exts in RULE_CATEGORIES if ext in exts), "기타")
        shown = base if category == "기타" else os.path.splitext(base)[0]
        groups.setdefault(category, []).append((kind, shown))

    def verb(kinds):
        unique = set(kinds)
        return KIND_VERBS[unique.pop()] if len(unique) == 1 else "변경"

    parts = []
    for category in [c for c, _ in RULE_CATEGORIES] + ["기타"]:
        items = groups.get(category)
        if not items:
            continue
        v = verb(k for k, _ in items)
        names = sorted(dict.fromkeys(n for _, n in items))
        if len(items) == 1:
            parts.append(f"{category} {names[0]} {v}")
        else:
            text = ", ".join(names[:MAX_NAMES]) + (f" 외 {len(names) - MAX_NAMES}개" if len(names) > MAX_NAMES else "")
            parts.append(f"{category} {len(items)}개 {v}: {text}")
    if not parts and metas:
        parts.append(f"메타 파일 {len(metas)}개 {verb(metas)}")
    return " / ".join(parts) if parts else "바뀐 파일 없음"
