using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCli.Mcp.Tests;

/// <summary>The wrong-major guard: each build runs on its own CMS major only, and names the package for the other.</summary>
public class CmsMajorTests
{
    [Fact]
    public void The_build_is_for_the_CMS_it_was_built_against()
    {
#if CMS13
        Assert.Equal((13, "OptiCli.Mcp.Cms13"), (CmsMajor.Supported, CmsMajor.Package));
#else
        Assert.Equal((12, "OptiCli.Mcp"), (CmsMajor.Supported, CmsMajor.Package));
#endif
        Assert.Equal(CmsMajor.Supported, CmsMajor.Loaded);
        Assert.Null(CmsMajor.Refusal(CmsMajor.Loaded));
        CmsMajor.Check(CmsMajor.Loaded);
    }

    [Fact]
    public void Another_CMS_major_is_refused_with_the_package_it_needs()
    {
#if CMS13
        Assert.Equal("This site runs CMS 12: use the OptiCli.Mcp package instead of OptiCli.Mcp.Cms13.",
            Assert.Throws<InvalidOperationException>(() => CmsMajor.Check(12)).Message);
#else
        Assert.Equal("This site runs CMS 13: use the OptiCli.Mcp.Cms13 package instead of OptiCli.Mcp.",
            Assert.Throws<InvalidOperationException>(() => CmsMajor.Check(13)).Message);
#endif
        var newer = Assert.Throws<InvalidOperationException>(() => CmsMajor.Check(14)).Message;
        Assert.StartsWith("This site runs CMS 14, which the opticli MCP module doesn't support", newer);
        Assert.Contains("OptiCli.Mcp is for CMS 12, OptiCli.Mcp.Cms13 for CMS 13", newer);
    }

    [Fact]
    public void The_check_runs_before_anything_is_registered_in_a_method_of_its_own()
    {
        // The registration is never inlined into AddOptiCliMcp, so on the wrong major its types aren't loaded before the check.
        var register = typeof(OptiCliMcpExtensions).GetMethod(nameof(OptiCliMcpExtensions.Register), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(register.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining));

        var services = new ServiceCollection();
        services.AddOptiCliMcp();
        Assert.NotEmpty(services);
    }
}
