---
title: Add blocks to a page with your assistant
summary: Have your assistant put new blocks in a page's content area, know where the CMS keeps them, review them, and publish the page and its blocks together.
audience: editors
order: 6
written-for: 0.35.0
---

A page's content area holds blocks: a text, a teaser, a call to action. Your assistant can add new ones, change them
and put them in order, as you would with **Create a new block** in the CMS. Where the new block ends up depends on how
your site is set up, and that matters when you publish. By the end of this guide your assistant has added blocks to a
page as a draft, you have reviewed them in the CMS, and the page has gone live with its blocks.

## Before you start

- Your assistant is connected to the site ([Connect your assistant to the CMS](connect-your-assistant.md)).
- You may create and edit content on the page in the CMS. The assistant can add a block only where **Create a new
  block** in the CMS would let you, and only of a type the CMS offers there.
- To publish through the assistant, the site must let assistants publish (it doesn't by default), and you must have
  allowed it on the consent page. Otherwise you publish in the CMS; step 5 says what to look out for.

## Steps

### 1. See what the page has

```prompt
Which blocks does the Alloy Plan page show in its main content area, in order?
```

The assistant lists the blocks with their names and types. Blocks that are content of their own have an id; blocks
stored in the page itself (inline blocks) have none. Both kinds can be on the same page.

### 2. Ask for the new blocks

Say what each block should contain and where it goes:

```prompt
Add a text block after the first block on the Alloy Plan page, named "Pricing intro", with a short paragraph saying
that every plan can be tried free for 30 days.
```

The assistant first shows you a dry run: the area as it will be, with the new block, its name and values. Nothing is
saved yet. A required field you didn't give, or a block type the area doesn't allow, shows up here, and the assistant
asks you for what is missing. When you agree, it saves the page as a draft.

### 3. Know where the new block is kept

Your site keeps a new block in one of two places, the same way **Create a new block** does in the CMS. The assistant
follows the site and can't choose the other:

- **In the page itself** (an inline block). It has no id of its own, and you find it only in the page's content area.
  It is saved and published with the page.
- **As a block of its own, in the page's "For this page" folder.** You see it in the assets pane when the page is
  open, under **For this page**. The assistant tells you its name and id, and that it was saved as a draft. It is
  published separately from the page (step 5). (**Create a new block** in the CMS publishes such a block at once when
  you may publish; the assistant saves it as a draft, so nothing goes live before you ask.)

Blocks the page had already stay as they are: an inline block stays inline when the assistant changes it.

### 4. Review the draft

The assistant gives you an edit link to the page's draft. Open it in the CMS and look at the content area, or open the
preview link to see the page as visitors will.

When the assistant changed a block of the page's folder that is already published, the preview still shows that
block's published version until the block is published too. The assistant tells you when that is the case.

To change a block later, name it:

```prompt
On the Alloy Plan page, change the "Pricing intro" block to say 14 days instead of 30.
```

A block of its own is changed as its own draft; an inline block through a new draft of the page.

### 5. Publish the page and its blocks

```prompt
Publish the Alloy Plan page.
```

The assistant tells you what goes live and asks you to confirm. When it publishes the page, it also publishes the drafts
of the blocks in the page's "For this page" folder that the page shows, and lists them. A draft of such a block that
someone else saved is left out until you confirm their changes may go live (see
[When someone else has changes on the page](someone-elses-draft.md)), and so is a block that needs a reviewer.

> **Note:** Publishing the page in the CMS publishes the page alone. Blocks of the page's folder that are drafts stay
> drafts, and visitors don't see them. If you publish in the CMS, publish those blocks too: open each one from
> **For this page** in the assets pane and publish it, before or after the page.

Where the site doesn't let assistants publish, the assistant tells you which blocks to publish along with the page.

### 6. Pages with an approval sequence

A reviewer's approval publishes the page alone. So on a site that keeps new blocks in the page's folder, the
assistant can't send the page for review while it shows blocks that aren't published. It tells you so, and offers two
ways:

- **A project.** The assistant saves the page and its new blocks in a CMS project and sends them for review together;
  publishing the project puts them live together. See
  [Collect changes in a project and publish them together](collect-changes-in-a-project.md).

  ```prompt
  Put the new blocks on Alloy Plan in the "Pricing" project and send them for review with the page.
  ```

- **Blocks first.** Keep the page as a draft, publish the blocks (or send them for review: blocks in the folder of a
  page with an approval sequence usually need approval too), then send the page for review.

## If something goes wrong

- **"You don't have Create access to …" or "The CMS edit UI doesn't offer … for a new block".** You couldn't make that
  block in the CMS either. Ask your CMS administrator for access, or choose another block type.
- **"… would go for review while its ContentAreas show blocks of its "For this page" folder that aren't published".**
  See step 6.
- **"… wasn't published along".** The page went live without that block's draft: someone else saved it, it needs a
  reviewer, or it has an error. The assistant says which; publish or send it for review on its own once it is ready.
- **The published page shows a block's old text, or not the new block.** That block is still a draft, for instance
  because the page was published in the CMS. Ask the assistant to publish that block, or publish it in the CMS.

## Related

- [Connect your assistant to the CMS](connect-your-assistant.md)
- [When someone else has changes on the page](someone-elses-draft.md)
- [Collect changes in a project and publish them together](collect-changes-in-a-project.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [New blocks in a ContentArea](https://github.com/Epinova/opticli#new-blocks-in-a-contentarea) and
  [Security model](https://github.com/Epinova/opticli#security-model)
