using System.Text.Json;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// The Visual Builder composition editor (CMS 13) on its own: the structure rules the CMS validates, node lookup by key or
/// name, keys kept and made, and the whole-composition write. The CMS side (blocks, values, display templates) is faked.
/// </summary>
public class CompositionEditorTests
{
    private sealed class Block(string type)
    {
        public string Type { get; } = type;

        public Dictionary<string, string> Values { get; } = [];

        public int Copy { get; init; }
    }

    private sealed class FakeBlocks : ICompositionBlocks
    {
        public List<string> StylesChecked { get; } = [];

        public List<(DraftNode Rows, Guid Blueprint)> Blueprints { get; } = [];

        public static CompositionBlock Of(string type, bool shared = false) => type switch
        {
            "Section" => new(new Block(type), type, shared, shared ? "200" : null, IsSection: true, SectionEnabled: true, ElementEnabled: false),
            "Banner" => new(new Block(type), type, shared, shared ? "201" : null, IsSection: false, SectionEnabled: true, ElementEnabled: false),
            "Text" => new(new Block(type), type, shared, shared ? "103" : null, IsSection: false, SectionEnabled: false, ElementEnabled: true),
            "Teaser" => new(new Block(type), type, shared, shared ? "202" : null, IsSection: false, SectionEnabled: false, ElementEnabled: false),
            _ => throw AgentException.Usage($"No block type {type}."),
        };

        public CompositionBlock New(string type, string where) => Of(type);

        public CompositionBlock Shared(string reference, string where) => reference switch
        {
            "103" => Of("Text", shared: true),
            "200" => Of("Section", shared: true),
            _ => throw AgentException.NotFound($"{where}: no content {reference}."),
        };

        public (CompositionBlock Block, IReadOnlyList<DraftNode> Rows, string? DisplayTemplate, IReadOnlyDictionary<string, string> DisplaySettings) Blueprint(Guid blueprint, string where)
        {
            var row = new DraftNode { NodeType = DraftNode.Row, Key = "blueprint-row", Name = "Row" };
            var column = new DraftNode { NodeType = DraftNode.Column, Key = "blueprint-column" };
            column.Children.Add(new DraftNode { NodeType = DraftNode.Component, Key = "blueprint-element", Name = "Copied", Block = Of("Text") });
            row.Children.Add(column);
            return (Of("Section"), [row], "look", new Dictionary<string, string> { ["tone"] = "dark" });
        }

        public CompositionBlock Writable(CompositionBlock block) =>
            block.Shared ? block : block with { Block = new Block(block.Type) { Copy = ((Block)block.Block).Copy + 1 } };

        public void SetProperties(CompositionBlock block, IReadOnlyDictionary<string, JsonElement> properties, string where)
        {
            foreach (var (name, value) in properties)
            {
                if (name == "Nope")
                {
                    throw AgentException.Usage($"{where}: 'Nope' is not a property of {block.Type}.");
                }
                ((Block)block.Block).Values[name] = value.ToString();
            }
        }

        public string DefaultName(CompositionBlock block) => $"New {block.Type}";

        public void CheckStyle(DraftNode node, string where)
        {
            if (node.DisplayTemplate == "nope")
            {
                throw AgentException.Usage($"{where}: the site has no display template 'nope'.");
            }
            StylesChecked.Add($"{node.Name}:{node.DisplayTemplate}");
        }
    }

    private int _keys;

    /// <summary>An experience: section Hero (row → column "Left" with Intro, column "Right" with a shared text), banner Promo.</summary>
    private (CompositionEditor Editor, FakeBlocks Blocks, DraftNode Root) Experience()
    {
        var root = new DraftNode { NodeType = DraftNode.Experience, Key = "root-key" };
        var hero = new DraftNode { NodeType = DraftNode.Section, Key = "hero", Name = "Hero", Block = FakeBlocks.Of("Section") };
        var row = new DraftNode { NodeType = DraftNode.Row, Key = "row", Name = "Two columns" };
        var left = new DraftNode { NodeType = DraftNode.Column, Key = "left", Name = "Left" };
        var right = new DraftNode { NodeType = DraftNode.Column, Key = "right", Name = "Right" };
        left.Children.Add(new DraftNode { NodeType = DraftNode.Component, Key = "intro", Name = "Intro", Block = FakeBlocks.Of("Text"), DisplayTemplate = "look", DisplaySettings = new(StringComparer.OrdinalIgnoreCase) { ["color"] = "accent" } });
        right.Children.Add(new DraftNode { NodeType = DraftNode.Component, Key = "shared", Name = "Shared", Block = FakeBlocks.Of("Text", shared: true) });
        row.Children.AddRange([left, right]);
        hero.Children.Add(row);
        root.Children.Add(hero);
        root.Children.Add(new DraftNode { NodeType = DraftNode.Component, Key = "promo", Name = "Promo", Block = FakeBlocks.Of("Banner") });
        var blocks = new FakeBlocks();
        return (new CompositionEditor(root, blocks, () => $"new-{++_keys}"), blocks, root);
    }

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static CompositionOperation Add(string? parent, CompositionNodeValue value, int? at = null) => new() { Op = CompositionOps.Add, Parent = parent, Value = value, At = at };

    private static IEnumerable<string> Keys(DraftNode node) => node.Children.Select(c => c.Key!);

    [Fact]
    public void Add_puts_a_new_inline_element_in_a_column_named_by_name_with_a_new_key_and_its_values()
    {
        var (editor, _, root) = Experience();

        editor.Apply([Add("Left", new CompositionNodeValue { Type = "Text", Properties = new Dictionary<string, JsonElement> { ["Heading"] = Json("Hi") } }, at: 0)]);

        var left = editor.Find("left", "", "");
        Assert.Equal(["new-1", "intro"], Keys(left));
        var added = left.Children[0];
        Assert.Equal((DraftNode.Component, "New Text"), (added.NodeType, added.Name));
        Assert.Equal("Hi", ((Block)added.Block!.Block).Values["Heading"]);
        Assert.Same(root, editor.Root);
    }

    [Fact]
    public void Add_without_a_parent_adds_to_the_outline_and_a_section_takes_its_rows_columns_and_elements_in_one_go()
    {
        var (editor, _, root) = Experience();

        editor.Apply([Add(null, new CompositionNodeValue
        {
            Type = "Section",
            Name = "Body",
            Nodes = [new CompositionNodeValue { Nodes = [new CompositionNodeValue { Name = "Only", Nodes = [new CompositionNodeValue { Ref = "103" }] }] }],
        })]);

        var body = root.Children[^1];
        Assert.Equal((DraftNode.Section, "Body"), (body.NodeType, body.Name));
        var element = body.Children.Single().Children.Single().Children.Single();
        Assert.Equal((DraftNode.Row, DraftNode.Column), (body.Children[0].NodeType, body.Children[0].Children[0].NodeType));
        Assert.True(element.Block!.Shared);
        Assert.Equal("103", element.Block.Ref);
    }

    [Fact]
    public void A_section_enabled_block_in_the_outline_is_a_component_whatever_it_is_called()
    {
        var (editor, _, root) = Experience();

        editor.Apply([Add(CompositionOps.Root, new CompositionNodeValue { NodeType = "section", Type = "Banner" })]);

        Assert.Equal(DraftNode.Component, root.Children[^1].NodeType);
    }

    [Theory]
    [InlineData("row", "Text", "a component can't go in")]
    [InlineData("Left", "Section", "can't be placed in a column")]
    [InlineData("Left", "Banner", "only element types")]
    [InlineData(null, "Text", "can't stand in an experience's outline")]
    [InlineData("Intro", "Text", "can't hold other nodes")]
    [InlineData("Shared", "Text", "can't hold other nodes")]
    public void Add_refuses_what_the_CMS_wouldnt_take_there(string? parent, string type, string message)
    {
        var (editor, _, _) = Experience();

        var refused = Assert.Throws<AgentException>(() => editor.Apply([Add(parent, new CompositionNodeValue { NodeType = parent == "row" ? "component" : null, Type = type })]));

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void Add_needs_exactly_one_of_type_ref_and_blueprint_and_no_block_on_a_row()
    {
        var (editor, _, _) = Experience();

        Assert.Contains("exactly one of type", Assert.Throws<AgentException>(() => editor.Apply([Add("Left", new CompositionNodeValue())])).Message);
        Assert.Contains("exactly one of type", Assert.Throws<AgentException>(() => editor.Apply([Add("Left", new CompositionNodeValue { Type = "Text", Ref = "103" })])).Message);
        Assert.Contains("a row has no block", Assert.Throws<AgentException>(() => editor.Apply([Add("Hero", new CompositionNodeValue { Type = "Text" })])).Message);
    }

    [Fact]
    public void A_shared_block_takes_no_properties_of_its_own()
    {
        var (editor, _, _) = Experience();

        var refused = Assert.Throws<AgentException>(() => editor.Apply([Add("Left", new CompositionNodeValue { Ref = "103", Properties = new Dictionary<string, JsonElement> { ["Heading"] = Json("x") } })]));

        Assert.Contains("shared content", refused.Message);
        Assert.Contains("set 103", refused.Hint);
    }

    [Fact]
    public void A_new_nodes_key_must_be_an_unused_GUID()
    {
        var (editor, _, _) = Experience();
        var key = Guid.NewGuid();

        editor.Apply([Add("Left", new CompositionNodeValue { Key = key.ToString("N"), Type = "Text" })]);

        Assert.Equal(key.ToString(), editor.Find("Left", "", "").Children[^1].Key);
        Assert.Contains("isn't a GUID", Assert.Throws<AgentException>(() => editor.Apply([Add("Left", new CompositionNodeValue { Key = "mine", Type = "Text" })])).Message);
        Assert.Contains("already has a node", Assert.Throws<AgentException>(() => editor.Apply([Add("Left", new CompositionNodeValue { Key = key.ToString(), Type = "Text" })])).Message);
    }

    [Fact]
    public void A_section_from_a_blueprint_gets_its_rows_with_new_keys_and_its_look()
    {
        var (editor, _, root) = Experience();

        editor.Apply([Add(null, new CompositionNodeValue { Blueprint = Guid.NewGuid(), Name = "Copy" }, at: 1)]);

        var copy = root.Children[1];
        Assert.Equal(("Copy", "look", "dark"), (copy.Name, copy.DisplayTemplate, copy.DisplaySettings["tone"]));
        Assert.All(CompositionEditor.All(copy).Skip(1), n => Assert.StartsWith("new-", n.Key));
        Assert.Equal("Copied", CompositionEditor.All(copy).Last().Name);
    }

    [Fact]
    public void Move_takes_a_node_to_another_parent_or_position_and_refuses_moves_the_structure_doesnt_allow()
    {
        var (editor, _, root) = Experience();

        editor.Apply([new CompositionOperation { Op = CompositionOps.Move, Node = "Intro", Parent = "Right", At = 0 }]);
        editor.Apply([new CompositionOperation { Op = CompositionOps.Move, Node = "promo", At = 0 }]);

        Assert.Equal(["intro", "shared"], Keys(editor.Find("Right", "", "")));
        Assert.Empty(editor.Find("Left", "", "").Children);
        Assert.Equal(["promo", "hero"], Keys(root));
        Assert.Contains("into itself", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Move, Node = "Hero", Parent = "Right" }])).Message);
        Assert.Contains("can't stand in an experience's outline", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Move, Node = "Intro", Parent = "root" }])).Message);
        Assert.Contains("out of range", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Move, Node = "Intro", At = 5 }])).Message);
    }

    [Fact]
    public void Remove_takes_the_node_with_everything_in_it_but_not_the_composition()
    {
        var (editor, _, root) = Experience();

        editor.Apply([new CompositionOperation { Op = CompositionOps.Remove, Node = "HERO" }]);

        Assert.Equal(["promo"], Keys(root));
        Assert.Contains("can't be removed", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Remove, Node = "root" }])).Message);
    }

    [Fact]
    public void Set_changes_a_name_styles_and_an_inline_blocks_values_on_a_copy_of_it()
    {
        var (editor, blocks, _) = Experience();
        var intro = editor.Find("Intro", "", "");
        var original = intro.Block!.Block;

        editor.Apply([new CompositionOperation
        {
            Op = CompositionOps.Set,
            Node = "intro",
            Value = new CompositionNodeValue
            {
                Name = "Opening",
                DisplaySettings = new Dictionary<string, string?> { ["color"] = null, ["size"] = "large" },
                Properties = new Dictionary<string, JsonElement> { ["Heading"] = Json("Changed") },
            },
        }]);

        Assert.Equal("Opening", intro.Name);
        Assert.Equal(new Dictionary<string, string> { ["size"] = "large" }, intro.DisplaySettings);
        Assert.NotSame(original, intro.Block.Block);
        Assert.Empty(((Block)original).Values);
        Assert.Equal(1, ((Block)intro.Block.Block).Copy);
        Assert.Equal(["Opening:look"], blocks.StylesChecked);

        editor.Apply([new CompositionOperation { Op = CompositionOps.Set, Node = "Opening", Value = new CompositionNodeValue { DisplayTemplate = "" } }]);
        Assert.Null(intro.DisplayTemplate);
        Assert.Empty(intro.DisplaySettings);
    }

    [Fact]
    public void Set_refuses_a_shared_blocks_values_a_block_change_and_names_on_the_composition_itself()
    {
        var (editor, _, root) = Experience();

        Assert.Contains("shared content", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation
        {
            Op = CompositionOps.Set, Node = "Shared", Value = new CompositionNodeValue { Properties = new Dictionary<string, JsonElement> { ["Heading"] = Json("x") } },
        }])).Message);
        Assert.Contains("not its block", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Set, Node = "Intro", Value = new CompositionNodeValue { Type = "Text" } }])).Message);
        Assert.Contains("no name of its own", Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Set, Value = new CompositionNodeValue { Name = "x" } }])).Message);

        editor.Apply([new CompositionOperation { Op = CompositionOps.Set, Node = "root", Value = new CompositionNodeValue { DisplayTemplate = "page" } }]);
        Assert.Equal("page", root.DisplayTemplate);
    }

    [Fact]
    public void A_display_template_the_site_doesnt_have_is_refused_by_the_check()
    {
        var (editor, _, _) = Experience();

        var refused = Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = CompositionOps.Set, Node = "Intro", Value = new CompositionNodeValue { DisplayTemplate = "nope" } }]));

        Assert.Contains("no display template 'nope'", refused.Message);
    }

    [Fact]
    public void Nodes_are_found_by_key_or_by_a_name_only_one_node_has()
    {
        var (editor, _, root) = Experience();
        editor.Apply([Add("Right", new CompositionNodeValue { Type = "Text", Name = "Twin" }), Add("Left", new CompositionNodeValue { Type = "Text", Name = "Twin" })]);

        Assert.Same(root, editor.Find(null, "", ""));
        Assert.Same(root, editor.Find(" ROOT ", "", ""));
        Assert.Equal("intro", editor.Find("INTRO", "", "").Key);
        Assert.Equal("intro", editor.Find("Intro", "", "").Key);
        var ambiguous = Assert.Throws<AgentException>(() => editor.Find("twin ", "where", "node"));
        Assert.Contains("2 nodes are named 'twin'", ambiguous.Message);
        Assert.Contains("by its key", ambiguous.Hint);
        var missing = Assert.Throws<AgentException>(() => editor.Find("Footer", "where", "node"));
        Assert.Equal(AgentErrorCodes.NotFound, missing.Code);
        Assert.Contains("section \"Hero\" (hero)", missing.Hint);
    }

    [Fact]
    public void A_whole_composition_keeps_the_blocks_of_the_keys_it_names_sets_their_given_values_and_drops_the_rest()
    {
        var (editor, _, root) = Experience();
        var introBlock = editor.Find("intro", "", "").Block!.Block;

        editor.Replace(new CompositionNodeValue
        {
            DisplayTemplate = "page",
            Nodes =
            [
                new CompositionNodeValue
                {
                    Key = "hero",
                    Nodes =
                    [
                        new CompositionNodeValue
                        {
                            Key = "row",
                            Nodes =
                            [
                                new CompositionNodeValue
                                {
                                    Key = "right",
                                    Name = "Only column",
                                    Nodes =
                                    [
                                        new CompositionNodeValue { Key = "shared" },
                                        new CompositionNodeValue { Key = "intro", Properties = new Dictionary<string, JsonElement> { ["Body"] = Json("<p>b</p>") } },
                                        new CompositionNodeValue { Type = "Text", Name = "New one" },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        });

        Assert.Equal(["hero"], Keys(root));
        Assert.Equal("page", root.DisplayTemplate);
        var column = editor.Find("right", "", "");
        Assert.Equal("Only column", column.Name);
        Assert.Equal(["shared", "intro", "new-1"], Keys(column));
        var intro = column.Children[1];
        // A copy of its block with the given value; its styles are as given (none).
        Assert.Equal("<p>b</p>", ((Block)intro.Block!.Block).Values["Body"]);
        Assert.NotSame(introBlock, intro.Block.Block);
        Assert.Null(intro.DisplayTemplate);
        Assert.Equal("Intro", intro.Name);
        Assert.Throws<AgentException>(() => editor.Find("left", "", ""));
    }

    [Fact]
    public void A_whole_composition_refuses_unknown_and_repeated_keys_and_a_changed_type()
    {
        var (editor, _, _) = Experience();

        Assert.Contains("no node of the composition has the key", Assert.Throws<AgentException>(() => editor.Replace(new CompositionNodeValue { Nodes = [new CompositionNodeValue { Key = "gone" }] })).Message);
        Assert.Contains("given twice", Assert.Throws<AgentException>(() => editor.Replace(new CompositionNodeValue { Nodes = [new CompositionNodeValue { Key = "promo" }, new CompositionNodeValue { Key = "promo" }] })).Message);
        Assert.Contains("is a Text, not a Teaser", Assert.Throws<AgentException>(() => editor.Replace(new CompositionNodeValue
        {
            Nodes = [new CompositionNodeValue { Key = "hero", Nodes = [new CompositionNodeValue { Key = "row", Nodes = [new CompositionNodeValue { Key = "left", Nodes = [new CompositionNodeValue { Key = "intro", Type = "Teaser" }] }] }] }],
        })).Message);
    }

    [Fact]
    public void A_whole_composition_can_move_a_node_by_its_key_where_the_structure_allows_it()
    {
        var (editor, _, root) = Experience();

        editor.Replace(new CompositionNodeValue
        {
            Nodes =
            [
                new CompositionNodeValue { Key = "promo" },
                new CompositionNodeValue { Key = "hero", Nodes = [new CompositionNodeValue { Key = "row", Nodes = [new CompositionNodeValue { Key = "left", Nodes = [new CompositionNodeValue { Key = "shared" }, new CompositionNodeValue { Key = "intro" }] }] }] },
            ],
        });

        Assert.Equal(["promo", "hero"], Keys(root));
        Assert.Equal(["shared", "intro"], Keys(editor.Find("left", "", "")));
        Assert.Contains("can't stand in an experience's outline", Assert.Throws<AgentException>(() => editor.Replace(new CompositionNodeValue { Nodes = [new CompositionNodeValue { Key = "intro" }] })).Message);
    }

    [Fact]
    public void Unknown_ops_are_refused_with_the_ones_there_are()
    {
        var (editor, _, _) = Experience();

        var refused = Assert.Throws<AgentException>(() => editor.Apply([new CompositionOperation { Op = "rename" }]));

        Assert.Contains("unknown op 'rename'", refused.Message);
        Assert.Contains("add, remove, move or set", refused.Hint);
    }
}
