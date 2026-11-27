---
title: Let assistants change things outside the CMS
summary: Write action tools so editors' assistants can save and publish events in a calendar service the site keeps outside the CMS, with the same dry run, confirmation and audit as content changes.
audience: mcp
order: 3
written-for: 0.38.0
---

Not everything editors work on is CMS content. Offers in a commerce system, events in a calendar service, a search
index: the site has its own code and editor UI for them. An action tool lets the assistant change them too, in the same
flow as the module's own changes: a dry run the editor sees, a confirmation, a token that runs only what was shown, the
publish gate and the audit log. In this guide an Alloy site keeps its events in a calendar service
(`calendar.example.com`), with drafts and publishing of its own. You add two tools: `save_event`, which saves an
event's draft, and `publish_event`, which puts it live.

## Before you start

- The module installed and working in your site: see [Install the MCP module in your site](install-the-module.md).
- opticli 0.38.0 or newer.
- Read [Write your first site-specific tool](custom-tools.md) first: action tools take arguments, names and
  descriptions the same way.
- The service's client in the site, here `IEventCalendar`, with `GetAsync`, `SaveDraftAsync`, `PublishAsync` and
  `Validate`. Each event belongs to a calendar page in the CMS.

## What the module does, and what your code does

A write tool returns a plan, and the module makes the changes itself, with the CMS's access checks. An action tool
makes the change in its own code, which the module can't see. So two things are yours:

- **The preview is honest.** It lists everything the run will change, with the values before and after.
- **The editor may make the change.** The calendar service doesn't know the CMS's access rights. Check them on the
  content the event belongs to, its calendar page, with `IOptiCliEditor.HasAccess`, and check that the event is on
  that page: the assistant names both, and an event id can name any event.

The module does the rest: the scopes, the publish and delete gates, the token, a record that makes each token run once,
and an audit line per thing changed.

## Build the tools

### 1. Write the save tool

```csharp
using System.ComponentModel;
using EPiServer.Core;
using EPiServer.Security;
using OptiCli.Mcp;

public sealed record SaveEventArgs(
    [property: Description("The id of the calendar page the event is on.")] int Calendar,
    [property: Description("The event's title.")] string Title,
    [property: Description("When it starts, ISO 8601.")] DateTimeOffset Starts,
    [property: Description("The event's id; leave it out to create one.")] string? Event = null);

[OptiCliActionTool("save_event", Title = "Save event", Effects = ActionEffects.Change)]
[Description("Saves an event in the calendar as a draft, or creates one. Editors review drafts in the calendar's own editor.")]
public sealed class SaveEventTool(IOptiCliEditor editor, IEventCalendar calendar) : IOptiCliActionTool<SaveEventArgs>
{
    public async Task<ActionPreview> PreviewAsync(SaveEventArgs args, CancellationToken cancellationToken)
    {
        var page = editor.Load<IContent>(new ContentReference(args.Calendar));
        if (!editor.HasAccess(page, AccessLevel.Edit))
        {
            throw new OptiCliToolException("refused", "You can't edit events on this calendar.", "Ask someone with Edit access on the calendar page.");
        }
        var current = args.Event is null ? null : await calendar.GetAsync(args.Event, cancellationToken);
        if (args.Event is not null && current?.Calendar != args.Calendar)
        {
            // Missing, or on another calendar page, whose access rights weren't checked.
            throw new OptiCliToolException("not_found", $"No event {args.Event} on this calendar.", "list_events gives the ids.");
        }

        var preview = new ActionPreview();
        var target = preview.Add("event", current?.Id, args.Title, ActionEffects.Change);
        if (current is not null)
        {
            target.Version(current.SavedUtc.ToString("O")).Links(editUrl: current.EditUrl);
        }
        if (args.Title != current?.Title)
        {
            target.Change("title", current?.Title, args.Title);
        }
        if (args.Starts != current?.Starts)
        {
            target.Change("starts", current?.Starts, args.Starts);
        }
        if (await calendar.TitleTakenAsync(args.Calendar, args.Title, current?.Id, cancellationToken))
        {
            target.Problem($"Another event on this calendar is called {args.Title}.", "title");
        }
        return preview;
    }

    public async Task RunAsync(SaveEventArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        var saved = await calendar.SaveDraftAsync(args.Calendar, args.Event, args.Title, args.Starts, run.Key, cancellationToken);
        run.Done(preview.Targets[0], "saved", id: saved.Id, editUrl: saved.EditUrl);
    }
}
```

- `Add` names the thing the call changes: its kind, its id (`null` for one the run creates), what the user knows it
  by, and its effect. A call that changes several things adds a target for each, in the order `RunAsync` changes them.
- `Change` adds only the fields that change. The dry run shows them, and the token covers them.
- `Version` is the version the change is made from. If someone saves the event between the dry run and the run, the
  run is refused with a `conflict`. Without a version, the `before` values do that for the fields the tool changes.
- `Problem` stops the dry run from giving a token. Put the service's own validation here, so the assistant hears about
  it before the editor agrees, not after.
- `run.Done` reports a target as soon as it is done. If the next one throws, the result still says this one was done.
- `run.Key` is the same for every call with the token. Pass it to a service that takes a key to ignore a repeated
  request.

### 2. Write the publish tool

The same shape, with `ActionEffects.Live`: publishing changes what visitors see.

```csharp
public sealed record PublishEventArgs(
    [property: Description("The id of the calendar page the event is on.")] int Calendar,
    [property: Description("The event's id (list_events).")] string Event);

[OptiCliActionTool("publish_event", Title = "Publish event", Effects = ActionEffects.Live)]
[Description("Publishes an event's draft in the calendar, so visitors see it.")]
public sealed class PublishEventTool(IOptiCliEditor editor, IEventCalendar calendar) : IOptiCliActionTool<PublishEventArgs>
{
    public async Task<ActionPreview> PreviewAsync(PublishEventArgs args, CancellationToken cancellationToken)
    {
        var page = editor.Load<IContent>(new ContentReference(args.Calendar));
        if (!editor.HasAccess(page, AccessLevel.Publish))
        {
            throw new OptiCliToolException("refused", "You can't publish events on this calendar.", "Ask someone with Publish access on the calendar page.");
        }
        var item = await calendar.GetAsync(args.Event, cancellationToken);
        if (item is null || item.Calendar != args.Calendar)
        {
            // Missing, or on another calendar page, whose access rights weren't checked.
            throw new OptiCliToolException("not_found", $"No event {args.Event} on this calendar.", "list_events gives the ids.");
        }
        var preview = new ActionPreview();
        var target = preview.Add("event", item.Id, item.Title, ActionEffects.Live)
            .Version(item.SavedUtc.ToString("O"))
            .Change("status", item.Status, "published")
            .Links(url: item.PublicUrl, editUrl: item.EditUrl);
        foreach (var error in calendar.Validate(item))
        {
            target.Problem(error.Message, error.Field);
        }
        if (item.Featured)
        {
            preview.Note("It is featured: visitors see it on the front page too.");
        }
        return preview;
    }

    public async Task RunAsync(PublishEventArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        var target = preview.Targets[0];
        await calendar.PublishAsync(target.Id!, run.Key, cancellationToken);
        run.Done(target, "published");
    }
}
```

A `Live` target needs the site to let assistants publish (`AllowPublish`) and the editor to have approved
`content:publish`, as `publish_content` does. Where they haven't, the assistant gets `publishingOff` and tells the
editor to publish in the calendar's own editor.

### 3. Register them

```csharp
services.AddOptiCliMcp()
    .WithActionTool<SaveEventTool>()
    .WithActionTool<PublishEventTool>()
    .WithInstructions("Events live in the calendar service, not in the CMS: change them with save_event and publish_event, never with update_content.");
```

The module checks names, attributes and argument types at startup, as for the other tools.

## Try the dry run and confirm flow

Rebuild, connect Claude Code as an editor, and ask:

```prompt
Move the summer concert on the Events calendar to 20 June at 19:00, and publish it once I've seen the change.
```

The assistant calls `save_event` with `dryRun: true`. The answer shows the target and its changes:

```json
{
  "tool": "save_event",
  "dryRun": true,
  "valid": true,
  "planToken": "CfDJ8…",
  "planTokenExpires": "2026-11-27T14:30:00+00:00",
  "targets": [
    {
      "target": 1,
      "kind": "event",
      "id": "ev-1042",
      "label": "Summer concert",
      "effect": "change",
      "version": "2026-11-20T09:12:44.0000000Z",
      "changes": [ { "field": "starts", "before": "2027-06-21T19:00:00+02:00", "after": "2027-06-20T19:00:00+02:00" } ],
      "editUrl": "https://calendar.example.com/edit/ev-1042"
    }
  ]
}
```

When you agree, the assistant calls the tool again with the same arguments, `dryRun: false` and the token. Then it does
the same for `publish_event`. Each result has the `editUrl` and `url` you gave the target, which the assistant gives you
instead of a link into CMS edit mode.

A token runs once. If the call times out and the assistant tries again with the same token, the module answers with
what the first call did and runs nothing again. If the site stopped in the middle of a run, the answer says so, and the
assistant asks you to check the event before a new dry run.

## Test it

- **Access rights.** Take Edit access on the calendar page away from your test editor. The dry run must be refused,
  and a calendar page they can't read must look missing (`not_found`). An event of another calendar page, named with
  a page they may edit, must be `not_found` too.
- **Change under the token.** Do a dry run, save the event in the calendar's own editor, then confirm. The run must be
  a `conflict`.
- **Run twice.** Confirm with the same token again. The answer has `repeated: true` and the service gets no second
  request.
- **Publishing off.** With `AllowPublish` off, `publish_event` must answer `publishingOff` before it asks the service
  for anything.
- **Check the audit log.** With `OptiCli.Mcp.Audit` at Information, a real run has a line per target with its effect,
  outcome and `event:ev-1042`. Titles and values aren't logged.

## If something goes wrong

- **The assistant gets `internal`:** your tool threw something other than `OptiCliToolException`, previewed a target
  with an effect its `Effects` don't have, or returned from `RunAsync` without reporting a target. The site's log has
  the details.
- **"The changes aren't the ones the dry run showed" on every run:** the preview isn't the same for the same state.
  Look for the current time, new GUIDs or an unsorted list in it.
- **A create runs again after a timeout:** the same token can't do that, but a new dry run will preview the create
  again if it doesn't find the event the first run made. Pass `run.Key` to the service, or look the event up by title
  in the preview.

## Related

- [Write your first site-specific tool](custom-tools.md)
- Reference: [Action tools](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/tools.md#action-tools), with every member of the
  preview, the effects and what they need, and the rules a tool must follow
