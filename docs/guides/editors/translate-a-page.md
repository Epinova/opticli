---
title: Translate a page with your assistant
summary: Have your assistant translate a page into another language, review the translation as a draft, and publish it only when you say so.
audience: editors
order: 2
written-for: 0.30.0
---

By the end of this guide a page has a new language version, written by your assistant and saved as a draft. You see
every translated field before anything is saved, you review the draft in the CMS, and it goes live only when you
publish it, or ask the assistant to. The examples translate the Alloy site's "About us" page from English into
Swedish.

## Before you start

- Your assistant is connected to the site ([Connect your assistant to the CMS](connect-your-assistant.md)), and you
  allowed it to create content and change drafts.
- The language you want is turned on for the site. Your CMS administrator turns languages on; the assistant can't.
- You have access to edit that language. The assistant gets no more access than you have.

## Steps

### 1. Check which languages the page has

```prompt
Which languages does the "About us" page have, and which languages can I translate it into on this site?
```

The assistant finds the page and tells you its languages. If you name a language the site hasn't turned on, it is
told which ones are on and passes that list to you.

### 2. Ask for a translation, shown before anything is saved

```prompt
Translate the "About us" page into Swedish. Show me the changes before you save anything.
```

The assistant reads the page and the fields its page type has, writes the translation, and then does a dry run: the
site checks the new language version without saving it. You get a list of fields, each with the English text before
and the Swedish text after, plus any problems the site found, such as a required field left empty.

Some things to know about what it can translate:

- **Only fields that have a value per language can be translated.** Fields that are shared by all languages (often
  images, links to other pages and settings) keep the English version's value. The CMS locks those outside the main
  language, and so does the assistant.
- **Fields the assistant leaves out keep the English text.** Ask it to translate every text field, or check the list.
- **Blocks that are shared between pages are content of their own.** Translating the page doesn't translate them. Ask
  for them separately; each gets a draft of its own.

### 3. Adjust the wording

Ask for changes until you are happy. Each time, the assistant shows a new dry run.

```prompt
Use "kontakta oss" instead of "hör av dig" in the introduction, and keep the heading shorter. Show me the changes again.
```

### 4. Save it as a draft

```prompt
That looks good. Save the Swedish version as a draft.
```

The assistant saves the Swedish version as a draft and gives you two links:

- **The edit link** opens the draft in the CMS's editing view, in Swedish. Review it there as you would any draft.
- **The preview link** shows the draft as visitors will see it, without the editing tools.

Both open only when you are signed in to the CMS. The assistant also gives you the page's public address, which works
once the Swedish version is published. It may tell you that the site's own code did something more on save, such as
creating or moving other content; check that too.

> **Note:** Your assistant may look at the draft itself, through a short-lived preview link of its own. That link is
> for the assistant. Review the draft through the edit and preview links.

### 5. Publish only when you say so

When the draft is right, publish it in the CMS, or ask the assistant:

```prompt
Publish the Swedish version of "About us".
```

The assistant first tells you what will go live and asks you to confirm. It publishes only after you say yes. What
happens then depends on the site:

- **Publishing is off for assistants** (the default, unless your developer turned it on). The assistant says so and
  leaves the draft for you to publish in the CMS.
- **The page has an approval sequence.** The assistant can't publish past the reviewers. It offers to send the draft
  for review instead, as **Ready for Review** does in the CMS. A reviewer then approves it.
- **Someone else has unpublished changes on the page.** The assistant stops and asks you first. See
  [When someone else has changes on the page](someone-elses-draft.md).

## If something goes wrong

- **"… already has a 'sv' branch".** The page is already translated into that language. Ask the assistant to change the
  existing Swedish version instead; that is a new draft too.
- **"… is not an enabled language on this site".** Ask your CMS administrator to turn the language on.
- **"… is not culture-specific, so it can only be changed on the master language".** That field is shared by all
  languages. Change it in the main language, or leave it as it is.
- **"The master language branch has never been published".** The CMS publishes the main language first. Publish the
  English page first, or keep the translation as a draft.
- **The assistant isn't allowed to do it.** Your access rights for that language, or for the page, don't allow it. Ask
  your CMS administrator.

## Related

- [Connect your assistant to the CMS](connect-your-assistant.md)
- [When someone else has changes on the page](someone-elses-draft.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [MCP server for editors](https://github.com/Epinova/opticli#mcp-server-for-editors) and
  [Security model](https://github.com/Epinova/opticli#security-model)
