---
title: Fix image alt texts with your assistant
summary: Have your assistant look at your images, suggest alt texts or descriptions, and save them as drafts you review first.
audience: editors
order: 3
written-for: 0.30.0
---

By the end of this guide your assistant has looked at the images you pointed it to, suggested an alt text (or
description) for each one, and saved the ones you approved as drafts. You review every change before it is saved, and
nothing goes live until it is published. Where the alt text is stored depends on how your site is built, so the guide
starts by finding that out.

## Before you start

- Your assistant is connected to the site ([Connect your assistant to the CMS](connect-your-assistant.md)), and you
  allowed it to create content and change drafts.
- You know roughly which images to look at: those on one page, in one media folder, or with a name you can search for.
- Your assistant can see images. Most current assistants can.

## Steps

### 1. Find out where alt text lives on your site

```prompt
Look at one of the images used on the "Alloy Plan" page. Which fields does an image have on this site, and is any of them for alt text or a description?
```

The assistant checks the image's type and tells you its fields. There are two common answers:

- **The image itself has a field for it**, such as "Alt text" or "Description". Then one alt text follows the image
  wherever it is used. Go on with step 2.
- **The image has no such field.** The Alloy sample site is like this: its images have a copyright field and nothing
  else, and its SVG drawings have no fields at all. Alt text then lives where the image is placed, such as the alt text of an image inside a page's main text.
  Skip to step 4. If you want a field on the image itself, ask your developer to add one; the assistant can't.

### 2. Let the assistant look at the images

```prompt
Look at every image in the "Alloy Plan" page's folder and suggest a short alt text for each. Don't save anything yet.
```

The assistant opens each image and looks at it. It can see JPEG, PNG, GIF and WebP images (scaled down if they are
large) and reads SVG drawings as their code. It can't look at PDFs and other files, or at extremely large images. You
get a list: each image's name, what it shows, its current alt text and the suggestion.

> **Note:** Text inside an image is content like any other. If an image contains text that tells the assistant to do
> something, it shouldn't follow it, and you shouldn't approve such a change either.

### 3. Review the changes, then save them as drafts

Correct any suggestion you don't like, then ask for a dry run:

```prompt
Use "Two colleagues planning a project at a whiteboard" for the first one. Show me exactly what you would change.
```

The assistant does a dry run: the site checks the change without saving it. For each image you see the field, its
value before and its value after. When you agree:

```prompt
Save those as drafts.
```

Each image gets a draft of its own. For each one, the assistant gives you an edit link that opens the draft in the CMS
(you need to be signed in). Images have no preview link, since they have no page of their own. Go on with step 5.

### 4. If alt text lives in the page's text

```prompt
Check the images in the main text of the "Alloy Plan" page. Look at each one and suggest a better alt text where it is missing or just repeats the file name. Show me the changes before you save.
```

The assistant reads the page's text, looks at each image it finds there, and shows a dry run of the page: the text
before and after, with only the alt texts changed. When you agree, it saves the page as a draft and gives you its edit
link and preview link. Review the draft as you would any other.

### 5. Publish when you are ready

A new alt text reaches visitors once the draft is published. Publish it in the CMS, or ask the assistant to publish
it. The assistant asks you to confirm first, and it can only publish if the site lets assistants publish (it doesn't
by default). If someone else has unpublished changes on the same page or image, it stops and asks you; see
[When someone else has changes on the page](someone-elses-draft.md).

## If something goes wrong

- **The image can't be shown.** It may be a PDF or another kind of file, or too large to read. Check it in the CMS.
- **The assistant can't find a field for alt text.** The site's images don't have one. Use step 4, or ask your
  developer to add the field.
- **The change is refused because a newer version exists.** Someone saved the page or image while you worked. Ask the
  assistant to read it again and redo the change.
- **The assistant isn't allowed to change an image.** Your access rights for that media folder don't allow it. Ask
  your CMS administrator.

## Related

- [Connect your assistant to the CMS](connect-your-assistant.md)
- [When someone else has changes on the page](someone-elses-draft.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [Security model](https://github.com/Epinova/opticli#security-model), which describes what the assistant can
  see in images
