#!/usr/bin/env python3
"""Checks the site agent's project routes (POST /v1/projects..., and `project`/`ready` on saves) against a running site.

usage: check-endpoints.py <site directory> [--keep | --disabled]

The site is a copy of the edge-case site (tests/fixtures/edge-cases), CMS 12 or 13, served by `opticli serve` with
this checkout's build, and with ProjectsFixture.cs (next to this script) copied into it, which publishes a page of its
own when a page named "opticli-pp-notify ..." is published. The token and port come from serve's state file
(~/.local/state/opticli/<site folder name>-*.json). It makes a scratch page below the approval root's parent, with
pages and projects of its own, checks each step, then moves the scratch page to the recycle bin and deletes its
projects (--keep leaves them). Exits 1 if a check failed.

--disabled checks a site served with project mode and the Projects gadget both off instead
(EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=false and EPiServer__CmsUI__ProjectUI__ProjectGadgetEnabled=false in
serve's environment; CMS 13 has project mode off already): every project write is refused (projectsDisabled).

Written for phase 1 of the CMS projects plan; phase 4's integration tests replace it.
"""
import base64
import glob
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

APPROVAL_ROOT = "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02"  # the edge-case plan's "Approval root"
MINIMAL_PDF = b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"


class Site:
    def __init__(self, directory):
        name = os.path.basename(os.path.normpath(directory))
        states = glob.glob(os.path.expanduser(f"~/.local/state/opticli/{name}-*.json"))
        if not states:
            sys.exit(f"No serve state for {name}: run `opticli serve` in {directory} first.")
        state = json.load(open(states[0]))
        self.token, self.port = state["token"], state["port"]
        self.base = f"http://127.0.0.1:{self.port}"

    def call(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(f"{self.base}/_opticli/v1{path}", data=data, method=method,
                                         headers={"X-OptiCli-Token": self.token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            return error.code, json.load(error)

    def ok(self, method, path, body=None):
        status, envelope = self.call(method, path, body)
        if not envelope.get("ok"):
            raise AssertionError(f"{method} {path} failed ({status}): {json.dumps(envelope.get('error'))}")
        return envelope["data"]

    def error(self, method, path, body=None):
        status, envelope = self.call(method, path, body)
        if envelope.get("ok"):
            raise AssertionError(f"{method} {path} succeeded, but should have failed: {json.dumps(envelope['data'])[:300]}")
        return status, envelope["error"]

    def hand_over(self, version, user):
        """Saves a version again as another user (the edge-case fixture's loopback endpoint)."""
        request = urllib.request.Request(f"{self.base}/opticli-fixture/hand-over?version={version}&user={user}", data=b"", method="POST")
        urllib.request.urlopen(request, timeout=60).close()


failures = []
passed = 0


def check(condition, what, detail=None):
    global passed
    if condition:
        passed += 1
        print(f"  ok    {what}")
    else:
        failures.append(what)
        print(f"  FAIL  {what}" + (f"\n        {json.dumps(detail)[:600]}" if detail is not None else ""))


def step(title):
    print(f"\n== {title}")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    site = Site(sys.argv[1])
    keep = "--keep" in sys.argv
    if "--disabled" in sys.argv:
        disabled(site)
        return
    stamp = datetime.now().strftime("%H%M%S")
    approval_root = site.ok("GET", f"/content/{APPROVAL_ROOT}")
    edge = approval_root["parent"]
    created_projects = []

    def page(name, parent, publish=True):
        return site.ok("POST", "/content", {"parent": str(parent), "type": "StandardPage", "name": name, "publish": publish})["content"]

    def draft(ref, body):
        return site.ok("POST", f"/content/{ref}/draft", body)

    def project(name):
        result = site.ok("POST", "/projects", {"name": name})
        created_projects.append(result["project"]["id"])
        return result["project"]["id"]

    container = page(f"opticli-pp scratch {stamp}", edge)["id"]
    print(f"Scratch page {container} below {edge}; agent on port {site.port}")
    try:
        step("create")
        dry = site.ok("POST", "/projects", {"name": f"pp main {stamp}", "dryRun": True})
        check(dry["dryRun"] and dry["project"]["id"] == 0 and dry["editorUi"] in ("projectMode", "gadget"), "dry-run create reports the project and the editor UI", dry)
        status, envelope = site.call("POST", "/projects", {"name": f"pp main {stamp}"})
        main_id = envelope["data"]["project"]["id"]
        created_projects.append(main_id)
        check(status == 201 and envelope["data"]["created"] and envelope["data"]["project"]["status"] == "active", "create makes an active project (201)", envelope)
        status, error = site.error("POST", "/projects", {"name": f"PP MAIN {stamp}"})
        check(status == 409 and error["code"] == "conflict", "a name an unpublished project has is refused", error)

        step("save into a project")
        a = page(f"opticli-pp A {stamp}", container)["id"]
        b = page(f"opticli-pp B {stamp}", container)["id"]
        c = page(f"opticli-pp C {stamp}", container)["id"]
        planned = draft(a, {"properties": {"TeaserText": "A1"}, "project": f"pp main {stamp}", "ready": True, "dryRun": True})
        check(planned["dryRun"] and planned["project"]["item"] == "added" and planned["project"]["ready"], "dry run reports the item it would add, ready", planned.get("project"))
        saved = draft(a, {"properties": {"TeaserText": "A1"}, "project": str(main_id), "ready": True})
        check(saved["project"] == {**saved["project"], "id": main_id, "item": "added", "ready": True} and saved["content"]["status"] == "checkedIn",
              "a save by id adds the version, marked ready (checkedIn)", saved.get("project"))
        first = saved["content"]["ref"]
        again = draft(a, {"properties": {"TeaserText": "A2"}, "project": f"pp main {stamp}"})
        check(again["project"]["item"] == "replaced" and again["project"]["replaced"] == first and not again["project"]["ready"]
              and again["content"]["status"] == "checkedOut", "a second save replaces the item, as a draft", again.get("project"))
        a_ready = draft(a, {"properties": {"TeaserText": "A3"}, "project": str(main_id), "ready": True})
        b_draft = draft(b, {"properties": {"TeaserText": "B1"}, "project": str(main_id)})
        check(b_draft["project"]["item"] == "added" and not b_draft["project"]["ready"], "another page goes in as a draft", b_draft.get("project"))

        status, error = site.error("POST", f"/content/{c}/draft", {"properties": {"TeaserText": "C1"}, "project": str(main_id), "publish": True})
        check(status == 400 and error["code"] == "usage", "project with publish is a usage error", error)
        status, error = site.error("POST", f"/content/{c}/draft", {"properties": {"TeaserText": "C1"}, "ready": True})
        check(status == 400 and error["code"] == "usage", "ready without project is a usage error", error)
        status, error = site.error("POST", f"/content/{c}/draft", {"properties": {"TeaserText": "C1"}, "project": f"no such project {stamp}"})
        check(status == 404 and error["code"] == "not_found", "an unknown project is not_found", error)

        warned = draft(a, {"properties": {"TeaserText": "A4"}})
        warnings = [v["message"] for v in warned.get("validation") or []]
        check("project" not in warned and any(f"project {main_id}" in w and a_ready["content"]["ref"] in w for w in warnings),
              "a save without project of content in one warns which version the project holds", warnings)

        other = project(f"pp other {stamp}")
        status, error = site.error("POST", f"/content/{a}/draft", {"properties": {"TeaserText": "A5"}, "project": str(other)})
        check(status == 409 and error.get("reason") == "inOtherProject", "content in another unpublished project is refused (inOtherProject)", error)

        lang = site.call("POST", f"/content/{c}/languages", {"lang": "sv", "name": f"opticli-pp C sv {stamp}", "project": str(main_id)})
        if lang[1].get("ok"):
            check(lang[1]["data"]["project"]["item"] == "added" and lang[1]["data"]["content"]["language"] == "sv", "a new language branch goes in, in its language", lang[1]["data"].get("project"))
        else:
            check(False, "a new language branch goes in, in its language", lang[1].get("error"))

        uploaded = site.ok("POST", "/media", {"forContent": str(container), "fileName": f"pp-{stamp}.pdf", "data": base64.b64encode(MINIMAL_PDF).decode(), "project": str(main_id)})
        check(uploaded["project"]["item"] == "added", "an upload goes in", uploaded.get("project"))
        media = uploaded["content"]["id"]

        created = site.ok("POST", "/content", {"parent": str(container), "type": "StandardPage", "name": f"opticli-pp new {stamp}", "project": str(main_id), "ready": True})
        check(created["project"]["item"] == "added" and created["project"]["ready"] and created["content"]["status"] == "checkedIn", "a new page goes in, ready", created.get("project"))

        unchanged = draft(b, {"properties": {"TeaserText": "B1"}, "project": str(main_id)})
        check(unchanged["project"]["item"] == "unchanged" and not unchanged.get("saved"), "a save that changes nothing leaves the item as it is", unchanged.get("project"))

        step("approval sequence")
        guarded = page(f"opticli-pp guarded {stamp}", approval_root["id"], publish=False)["id"]
        status, error = site.error("POST", f"/content/{guarded}/draft", {"properties": {"TeaserText": "G1"}, "project": str(main_id), "ready": True})
        check(status == 403 and error.get("reason") == "approvalSequence", "ready under an approval sequence is refused", error)
        review = draft(guarded, {"properties": {"TeaserText": "G1"}, "project": str(main_id), "ready": True, "requestApproval": True})
        check(review.get("approvalRequested") and review["project"]["item"] == "added" and not review["project"]["ready"]
              and review["content"]["status"] == "awaitingApproval", "ready with requestApproval sends it for review, in the project", review)

        step("ready")
        items = site.ok("POST", f"/projects/{main_id}/ready", {"items": [str(b)], "dryRun": True})
        check(items["dryRun"] and [i["outcome"] for i in items["items"]] == ["ready"], "dry run of ready lists the item", items["items"])
        items = site.ok("POST", f"/projects/{main_id}/ready", {"items": [str(b)]})
        check([i["outcome"] for i in items["items"]] == ["ready"] and items["items"][0]["content"]["status"] == "checkedIn"
              and items["items"][0]["content"]["ref"] == b_draft["content"]["ref"], "ready marks the item ready, same version", items["items"])
        items = site.ok("POST", f"/projects/{main_id}/ready", {"items": [str(b)]})
        check([i["outcome"] for i in items["items"]] == ["alreadyReady"], "ready again is alreadyReady", items["items"])
        status, error = site.error("POST", f"/projects/{main_id}/ready", {"items": [str(guarded)]})
        check(status == 409 and error.get("reason") == "inReview" and error["project"]["items"][0]["problem"]["reason"] == "inReview",
              "an item in review can't be marked ready; every problem is in error.project", error)
        status, error = site.error("POST", f"/projects/{main_id}/ready", {"items": [str(container)]})
        check(status == 404, "content that isn't in the project is not_found", error)
        all_ready = site.ok("POST", f"/projects/{main_id}/ready", {"all": True, "dryRun": True})
        outcomes = {i["content"]["id"]: i["outcome"] for i in all_ready["items"]}
        check(outcomes.get(guarded) == "skipped" and outcomes.get(b) == "alreadyReady" and outcomes.get(c) == "ready",
              "all takes the drafts and skips the rest", outcomes)
        guarded2 = page(f"opticli-pp guarded 2 {stamp}", approval_root["id"], publish=False)["id"]
        draft(guarded2, {"properties": {"TeaserText": "G2"}, "project": str(main_id)})
        status, error = site.error("POST", f"/projects/{main_id}/ready", {"items": [str(guarded2)]})
        check(status == 403 and error.get("reason") == "approvalSequence", "ready of an item under an approval sequence is refused", error)
        status, error = site.error("POST", f"/projects/{main_id}/ready", {"items": [str(guarded2)], "comment": "Please review"})
        check(status == 400, "a comment without requestApproval is a usage error", error)
        reviewed = site.ok("POST", f"/projects/{main_id}/ready", {"items": [str(guarded2), str(b)], "requestApproval": True, "comment": "Please review"})
        check([(i["outcome"], i["content"]["status"]) for i in reviewed["items"]] == [("alreadyReady", "checkedIn"), ("approvalRequested", "awaitingApproval")]
              and not any(i.get("validation") for i in reviewed["items"]), "requestApproval sends it for review with the comment; others stay as they are", reviewed["items"])
        site.ok("POST", f"/projects/{main_id}/remove", {"items": [str(guarded2)]})

        step("pending draft by someone else")
        d = page(f"opticli-pp D {stamp}", container)["id"]
        d_draft = draft(d, {"properties": {"TeaserText": "colleague"}})
        site.hand_over(d_draft["content"]["ref"], "pp-colleague")
        d_ready = draft(d, {"properties": {"MetaDescription": "ours"}, "project": str(main_id), "ready": True})
        check(d_ready["project"]["ready"], "a draft on top of a colleague's goes in ready", d_ready.get("project"))

        step("publish")
        site.ok("POST", f"/projects/{main_id}/remove", {"items": [str(guarded)]})
        dry = site.ok("POST", f"/projects/{main_id}/publish", {"dryRun": True})
        problems = [i for i in (dry.get("published") or []) if i.get("problem")]
        check(not dry["valid"] and len(problems) == 1 and problems[0]["problem"]["reason"] == "pendingDraft" and problems[0]["pendingDraft"]["savedBy"] == "pp-colleague",
              "dry run lists the colleague's draft as the only problem", dry)
        status, error = site.error("POST", f"/projects/{main_id}/publish", {})
        check(status == 409 and error.get("reason") == "pendingDraft" and error.get("pendingDraft"), "publish without includeDraft is refused (pendingDraft)", error)
        notify = page(f"opticli-pp-notify {stamp}", container)["id"]
        draft(notify, {"properties": {"TeaserText": "N1"}, "project": str(main_id), "ready": True})
        c_draft = draft(c, {"properties": {"TeaserText": "C draft"}, "project": str(main_id)})
        published = site.ok("POST", f"/projects/{main_id}/publish", {"includeDraft": True})
        live = {i["content"]["id"] for i in published.get("published") or []}
        behind = {i["content"]["id"] for i in published.get("notReady") or []}
        check(published["project"]["status"] == "published" and {a, b, d, notify}.isdisjoint(behind) and {a, b, d, notify} <= live,
              "publish puts the ready items live and the project is published", published)
        check(c in behind and media in behind, "items that aren't ready stay behind", published.get("notReady"))
        made = [s for s in published.get("siteChanges") or [] if s["content"]["name"] == f"opticli-pp-made opticli-pp-notify {stamp}"]
        check(len(made) == 1 and "published" in made[0]["actions"], "siteChanges has the page the site's handler made during the publish", published.get("siteChanges"))
        check(all(s["content"]["id"] not in live for s in published.get("siteChanges") or []), "siteChanges leaves out the items' own publish", published.get("siteChanges"))
        check(all(i.get("previouslyPublished") for i in published.get("published") or [] if i["content"]["id"] in (a, b, d)),
              "each published item says which version was live before", published.get("published"))
        a_now = site.ok("GET", f"/content/{a}")
        check(a_now["version"] == int(a_ready["content"]["ref"].split("_")[1]), "the published version is the ready one (same version id)", a_now.get("version"))
        status, error = site.error("POST", f"/projects/{main_id}/publish", {})
        check(status == 409 and error.get("reason") == "projectNotActive", "a published project doesn't publish again", error)
        status, error = site.error("POST", f"/content/{c}/draft", {"properties": {"TeaserText": "C2"}, "project": str(main_id)})
        check(status == 409 and error.get("reason") == "projectNotActive", "a published project takes no saves", error)

        step("nothing ready")
        empty = project(f"pp empty {stamp}")
        draft(c, {"properties": {"TeaserText": "C3"}, "project": str(empty)})
        status, error = site.error("POST", f"/projects/{empty}/publish", {})
        check(status == 409 and error.get("reason") == "nothingReady", "a publish with no ready item is refused", error)
        all_ready = site.ok("POST", f"/projects/{empty}/ready", {"all": True})
        check([(i["content"]["id"], i["outcome"], i["content"]["status"]) for i in all_ready["items"]] == [(c, "ready", "checkedIn")]
              and all_ready["project"]["ready"] == 1, "ready with all marks the drafts ready", all_ready)

        step("a publish the CMS fails to start")
        flaky = project(f"pp flaky {stamp}")
        f = page(f"opticli-pp-flaky {stamp}", container)["id"]
        draft(f, {"properties": {"TeaserText": "F1"}, "project": str(flaky), "ready": True})
        retried = site.ok("POST", f"/projects/{flaky}/publish", {})
        check(retried["project"]["status"] == "published" and [i["content"]["id"] for i in retried.get("published") or []] == [f]
              and any("tried again" in v["message"] for v in retried.get("validation") or []),
              "a publish that failed with CMS 12's executor error before anything went live is tried again, and says so", retried)

        step("schedule, reactivate, delete")
        later = project(f"pp later {stamp}")
        e = page(f"opticli-pp E {stamp}", container)["id"]
        e_ready = draft(e, {"properties": {"TeaserText": "E1"}, "project": str(later), "ready": True})
        at = (datetime.now(timezone.utc) + timedelta(days=30)).replace(microsecond=0).isoformat().replace("+00:00", "Z")
        scheduled = site.ok("POST", f"/projects/{later}/publish", {"publishAt": at})
        check(scheduled["project"]["status"] == "delayedPublished" and [i["content"]["status"] for i in scheduled.get("scheduled") or []] == ["delayedPublish"],
              "a scheduled publish schedules the ready items", scheduled)
        status, error = site.error("POST", f"/content/{e}/draft", {"properties": {"TeaserText": "E2"}, "project": str(later)})
        check(status == 409 and "reactivate" in error.get("hint", ""), "a scheduled project takes no saves; the hint says reactivate", error)
        status, error = site.error("DELETE", f"/projects/{later}")
        check(status == 409, "a scheduled project isn't deleted", error)
        reactivated = site.ok("POST", f"/projects/{later}/reactivate", {})
        check(reactivated["reactivated"] and reactivated["project"]["status"] == "active"
              and [(i["outcome"], i["content"]["status"], i["content"]["ref"]) for i in reactivated["items"]] == [("unscheduled", "checkedOut", e_ready["content"]["ref"])],
              "reactivate makes it active and the item a draft again, same version", reactivated)
        status, error = site.error("POST", f"/projects/{later}/reactivate", {})
        check(status == 409, "an active project isn't reactivated", error)
        removed = site.ok("POST", f"/projects/{later}/remove", {"items": [str(e)]})
        check([i["outcome"] for i in removed["items"]] == ["removed"] and removed["project"]["items"] == 0, "remove takes the item out", removed)
        deleted = site.ok("DELETE", f"/projects/{later}")
        check(deleted["deleted"], "delete deletes the project", deleted)
        created_projects.remove(later)
        status, error = site.error("POST", f"/projects/{later}/ready", {"all": True})
        check(status == 404, "a deleted project is not_found", error)
    except AssertionError as exception:
        failures.append(str(exception))
        print(f"  FAIL  {exception}")
    finally:
        if not keep:
            site.call("DELETE", f"/content/{container}")
            for project_id in created_projects:
                site.call("DELETE", f"/projects/{project_id}")
    print(f"\n{passed} passed, {len(failures)} failed")
    for failure in failures:
        print(f"  - {failure}")
    sys.exit(1 if failures else 0)


def disabled(site):
    step("projects turned off")
    approval_root = site.ok("GET", f"/content/{APPROVAL_ROOT}")
    for method, path, body in [
        ("POST", "/projects", {"name": "pp disabled", "dryRun": True}),
        ("POST", f"/content/{approval_root['parent']}/draft", {"properties": {"TeaserText": "x"}, "project": "1", "dryRun": True}),
        ("POST", "/projects/1/publish", {"dryRun": True}),
    ]:
        status, error = site.error(method, path, body)
        check(status == 403 and error.get("reason") == "projectsDisabled", f"{method} {path} is refused (projectsDisabled)", error)
    print(f"\n{passed} passed, {len(failures)} failed")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
