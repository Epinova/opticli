---
title: Connect your assistant to the CMS
summary: Connect Claude or another assistant to your Optimizely site, sign in with your own CMS account, and check that it works.
audience: editors
order: 1
written-for: 0.30.0
---

By the end of this guide your AI assistant is connected to your Optimizely CMS site. It works as you: it sees what you
can see in the CMS, and it can only change what you may change. You sign in with the same login you use for the CMS,
and you decide on a consent page what the assistant may do. Changes are saved as drafts unless you ask for more.

## Before you start

- Your developer has installed the opticli module on the site. Ask them for the site's address for assistants.
- You have an editor account on the site. Your developer decides which roles may connect an assistant.
- You have an assistant that can connect to an outside server and sign in through your browser: Claude Desktop,
  Claude on the web (claude.ai) or Claude Code, or another assistant with that kind of connection.

## Steps

### 1. Get the address from your developer

There are two forms of the address. Which one you need depends on the assistant:

| Address | Used by |
|---|---|
| The site itself, such as `https://www.example.com` | the Claude Desktop extension, which adds the rest |
| The full address, such as `https://www.example.com/episerver/opticli/mcp` | every other assistant |

Your developer may give you an address on a host of its own instead. Use the one they give you.

### 2. Add the site to your assistant

**Claude Desktop (recommended).** Ask your developer for the extension file, `opticli-cms.mcpb`. It is also attached
to every [opticli release](https://github.com/epinova/opticli/releases). Open the file, or drag it into Claude
Desktop's Settings → Extensions. Fill in **CMS address** with the site's address. Your developer may give you an
extension made for your site instead: it has the address built in, so you aren't asked for it, and you can install
one for the test site and one for the live site side by side. The extension connects from your own computer, so it
also works when the site is only reachable on your company network. You don't need to install anything else.

**Claude on the web, on mobile, or Claude Desktop without the extension.** Add a custom connector with the full
address. On Team and Enterprise plans, an owner of your organization may have to add it for everyone. A connector
calls the site from Anthropic's servers, not from your computer, so the site must be reachable from the internet.

**Claude Code.** Run `claude mcp add --transport http cms <full address>`, then type `/mcp` in Claude Code to sign in.

**Other assistants.** Add a remote server (sometimes called a connector or an MCP server) with the full address. An
assistant on your own computer connects as it is. An assistant that runs in the browser, such as ChatGPT on the web,
is refused until your developer allows it (see "If something goes wrong" below).

### 3. Sign in and allow the connection

The first time the assistant uses the site, your browser opens the site's own login page. Sign in as you always do.
Then a consent page asks **Connect** followed by the assistant's name. It shows:

- who you are signed in as;
- who published the assistant's description, or a warning that the app named itself and the site can't confirm who
  made it. Only allow it if you added the connection yourself, just now;
- what the assistant may do, with a checkbox each: read the content you have access to (always), create content and
  change drafts, and, only where the site allows it, publish and unpublish content.

Untick anything you don't want the assistant to do, then choose **Allow**. You are sent back to the assistant.

> **Note:** If the site doesn't let assistants publish, the consent page says so. Then you publish in the CMS
> yourself, which is the default.

### 4. Check who the assistant is

Ask the assistant who it works as:

```prompt
Who am I on the CMS, and what are you allowed to do there?
```

The assistant answers with your name and roles in the CMS, the name of the app you connected, what you allowed on the
consent page, and what the site allows: publishing, deleting, the largest file it may upload, and whether it may open
short-lived preview links of its own. This proves three things: the connection works, the assistant acts as you, and
it has no more rights than you have.

### 5. Try a first read

```prompt
Find the "About us" page and tell me what's on it.
```

The assistant finds the page, reads it and sums it up. Nothing is changed by reading.

### 6. See or revoke your connections

Open `/episerver/opticli/connections` on the site (your developer may have moved it). It lists the assistants you
connected, what you allowed each one, when it connected and when it was last used. **Revoke** cuts an assistant off
at once. A connection also ends on its own after a while (30 days unless your developer changed it), and then you sign
in again the same way.

> **Note:** Don't use this connection in the same chat as tools that open web pages or read other outside content.
> Text on such a page is then one step away from your CMS.

## If something goes wrong

- **"Return address not allowed".** The site doesn't accept connections from this web-based assistant. Only your
  developer can fix that, by adding the assistant's address to the site's allowed list (the `AllowedRedirectHosts`
  setting). Claude on the web is allowed by default. Until then, use an assistant that runs on your own computer, such
  as the Claude Desktop extension.
- **"No access" and "isn't an editor on this site".** Your account doesn't have a role that may connect an assistant.
  Ask your CMS administrator.
- **"Unknown app".** Remove the connector or server in your assistant and add it again.
- **A web-based assistant can't reach the site at all.** The site is probably only on your company network. Use the
  Claude Desktop extension, or ask your developer.
- **The assistant says it isn't allowed to change or publish anything.** You may have unticked it on the consent page,
  or the site has it turned off. Revoke the connection and connect again to choose differently.
- **"The connection is as old as this site allows; connect again."** Remove the connection in your assistant, add it
  again and sign in as in step 3.

## Related

- [Translate a page with your assistant](translate-a-page.md)
- [Fix image alt texts with your assistant](image-alt-texts.md)
- [When someone else has changes on the page](someone-elses-draft.md)
- [Opticli for editors](https://epinova.github.io/opticli/editors/)
- README: [Connecting an assistant](https://github.com/Epinova/opticli#connecting-an-assistant),
  [Connecting Claude](https://github.com/Epinova/opticli#connecting-claude) and
  [Security model](https://github.com/Epinova/opticli#security-model)
