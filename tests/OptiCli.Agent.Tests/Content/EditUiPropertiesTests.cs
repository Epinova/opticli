using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// Which properties an editor sees and may change: what the CMS edit UI shows them, from the model, the site's tabs and
/// the CMS UI's own metadata. The developer sees and changes everything.
/// </summary>
public class EditUiPropertiesTests
{
    private const int AdminTab = 42;

    private static IServiceProvider Services(IEditUiMetadata? metadata = null)
    {
        var tabs = Recorder<ITabDefinitionRepository>.Create();
        tabs.Recorder.Answer = (method, _) => method.Name == nameof(ITabDefinitionRepository.List)
            ? new[] { new TabDefinition { ID = AdminTab, Name = "Admin", RequiredAccess = AccessLevel.Administer } }
            : null;
        var services = new ServiceCollection()
            .AddSingleton(tabs.Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])));
        if (metadata is not null)
        {
            services.AddSingleton(metadata);
        }
        return services.BuildServiceProvider();
    }

    private static EditUiProperties Editor(IEditUiMetadata? metadata = null) =>
        new CmsCall(Services(metadata), CancellationToken.None, CmsCaller.Editor).Properties;

    private static EditUiProperties Developer() => new CmsCall(Services(), CancellationToken.None, CmsCaller.Developer).Properties;

    [Fact]
    public void The_model_and_the_sites_settings_hide_or_lock_properties_for_an_editor()
    {
        var block = FieldsBlock.Create();
        var editor = Editor();

        Assert.Equal(PropertyAccess.Editable, editor.Access(block, block.Property["Heading"]));
        Assert.Equal(PropertyAccess.Hidden, editor.Access(block, block.Property["NotInEditMode"]));
        Assert.Equal(PropertyAccess.Hidden, editor.Access(block, block.Property["Scaffolded"]));
        Assert.Equal(PropertyAccess.ReadOnly, editor.Access(block, block.Property["Locked"]));
        Assert.Equal(PropertyAccess.ReadOnly, editor.Access(block, block.Property["ReadOnlyOne"]));
        Assert.All(block.Property, p => Assert.Equal(PropertyAccess.Editable, Developer().Access(block, p)));
    }

    [Fact]
    public void A_tab_whose_required_access_the_editor_lacks_on_the_content_hides_its_properties()
    {
        var page = new CmsCallTests.Secured(new ContentReference(5), AccessLevel.Read | AccessLevel.Edit | AccessLevel.Publish);
        page.Property.Add("Scripts", new PropertyString { PropertyDefinitionID = 1, OwnerTab = AdminTab });
        page.Property.Add("Teaser", new PropertyString { PropertyDefinitionID = 2, OwnerTab = 0 });

        Assert.Equal(PropertyAccess.Hidden, Editor().Access(page, page.Property["Scripts"]));
        Assert.Equal(PropertyAccess.Editable, Editor().Access(page, page.Property["Teaser"]));
        var admin = new CmsCallTests.Secured(new ContentReference(5), AccessLevel.FullAccess);
        admin.Property.Add("Scripts", new PropertyString { PropertyDefinitionID = 1, OwnerTab = AdminTab });
        Assert.Equal(PropertyAccess.Editable, Editor().Access(admin, admin.Property["Scripts"]));
    }

    [Fact]
    public void The_CMS_UIs_metadata_hides_or_locks_more_but_never_opens_what_the_model_closes()
    {
        var block = FieldsBlock.Create();
        var editor = Editor(new Metadata(new() { ["Heading"] = PropertyAccess.ReadOnly, ["Teaser"] = PropertyAccess.Hidden, ["Scaffolded"] = PropertyAccess.Editable }));

        Assert.Equal(PropertyAccess.ReadOnly, editor.Access(block, block.Property["Heading"]));
        Assert.Equal(PropertyAccess.Hidden, editor.Access(block, block.Property["Teaser"]));
        Assert.Equal(PropertyAccess.Hidden, editor.Access(block, block.Property["Scaffolded"]));
    }

    [Fact]
    public void The_edit_UIs_lock_outside_the_master_language_is_left_to_opticlis_own_check()
    {
        var swedish = new CmsCallTests.Localized(new ContentReference(5), CultureInfo.GetCultureInfo("sv"));
        swedish.Property.Add("Shared", new PropertyString { PropertyDefinitionID = 1, IsLanguageSpecific = false });
        swedish.Property.Add("Translated", new PropertyString { PropertyDefinitionID = 2, IsLanguageSpecific = true });
        var editor = Editor(new Metadata(new() { ["Shared"] = PropertyAccess.ReadOnly, ["Translated"] = PropertyAccess.ReadOnly }));

        Assert.Equal(PropertyAccess.Editable, editor.Access(swedish, swedish.Property["Shared"]));
        Assert.Equal(PropertyAccess.ReadOnly, editor.Access(swedish, swedish.Property["Translated"]));
    }

    [Fact]
    public void Writing_a_property_the_edit_UI_doesnt_let_the_editor_change_is_refused_and_its_changes_arent_shown()
    {
        var block = FieldsBlock.Create();
        var editor = Editor();

        var refused = Assert.Throws<AgentException>(() => editor.RequireEditable(block, block.Property["Locked"]));
        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains("not editable in the CMS edit UI for you", refused.Message);
        Assert.DoesNotContain("Scaffolded", refused.Hint);
        Assert.Contains("Heading", refused.Hint);
        Developer().RequireEditable(block, block.Property["Locked"]);

        var value = JsonSerializer.SerializeToElement("x");
        PropertyChange[] changes = [new("Heading", null, value), new("NotInEditMode", null, value), new("Name", null, value)];
        Assert.Equal(["Heading", "Name"], editor.Shown(block, changes).Select(c => c.Property));
        Assert.Equal(3, Developer().Shown(block, changes).Count);
    }

    [Fact]
    public void When_the_CMS_UIs_metadata_cant_be_built_the_model_decides_what_is_shown_and_nothing_may_be_changed()
    {
        var block = FieldsBlock.Create();
        var editor = Editor(new Metadata(null));

        Assert.True(editor.Shown(block, block.Property["Heading"]));
        Assert.False(editor.Shown(block, block.Property["Scaffolded"]));
        var refused = Assert.Throws<AgentException>(() => editor.RequireEditable(block, block.Property["Heading"]));
        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("couldn't be told", refused.Message);
        // The developer never asks.
        new CmsCall(Services(new Metadata(null)), CancellationToken.None, CmsCaller.Developer).Properties.RequireEditable(block, block.Property["Heading"]);
    }

    [Fact]
    public void A_diff_of_a_local_block_or_a_block_list_leaves_out_what_the_editor_doesnt_see_inside_it()
    {
        var page = new CmsCallTests.Secured(new ContentReference(5), AccessLevel.FullAccess);
        var local = FieldsBlock.Create();
        page.Property.Add("Local", new PropertyBlock<FieldsBlock>(local) { PropertyDefinitionID = 10 });
        var editor = Editor();
        JsonElement Block(string heading) => JsonSerializer.SerializeToElement(new { Heading = heading, Scaffolded = "secret", Locked = "locked", Unknown = "kept" });

        var shown = Assert.Single(editor.Shown(page, [new PropertyChange("Local", Block("before"), Block("after"))]));

        Assert.Equal("""{"Heading":"before","Unknown":"kept"}""", shown.Before!.Value.GetRawText());
        Assert.Equal("""{"Heading":"after","Unknown":"kept"}""", shown.After!.Value.GetRawText());
        Assert.Contains("secret", Developer().Shown(page, [new PropertyChange("Local", Block("before"), Block("after"))])[0].After!.Value.GetRawText());
    }

    [Fact]
    public void A_type_shows_an_editor_only_the_properties_its_settings_make_editable()
    {
        var editor = Editor();
        PropertyDefinition Definition(bool displayEditUI = true) => new() { Name = "X", DisplayEditUI = displayEditUI };

        Assert.True(editor.Shown(Definition(), typeof(FieldsBlock).GetProperty(nameof(FieldsBlock.Heading))));
        Assert.True(editor.Shown(Definition(), null));
        Assert.False(editor.Shown(Definition(displayEditUI: false), null));
        Assert.False(editor.Shown(Definition(), typeof(FieldsBlock).GetProperty(nameof(FieldsBlock.Scaffolded))));
        Assert.False(editor.Shown(Definition(), typeof(FieldsBlock).GetProperty(nameof(FieldsBlock.Locked))));
        Assert.True(Developer().Shown(Definition(displayEditUI: false), null));
    }

    /// <summary>A block type with every kind of setting that hides or locks a property.</summary>
    public class FieldsBlock : BlockData
    {
        public virtual string? Heading { get; set; }

        public virtual string? Teaser { get; set; }

        /// <summary>Not displayed in edit mode, as admin mode sets it (the property's definition).</summary>
        public virtual string? NotInEditMode { get; set; }

        [ScaffoldColumn(false)]
        public virtual string? Scaffolded { get; set; }

        [Editable(false)]
        public virtual string? Locked { get; set; }

        [ReadOnly(true)]
        public virtual string? ReadOnlyOne { get; set; }

        public static FieldsBlock Create()
        {
            var block = new FieldsBlock();
            var id = 1;
            foreach (var name in new[] { nameof(Heading), nameof(Teaser), nameof(NotInEditMode), nameof(Scaffolded), nameof(Locked), nameof(ReadOnlyOne) })
            {
                block.Property.Add(name, new PropertyString { PropertyDefinitionID = id++, DisplayEditUI = name != nameof(NotInEditMode) });
            }
            return block;
        }
    }

    /// <summary>The CMS UI's metadata, faked: the same restrictions for every owner, or none to be had (null).</summary>
    private sealed class Metadata(Dictionary<string, PropertyAccess>? restricted) : IEditUiMetadata
    {
        public IReadOnlyDictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>? Restricted(IContentData owner) =>
            restricted is null ? null : new Dictionary<IContentData, IReadOnlyDictionary<string, PropertyAccess>>(ReferenceEqualityComparer.Instance) { [owner] = restricted };
    }
}
