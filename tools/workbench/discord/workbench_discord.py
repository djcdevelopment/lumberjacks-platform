#!/usr/bin/env python3
"""Provision and maintain the #workbench Discord forum from this repository.

DESIGN PRINCIPLE -- STRUCTURE, NEVER CONVERSATION. This tool creates and maintains
channel structure (a forum channel, its tag taxonomy, its post guidelines) and the
opening posts that are checked into this repo as seed files. It has no code path that
writes a sentence a human did not write into the repo first: every byte it can post
comes from a file under Lumberjacks/docs/workbench/discord/, rendered verbatim. It
never replies, never reacts, never DMs, never mentions anyone (allowed_mentions is
always empty), and there is no free-text message argument anywhere in the CLI.
Community replies come from Derek, on Derek's batch rhythm. A community that feels
botted is the one failure this tool must never cause.

NO ALWAYS-ON PROCESS. This is a batch CLI. There is no gateway connection, no
event loop, no daemon, and nothing to keep running between sessions. State lives in
one JSON file in the repo.

NO HEARTH / MECHNET DEPENDENCY. Standard library only, no model calls, no local
fleet. See docs/baseline-vision-and-boundary.md -- operator infrastructure is an
accelerant, never a requirement for community-facing automation.

THE ANNOUNCEMENT IS NOT POSTABLE FROM HERE. 00-announcement.md is on a hardcoded
denylist that config cannot shrink, and no subcommand posts it. Announcing is a
deliberate Derek action (DEREK-BATCH-1.md item 10 / Batch 2). If that should ever
change, it is a code change plus a checklist item -- not a flag.

Subcommands:
  check      Repo-only invariants. No network, no token. Exit non-zero on drift.
  invite     Print the OAuth2 invite URL with the exact minimum permission set.
  plan       Compute what would change and write an approval receipt. Never writes
             to Discord. --offline predicts against an empty server with no token.
  apply      Converge Discord to the repo. Requires --yes. Refuses blocked posts.
  export     Pull forum threads into DiscordChatExporter-shaped JSON for
             tools/workbench/distill_feedback.py.
  self-test  Offline test suite against a simulated guild.

Credentials never live in this repo. See DEFAULT_TOKEN_FILE below and
Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

HERE = Path(__file__).resolve()
TOOL_DIR = HERE.parent
REPO_ROOT = HERE.parents[3]
DISCORD_DOCS = REPO_ROOT / "Lumberjacks" / "docs" / "workbench" / "discord"
WORKBENCH_JSON = REPO_ROOT / "Lumberjacks" / "docs" / "workbench" / "workbench.json"
DEFAULT_CONFIG = TOOL_DIR / "provision.json"
DEFAULT_STATE = TOOL_DIR / "provision-state.json"
DEFAULT_RECEIPT_DIR = TOOL_DIR / "receipts"

# Credentials live outside the repo. Env var wins; then a file named by
# WORKBENCH_DISCORD_TOKEN_FILE; then this default under the user profile.
ENV_TOKEN = "WORKBENCH_DISCORD_TOKEN"
ENV_TOKEN_FILE = "WORKBENCH_DISCORD_TOKEN_FILE"
# Searched in order. Both live under the user profile, never in the repo.
DEFAULT_TOKEN_FILES = (
    Path.home() / ".baseline" / "workbench-discord.token",
    Path.home() / ".baseline" / "discord.env",
)
# Key names accepted in a KEY=VALUE token file when it holds more than one entry.
TOKEN_KEY_NAMES = ("WORKBENCH_DISCORD_TOKEN", "DISCORD_BOT_TOKEN", "DISCORD_TOKEN", "BOT_TOKEN", "TOKEN", "KEY")

# Seed files this tool will never post, whatever a config says. Announcing is Derek's.
NEVER_POST = frozenset({"00-announcement.md"})

API_BASE = "https://discord.com/api/v10"
USER_AGENT = "DiscordBot (https://github.com/djcdevelopment/baseline, 1.0) baseline-workbench-provisioner"

CHANNEL_TYPE_FORUM = 15
CHANNEL_FLAG_PINNED = 1 << 1  # a forum post pinned to the top of the channel
CHANNEL_FLAG_REQUIRE_TAG = 1 << 4  # "Require people to select tags"
SORT_ORDER_LATEST_ACTIVITY = 0
SORT_ORDER_CREATION_DATE = 1
SORT_ORDERS = {"recent_activity": SORT_ORDER_LATEST_ACTIVITY, "creation_date": SORT_ORDER_CREATION_DATE}

MESSAGE_LIMIT = 2000  # Discord's per-message content ceiling
THREAD_NAME_LIMIT = 100
TAG_NAME_LIMIT = 20

# Least privilege. Deliberately excludes Administrator, Manage Roles, Manage Guild,
# Mention Everyone, Manage Messages (editing our own messages needs none of it), and
# anything moderation-shaped.
PERMISSIONS = {
    "MANAGE_CHANNELS": 1 << 4,  # create/edit the forum channel, its tags and flags
    "VIEW_CHANNEL": 1 << 10,
    "SEND_MESSAGES": 1 << 11,  # create forum posts
    "READ_MESSAGE_HISTORY": 1 << 16,  # drift detection + feedback export
    "MANAGE_THREADS": 1 << 34,  # pin the guideline post, apply moderated status tags
    "CREATE_PUBLIC_THREADS": 1 << 35,
    "SEND_MESSAGES_IN_THREADS": 1 << 38,  # continuation chunks of a long post
}
PERMISSIONS_INT = 0
for _bit in PERMISSIONS.values():
    PERMISSIONS_INT |= _bit

# Any <ALL-CAPS-TOKEN> left in a rendered post body is an unfilled placeholder.
PLACEHOLDER_RE = re.compile(r"<([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*)>")

# DiscordChatExporter MessageKind names, keyed by the REST API's integer type.
DCE_MESSAGE_KINDS = {
    0: "Default",
    1: "RecipientAdd",
    2: "RecipientRemove",
    3: "Call",
    4: "ChannelNameChange",
    5: "ChannelIconChange",
    6: "ChannelPinnedMessage",
    7: "GuildMemberJoin",
    18: "ThreadCreated",
    19: "Reply",
    20: "ChatInputCommand",
    21: "ThreadStarterMessage",
}


class ToolError(Exception):
    """Operator-facing failure: printed without a traceback."""


# --------------------------------------------------------------------------- #
# Repo sources -- the forum's shape is derived from checked-in files
# --------------------------------------------------------------------------- #


@dataclass(frozen=True)
class Tag:
    name: str
    moderated: bool  # True == only Manage Threads holders may apply it


@dataclass
class Seed:
    path: Path
    title: str
    body: str


def parse_seed(path: Path) -> Seed:
    """Split a seed file into its title and the post body below the `---` divider.

    Title comes from the Meta line's `title suggestion -- "X"` when present,
    otherwise from the body's leading **bold heading**. Both forms exist in
    Lumberjacks/docs/workbench/discord/ today (05 has no title suggestion)."""
    if not path.exists():
        raise ToolError(f"seed file not found: {_rel(path)}")
    text = path.read_text(encoding="utf-8")
    parts = re.split(r"^---\s*$", text, maxsplit=1, flags=re.MULTILINE)
    if len(parts) != 2:
        raise ToolError(f"{_rel(path)}: no `---` divider; cannot tell meta from post body")
    meta, body = parts[0], parts[1].strip()
    if not body:
        raise ToolError(f"{_rel(path)}: nothing below the divider")

    title = ""
    m = re.search(r"title suggestion\s*[—–-]\s*[\"“]([^\"”]+)[\"”]", meta)
    if m:
        title = m.group(1).strip()
    if not title:
        first = body.splitlines()[0].strip()
        m = re.match(r"^\*\*(.+?)\*\*$", first)
        if m:
            title = m.group(1).strip()
    if not title:
        raise ToolError(f"{_rel(path)}: no title suggestion in the meta line and no bold heading to fall back on")
    if len(title) > THREAD_NAME_LIMIT:
        raise ToolError(f"{_rel(path)}: title is {len(title)} chars, Discord's limit is {THREAD_NAME_LIMIT}")
    return Seed(path=path, title=title, body=body)


def parse_tags(doc_path: Path) -> list[Tag]:
    """Read the 8-tag taxonomy out of 07-forum-tags-setup.md.

    The doc is the source of truth (it is what Derek reads and edits), so the tags
    are parsed from it rather than duplicated into config. Shape relied on:
    a `## Tags` section, numbered ``1. `name` -- ...`` entries, and a line beginning
    `Status (` that separates member-facing tags from status tags. If any of that
    stops holding, this raises instead of silently provisioning a wrong taxonomy."""
    if not doc_path.exists():
        raise ToolError(f"tags doc not found: {_rel(doc_path)}")
    lines = doc_path.read_text(encoding="utf-8").splitlines()
    in_section = False
    moderated = False
    tags: list[Tag] = []
    for line in lines:
        if line.startswith("## "):
            in_section = line.startswith("## Tags")
            continue
        if not in_section:
            continue
        if re.match(r"^Status\s*\(", line.strip()):
            moderated = True
            continue
        m = re.match(r"^\d+\.\s+`([^`]+)`", line)
        if m:
            tags.append(Tag(name=m.group(1).strip(), moderated=moderated))
    if len(tags) != 8:
        raise ToolError(
            f"{_rel(doc_path)}: expected 8 tags in the `## Tags` section, parsed {len(tags)} "
            f"({[t.name for t in tags]}). Fix the doc or this parser -- do not guess a taxonomy."
        )
    member_facing = [t for t in tags if not t.moderated]
    status = [t for t in tags if t.moderated]
    if len(member_facing) != 4 or len(status) != 4:
        raise ToolError(
            f"{_rel(doc_path)}: expected 4 member-facing + 4 status tags, got "
            f"{len(member_facing)} + {len(status)}"
        )
    for t in tags:
        if len(t.name) > TAG_NAME_LIMIT:
            raise ToolError(f"tag '{t.name}' is {len(t.name)} chars; Discord's limit is {TAG_NAME_LIMIT}")
    return tags


def parse_post_guidelines(doc_path: Path) -> str:
    """The forum's Post Guidelines box, quoted in 07-forum-tags-setup.md."""
    text = doc_path.read_text(encoding="utf-8")
    m = re.search(
        r"Post guidelines box:.*?[—–-]\s*[\"“](.+?)[\"”]",
        text,
        flags=re.DOTALL,
    )
    if not m:
        raise ToolError(f"{_rel(doc_path)}: could not find the quoted post-guidelines text")
    return re.sub(r"\s+", " ", m.group(1)).strip()


# --------------------------------------------------------------------------- #
# Config
# --------------------------------------------------------------------------- #


@dataclass
class PostSpec:
    key: str
    seed_file: str
    tags: list[str]
    pinned: bool
    workbench_tools: list[str]
    seed: Seed = field(init=False)

    def __post_init__(self) -> None:
        self.seed = parse_seed(DISCORD_DOCS / self.seed_file)


@dataclass
class Config:
    guild_id: str
    channel_name: str
    require_tag: bool
    sort_order: str
    site_base_url: Optional[str]
    posts: list[PostSpec]
    tags: list[Tag]
    guidelines: str
    never_post: frozenset[str]

    @property
    def sort_order_value(self) -> int:
        return SORT_ORDERS[self.sort_order]


def load_config(path: Path, guild_override: Optional[str] = None, site_base_override: Optional[str] = None) -> Config:
    if not path.exists():
        raise ToolError(f"config not found: {_rel(path)}")
    raw = json.loads(path.read_text(encoding="utf-8"))
    if raw.get("schema_version") != 1:
        raise ToolError(f"{_rel(path)}: unsupported schema_version {raw.get('schema_version')!r}")

    tags_doc = DISCORD_DOCS / raw.get("tags_source", "07-forum-tags-setup.md")
    channel = raw.get("channel") or {}
    sort_order = channel.get("sort_order", "recent_activity")
    if sort_order not in SORT_ORDERS:
        raise ToolError(f"{_rel(path)}: sort_order must be one of {sorted(SORT_ORDERS)}")

    # The denylist can be widened by config, never narrowed.
    never = frozenset(NEVER_POST | {str(x) for x in raw.get("never_post", [])})

    posts: list[PostSpec] = []
    seen_keys: set[str] = set()
    for entry in raw.get("posts", []):
        seed_file = entry["seed"]
        if seed_file in never:
            raise ToolError(
                f"{_rel(path)}: post '{entry.get('key')}' points at {seed_file}, which this tool never posts. "
                "Announcing is a deliberate Derek action, not a provisioning step."
            )
        key = entry["key"]
        if key in seen_keys:
            raise ToolError(f"{_rel(path)}: duplicate post key '{key}'")
        seen_keys.add(key)
        posts.append(
            PostSpec(
                key=key,
                seed_file=seed_file,
                tags=list(entry.get("tags", [])),
                pinned=bool(entry.get("pinned", False)),
                workbench_tools=list(entry.get("workbench_tools", [])),
            )
        )
    if not posts:
        raise ToolError(f"{_rel(path)}: no posts configured")

    tags = parse_tags(tags_doc)
    tag_names = {t.name for t in tags}
    for post in posts:
        for name in post.tags:
            if name not in tag_names:
                raise ToolError(
                    f"{_rel(path)}: post '{post.key}' wants tag '{name}', which is not in the taxonomy "
                    f"parsed from {_rel(tags_doc)} ({sorted(tag_names)})"
                )

    titles = [p.seed.title for p in posts]
    dupes = {t for t in titles if titles.count(t) > 1}
    if dupes:
        raise ToolError(f"two posts resolve to the same thread title {sorted(dupes)}; titles are the match key")

    guild_id = guild_override or raw.get("guild_id")
    if not guild_id:
        raise ToolError("no guild_id in config and none passed with --guild-id")

    site_base = site_base_override if site_base_override is not None else raw.get("site_base_url")
    if site_base:
        site_base = site_base.rstrip("/")

    return Config(
        guild_id=str(guild_id),
        channel_name=channel.get("name", "workbench"),
        require_tag=bool(channel.get("require_tag", True)),
        sort_order=sort_order,
        site_base_url=site_base,
        posts=posts,
        tags=tags,
        guidelines=parse_post_guidelines(tags_doc),
        never_post=never,
    )


# --------------------------------------------------------------------------- #
# Rendering: placeholder resolution + chunking
# --------------------------------------------------------------------------- #


@dataclass
class RenderedPost:
    spec: PostSpec
    title: str
    chunks: list[str]
    blocked_reason: Optional[str]

    @property
    def body(self) -> str:
        return "\n\n".join(self.chunks)

    @property
    def content_sha256(self) -> str:
        return hashlib.sha256(self.body.encode("utf-8")).hexdigest()


def load_workbench_tools() -> dict[str, dict]:
    if not WORKBENCH_JSON.exists():
        return {}
    data = json.loads(WORKBENCH_JSON.read_text(encoding="utf-8"))
    return {t["id"]: t for t in data.get("tools", [])}


def resolve_urls(body: str, spec: PostSpec, tools: dict[str, dict], site_base: Optional[str]) -> str:
    """Fill <ONEPAGER-URL> / <ACCESS-URL> from workbench.json plus the site base URL.

    Both are derived, never typed twice: the one-pager is the tool's anchor on the
    catalog page (workbench.mjs renders `<article id="{tool.id}">`), and the access
    link is the tool's own access.href, made absolute when it is site-relative. A
    not-published tool has no access.href; there "get it" honestly means "read the
    source", so the tool's public source.href stands in — never invented, only the
    other field the catalog already carries."""
    if "<ONEPAGER-URL>" not in body and "<ACCESS-URL>" not in body:
        return body
    if not site_base:
        return body  # left unresolved on purpose; the placeholder guard blocks the post
    if len(spec.workbench_tools) != 1:
        return body
    tool = tools.get(spec.workbench_tools[0])
    if not tool:
        return body

    body = body.replace("<ONEPAGER-URL>", f"{site_base}/workbench#{tool['id']}")
    access = tool.get("access") or {}
    href = access.get("href")
    if not (isinstance(href, str) and href):
        source = tool.get("source") or {}
        source_href = source.get("href")
        if isinstance(source_href, str) and source_href:
            href = source_href
    if isinstance(href, str) and href:
        absolute = f"{site_base}{href}" if href.startswith("/") else href
        body = body.replace("<ACCESS-URL>", absolute)
    return body


def chunk_content(body: str, limit: int = MESSAGE_LIMIT) -> list[str]:
    """Split a post body into <=limit-char messages on paragraph boundaries.

    No chunk markers are added -- a "(1/2)" tag is exactly the botted texture this
    tool exists to avoid. Splits fall on blank lines, then line breaks, then (only
    for a single overlong line) a hard cut."""
    if len(body) <= limit:
        return [body]
    chunks: list[str] = []
    current = ""

    def flush() -> None:
        nonlocal current
        if current.strip():
            chunks.append(current.strip())
        current = ""

    for para in body.split("\n\n"):
        candidate = f"{current}\n\n{para}" if current else para
        if len(candidate) <= limit:
            current = candidate
            continue
        flush()
        if len(para) <= limit:
            current = para
            continue
        for line in para.split("\n"):
            candidate = f"{current}\n{line}" if current else line
            if len(candidate) <= limit:
                current = candidate
                continue
            flush()
            while len(line) > limit:
                chunks.append(line[:limit])
                line = line[limit:]
            current = line
    flush()
    return chunks


def render_post(spec: PostSpec, tools: dict[str, dict], site_base: Optional[str]) -> RenderedPost:
    body = resolve_urls(spec.seed.body, spec, tools, site_base)
    leftovers = sorted(set(PLACEHOLDER_RE.findall(body)))
    blocked = None
    if leftovers:
        tokens = ", ".join(f"<{t}>" for t in leftovers)
        if site_base:
            blocked = f"unresolved placeholder(s) {tokens} -- no substitution rule produced a value"
        else:
            blocked = (
                f"unresolved placeholder(s) {tokens} -- site_base_url is not set, so the catalog "
                "page and download links do not exist yet (fill it after the /workbench deploy)"
            )
    return RenderedPost(spec=spec, title=spec.seed.title, chunks=chunk_content(body), blocked_reason=blocked)


# --------------------------------------------------------------------------- #
# Transport
# --------------------------------------------------------------------------- #


class Transport:
    def request(self, method: str, path: str, payload: Optional[dict] = None, query: Optional[dict] = None) -> Any:
        raise NotImplementedError


class HttpTransport(Transport):
    """Minimal Discord REST client. Retries 429/5xx a bounded number of times."""

    MAX_ATTEMPTS = 4

    def __init__(self, token: str, sleep=time.sleep) -> None:
        self._token = token
        self._sleep = sleep

    def request(self, method: str, path: str, payload: Optional[dict] = None, query: Optional[dict] = None) -> Any:
        url = f"{API_BASE}{path}"
        if query:
            url = f"{url}?{urllib.parse.urlencode({k: v for k, v in query.items() if v is not None})}"
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        for attempt in range(1, self.MAX_ATTEMPTS + 1):
            req = urllib.request.Request(url=url, data=data, method=method)
            req.add_header("Authorization", f"Bot {self._token}")
            req.add_header("User-Agent", USER_AGENT)
            if data is not None:
                req.add_header("Content-Type", "application/json")
            try:
                with urllib.request.urlopen(req, timeout=30) as resp:
                    raw = resp.read()
                    return json.loads(raw) if raw else None
            except urllib.error.HTTPError as exc:
                body = exc.read().decode("utf-8", errors="replace")
                if exc.code == 429 and attempt < self.MAX_ATTEMPTS:
                    self._sleep(self._retry_after(body))
                    continue
                if 500 <= exc.code < 600 and attempt < self.MAX_ATTEMPTS:
                    self._sleep(min(2 ** attempt, 8))
                    continue
                raise ToolError(f"Discord {method} {path} failed: HTTP {exc.code} {body[:400]}") from exc
            except urllib.error.URLError as exc:
                raise ToolError(f"Discord {method} {path} failed: {exc.reason}") from exc
        raise ToolError(f"Discord {method} {path} failed after {self.MAX_ATTEMPTS} attempts")

    @staticmethod
    def _retry_after(body: str) -> float:
        try:
            return min(float(json.loads(body).get("retry_after", 1.0)), 30.0)
        except (ValueError, AttributeError, TypeError):
            return 1.0


class DiscordClient:
    def __init__(self, transport: Transport) -> None:
        self._t = transport
        self._me: Optional[dict] = None

    # -- reads ------------------------------------------------------------- #
    def me(self) -> dict:
        if self._me is None:
            self._me = self._t.request("GET", "/users/@me")
        return self._me

    def guild(self, guild_id: str) -> dict:
        return self._t.request("GET", f"/guilds/{guild_id}")

    def guild_channels(self, guild_id: str) -> list[dict]:
        return self._t.request("GET", f"/guilds/{guild_id}/channels") or []

    def invite(self, code: str) -> dict:
        return self._t.request("GET", f"/invites/{code}", query={"with_expiration": "true"})

    def active_threads(self, guild_id: str) -> list[dict]:
        return (self._t.request("GET", f"/guilds/{guild_id}/threads/active") or {}).get("threads", [])

    def archived_threads(self, channel_id: str) -> list[dict]:
        out: list[dict] = []
        before: Optional[str] = None
        while True:
            page = self._t.request(
                "GET", f"/channels/{channel_id}/threads/archived/public", query={"limit": 100, "before": before}
            ) or {}
            threads = page.get("threads", [])
            out.extend(threads)
            if not page.get("has_more") or not threads:
                return out
            before = threads[-1].get("thread_metadata", {}).get("archive_timestamp")
            if not before:
                return out

    def message(self, channel_id: str, message_id: str) -> Optional[dict]:
        try:
            return self._t.request("GET", f"/channels/{channel_id}/messages/{message_id}")
        except ToolError as exc:
            if "HTTP 404" in str(exc):
                return None
            raise

    def first_messages(self, channel_id: str, limit: int = 10) -> list[dict]:
        """The oldest messages in a thread, ascending. Used to rediscover a managed
        post when the state file has no record of it -- without this, a lost state
        file would make a two-message post look one message short and append a
        duplicate continuation."""
        page = self._t.request("GET", f"/channels/{channel_id}/messages", query={"limit": limit, "after": 0}) or []
        return list(reversed(page))

    def messages(self, channel_id: str) -> list[dict]:
        """Every message in a channel/thread, oldest first."""
        collected: list[dict] = []
        before: Optional[str] = None
        while True:
            page = self._t.request("GET", f"/channels/{channel_id}/messages", query={"limit": 100, "before": before}) or []
            collected.extend(page)
            if len(page) < 100:
                break
            before = page[-1]["id"]
        return list(reversed(collected))

    # -- writes ------------------------------------------------------------ #
    def create_forum(self, guild_id: str, name: str, topic: str, tags: list[Tag], sort_order: int) -> dict:
        return self._t.request(
            "POST",
            f"/guilds/{guild_id}/channels",
            {
                "name": name,
                "type": CHANNEL_TYPE_FORUM,
                "topic": topic,
                "available_tags": [{"name": t.name, "moderated": t.moderated} for t in tags],
                "default_sort_order": sort_order,
            },
        )

    def modify_channel(self, channel_id: str, payload: dict) -> dict:
        return self._t.request("PATCH", f"/channels/{channel_id}", payload)

    def create_forum_post(self, forum_id: str, name: str, content: str, tag_ids: list[str]) -> dict:
        return self._t.request(
            "POST",
            f"/channels/{forum_id}/threads",
            {
                "name": name,
                "applied_tags": tag_ids,
                "message": {"content": content, "allowed_mentions": {"parse": []}},
            },
        )

    def create_message(self, channel_id: str, content: str) -> dict:
        return self._t.request(
            "POST",
            f"/channels/{channel_id}/messages",
            {"content": content, "allowed_mentions": {"parse": []}},
        )

    def edit_message(self, channel_id: str, message_id: str, content: str) -> dict:
        return self._t.request(
            "PATCH",
            f"/channels/{channel_id}/messages/{message_id}",
            {"content": content, "allowed_mentions": {"parse": []}},
        )

    def create_channel_invite(self, channel_id: str) -> dict:
        """A never-expiring, unlimited-use join invite. The one write here that is not
        content: an invite is an access artifact, so it rides the same --yes ceremony
        as every content write."""
        return self._t.request(
            "POST",
            f"/channels/{channel_id}/invites",
            {"max_age": 0, "max_uses": 0, "unique": True},
        )


# --------------------------------------------------------------------------- #
# Live state + plan
# --------------------------------------------------------------------------- #


@dataclass
class LivePost:
    thread_id: str
    title: str
    pinned: bool
    applied_tag_ids: list[str]
    message_ids: list[str]
    contents: list[str]
    authored_by_bot: bool


@dataclass
class LiveState:
    known: bool
    channel: Optional[dict] = None
    posts: dict[str, LivePost] = field(default_factory=dict)  # keyed by thread title

    @property
    def channel_id(self) -> Optional[str]:
        return self.channel.get("id") if self.channel else None

    @property
    def tag_ids_by_name(self) -> dict[str, str]:
        if not self.channel:
            return {}
        return {t["name"]: t["id"] for t in self.channel.get("available_tags", [])}


@dataclass
class Action:
    verb: str  # CREATE / UPDATE / PIN / BLOCKED / OK
    target: str
    detail: str
    apply: Optional[Any] = None  # callable executed by `apply`; None means nothing to do


@dataclass
class Plan:
    config: Config
    live: LiveState
    rendered: list[RenderedPost]
    actions: list[Action]
    notes: list[str]

    @property
    def changes(self) -> list[Action]:
        return [a for a in self.actions if a.apply is not None]

    @property
    def blocked(self) -> list[Action]:
        return [a for a in self.actions if a.verb == "BLOCKED"]

    @property
    def plan_hash(self) -> str:
        payload = "\n".join(f"{a.verb}|{a.target}|{a.detail}" for a in self.actions)
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:12]


def _rediscover_post(client: DiscordClient, thread_id: str, bot_id: str) -> tuple[list[str], list[str], bool]:
    messages = client.first_messages(thread_id)
    if not messages:
        return [thread_id], [""], False
    if str((messages[0].get("author") or {}).get("id")) != bot_id:
        return [str(messages[0]["id"])], [messages[0].get("content") or ""], False
    ours: list[dict] = []
    for msg in messages:
        if str((msg.get("author") or {}).get("id")) != bot_id:
            break
        ours.append(msg)
    return [str(m["id"]) for m in ours], [m.get("content") or "" for m in ours], True


def read_live_state(client: DiscordClient, cfg: Config, state: dict) -> LiveState:
    channels = client.guild_channels(cfg.guild_id)
    channel = next(
        (c for c in channels if c.get("type") == CHANNEL_TYPE_FORUM and c.get("name") == cfg.channel_name),
        None,
    )
    if channel is None:
        by_id = state.get("channel_id")
        channel = next((c for c in channels if c.get("id") == by_id), None) if by_id else None
    if channel is None:
        return LiveState(known=True, channel=None)

    bot_id = str(client.me().get("id"))
    threads = [t for t in client.active_threads(cfg.guild_id) if str(t.get("parent_id")) == str(channel["id"])]
    threads += client.archived_threads(str(channel["id"]))

    posts: dict[str, LivePost] = {}
    known_messages = {p.get("thread_id"): p.get("message_ids", []) for p in state.get("posts", {}).values()}
    for thread in threads:
        tid = str(thread["id"])
        flags = int(thread.get("flags") or 0)
        recorded = known_messages.get(tid)
        if recorded:
            message_ids = list(recorded)
            contents = []
            authored_by_bot = True
            for mid in message_ids:
                msg = client.message(tid, mid)
                if msg is None:
                    contents.append("")
                    continue
                contents.append(msg.get("content") or "")
                if str((msg.get("author") or {}).get("id")) != bot_id:
                    authored_by_bot = False
        else:
            # Nothing recorded for this thread: read it. A post we manage is the
            # starter message plus the unbroken run of our own messages after it --
            # anything from that point on is somebody else's reply, not our content.
            message_ids, contents, authored_by_bot = _rediscover_post(client, tid, bot_id)
        posts[thread.get("name", "")] = LivePost(
            thread_id=tid,
            title=thread.get("name", ""),
            pinned=bool(flags & CHANNEL_FLAG_PINNED),
            applied_tag_ids=[str(x) for x in (thread.get("applied_tags") or [])],
            message_ids=message_ids,
            contents=contents,
            authored_by_bot=authored_by_bot,
        )
    return LiveState(known=True, channel=channel, posts=posts)


def build_plan(cfg: Config, live: LiveState, tools: dict[str, dict]) -> Plan:
    rendered = [render_post(p, tools, cfg.site_base_url) for p in cfg.posts]
    actions: list[Action] = []
    notes: list[str] = []

    # --- channel ---------------------------------------------------------- #
    if live.channel is None:
        actions.append(
            Action(
                "CREATE",
                f"#{cfg.channel_name}",
                f"forum channel, sort={cfg.sort_order}, {len(cfg.tags)} tags, guidelines set",
                apply="create_channel",
            )
        )
        if cfg.require_tag:
            actions.append(
                Action("UPDATE", f"#{cfg.channel_name}", "turn on Require people to select tags", apply="require_tag")
            )
    else:
        existing = {t["name"]: t for t in live.channel.get("available_tags", [])}
        missing = [t for t in cfg.tags if t.name not in existing]
        if missing:
            actions.append(
                Action("UPDATE", f"#{cfg.channel_name}", f"add missing tag(s): {', '.join(t.name for t in missing)}", apply="sync_tags")
            )
        wrong_mode = [t for t in cfg.tags if t.name in existing and bool(existing[t.name].get("moderated")) != t.moderated]
        if wrong_mode:
            actions.append(
                Action(
                    "UPDATE",
                    f"#{cfg.channel_name}",
                    "correct who may apply: " + ", ".join(f"{t.name} -> {'status' if t.moderated else 'member-facing'}" for t in wrong_mode),
                    apply="sync_tags",
                )
            )
        extra = [name for name in existing if name not in {t.name for t in cfg.tags}]
        if extra:
            notes.append(
                f"{len(extra)} tag(s) exist in Discord but not in the repo taxonomy: {', '.join(sorted(extra))}. "
                "Left alone -- removing a tag strips it from every post that carries it. Delete by hand if unwanted."
            )
        if (live.channel.get("topic") or "").strip() != cfg.guidelines:
            actions.append(Action("UPDATE", f"#{cfg.channel_name}", "post guidelines differ from the repo text", apply="topic"))
        flags = int(live.channel.get("flags") or 0)
        if cfg.require_tag and not flags & CHANNEL_FLAG_REQUIRE_TAG:
            actions.append(Action("UPDATE", f"#{cfg.channel_name}", "turn on Require people to select tags", apply="require_tag"))
        if int(live.channel.get("default_sort_order") or 0) != cfg.sort_order_value:
            actions.append(Action("UPDATE", f"#{cfg.channel_name}", f"set sort order to {cfg.sort_order}", apply="sort_order"))

    # --- posts ------------------------------------------------------------ #
    for post in rendered:
        label = f'"{post.title}"'
        if post.blocked_reason:
            actions.append(Action("BLOCKED", label, post.blocked_reason))
            continue
        current = live.posts.get(post.title)
        if current is None:
            tag_note = ", ".join(post.spec.tags) if post.spec.tags else "no tag"
            actions.append(
                Action(
                    "CREATE",
                    label,
                    f"forum post from {post.spec.seed_file} ({len(post.chunks)} message(s), tag: {tag_note})"
                    + (", pinned" if post.spec.pinned else ""),
                    apply=("create_post", post),
                )
            )
            continue
        if not current.authored_by_bot:
            actions.append(
                Action(
                    "BLOCKED",
                    label,
                    "this post was created by hand, not by this tool -- a bot can only edit its own messages, "
                    "so content sync is impossible. Delete and let the tool recreate it, or keep maintaining it by hand.",
                )
            )
            continue
        if current.contents != post.chunks:
            if len(post.chunks) < len(current.contents):
                actions.append(
                    Action(
                        "BLOCKED",
                        label,
                        f"the repo version is now {len(post.chunks)} message(s) but the live post has "
                        f"{len(current.contents)} -- shrinking means deleting a posted message. Do that by hand.",
                    )
                )
                continue
            actions.append(Action("UPDATE", label, f"content differs from {post.spec.seed_file}", apply=("update_post", post, current)))
        if post.spec.pinned and not current.pinned:
            actions.append(Action("PIN", label, "pin to the top of the forum", apply=("pin_post", current)))
        want_tags = {live.tag_ids_by_name.get(n) for n in post.spec.tags} - {None}
        if want_tags and not want_tags.issubset(set(current.applied_tag_ids)):
            actions.append(Action("UPDATE", label, f"apply tag(s): {', '.join(post.spec.tags)}", apply=("tag_post", post, current)))

    if not any(a.apply for a in actions) and not any(a.verb == "BLOCKED" for a in actions):
        actions.append(Action("OK", "everything", "Discord already matches the repo; nothing to do"))
    return Plan(config=cfg, live=live, rendered=rendered, actions=actions, notes=notes)


# --------------------------------------------------------------------------- #
# Apply
# --------------------------------------------------------------------------- #


def _tag_payload(cfg: Config, live: LiveState) -> list[dict]:
    """Full available_tags list, preserving ids of tags that already exist (Discord
    treats an omitted tag as a deletion) and keeping unknown extras untouched."""
    existing = {t["name"]: t for t in (live.channel or {}).get("available_tags", [])}
    payload: list[dict] = []
    for tag in cfg.tags:
        current = existing.get(tag.name)
        entry: dict[str, Any] = {"name": tag.name, "moderated": tag.moderated}
        if current:
            entry["id"] = current["id"]
            entry.setdefault("emoji_id", current.get("emoji_id"))
            entry.setdefault("emoji_name", current.get("emoji_name"))
        payload.append(entry)
    for name, current in existing.items():
        if name not in {t.name for t in cfg.tags}:
            payload.append(current)
    return payload


def apply_plan(plan: Plan, client: DiscordClient, state: dict, log=print, pause: float = 0.4) -> dict:
    cfg, live = plan.config, plan.live
    state.setdefault("posts", {})

    def channel_id() -> str:
        cid = live.channel_id
        if not cid:
            raise ToolError("no channel id -- the forum channel step must run first")
        return cid

    def create_channel() -> None:
        created = client.create_forum(cfg.guild_id, cfg.channel_name, cfg.guidelines, cfg.tags, cfg.sort_order_value)
        live.channel = created
        state["channel_id"] = str(created["id"])
        state["channel_name"] = cfg.channel_name
        log(f"  created forum channel #{cfg.channel_name} ({created['id']})")

    def require_tag() -> None:
        flags = int((live.channel or {}).get("flags") or 0) | CHANNEL_FLAG_REQUIRE_TAG
        live.channel = client.modify_channel(channel_id(), {"flags": flags})
        log("  required tags: on")

    def sync_tags() -> None:
        live.channel = client.modify_channel(channel_id(), {"available_tags": _tag_payload(cfg, live)})
        log(f"  tag taxonomy synced ({len(cfg.tags)} repo tags)")

    def topic() -> None:
        live.channel = client.modify_channel(channel_id(), {"topic": cfg.guidelines})
        log("  post guidelines updated")

    def sort_order() -> None:
        live.channel = client.modify_channel(channel_id(), {"default_sort_order": cfg.sort_order_value})
        log(f"  sort order set to {cfg.sort_order}")

    def clear_require_tag_if_needed(post: RenderedPost) -> Optional[int]:
        """Discord rejects an untagged post when Require Tags is on (error 40067).
        The pinned guideline post carries no tag by design, so the flag is dropped
        for exactly that create and restored immediately after."""
        flags = int((live.channel or {}).get("flags") or 0)
        if post.spec.tags or not flags & CHANNEL_FLAG_REQUIRE_TAG:
            return None
        live.channel = client.modify_channel(channel_id(), {"flags": flags & ~CHANNEL_FLAG_REQUIRE_TAG})
        return flags

    def create_post(post: RenderedPost) -> None:
        tag_ids = [live.tag_ids_by_name[n] for n in post.spec.tags if n in live.tag_ids_by_name]
        restore = clear_require_tag_if_needed(post)
        try:
            thread = client.create_forum_post(channel_id(), post.title, post.chunks[0], tag_ids)
        finally:
            if restore is not None:
                live.channel = client.modify_channel(channel_id(), {"flags": restore})
        tid = str(thread["id"])
        message_ids = [tid]
        for extra in post.chunks[1:]:
            time.sleep(pause)
            message_ids.append(str(client.create_message(tid, extra)["id"]))
        if post.spec.pinned:
            client.modify_channel(tid, {"flags": int(thread.get("flags") or 0) | CHANNEL_FLAG_PINNED})
        state["posts"][post.spec.key] = {
            "key": post.spec.key,
            "seed": post.spec.seed_file,
            "title": post.title,
            "thread_id": tid,
            "message_ids": message_ids,
            "content_sha256": post.content_sha256,
            "pinned": post.spec.pinned,
            "workbench_tools": post.spec.workbench_tools,
            "url": thread_url(cfg.guild_id, tid),
        }
        live.posts[post.title] = LivePost(
            thread_id=tid,
            title=post.title,
            pinned=post.spec.pinned,
            applied_tag_ids=tag_ids,
            message_ids=message_ids,
            contents=list(post.chunks),
            authored_by_bot=True,
        )
        log(f'  created post "{post.title}" -> {thread_url(cfg.guild_id, tid)}')

    def update_post(post: RenderedPost, current: LivePost) -> None:
        for idx, chunk in enumerate(post.chunks):
            if idx < len(current.message_ids):
                if current.contents[idx] != chunk:
                    client.edit_message(current.thread_id, current.message_ids[idx], chunk)
                    time.sleep(pause)
            else:
                current.message_ids.append(str(client.create_message(current.thread_id, chunk)["id"]))
                time.sleep(pause)
        current.contents = list(post.chunks)
        entry = state["posts"].setdefault(post.spec.key, {"key": post.spec.key, "seed": post.spec.seed_file})
        entry.update(
            {
                "title": post.title,
                "thread_id": current.thread_id,
                "message_ids": current.message_ids,
                "content_sha256": post.content_sha256,
                "workbench_tools": post.spec.workbench_tools,
                "url": thread_url(cfg.guild_id, current.thread_id),
            }
        )
        log(f'  updated post "{post.title}" ({len(post.chunks)} message(s))')

    def pin_post(current: LivePost) -> None:
        client.modify_channel(current.thread_id, {"flags": CHANNEL_FLAG_PINNED})
        current.pinned = True
        log(f'  pinned "{current.title}"')

    def tag_post(post: RenderedPost, current: LivePost) -> None:
        tag_ids = sorted({*current.applied_tag_ids, *(live.tag_ids_by_name[n] for n in post.spec.tags if n in live.tag_ids_by_name)})
        client.modify_channel(current.thread_id, {"applied_tags": tag_ids})
        current.applied_tag_ids = tag_ids
        log(f'  tagged "{current.title}": {", ".join(post.spec.tags)}')

    simple = {"create_channel": create_channel, "require_tag": require_tag, "sync_tags": sync_tags, "topic": topic, "sort_order": sort_order}
    dispatch = {"create_post": create_post, "update_post": update_post, "pin_post": pin_post, "tag_post": tag_post}

    done = 0
    for action in plan.actions:
        if action.apply is None:
            continue
        log(f"{action.verb} {action.target}: {action.detail}")
        if isinstance(action.apply, str):
            simple[action.apply]()
        else:
            dispatch[action.apply[0]](*action.apply[1:])
        done += 1
        time.sleep(pause)

    state["schema_version"] = 1
    state["guild_id"] = cfg.guild_id
    state["last_apply_utc"] = _now().strftime("%Y-%m-%dT%H:%M:%SZ")
    if live.channel:
        state["channel_id"] = str(live.channel["id"])
        state["channel_name"] = cfg.channel_name
    log(f"\n{done} action(s) applied.")
    return state


# --------------------------------------------------------------------------- #
# Export -> DiscordChatExporter-shaped JSON for distill_feedback.py
# --------------------------------------------------------------------------- #


def dce_author(msg: dict) -> dict:
    author = msg.get("author") or {}
    member = msg.get("member") or {}
    nickname = member.get("nick") or author.get("global_name") or ""
    return {
        "id": str(author.get("id", "")),
        "name": author.get("username", ""),
        "discriminator": str(author.get("discriminator", "0000")),
        "nickname": nickname,
        "color": None,
        "isBot": bool(author.get("bot", False)),
        "roles": [],
        "avatarUrl": author.get("avatar") or "",
    }


def dce_message(msg: dict) -> dict:
    return {
        "id": str(msg.get("id", "")),
        "type": DCE_MESSAGE_KINDS.get(int(msg.get("type", 0)), f"Unknown{msg.get('type')}"),
        "timestamp": msg.get("timestamp"),
        "timestampEdited": msg.get("edited_timestamp"),
        "callEndedTimestamp": None,
        "isPinned": bool(msg.get("pinned", False)),
        "content": msg.get("content") or "",
        "author": dce_author(msg),
        "attachments": [
            {
                "id": str(a.get("id", "")),
                "url": a.get("url", ""),
                "fileName": a.get("filename", ""),
                "fileSizeBytes": a.get("size", 0),
            }
            for a in msg.get("attachments") or []
        ],
        "embeds": [],
        "stickers": [],
        "reactions": [
            {
                "emoji": {"id": (r.get("emoji") or {}).get("id"), "name": (r.get("emoji") or {}).get("name", ""), "isAnimated": False},
                "count": r.get("count", 0),
            }
            for r in msg.get("reactions") or []
        ],
        "mentions": [
            {"id": str(u.get("id", "")), "name": u.get("username", ""), "discriminator": str(u.get("discriminator", "0000")), "nickname": "", "isBot": bool(u.get("bot", False))}
            for u in msg.get("mentions") or []
        ],
    }


def safe_filename(name: str) -> str:
    normalized = unicodedata.normalize("NFKD", name)
    cleaned = re.sub(r"[^A-Za-z0-9 _.-]+", "-", normalized).strip(" .-")
    return re.sub(r"-{2,}", "-", cleaned) or "thread"


def export_threads(client: DiscordClient, cfg: Config, state: dict, out_dir: Path, log=print) -> list[Path]:
    """Write one DiscordChatExporter-shaped JSON file per forum thread.

    This bot's own messages are left out. They are seed files this repo already
    holds, not community feedback, and distill_feedback.py's heuristics would
    happily classify our own "errors pasted verbatim, not summarized" line as a bug
    report. Skipping them at the source keeps the candidate journal honest."""
    live = read_live_state(client, cfg, state)
    if live.channel is None:
        raise ToolError(f"no #{cfg.channel_name} forum channel found in guild {cfg.guild_id} -- nothing to export")
    forum = live.channel
    guild = client.guild(cfg.guild_id)
    bot_id = str(client.me().get("id"))
    threads = [t for t in client.active_threads(cfg.guild_id) if str(t.get("parent_id")) == str(forum["id"])]
    threads += client.archived_threads(str(forum["id"]))

    out_dir.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    skipped = 0
    exported_at = _now().strftime("%Y-%m-%dT%H:%M:%SZ")
    for thread in threads:
        raw = client.messages(str(thread["id"]))
        own = [m for m in raw if str((m.get("author") or {}).get("id")) == bot_id]
        skipped += len(own)
        messages = [dce_message(m) for m in raw if str((m.get("author") or {}).get("id")) != bot_id]
        doc = {
            "guild": {"id": str(guild.get("id", cfg.guild_id)), "name": guild.get("name", ""), "iconUrl": ""},
            "channel": {
                "id": str(thread["id"]),
                "type": "GuildPublicThread",
                "categoryId": str(forum["id"]),
                "category": forum.get("name", cfg.channel_name),
                "name": thread.get("name", ""),
                "topic": "",
            },
            "dateRange": {"after": None, "before": None},
            "exportedAt": exported_at,
            "messageCount": len(messages),
            "messages": messages,
        }
        path = out_dir / f"{safe_filename(forum.get('name', cfg.channel_name))} - {safe_filename(thread.get('name', ''))} [{thread['id']}].json"
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
        written.append(path)
        log(f"  {len(messages):4d} message(s)  {path.name}")
    if skipped:
        log(f"  (skipped {skipped} message(s) this bot posted from the repo -- they are not community feedback)")
    return written


# --------------------------------------------------------------------------- #
# Receipt
# --------------------------------------------------------------------------- #


def render_receipt(plan: Plan, offline: bool, state_path: Path) -> str:
    cfg = plan.config
    L: list[str] = []
    L.append("# Workbench Discord provisioning receipt")
    L.append("")
    L.append(f"- Generated: {_now().strftime('%Y-%m-%dT%H:%M:%SZ')} (UTC)")
    L.append(f"- Guild: `{cfg.guild_id}`")
    L.append(f"- Channel: `#{cfg.channel_name}` (forum)")
    L.append(f"- Plan hash: `{plan.plan_hash}`")
    L.append(f"- Mode: **{'OFFLINE PREDICTION' if offline else 'LIVE DRY RUN'}** -- nothing was written to Discord.")
    L.append("")
    if offline:
        L.append(
            "> Offline mode assumes the server is empty (no `#workbench` channel, no posts). It is a "
            "prediction from repo files, not a reading of the live server. Re-run `plan` with a token "
            "to confirm against Discord before applying."
        )
        L.append("")

    L.append("## What this run will never do")
    L.append("")
    L.append("- Post `00-announcement.md`. It is on a hardcoded denylist; no flag posts it.")
    L.append("- Write a sentence nobody wrote in the repo. Every message body is a seed file, verbatim.")
    L.append("- Reply, react, DM, or mention anyone (`allowed_mentions` is empty on every write).")
    L.append("- Delete a tag, a post, or a message. Shrinking content is reported and left to a human.")
    L.append("")

    L.append("## Channel")
    L.append("")
    L.append(f"- Post guidelines: \"{cfg.guidelines}\"")
    L.append(f"- Require people to select tags: {'ON' if cfg.require_tag else 'off'}")
    L.append(f"- Sort: {cfg.sort_order}")
    L.append("")
    L.append("## Tag taxonomy")
    L.append("")
    L.append(f"Parsed from `{_rel(DISCORD_DOCS / '07-forum-tags-setup.md')}` -- the doc is the source of truth.")
    L.append("")
    L.append("| # | Tag | Who can apply it |")
    L.append("|---|---|---|")
    for i, tag in enumerate(cfg.tags, start=1):
        L.append(f"| {i} | `{tag.name}` | {'status -- Derek + contributors with triage rights' if tag.moderated else 'anyone posting'} |")
    L.append("")

    L.append("## Posts")
    L.append("")
    L.append("| Post title | Seed | Tag | Pinned | Messages | State |")
    L.append("|---|---|---|---|---|---|")
    for post in plan.rendered:
        state_label = "BLOCKED" if post.blocked_reason else ("exists" if post.title in plan.live.posts else "to create")
        L.append(
            f"| {post.title} | `{post.spec.seed_file}` | {', '.join(post.spec.tags) or '(none)'} | "
            f"{'yes' if post.spec.pinned else 'no'} | {len(post.chunks)} | {state_label} |"
        )
    L.append("")
    for post in plan.rendered:
        L.append(f"### {post.title}")
        L.append("")
        if post.blocked_reason:
            L.append(f"**BLOCKED** -- {post.blocked_reason}")
            L.append("")
            continue
        L.append(f"First message ({len(post.chunks[0])} chars of {MESSAGE_LIMIT}):")
        L.append("")
        L.append("```")
        L.extend(post.chunks[0].splitlines()[:8])
        if len(post.chunks[0].splitlines()) > 8:
            L.append(f"... ({len(post.chunks[0].splitlines()) - 8} more lines)")
        L.append("```")
        L.append("")
        if len(post.chunks) > 1:
            L.append(
                f"Continues in {len(post.chunks) - 1} more message(s) in the same thread "
                f"(the body is {len(post.body)} chars; Discord caps one message at {MESSAGE_LIMIT}). "
                "Split on paragraph boundaries, no part markers."
            )
            L.append("")

    L.append("## Actions")
    L.append("")
    if not plan.changes and not plan.blocked:
        L.append("Nothing to do -- Discord already matches the repo.")
        L.append("")
    for action in plan.actions:
        if action.verb == "OK":
            L.append(f"- **OK** -- {action.detail}")
        else:
            L.append(f"- **{action.verb}** {action.target} -- {action.detail}")
    L.append("")
    if plan.notes:
        L.append("## Notes")
        L.append("")
        for note in plan.notes:
            L.append(f"- {note}")
        L.append("")

    L.append("## Approval")
    L.append("")
    if plan.blocked:
        L.append(f"{len(plan.blocked)} item(s) are blocked and will be skipped. Resolve them or accept the partial run.")
        L.append("")
    L.append("Derek: if the above is what you want to appear in the server, run")
    L.append("")
    L.append("```")
    L.append(f"python tools/workbench/discord/workbench_discord.py apply --yes --expect-plan {plan.plan_hash}")
    L.append("```")
    L.append("")
    L.append(f"Thread ids and URLs land in `{_rel(state_path)}` for the workbench.json `discussion.href` fill.")
    L.append("")
    return "\n".join(L)


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _rel(path: Path) -> str:
    try:
        return path.resolve().relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return str(path)


def thread_url(guild_id: str, thread_id: str) -> str:
    return f"https://discord.com/channels/{guild_id}/{thread_id}"


def load_state(path: Path) -> dict:
    if not path.exists():
        return {"schema_version": 1, "posts": {}}
    return json.loads(path.read_text(encoding="utf-8"))


def save_state(path: Path, state: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def parse_token_file(path: Path) -> str:
    """Read a token from either a bare-token file or a KEY=VALUE env file.

    Both shapes exist in the wild and both are fine, so neither is a trap: a file
    holding nothing but the token works, and so does `KEY=<token>` (or
    `DISCORD_TOKEN=`, or `export TOKEN=`, quoted or not). A single entry is used
    whatever it is named; only a file with several entries has to name one this
    tool recognises."""
    # utf-8-sig, not utf-8: PowerShell 5.1's Out-File/Set-Content -Encoding utf8
    # writes a BOM, and a BOM in front of the token turns into a baffling HTTP 401.
    text = path.read_text(encoding="utf-8-sig")
    entries: list[tuple[str, str]] = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.lower().startswith("export "):
            line = line[len("export "):].lstrip()
        if "=" in line:
            name, value = line.split("=", 1)
            name = name.strip().upper()
        else:
            name, value = "", line
        value = value.strip().strip('"').strip("'").strip()
        if value.lower().startswith("bot "):  # someone pasted the Authorization header
            value = value[4:].strip()
        if value:
            entries.append((name, value))

    if not entries:
        raise ToolError(f"{path} holds no token. Expected either the token on its own line, or a line like KEY=<token>.")
    if len(entries) == 1:
        token = entries[0][1]
    else:
        named = {name: value for name, value in entries}
        token = next((named[k] for k in TOKEN_KEY_NAMES if k in named), "")
        if not token:
            raise ToolError(
                f"{path} has {len(entries)} entries ({', '.join(sorted(n or '(unnamed)' for n, _ in entries))}) "
                f"and none is named one of: {', '.join(TOKEN_KEY_NAMES)}. Rename the token's line or point "
                f"{ENV_TOKEN_FILE} at a file holding only the token."
            )

    # Bot tokens are ~59-72 chars of dot-separated base64 and never contain a space.
    # 50 is a floor with headroom, chosen to catch the real mistakes -- a placeholder
    # left in place, an app id, a key name read as its own value -- before they turn
    # into an HTTP 401 nobody can explain. Length only; the value is never echoed.
    if " " in token or len(token) < 50:
        raise ToolError(
            f"the value read from {path} does not look like a bot token: {len(token)} chars"
            f"{', contains a space' if ' ' in token else ''}. A Discord bot token is around 60-72 "
            "characters. Check the file holds the token itself, not a placeholder or an application id."
        )
    return token


def load_token(explicit: Optional[Path] = None) -> str:
    token = os.environ.get(ENV_TOKEN, "").strip()
    if token and explicit is None:
        return token

    if explicit is not None:
        candidates = [explicit.expanduser()]
    elif os.environ.get(ENV_TOKEN_FILE):
        candidates = [Path(os.environ[ENV_TOKEN_FILE]).expanduser()]
    else:
        candidates = [p.expanduser() for p in DEFAULT_TOKEN_FILES]

    token_file = next((p for p in candidates if p.exists()), None)
    if token_file is None:
        looked = "\n  ".join(str(p) for p in candidates)
        raise ToolError(
            f"no bot token. Set {ENV_TOKEN}, pass --token-file, point {ENV_TOKEN_FILE} at a file, "
            f"or create one of these:\n  {looked}\n"
            "The file may hold the bare token or a line like KEY=<token>. "
            "See Lumberjacks/docs/workbench/discord/09-discord-bot-setup.md."
        )
    resolved = token_file.resolve()
    if resolved == REPO_ROOT or REPO_ROOT in resolved.parents:
        raise ToolError(
            f"refusing to read a token from inside the repository: {resolved}\n"
            f"Move it under {Path.home() / '.baseline'} instead. A credential in the working tree is one "
            "`git add -A` away from being committed, and this repo's automation commits and pushes on its own."
        )
    return parse_token_file(token_file)


def invite_url(app_id: str) -> str:
    return f"https://discord.com/oauth2/authorize?client_id={app_id}&scope=bot&permissions={PERMISSIONS_INT}"


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #


def cmd_check(args: argparse.Namespace) -> int:
    cfg = load_config(args.config, args.guild_id, args.site_base_url)
    tools = load_workbench_tools()
    rendered = [render_post(p, tools, cfg.site_base_url) for p in cfg.posts]
    print(f"config      {_rel(args.config)}")
    print(f"guild       {cfg.guild_id}")
    print(f"channel     #{cfg.channel_name} (forum, require-tag={'on' if cfg.require_tag else 'off'}, sort={cfg.sort_order})")
    print(f"guidelines  \"{cfg.guidelines}\"")
    print(f"tags        {len(cfg.tags)}: " + ", ".join(f"{t.name}{'*' if t.moderated else ''}" for t in cfg.tags) + "   (* = status tag)")
    print(f"never-post  {', '.join(sorted(cfg.never_post))}")
    print("posts:")
    blocked = 0
    for post in rendered:
        flag = "BLOCKED" if post.blocked_reason else "ok"
        print(f"  [{flag:>7}] {post.title:<24} {post.spec.seed_file}  ({len(post.chunks)} msg, tags: {', '.join(post.spec.tags) or 'none'})")
        if post.blocked_reason:
            blocked += 1
            print(f"            -> {post.blocked_reason}")
    if blocked:
        print(f"\n{blocked} post(s) cannot be published yet. That is the guard working, not a bug.")
        return 1
    print("\nAll posts render clean.")
    return 0


def cmd_invite(args: argparse.Namespace) -> int:
    print("Minimum permission set:")
    for name, bit in sorted(PERMISSIONS.items(), key=lambda kv: kv[1]):
        print(f"  {name:<26} {bit}")
    print(f"\npermissions integer: {PERMISSIONS_INT}")
    if args.app_id:
        print(f"\n{invite_url(args.app_id)}")
    else:
        print(f"\n{invite_url('YOUR_APP_ID')}")
        print("(pass --app-id to get the exact URL)")
    return 0


def ensure_permanent_invite(client: DiscordClient, state: dict) -> tuple[dict, str, bool]:
    """Replace an expiring guild invite with a never-expiring one on the same channel.

    Returns (state, message, changed). The old invite is never revoked: every copy of
    it already in the wild keeps working until Discord retires it on its own schedule.
    The offline check() warns two weeks before an expiry recorded in the state file;
    this is the maintenance action that ends that clock for good."""
    recorded = state.get("invite_href") or ""
    code = recorded.rsplit("/", 1)[-1] if recorded else ""
    if not code:
        raise ToolError("provision-state has no invite_href to work from")
    live = client.invite(code)
    channel_id = (live.get("channel") or {}).get("id")
    expires = live.get("expires_at")
    if expires is None:
        if state.get("invite_expires_at") is not None:
            return dict(state, invite_expires_at=None), f"{recorded} is already never-expiring; state now says so", True
        return state, f"{recorded} is already never-expiring", False
    if not channel_id:
        raise ToolError(f"the live invite {recorded} names no channel; cannot mint a replacement")
    created = client.create_channel_invite(channel_id)
    new_href = f"https://discord.gg/{created['code']}"
    new_state = dict(state, invite_href=new_href, invite_expires_at=None)
    return new_state, f"created {new_href} (never expires); {recorded} is left to lapse {expires}", True


def cmd_guild_invite(args: argparse.Namespace) -> int:
    """The guild join invite the public page carries -- report it, and with
    --ensure-permanent replace an expiring one in place."""
    state = load_state(args.state)
    recorded = state.get("invite_href") or ""
    code = recorded.rsplit("/", 1)[-1] if recorded else ""
    if not code:
        raise ToolError("provision-state has no invite_href to work from")
    client = DiscordClient(HttpTransport(load_token(args.token_file)))
    live = client.invite(code)
    channel = live.get("channel") or {}
    expires = live.get("expires_at")
    print(f"invite  {recorded}")
    print(f"channel #{channel.get('name')} ({channel.get('id')})")
    print(f"expires {expires or 'never'}")
    if not args.ensure_permanent:
        return 0
    if expires is None and state.get("invite_expires_at") is None:
        print("\nNothing to do -- the invite is already never-expiring and the state agrees.")
        return 0
    if not args.yes:
        if expires is None:
            print("\nWould sync invite_expires_at: null into provision-state (no Discord write).")
        else:
            print(f"\nWould create a never-expiring invite on #{channel.get('name')} and record it; {recorded} would be left to lapse {expires}.")
        print("Refusing to touch a live community server without --yes.")
        return 2
    new_state, message, changed = ensure_permanent_invite(client, state)
    if changed:
        save_state(args.state, new_state)
    print(f"\n{message}")
    print(f"state   {_rel(args.state)}")
    if new_state.get("invite_href") != recorded:
        print("\nNext (a separate, approved step): update workbench.json feedback.invite_href to the new invite, render, publish.")
    return 0


def _plan_and_receipt(args: argparse.Namespace) -> tuple[Plan, Optional[DiscordClient], dict]:
    cfg = load_config(args.config, args.guild_id, args.site_base_url)
    state = load_state(args.state)
    tools = load_workbench_tools()
    client: Optional[DiscordClient] = None
    if args.offline:
        live = LiveState(known=False)
    else:
        client = DiscordClient(HttpTransport(load_token(args.token_file)))
        live = read_live_state(client, cfg, state)
    return build_plan(cfg, live, tools), client, state


def cmd_plan(args: argparse.Namespace) -> int:
    plan, _client, _state = _plan_and_receipt(args)
    receipt = render_receipt(plan, offline=args.offline, state_path=args.state)
    out = args.receipt_out or (DEFAULT_RECEIPT_DIR / f"{_now().strftime('%Y-%m-%d')}-plan{'-offline' if args.offline else ''}.md")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(receipt, encoding="utf-8")

    for action in plan.actions:
        print(f"{action.verb:<8} {action.target}: {action.detail}")
    for note in plan.notes:
        print(f"NOTE     {note}")
    print(f"\nplan hash {plan.plan_hash}")
    print(f"receipt   {_rel(out)}")
    print("\nNothing was written to Discord. Review the receipt, then run `apply --yes`.")
    return 0


def cmd_apply(args: argparse.Namespace) -> int:
    if args.offline:
        raise ToolError("apply needs a live connection; --offline is plan-only")
    plan, client, state = _plan_and_receipt(args)
    if args.expect_plan and args.expect_plan != plan.plan_hash:
        raise ToolError(
            f"plan hash changed since the approved receipt ({args.expect_plan} -> {plan.plan_hash}). "
            "Re-run `plan`, re-read the receipt, and approve the new one."
        )
    if not plan.changes:
        print("Nothing to do -- Discord already matches the repo.")
        for action in plan.blocked:
            print(f"BLOCKED  {action.target}: {action.detail}")
        return 0
    if not args.yes:
        for action in plan.changes:
            print(f"{action.verb:<8} {action.target}: {action.detail}")
        print("\nRefusing to touch a live community server without --yes.")
        return 2
    assert client is not None
    state = apply_plan(plan, client, state)
    save_state(args.state, state)
    print(f"state     {_rel(args.state)}")
    posts = state.get("posts", {})
    if posts:
        print("\nThread URLs (for workbench.json discussion.href -- a separate, approved step):")
        for entry in posts.values():
            for tool_id in entry.get("workbench_tools", []):
                print(f"  {tool_id:<26} {entry['url']}")
    for action in plan.blocked:
        print(f"\nBLOCKED  {action.target}: {action.detail}")
    return 0


def cmd_whoami(args: argparse.Namespace) -> int:
    """Prove the credential works before anything else is attempted."""
    cfg = load_config(args.config, args.guild_id, args.site_base_url)
    client = DiscordClient(HttpTransport(load_token(args.token_file)))
    me = client.me()
    print(f"token   OK -- authenticated as {me.get('username')} (id {me.get('id')}, bot={me.get('bot')})")
    try:
        guild = client.guild(cfg.guild_id)
    except ToolError as exc:
        detail = "the bot is not in this server, or cannot see it" if "HTTP 403" in str(exc) or "HTTP 404" in str(exc) else str(exc)
        print(f"guild   FAILED for {cfg.guild_id} -- {detail}")
        # A bot user's id is its application id, so the invite link needs nothing else.
        print(f"\nOpen this, pick the server, authorize:\n  {invite_url(str(me.get('id')))}")
        return 1
    print(f"guild   OK -- {guild.get('name')} ({guild.get('id')})")
    channels = client.guild_channels(cfg.guild_id)
    forum = next((c for c in channels if c.get("type") == CHANNEL_TYPE_FORUM and c.get("name") == cfg.channel_name), None)
    print(f"channel {'OK -- #' + cfg.channel_name + ' exists (' + str(forum['id']) + ')' if forum else 'not created yet -- that is what `apply` does'}")
    print("\nReady. Next: plan")
    return 0


def cmd_export(args: argparse.Namespace) -> int:
    cfg = load_config(args.config, args.guild_id, args.site_base_url)
    state = load_state(args.state)
    client = DiscordClient(HttpTransport(load_token(args.token_file)))
    print(f"Exporting #{cfg.channel_name} threads to {args.out} ...")
    written = export_threads(client, cfg, state, args.out)
    print(f"\n{len(written)} thread(s) exported. Next:")
    print(f"  python tools/workbench/distill_feedback.py --export-dir {args.out}")
    return 0


# --------------------------------------------------------------------------- #
# Self-test
# --------------------------------------------------------------------------- #


class FakeTransport(Transport):
    """A small in-memory Discord that implements only what this tool calls."""

    def __init__(self, guild_id: str) -> None:
        self.guild_id = guild_id
        self.channels: dict[str, dict] = {}
        self.threads: dict[str, dict] = {}
        self.messages: dict[str, list[dict]] = {}
        self.invites: dict[str, dict] = {}
        self.bot_id = "999"
        self._next = 1000
        self.calls: list[tuple[str, str]] = []

    def _id(self) -> str:
        self._next += 1
        return str(self._next)

    def request(self, method: str, path: str, payload: Optional[dict] = None, query: Optional[dict] = None) -> Any:
        self.calls.append((method, path))
        if method == "GET" and path == "/users/@me":
            return {"id": self.bot_id, "username": "workbench-bot", "bot": True}
        if method == "GET" and path == f"/guilds/{self.guild_id}":
            return {"id": self.guild_id, "name": "Fixture Guild"}
        if method == "GET" and path == f"/guilds/{self.guild_id}/channels":
            return list(self.channels.values())
        if method == "GET" and path == f"/guilds/{self.guild_id}/threads/active":
            return {"threads": list(self.threads.values())}
        if method == "GET" and path.endswith("/threads/archived/public"):
            return {"threads": [], "has_more": False}
        if method == "POST" and path == f"/guilds/{self.guild_id}/channels":
            cid = self._id()
            tags = [dict(t, id=self._id()) for t in payload.get("available_tags", [])]
            channel = {
                "id": cid,
                "name": payload["name"],
                "type": payload["type"],
                "topic": payload.get("topic", ""),
                "available_tags": tags,
                "default_sort_order": payload.get("default_sort_order", 0),
                "flags": 0,
            }
            self.channels[cid] = channel
            return channel
        m = re.fullmatch(r"/channels/([0-9]+)", path)
        if m and method == "PATCH":
            target = self.channels.get(m.group(1)) or self.threads.get(m.group(1))
            if target is None:
                raise ToolError("HTTP 404 unknown channel")
            for key, value in payload.items():
                if key == "available_tags":
                    existing = {t["name"]: t for t in target.get("available_tags", [])}
                    target["available_tags"] = [dict(t, id=t.get("id") or existing.get(t["name"], {}).get("id") or self._id()) for t in value]
                else:
                    target[key] = value
            return target
        m = re.fullmatch(r"/channels/([0-9]+)/threads", path)
        if m and method == "POST":
            forum = self.channels[m.group(1)]
            if int(forum.get("flags") or 0) & CHANNEL_FLAG_REQUIRE_TAG and not payload.get("applied_tags"):
                raise ToolError("Discord POST failed: HTTP 400 {'code': 40067}")
            tid = self._id()
            thread = {
                "id": tid,
                "name": payload["name"],
                "parent_id": forum["id"],
                "applied_tags": payload.get("applied_tags", []),
                "flags": 0,
                "thread_metadata": {"archive_timestamp": "2026-07-29T00:00:00+00:00"},
            }
            self.threads[tid] = thread
            self.messages[tid] = [self._message(tid, tid, payload["message"]["content"])]
            return thread
        m = re.fullmatch(r"/channels/([0-9]+)/messages", path)
        if m and method == "POST":
            tid = m.group(1)
            msg = self._message(tid, self._id(), payload["content"])
            self.messages.setdefault(tid, []).append(msg)
            return msg
        if m and method == "GET":
            return list(reversed(self.messages.get(m.group(1), [])))
        m = re.fullmatch(r"/channels/([0-9]+)/messages/([0-9]+)", path)
        if m:
            found = next((x for x in self.messages.get(m.group(1), []) if x["id"] == m.group(2)), None)
            if method == "GET":
                if found is None:
                    raise ToolError("HTTP 404 unknown message")
                return found
            if method == "PATCH":
                found["content"] = payload["content"]
                return found
        m = re.fullmatch(r"/invites/([A-Za-z0-9-]+)", path)
        if m and method == "GET":
            found = self.invites.get(m.group(1))
            if found is None:
                raise ToolError("HTTP 404 unknown invite")
            return found
        m = re.fullmatch(r"/channels/([0-9]+)/invites", path)
        if m and method == "POST":
            channel = self.channels.get(m.group(1))
            if channel is None:
                raise ToolError("HTTP 404 unknown channel")
            code = f"perm{self._id()}"
            invite = {
                "code": code,
                "channel": {"id": channel["id"], "name": channel["name"], "type": channel["type"]},
                "guild": {"id": self.guild_id},
                "max_age": payload.get("max_age"),
                "expires_at": None if payload.get("max_age") == 0 else "2026-08-28T06:33:12+00:00",
            }
            self.invites[code] = invite
            return invite
        raise ToolError(f"FakeTransport: unhandled {method} {path}")

    def _message(self, channel_id: str, message_id: str, content: str, author_id: Optional[str] = None) -> dict:
        is_bot = author_id is None or author_id == self.bot_id
        return {
            "id": message_id,
            "type": 0,
            "timestamp": "2026-07-29T00:00:00+00:00",
            "edited_timestamp": None,
            "pinned": False,
            "content": content,
            "channel_id": channel_id,
            "author": {
                "id": author_id or self.bot_id,
                "username": "workbench-bot" if is_bot else f"member{author_id}",
                "discriminator": "0000",
                "bot": is_bot,
            },
            "attachments": [],
            "reactions": [],
            "mentions": [],
        }


def _raises(fn) -> bool:
    try:
        fn()
    except ToolError:
        return True
    return False


def run_self_test() -> bool:
    results: list[tuple[bool, str]] = []

    def check(condition: bool, description: str, detail: str = "") -> None:
        results.append((bool(condition), description if condition else f"{description}: {detail}"))

    cfg = load_config(DEFAULT_CONFIG)
    tools = load_workbench_tools()

    # --- repo parsing ------------------------------------------------------ #
    check(len(cfg.tags) == 8, "8 tags parsed from 07-forum-tags-setup.md", f"got {len(cfg.tags)}")
    check([t.name for t in cfg.tags][:4] == ["question", "bug", "claiming a task", "first task done"], "member-facing tags parsed in order", f"{[t.name for t in cfg.tags][:4]}")
    check(all(t.moderated for t in cfg.tags[4:]) and not any(t.moderated for t in cfg.tags[:4]), "status tags marked moderated, member-facing tags not")
    check("ladder: claimed" in {t.name for t in cfg.tags}, "the ladder tag is present")
    check(len(cfg.posts) == 7, "7 posts configured", f"got {len(cfg.posts)}")
    check(any(p.pinned for p in cfg.posts), "one post is marked pinned")
    check(cfg.guidelines.startswith("One post per topic"), "post guidelines parsed from the doc", cfg.guidelines[:40])
    check("Contributor" in (DISCORD_DOCS / "05-pinned-how-this-works.md").read_text(encoding="utf-8"), "pinned seed uses the current ladder wording (Contributor)")

    # --- denylist ---------------------------------------------------------- #
    check("00-announcement.md" in cfg.never_post, "the announcement is on the denylist")
    check(not any(p.seed_file in NEVER_POST for p in cfg.posts), "no configured post points at a denylisted seed")

    # --- placeholder guard ------------------------------------------------- #
    pre_deploy = [render_post(p, tools, None) for p in cfg.posts]
    blocked = [r for r in pre_deploy if r.blocked_reason]
    check(len(blocked) == 5, "5 posts blocked pre-deploy on unresolved URLs", f"got {[r.title for r in blocked]}")
    check(all("ONEPAGER-URL" in r.blocked_reason or "ACCESS-URL" in r.blocked_reason for r in blocked), "block reasons name the placeholder")

    post_deploy = [render_post(p, tools, "https://example.test") for p in cfg.posts]
    check(not any(r.blocked_reason for r in post_deploy), "every post renders clean once site_base_url is set", f"{[r.blocked_reason for r in post_deploy if r.blocked_reason]}")
    qp = next(r for r in post_deploy if r.spec.key == "quest-picker")
    check("https://example.test/workbench#quest-picker" in qp.body, "one-pager URL derived from the catalog anchor")
    mc = next(r for r in post_deploy if r.spec.key == "mcp-mod-channel")
    check("https://github.com/djcdevelopment/baseline/tree/main/network/mcp" in mc.body, "a not-published tool's access URL falls back to its public source href")
    check("https://example.test/workbench/downloads/quest-picker" in qp.body, "access URL derived from workbench.json access.href")
    check(not PLACEHOLDER_RE.search(qp.body), "no placeholder survives substitution")

    # --- chunking ---------------------------------------------------------- #
    for r in post_deploy:
        check(all(len(c) <= MESSAGE_LIMIT for c in r.chunks), f"'{r.title}' chunks fit Discord's limit", f"{[len(c) for c in r.chunks]}")
    multi = [r for r in post_deploy if len(r.chunks) > 1]
    check(bool(multi), "at least one long seed splits into multiple messages")
    check(all("\n\n".join(c for c in r.chunks) for r in post_deploy), "chunks are non-empty")
    long_body = "\n\n".join(f"para {i} " + "x" * 300 for i in range(20))
    check(all(len(c) <= 500 for c in chunk_content(long_body, 500)), "chunker respects an arbitrary limit")
    check("".join(chunk_content("y" * 4500, 2000)) == "y" * 4500, "a single overlong line is hard-split without loss")

    # --- plan / apply against a simulated guild ---------------------------- #
    fake = FakeTransport(cfg.guild_id)
    client = DiscordClient(fake)
    cfg_live = load_config(DEFAULT_CONFIG, site_base_override="https://example.test")
    state: dict = {"schema_version": 1, "posts": {}}

    live0 = read_live_state(client, cfg_live, state)
    plan0 = build_plan(cfg_live, live0, tools)
    check(live0.channel is None, "greenfield: no forum channel found")
    check(any(a.apply == "create_channel" for a in plan0.actions), "plan creates the forum channel")
    check(sum(1 for a in plan0.actions if isinstance(a.apply, tuple) and a.apply[0] == "create_post") == 7, "plan creates 7 posts")
    check(not plan0.blocked, "nothing blocked once URLs resolve", f"{[a.detail for a in plan0.blocked]}")
    receipt = render_receipt(plan0, offline=False, state_path=DEFAULT_STATE)
    check("00-announcement.md" in receipt and "denylist" in receipt, "receipt states the announcement is never posted")
    check(plan0.plan_hash in receipt, "receipt carries the plan hash")

    state = apply_plan(plan0, client, state, log=lambda *a, **k: None, pause=0)
    forum = next(c for c in fake.channels.values() if c["name"] == cfg.channel_name)
    check(int(forum["flags"]) & CHANNEL_FLAG_REQUIRE_TAG != 0, "required tags left ON after apply", f"flags={forum['flags']}")
    check(len(forum["available_tags"]) == 8, "8 tags live on the channel", f"{len(forum['available_tags'])}")
    check(forum["topic"] == cfg.guidelines, "post guidelines written to the channel")
    check(len(fake.threads) == 7, "7 forum posts created", f"{len(fake.threads)}")
    pinned = [t for t in fake.threads.values() if int(t.get("flags") or 0) & CHANNEL_FLAG_PINNED]
    check(len(pinned) == 1 and pinned[0]["name"].startswith("How this works"), "the guideline post is pinned", f"{[t['name'] for t in pinned]}")
    check(all(t["applied_tags"] for t in fake.threads.values() if t["name"] != pinned[0]["name"]), "every tool post carries a tag")
    check(len(state["posts"]) == 7, "state records 7 posts")
    check(all(entry["url"].startswith("https://discord.com/channels/") for entry in state["posts"].values()), "state records thread URLs")

    # --- idempotency ------------------------------------------------------- #
    live1 = read_live_state(client, cfg_live, state)
    plan1 = build_plan(cfg_live, live1, tools)
    check(not plan1.changes, "second plan is a no-op (idempotent)", f"{[(a.verb, a.target, a.detail) for a in plan1.changes]}")

    # --- state loss + a member reply must not produce duplicate messages ---- #
    two_chunk_id = next(tid for tid, t in fake.threads.items() if t["name"] == "Recoverable pieces")
    fake.messages[two_chunk_id].append(fake._message(two_chunk_id, fake._id(), "ran it, it broke", author_id="77"))
    forgotten: dict = {"schema_version": 1, "posts": {}}
    plan_lost = build_plan(cfg_live, read_live_state(client, cfg_live, forgotten), tools)
    check(
        not plan_lost.changes,
        "a lost state file rediscovers posts instead of appending duplicates",
        f"{[(a.verb, a.target, a.detail) for a in plan_lost.changes]}",
    )
    check(len(fake.messages[two_chunk_id]) == 3, "the member reply is still there, untouched", f"{len(fake.messages[two_chunk_id])}")

    # --- drift: an edited pin and a removed tag come back ------------------ #
    pin_thread = pinned[0]
    fake.messages[pin_thread["id"]][0]["content"] = "someone edited this by hand"
    forum["available_tags"] = [t for t in forum["available_tags"] if t["name"] != "resolved"]
    forum["topic"] = "drifted guidelines"
    live2 = read_live_state(client, cfg_live, state)
    plan2 = build_plan(cfg_live, live2, tools)
    check(any(isinstance(a.apply, tuple) and a.apply[0] == "update_post" for a in plan2.actions), "edited pin detected as content drift")
    check(any(a.apply == "sync_tags" for a in plan2.actions), "removed tag detected")
    check(any(a.apply == "topic" for a in plan2.actions), "changed guidelines detected")
    apply_plan(plan2, client, state, log=lambda *a, **k: None, pause=0)
    check(fake.messages[pin_thread["id"]][0]["content"].startswith("**How this works"), "pin content restored from the repo")
    check(len(forum["available_tags"]) == 8, "missing tag restored without dropping the others", f"{len(forum['available_tags'])}")
    plan3 = build_plan(cfg_live, read_live_state(client, cfg_live, state), tools)
    check(not plan3.changes, "converges back to a no-op after repair", f"{[(a.verb, a.detail) for a in plan3.changes]}")

    # --- a hand-made post is reported, never silently mangled -------------- #
    manual_id = fake._id()
    fake.threads[manual_id] = {"id": manual_id, "name": "Quest picker", "parent_id": forum["id"], "applied_tags": [], "flags": 0, "thread_metadata": {"archive_timestamp": "2026-07-29T00:00:00+00:00"}}
    fake.messages[manual_id] = [fake._message(manual_id, manual_id, "pasted by a human", author_id="42")]
    saved = state["posts"]["quest-picker"]
    state["posts"]["quest-picker"] = dict(saved, thread_id=manual_id, message_ids=[manual_id])
    del fake.threads[saved["thread_id"]]
    plan4 = build_plan(cfg_live, read_live_state(client, cfg_live, state), tools)
    check(any(a.verb == "BLOCKED" and "created by hand" in a.detail for a in plan4.actions), "a hand-pasted post is blocked, not silently skipped", f"{[(a.verb, a.detail[:40]) for a in plan4.actions]}")
    state["posts"]["quest-picker"] = saved
    fake.threads[saved["thread_id"]] = {"id": saved["thread_id"], "name": "Quest picker", "parent_id": forum["id"], "applied_tags": [forum["available_tags"][0]["id"]], "flags": 0, "thread_metadata": {"archive_timestamp": "2026-07-29T00:00:00+00:00"}}
    del fake.threads[manual_id]

    # --- export shape feeds distill_feedback.py ---------------------------- #
    import tempfile

    thread_id = next(tid for tid, t in fake.threads.items() if t["name"] == "Quest picker")
    fake.messages[thread_id].append(fake._message(thread_id, fake._id(), "How do I point this at my own guild?", author_id="77"))
    with tempfile.TemporaryDirectory(prefix="workbench-discord-selftest-") as tmp:
        out = Path(tmp) / "exports"
        written = export_threads(client, cfg_live, state, out, log=lambda *a, **k: None)
        check(len(written) == len(fake.threads), "one export file per thread", f"{len(written)} vs {len(fake.threads)}")
        docs = [json.loads(p.read_text(encoding="utf-8")) for p in written]
        doc = next(d for d in docs if d["messages"])
        check({"guild", "channel", "dateRange", "exportedAt", "messageCount", "messages"} <= set(doc), "export has DiscordChatExporter's top-level keys")
        check({"id", "type", "categoryId", "category", "name", "topic"} <= set(doc["channel"]), "export channel block matches the exporter shape")
        check(doc["channel"]["category"] == cfg.channel_name, "thread export names the parent forum as its category")
        check(doc["messageCount"] == len(doc["messages"]), "messageCount matches what was written")
        check(sum(len(d["messages"]) for d in docs) == 2, "only the two member messages reach the export; the bot's own posts stay out", f"{sum(len(d['messages']) for d in docs)}")
        check(all(not m["author"]["isBot"] for d in docs for m in d["messages"]), "nothing bot-authored survives into an export file")
        msg = doc["messages"][0]
        check({"id", "type", "timestamp", "content", "author"} <= set(msg), "export message carries the fields the distiller reads")
        check(msg["type"] in CONTENT_TYPES_FOR_DISTILLER, "message type maps to a DiscordChatExporter kind the distiller accepts", msg["type"])
        check({"id", "name", "discriminator", "nickname", "isBot"} <= set(msg["author"]), "export author block matches the exporter shape")

        sys.path.insert(0, str(TOOL_DIR.parent))
        try:
            import distill_feedback  # noqa: PLC0415 -- imported here so the self-test proves the real contract

            out_jsonl = Path(tmp) / "candidates.jsonl"
            rc = distill_feedback.main(["--export-dir", str(out), "--output", str(out_jsonl)])
            check(rc == 0, "distill_feedback.py consumes this export without error", f"exit {rc}")
            rows = [json.loads(x) for x in out_jsonl.read_text(encoding="utf-8").splitlines() if x.strip()] if out_jsonl.exists() else []
            check(len(rows) == 2, "the distiller produced one candidate per member message and nothing else", f"{rows}")
            check({r["kind"] for r in rows} == {"question", "bug"}, "both heuristics fired on the right messages", f"{[(r['kind'], r['excerpt']) for r in rows]}")
            check(any(r["thread"] == "Quest picker" and r["kind"] == "question" for r in rows), "the candidate carries its thread title", f"{rows}")
            check(all(r["author"] != "workbench-bot" for r in rows), "no candidate is attributed to the bot", f"{[r['author'] for r in rows]}")
        except ImportError as exc:  # pragma: no cover
            check(False, "distill_feedback.py importable for the contract test", str(exc))
        finally:
            sys.path.pop(0)

    # --- token files: every shape an operator actually produces ------------ #
    # Deliberately NOT shaped like a real bot token. GitHub push protection scans for
    # the genuine pattern, and a realistic-looking fixture blocks the push -- it did,
    # on 2026-07-29. parse_token_file only cares about length and the absence of
    # spaces, so a self-labelling string exercises every path just as well. Do not
    # "improve" this back into something that looks authentic.
    fake_token = "SELF-TEST-NOT-A-REAL-TOKEN-" + "0123456789" * 3
    with tempfile.TemporaryDirectory(prefix="workbench-token-selftest-") as tmp:
        tdir = Path(tmp)
        cases = [
            ("bare token", fake_token.encode()),
            ("bare token, trailing CRLF", fake_token.encode() + b"\r\n"),
            ("BOM + CRLF (PS 5.1 -Encoding utf8)", b"\xef\xbb\xbf" + fake_token.encode() + b"\r\n"),
            ("KEY= form, no quotes", f"KEY={fake_token}\r\n".encode()),
            ("DISCORD_TOKEN= form", f"DISCORD_TOKEN={fake_token}\n".encode()),
            ("quoted value", f'WORKBENCH_DISCORD_TOKEN="{fake_token}"\n'.encode()),
            ("export prefix", f"export TOKEN={fake_token}\n".encode()),
            ("pasted Authorization header", f"KEY=Bot {fake_token}\n".encode()),
            ("comments and blank lines", f"# my bot\n\nKEY={fake_token}\n\n".encode()),
            ("several keys, one recognised", f"APP_ID=123456\nKEY={fake_token}\nNOTE=x\n".encode()),
        ]
        for label, blob in cases:
            f = tdir / "t.env"
            f.write_bytes(blob)
            try:
                got = parse_token_file(f)
            except ToolError as exc:
                got = f"<error: {exc}>"
            check(got == fake_token, f"token file parsed: {label}", f"got {got!r}")

        f = tdir / "t.env"
        f.write_bytes(b"KEY=\n")
        check(_raises(lambda: parse_token_file(f)), "an empty value is rejected, not returned as ''")
        f.write_bytes(b"APP_ID=123456\nSECRET_THING=abcdefghijklmnopqrstuvwxyz\n")
        check(_raises(lambda: parse_token_file(f)), "several unrecognised keys is an error, not a guess")
        f.write_bytes(b"KEY=paste_your_token_here\n")
        check(_raises(lambda: parse_token_file(f)), "an unreplaced placeholder is rejected on length")
        f.write_bytes(b"KEY=1531911987074957442\n")
        check(_raises(lambda: parse_token_file(f)), "an id pasted where the token goes is rejected")
        check(len(fake_token) >= 50, "the self-test's own fixture clears the length floor", f"{len(fake_token)}")

    in_repo = TOOL_DIR / "selftest-scratch.token"
    in_repo.write_text("x" * 60, encoding="utf-8")
    try:
        message = ""
        try:
            load_token(in_repo)
        except ToolError as exc:
            message = str(exc)
        check("inside the repository" in message, "a token file inside the repo is refused outright", message or "no error raised")
    finally:
        in_repo.unlink(missing_ok=True)

    # --- permissions ------------------------------------------------------- #
    check(PERMISSIONS_INT == sum(PERMISSIONS.values()), "permission integer is the sum of the named bits")
    check("ADMINISTRATOR" not in PERMISSIONS and PERMISSIONS_INT & (1 << 3) == 0, "no Administrator bit")
    check(PERMISSIONS_INT & (1 << 17) == 0, "no Mention Everyone bit")
    check(str(PERMISSIONS_INT) in invite_url("123"), "invite URL carries the computed permission integer")

    # --- guild-invite maintenance ------------------------------------------ #
    finv = FakeTransport(cfg.guild_id)
    fclient = DiscordClient(finv)
    general_id = finv._id()
    finv.channels[general_id] = {"id": general_id, "name": "general", "type": 0, "available_tags": [], "flags": 0}
    finv.invites["OLDCODE"] = {
        "code": "OLDCODE",
        "channel": {"id": general_id, "name": "general", "type": 0},
        "guild": {"id": cfg.guild_id},
        "expires_at": "2026-08-28T06:33:12+00:00",
    }
    inv_state = {"invite_href": "https://discord.gg/OLDCODE", "invite_expires_at": "2026-08-28T06:33:12+00:00"}
    new_state, _msg, changed = ensure_permanent_invite(fclient, inv_state)
    check(changed and new_state["invite_href"] != inv_state["invite_href"], "an expiring invite is replaced, not edited")
    new_code = new_state["invite_href"].rsplit("/", 1)[-1]
    check(finv.invites.get(new_code, {}).get("expires_at") is None, "the replacement invite never expires")
    check(finv.invites.get(new_code, {}).get("channel", {}).get("id") == general_id, "the replacement lands on the same channel")
    check(new_state["invite_expires_at"] is None, "state records the null expiry")
    check("OLDCODE" in finv.invites, "the old invite is left to lapse, never revoked")
    _same_state, _msg2, changed2 = ensure_permanent_invite(fclient, new_state)
    check(not changed2, "a permanent invite with agreeing state is a no-op")

    ok = True
    for passed, description in results:
        if not passed:
            ok = False
        print(f"[{'PASS' if passed else 'FAIL'}] {description}")
    print(f"\n{sum(1 for p, _ in results if p)}/{len(results)} assertions passed")
    return ok


# The three DiscordChatExporter kinds distill_feedback.py treats as community content.
CONTENT_TYPES_FOR_DISTILLER = {"Default", "Reply", "ThreadStarterMessage"}


def _make_console_utf8_safe() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError, OSError):
            pass


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="workbench_discord.py",
        description="Provision and maintain the #workbench Discord forum from this repo. Structure only, never conversation.",
    )
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG, help=f"provisioning config (default: {_rel(DEFAULT_CONFIG)})")
    parser.add_argument("--state", type=Path, default=DEFAULT_STATE, help=f"state file (default: {_rel(DEFAULT_STATE)})")
    parser.add_argument("--guild-id", default=None, help="override the config's guild id")
    parser.add_argument("--site-base-url", default=None, help="e.g. https://comfy-p7.duckdns.org -- fills the catalog/download links after the deploy")
    parser.add_argument(
        "--token-file",
        type=Path,
        default=None,
        help="file holding the bot token (bare, or a KEY=<token> line). Must be outside this repo. "
        f"Default search: {', '.join(str(p) for p in DEFAULT_TOKEN_FILES)}",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("whoami", help="prove the token works and the bot can see the server")
    p.set_defaults(func=cmd_whoami)

    p = sub.add_parser("check", help="repo-only invariants; no network, no token")
    p.set_defaults(func=cmd_check)

    p = sub.add_parser("invite", help="print the OAuth2 invite URL and its permission set")
    p.add_argument("--app-id", default=None)
    p.set_defaults(func=cmd_invite)

    p = sub.add_parser("guild-invite", help="the guild join invite the page carries; --ensure-permanent replaces an expiring one")
    p.add_argument("--ensure-permanent", action="store_true", help="mint a never-expiring invite on the same channel and record it")
    p.add_argument("--yes", action="store_true", help="required to mint: this creates a live access artifact")
    p.set_defaults(func=cmd_guild_invite)

    p = sub.add_parser("plan", help="dry run: compute changes and write an approval receipt")
    p.add_argument("--offline", action="store_true", help="predict against an empty server; no token needed")
    p.add_argument("--receipt-out", type=Path, default=None)
    p.set_defaults(func=cmd_plan)

    p = sub.add_parser("apply", help="converge Discord to the repo (requires --yes)")
    p.add_argument("--yes", action="store_true", help="required: this writes to a live community server")
    p.add_argument("--expect-plan", default=None, help="plan hash from the approved receipt; refuses if it changed")
    p.add_argument("--offline", action="store_true", help=argparse.SUPPRESS)
    p.set_defaults(func=cmd_apply)

    p = sub.add_parser("export", help="export forum threads as DiscordChatExporter-shaped JSON")
    p.add_argument("--out", type=Path, required=True, help="output directory (keep it outside the repo or gitignored)")
    p.add_argument("--offline", action="store_true", help=argparse.SUPPRESS)
    p.set_defaults(func=cmd_export)

    p = sub.add_parser("self-test", help="offline test suite against a simulated guild")
    p.set_defaults(func=lambda args: 0 if run_self_test() else 1)
    return parser


def main(argv: Optional[list[str]] = None) -> int:
    _make_console_utf8_safe()
    args = build_arg_parser().parse_args(argv)
    try:
        return args.func(args)
    except ToolError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
