---
title: When someone else has changes on the page
summary: Understand why your assistant stops before publishing a colleague's unpublished changes, decide what goes live, and send pages for review where an approval sequence applies.
audience: editors
order: 4
written-for: 0.30.1
---

Publishing in the CMS puts a whole version of a page live, not just the fields you changed. So if a colleague has saved
changes on a page that aren't published yet, and your change is built on top of them, publishing yours would publish
theirs too. Your assistant doesn't do that without asking you. By the end of this guide you know what the assistant
tells you in that case, how to decide, and how review requests work on pages with an approval sequence.

## Before you start

- Your assistant is connected to the site ([Connect your assistant to the CMS](connect-your-assistant.md)).
- To publish through the assistant, the site must let assistants publish (it doesn't by default), and you must have
  allowed it on the consent page. Otherwise you publish in the CMS, and this guide still explains what you see there.
- Talking to your colleague is often the quickest way out. The assistant tells you who it is.

## Steps

### 1. See what is waiting on the page

```prompt
Which versions does the "Alloy Track" page have that aren't published yet, and who saved them?
```

The assistant lists the page's saved versions, newest first: whether each is a draft, published, or waiting for
review, who saved it and when.

### 2. Know that your draft starts from the newest version

When the assistant changes a page, it saves a new draft based on the newest version, not on the published one. If a
colleague's draft is the newest version, your draft carries their changes too. Saving that draft changes nothing for
visitors. It matters only when the draft is published.

If someone saves the page while your assistant is working on it, the assistant's save is refused rather than
overwriting their work. The assistant then reads the page again and redoes your change on top of theirs.

### 3. Ask to publish, and read what the assistant tells you

```prompt
Change the heading of "Alloy Track" to "Track every project in one place" and publish it.
```

The assistant first shows you a dry run of the change, which may already mention a colleague's unpublished changes.
If you go ahead and they would go live with yours, the publish is refused, and the assistant tells you:

- **whose changes they are**, as the CMS recorded the name. If there is no name, a scheduled job or an import saved
  them;
- **when they were saved**;
- **which fields differ from the published page**, with the published value and the new one.

Nothing has gone live at this point. The same happens when you ask for an earlier version by name ("publish the
version from Tuesday"): if it holds a colleague's changes, the assistant asks you first.

### 4. Decide what to do

You have three choices:

- **Publish everything.** If you know the colleague's changes are ready, say so. Only then does the assistant
  publish again, confirming that their changes go live with yours.

  ```prompt
  Yes, those changes are ready too. Publish the page with them.
  ```

- **Wait.** Leave your change as a draft and talk to your colleague. Either of you can publish later, when both
  changes are ready.
- **Throw their draft away.** Only after agreeing with your colleague. A discarded draft is deleted for good. The
  assistant can only do this if the site lets assistants delete content; otherwise you discard it in the CMS. Your own
  drafts you can always discard.

Your assistant can't publish only your change and leave a colleague's out of a version that holds both.

### 5. Pages with an approval sequence

Some pages need a reviewer's approval before they go live. On those, the assistant never publishes. It offers to send
the page for review instead, as **Ready for Review** does in the CMS:

```prompt
Send the "Alloy Track" page for review.
```

A review request never publishes anything, so it works even where assistants may not publish. Things to know:

- **The reviewer sees the whole version**, a colleague's unpublished changes included. The assistant doesn't stop for
  those here, but it tells you whose changes went for review with yours, already in the dry run when it changes the
  page first. Check step 1 before you send the page if you'd rather not include them.
- **A page in review is locked.** Neither you nor the assistant can change it until a reviewer approves or rejects it
  in the CMS.
- **A page without an approval sequence can't be sent for review.** The assistant tells you, and nothing is saved.
  Publish it as usual, or keep it as a draft.

## If something goes wrong

- **"This site doesn't let assistants publish".** Review the draft through the edit link the assistant gives you, and
  publish it in the CMS.
- **"… is in review: version … awaits approval".** A reviewer has to approve or reject it in the CMS first.
- **"… is not the latest version".** Someone saved in the meantime. Ask the assistant to read the page again and redo
  the change.
- **"This site doesn't let assistants delete content, or discard what someone else saved".** Discard the draft in the
  CMS, after talking to your colleague.

> **Note:** Before anything goes live, ask your assistant to tell you exactly what will be published. The check for
> other people's changes is a safety net, not a replacement for reading what goes live.

## Related

- [Connect your assistant to the CMS](connect-your-assistant.md)
- [Translate a page with your assistant](translate-a-page.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [Security model](https://github.com/Epinova/opticli#security-model), on approval sequences and confirming
  other people's changes
