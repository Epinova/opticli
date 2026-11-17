---
title: Collect changes in a project and publish them together
summary: Have your assistant put the drafts of a campaign or a new section in a CMS project, review them together in the CMS, and publish them in one go, now or at a set time.
audience: editors
order: 5
written-for: 0.33.0
---

Some changes belong together: a spring campaign that touches the start page, three product pages and a new teaser,
or a new section of the site. You want to review them side by side, and they should go live at the same moment, not one
by one. The CMS has projects for that, and your assistant can work with them. By the end of this guide your assistant
has saved its drafts in a project, you have reviewed them in the CMS, and the project has gone live, now or at a time
you chose.

## Before you start

- Your assistant is connected to the site ([Connect your assistant to the CMS](connect-your-assistant.md)).
- The site has projects turned on. Most sites have: either the **project bar** at the top of the edit view (project
  mode), or the **Projects gadget** you can add to a pane (the default on CMS 13). Your developer knows which.
- To publish through the assistant, the site must let assistants publish (it doesn't by default), and you must have
  allowed it on the consent page. Otherwise you publish the project in the CMS yourself; step 5 says what to look out
  for.

## Steps

### 1. Ask for a project

Say what the changes are for and that you want them reviewed together:

```prompt
I want to prepare the spring campaign: new texts on the start page and on the Alloy Plan, Alloy Track and Alloy Meet
pages. Put the changes in a project called "Spring campaign" so I can review them together.
```

When you ask for several related changes without mentioning a project, the assistant may suggest one. It asks first,
and offers an existing project if one fits. A project name is taken while that project is unpublished, so for a second
campaign of the same name it suggests using the existing project instead.

The assistant answers with the project's id and, where the site has project mode, a link to the project in the CMS.
On a site with only the Projects gadget there is no such link: the assistant tells you to open the gadget instead.

### 2. Let the assistant make the changes into the project

```prompt
On the start page, change the heading to "Spring is here". On the three product pages, start the teaser text with
"New this spring:". Save everything in "Spring campaign".
```

As always, the assistant shows you a dry run first and saves only when you agree. Each change is saved as a draft, and
the draft is put in the project. Things to know:

- **A change goes into the project only when the assistant names it.** There is no "current project" the assistant
  remembers on its own. If you ask for more changes later, say that they belong in the project too.
- **The project holds one version of each page per language.** When the assistant changes a page again, the new draft
  takes the place of the earlier one in the project.
- **A page can only be in one project at a time** (until that project is published). If the page is already in another
  project, the save is refused and the assistant tells you which project has it.
- **Your own saves.** Opening the project's link in the CMS makes it your current project in the edit view, so changes
  you then make yourself in the CMS go into it too. The assistant reminds you of that.

### 3. Review the project in the CMS

Open the link the assistant gave you, or the project in the Projects gadget. You see every page in the project with its
status: draft, Ready to Publish, or in review. Open each one from there, or through the edit link the assistant gave for
each draft, and check it the way you always do.

If the overview shows an old status (a page you just had marked ready still says draft), use the overview's Refresh, or
reload the page.

You can also ask the assistant:

```prompt
What is in the "Spring campaign" project, and what is ready?
```

### 4. Mark the pages that are ready

Publishing a project puts live only the pages marked **Ready to Publish**. Mark them in the CMS, or ask the assistant:

```prompt
Mark everything in "Spring campaign" ready to publish.
```

The assistant does a dry run first and tells you if something can't be marked. Things to know:

- **Only when you say so.** The assistant marks pages ready only when you ask for it. Pages still being worked on can
  stay drafts: they stay behind when the project is published.
- **Pages with an approval sequence** can't be marked ready, since that would skip the reviewers. The assistant offers
  to send them for review instead, with a comment for the reviewers if you like. Once a reviewer approves a page, it
  counts as ready.
- **A colleague's draft.** If the newest version of a page in the project was saved by someone else, the assistant asks
  you before marking it ready. Marking it ready is what says "this can go live", so it should be your colleague's
  decision, or yours after talking to them.
- If you can't edit some of the pages, the assistant leaves those as they are and tells you which ones.

### 5. Publish the project, or schedule it

Ask for it when you are ready:

```prompt
Publish the "Spring campaign" project.
```

Or at a set time:

```prompt
Schedule the "Spring campaign" project to go live on 1 March at 07:00.
```

The assistant first shows you what will happen, and publishes only once you confirm:

- **what goes live**: every page marked ready;
- **whose changes go with it**: if a colleague marked a page ready, their changes on it go live without the assistant
  asking again, since marking it ready was their decision. Changes someone else saved that nobody marked ready, the
  assistant asks you about first;
- **what stays behind**: pages that are still drafts or in review. They stay exactly as they are, in the project.

If any ready page has a problem (a required field is empty, say), nothing is published, and the assistant tells you
which page and why.

**Publishing in the CMS yourself.** Where the assistant may not publish, or you prefer to do it yourself, publish the
project in the CMS. With the project bar (project mode), the CMS also publishes only the pages marked ready. **The
Projects gadget's Publish button is different: it puts every unpublished page of the project live, ready or not.** Before
you publish from the gadget, check that every page in the project should go live, or take out the ones that shouldn't
(step 6).

### 6. Change your mind

- **Take a page out of the project:**

  ```prompt
  Take the Alloy Meet page out of "Spring campaign".
  ```

  The draft stays as it is; it just isn't published with the project.
- **Cancel a scheduled publish:**

  ```prompt
  Cancel the scheduled publish of "Spring campaign".
  ```

  The project is active again, and its pages are drafts again. Mark them ready again before you publish or schedule it
  anew. Like publishing, this needs the site to let assistants publish; otherwise you do it in the CMS.
- **Delete the project** in the CMS. The assistant can't. Deleting a project leaves its drafts as they are.

## If something goes wrong

- **"Projects are turned off on this site".** The CMS has neither the project bar nor the Projects gadget. Ask your
  developer, or let the assistant save the drafts without a project.
- **"… is in project …".** The page is already in another project that isn't published yet. Save it in that project instead, or take it
  out of the other one first (after checking with whoever runs that project).
- **"No item of project … is marked Ready to publish".** There is nothing to publish yet. Mark the pages that
  should go live (step 4).
- **"Project … is published".** A published project is done and takes no more changes. Ask for a new project for
  further changes.
- **"Project … is scheduled to be published".** A scheduled project takes no changes. Cancel the scheduled publish first (step
  6), make the change, mark the pages ready again and schedule it again.
- **"This site doesn't let assistants publish".** Have the assistant mark the pages ready, then publish the project in
  the CMS (mind the gadget's Publish button, step 5).
- **A page you marked ready shows as draft in the overview.** Refresh the overview or reload the page.

> **Note:** Before a project goes live, ask the assistant to list exactly what will be published and what stays behind.
> A project can hold more pages than you remember, also ones a colleague added.

## Related

- [When someone else has changes on the page](someone-elses-draft.md)
- [Connect your assistant to the CMS](connect-your-assistant.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [Projects for editors' assistants](https://github.com/Epinova/opticli#projects-for-editors-assistants) and
  [Security model](https://github.com/Epinova/opticli#security-model)
