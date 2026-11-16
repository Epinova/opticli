#!/usr/bin/env python3
"""Checks the CLI's project commands (`opticli projects ...`, `--in-project`/`--ready` on the writes, `project` in plans)
against a running site, end to end, through the opticli command line.

usage: check-cli.py <site directory> [--opticli <command>] [--keep]

Run it where the CLI runs (inside the `dev` distrobox: `distrobox enter dev -- python3 tests/fixtures/projects/check-cli.py
~/demo/SpikeProj12`). The site is the one check-endpoints.py (next to this script) runs against: a copy of the edge-case
site, CMS 12 or 13, served by `opticli serve` with this checkout's build and ProjectsFixture.cs copied into it.
--opticli is the command that runs this checkout's CLI (default: ~/demo/tools-spike/opticli, a wrapper around
src/OptiCli/bin/Debug/net8.0/opticli.dll; build the worktree first). The CLI runs in the site directory; the site's
port (for the fixture's hand-over endpoint) comes from serve's state file. It makes a scratch page below the approval
root's parent with pages and projects of its own, then moves the scratch page to the recycle bin and deletes its
projects (--keep leaves them). The terminal prompt of `projects publish` is answered through a pseudo-terminal.
Exits 1 if a check failed.

Written for phase 2 of the CMS projects plan; phase 4's integration tests replace it.
"""
import glob
import json
import os
import pty
import select
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timedelta, timezone

APPROVAL_ROOT = "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02"  # the edge-case plan's "Approval root"

failures = []
passed = 0


def check(condition, what, detail=None):
    global passed
    if condition:
        passed += 1
        print(f"  ok    {what}")
    else:
        failures.append(what)
        print(f"  FAIL  {what}" + (f"\n        {json.dumps(detail)[:700]}" if detail is not None else ""))


def step(title):
    print(f"\n== {title}")


class Cli:
    def __init__(self, site, command):
        self.site = os.path.abspath(os.path.expanduser(site))
        self.command = os.path.expanduser(command)
        name = os.path.basename(os.path.normpath(self.site))
        states = glob.glob(os.path.expanduser(f"~/.local/state/opticli/{name}-*.json"))
        if not states:
            sys.exit(f"No serve state for {name}: run `opticli serve` in {self.site} first.")
        self.port = json.load(open(states[0]))["port"]
        self.scratch = os.path.join(os.path.expanduser("~/.cache"), f"opticli-check-cli-{os.getpid()}")
        os.makedirs(self.scratch, exist_ok=True)

    def run(self, *args):
        """(exit code, envelope) of one run with --json."""
        process = subprocess.run([self.command, *map(str, args), "--json"], cwd=self.site, capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=600)
        try:
            envelope = json.loads(process.stdout)
        except json.JSONDecodeError:
            envelope = {"ok": False, "error": {"code": "unparsed", "message": process.stdout[-500:] + process.stderr[-500:]}}
        return process.returncode, envelope

    def ok(self, *args):
        code, envelope = self.run(*args)
        if code != 0 or not envelope.get("ok"):
            raise AssertionError(f"opticli {' '.join(map(str, args))} failed ({code}): {json.dumps(envelope.get('error'))[:600]}")
        return envelope["data"], envelope["meta"]

    def fails(self, *args):
        code, envelope = self.run(*args)
        return code, envelope.get("error") or {}

    def terminal(self, answer, *args):
        """Runs opticli on a pseudo-terminal, answers its [y/N] question, and returns (exit code, everything it printed)."""
        pid, fd = pty.fork()
        if pid == 0:
            os.chdir(self.site)
            os.execvp(self.command, [self.command, *map(str, args), "--json"])
        output, answered, deadline = b"", False, time.time() + 600
        while time.time() < deadline:
            ready, _, _ = select.select([fd], [], [], 1)
            if ready:
                try:
                    chunk = os.read(fd, 4096)
                except OSError:
                    break
                if not chunk:
                    break
                output += chunk
                if not answered and b"[y/N]" in output:
                    os.write(fd, (answer + "\n").encode())
                    answered = True
        _, status = os.waitpid(pid, 0)
        return os.waitstatus_to_exitcode(status), output.decode(errors="replace")

    def plan(self, name, plan):
        path = os.path.join(self.scratch, name)
        with open(path, "w") as file:
            json.dump(plan, file)
        return path

    def hand_over(self, version, user):
        """Saves a version again as another user (the edge-case fixture's loopback endpoint)."""
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/opticli-fixture/hand-over?version={version}&user={user}", data=b"", method="POST")
        urllib.request.urlopen(request, timeout=60).close()


def json_in(text):
    """The JSON envelope in what a run on a terminal printed (after the prompt)."""
    start = text.rfind('{"ok"')
    try:
        return json.loads(text[start:].strip()) if start >= 0 else {}
    except json.JSONDecodeError:
        return {}


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    args = sys.argv[1:]
    opticli = args[args.index("--opticli") + 1] if "--opticli" in args else "~/demo/tools-spike/opticli"
    keep = "--keep" in args
    cli = Cli(args[0], opticli)
    stamp = datetime.now().strftime("%H%M%S")
    edge = cli.ok("get", APPROVAL_ROOT)[0]["parent"]
    projects = []

    def page(name, parent, publish=True):
        data, _ = cli.ok("create", parent, "--type", "StandardPage", "--name", name, *(["--publish"] if publish else []))
        return data["ref"]

    def project_id(name):
        listed, _ = cli.ok("projects")
        return next((p["id"] for p in listed if p["name"] == name), None)

    container = page(f"opticli-cli scratch {stamp}", edge)
    print(f"Scratch page {container} below {edge}; CLI {cli.command} in {cli.site}")
    try:
        step("usage, before the site is asked")
        code, error = cli.fails("set", container, "TeaserText=x", "--ready")
        check(code == 1 and error.get("code") == "usage", "--ready without --in-project is a usage error", error)
        code, error = cli.fails("set", container, "TeaserText=x", "--in-project", "Any", "--publish")
        check(code == 1 and "isn't published or scheduled by itself" in error.get("message", ""), "--in-project with --publish is a usage error", error)
        code, error = cli.fails("projects", "ready", "Any")
        check(code == 1 and error.get("code") == "usage", "projects ready needs refs or --all", error)

        step("projects create")
        name = f"cli main {stamp}"
        dry, _ = cli.ok("projects", "create", name, "--dry-run")
        check(dry["dryRun"] and dry["project"]["id"] == 0 and dry["editorUi"] in ("projectMode", "gadget"), "dry-run create reports it and the editor UI", dry)
        made, meta = cli.ok("projects", "create", name)
        main_id = made["project"]["id"]
        projects.append(main_id)
        check(made["created"] and made["project"]["status"] == "active" and meta["source"] == "agent", "create makes an active project", made)
        code, error = cli.fails("projects", "create", name.upper())
        check(code == 5 and error.get("code") == "conflict", "a name an unpublished project has is a conflict (exit 5)", error)

        step("saves with --in-project")
        a, b, c = (page(f"opticli-cli {x} {stamp}", container) for x in "abc")
        saved, _ = cli.ok("set", a, "TeaserText=A1", "--in-project", name, "--ready")
        check(saved["project"]["item"] == "added" and saved["project"]["ready"] and saved["status"] == "checkedIn", "set --in-project --ready adds the draft, ready", saved.get("project"))
        saved, _ = cli.ok("set", b, "TeaserText=B1", "--in-project", main_id)
        check(saved["project"]["item"] == "added" and not saved["project"]["ready"], "set --in-project <id> adds a draft that isn't ready", saved.get("project"))
        again, _ = cli.ok("set", b, "TeaserText=B2", "--in-project", main_id)
        check(again["project"]["item"] == "replaced" and again["project"]["replaced"] == saved["version"], "a second save replaces the item", again.get("project"))
        dry, _ = cli.ok("set", c, "TeaserText=C1", "--in-project", name, "--ready", "--dry-run")
        check(dry["dryRun"] and dry["project"]["item"] == "added" and dry["project"]["ready"], "a dry run reports what it would do to the project", dry.get("project"))
        outside, _ = cli.ok("set", b, "TeaserText=B3")
        check(any("isn't in it" in v["message"] for v in outside.get("validation") or []), "a save without a project warns that the content is in one", outside.get("validation"))
        cli.ok("set", b, "TeaserText=B4", "--in-project", main_id)
        code, error = cli.fails("set", c, "TeaserText=x", "--in-project", f"no such project {stamp}")
        check(code == 2 and error.get("code") == "not_found", "an unknown project is not_found (exit 2)", error)

        step("projects (read)")
        listed, _ = cli.ok("projects")
        mine = next(p for p in listed if p["id"] == main_id)
        check(mine["items"] == 2 and mine["ready"] == 1, "the list counts items and ready ones", mine)
        items, _ = cli.ok("projects", name)
        statuses = {i["ref"]: i["status"] for i in items}
        check(statuses.get(a) == "checkedIn" and statuses.get(b) == "checkedOut", "projects <name> shows each item's status", items)

        step("projects publish: what is ready, other people's drafts, the prompt")
        dry, _ = cli.ok("projects", "publish", name, "--dry-run")
        live = [i["content"]["id"] for i in dry.get("published") or []]
        behind = [i["content"]["id"] for i in dry.get("notReady") or []]
        check(live == [int(a)] and behind == [int(b)], "the dry run lists what goes live and what stays behind", dry)
        colleague, _ = cli.ok("set", c, "MetaDescription=theirs")
        cli.hand_over(colleague["version"], "cli-colleague")
        cli.ok("set", c, "TeaserText=ours", "--in-project", name, "--ready")
        code, error = cli.fails("projects", "publish", name)
        problems = [i for i in (error.get("details", {}).get("project") or {}).get("published") or [] if i.get("problem")]
        check(code == 5 and error["details"].get("reason") == "pendingDraft" and "--include-draft" in error.get("hint", "") and len(problems) == 1,
              "a colleague's draft in a ready item stops the publish (exit 5, details.project)", error)
        code, printed = cli.terminal("n", "projects", "publish", name)
        envelope = json_in(printed)
        check(code == 5 and "Stays behind, not ready (1)" in printed and "cli-colleague" in printed and "Not published, as answered" in json.dumps(envelope),
              "on a terminal it shows what goes live, what stays and whose changes, and a no publishes nothing", printed[-1500:])
        removed, _ = cli.ok("projects", "remove", name, c)
        check([i["outcome"] for i in removed["items"]] == ["removed"], "projects remove takes the item out", removed)
        notify = page(f"opticli-pp-notify cli {stamp}", container)
        cli.ok("set", notify, "TeaserText=N1", "--in-project", name, "--ready")
        code, printed = cli.terminal("y", "projects", "publish", name)
        envelope = json_in(printed)
        data = envelope.get("data") or {}
        live = sorted(i["content"]["id"] for i in data.get("published") or [])
        check(code == 0 and data.get("project", {}).get("status") == "published" and live == sorted([int(a), int(notify)]),
              "a yes publishes the ready items, and the project is published", printed[-1500:])
        check(any("siteChanges" in w for w in envelope.get("meta", {}).get("warnings") or []), "the site's own changes are in meta.warnings", envelope.get("meta"))
        b_now, _ = cli.ok("get", b, "--version", "latest", "--fields", "name")
        check(b_now["status"] == "checkedOut", "the item that wasn't ready is still a draft", b_now.get("status"))
        code, error = cli.fails("set", b, "TeaserText=late", "--in-project", main_id)
        check(code == 5 and error["details"].get("reason") == "projectNotActive", "a published project takes no more saves (exit 5)", error)

        step("schedule, reactivate, delete")
        later = f"cli later {stamp}"
        later_id = cli.ok("projects", "create", later)[0]["project"]["id"]
        projects.append(later_id)
        code, error = cli.fails("projects", "publish", later)
        check(code == 5 and error["details"].get("reason") == "nothingReady", "nothing ready is a conflict (exit 5)", error)
        cli.ok("set", b, "TeaserText=B5", "--in-project", later)
        readied, _ = cli.ok("projects", "ready", later, "--all")
        check([i["outcome"] for i in readied["items"]] == ["ready"] and readied["project"]["ready"] == 1, "projects ready --all marks the drafts ready", readied)
        at = (datetime.now(timezone.utc) + timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ")
        scheduled, _ = cli.ok("projects", "publish", later, "--at", at)
        check(scheduled["project"]["status"] == "delayedPublished" and len(scheduled.get("scheduled") or []) == 1, "--at schedules the ready items", scheduled)
        code, error = cli.fails("set", b, "TeaserText=B6", "--in-project", later)
        check(code == 5 and "projects reactivate" in error.get("hint", ""), "a scheduled project points at reactivate", error)
        code, error = cli.fails("projects", "delete", later, "--dry-run")
        check(code == 5 and "reactivate" in error.get("hint", ""), "delete --dry-run refuses a scheduled project", error)
        back, _ = cli.ok("projects", "reactivate", later)
        check(back["reactivated"] and back["project"]["status"] == "active" and [i["outcome"] for i in back["items"]] == ["unscheduled"],
              "reactivate cancels it, and the item is a draft again", back)
        preview, meta = cli.ok("projects", "delete", later, "--dry-run")
        check(preview["dryRun"] and not preview["deleted"] and meta["source"] == "db" and len(preview["items"]) == 1, "delete --dry-run checks it in the database", preview)
        deleted, _ = cli.ok("projects", "delete", later)
        check(deleted["deleted"], "delete deletes the project", deleted)
        projects.remove(later_id)
        check(project_id(later) is None, "the project is gone", None)

        step("approval sequence")
        guarded = page(f"opticli-cli guarded {stamp}", APPROVAL_ROOT, publish=False)
        review = f"cli review {stamp}"
        projects.append(cli.ok("projects", "create", review)[0]["project"]["id"])
        code, error = cli.fails("set", guarded, "TeaserText=G1", "--in-project", review, "--ready")
        check(code == 3 and error["details"].get("reason") == "approvalSequence", "--ready under an approval sequence is refused (exit 3)", error)
        sent, _ = cli.ok("set", guarded, "TeaserText=G1", "--in-project", review, "--ready", "--request-approval")
        check(sent.get("approvalRequested") and sent["project"]["item"] == "added" and not sent["project"]["ready"], "--request-approval sends it for review, in the project", sent)

        step("plans")
        plan_name = f"cli plan {stamp}"
        path = cli.plan("plan.json", {"project": plan_name, "operations": [
            {"op": "create", "id": "page", "parent": container, "type": "StandardPage", "name": f"opticli-cli planned {stamp}", "ready": True},
            {"op": "set", "ref": "$page", "properties": {"TeaserText": "planned"}},
            {"op": "set", "ref": c, "properties": {"TeaserText": "from the plan"}, "ready": True},
            {"op": "create", "parent": container, "type": "StandardPage", "name": f"opticli-cli outside {stamp}", "project": None},
        ]})
        dry, _ = cli.ok("apply", path, "--dry-run")
        check(dry["projects"] == [{"name": plan_name, "status": "toCreate"}] and any("doesn't exist yet" in w for w in dry["operations"][0].get("warnings") or []),
              "a dry run says the run makes the project", dry.get("projects"))
        check(project_id(plan_name) is None, "a dry run makes no project", None)
        run, _ = cli.ok("apply", path)
        made = run["projects"][0]
        projects.append(made["id"])
        page_ref = run["created"]["page"]
        check(made["status"] == "created" and made.get("undo", "").startswith("opticli projects delete"), "the run made the project", made)
        check(run["operations"][1].get("savedInto") is not None and run["operations"][3]["result"].get("project") is None,
              "later steps save into the draft, and project: null leaves a step out", run["operations"])
        items, _ = cli.ok("projects", made["id"])
        statuses = {i["ref"]: i["status"] for i in items}
        check(statuses == {page_ref: "checkedIn", c: "checkedIn"}, "both drafts are in the project and ready (the planned page once the plan ran)", items)
        check(page_ref + "_" in "".join(made.get("markedReady") or []), "data.projects says what was marked ready at the end", made)
        again, _ = cli.ok("apply", path, "--dry-run")
        check(again["projects"] == [{"id": made["id"], "name": plan_name, "status": "existing"}], "run again, the plan finds the project by name", again.get("projects"))
        bad = cli.plan("bad.json", {"project": plan_name, "operations": [{"op": "set", "ref": c, "properties": {"TeaserText": "x"}, "publish": True}]})
        code, error = cli.fails("apply", bad, "--dry-run")
        check(code == 1 and "\"project\": null" in error.get("message", ""), "a step that publishes under the plan's project is a plan problem", error)
        code, error = cli.fails("apply", path, "--publish", "--dry-run")
        check(code == 1 and "--publish" in error.get("message", ""), "apply --publish with a project is a usage error", error)
    finally:
        if not keep:
            for project in projects:
                cli.run("projects", "reactivate", project)
                cli.run("projects", "delete", project)
            cli.run("delete", container, "--ignore-references")
    print(f"\n{passed} passed, {len(failures)} failed")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
