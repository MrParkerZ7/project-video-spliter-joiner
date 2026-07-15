using System;
using System.Linq;
using FluentAssertions;
using VideoSplitJoiner.Core;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Architectural guard: the Core assembly must never take a dependency on WPF.
/// Keeps the UI ⇄ Core seam clean so Core stays independently testable and reusable.
/// </summary>
public class CoreIsUiFreeTests
{
    private static readonly string[] WpfAssemblies =
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
    };

    [Fact]
    public void CoreAssembly_ShouldNotReferenceWpf()
    {
        var referenced = typeof(AppInfo).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (var wpf in WpfAssemblies)
        {
            referenced.Should().NotContain(
                name => string.Equals(name, wpf, StringComparison.OrdinalIgnoreCase),
                because: $"Core must stay UI-free but referenced {wpf}");
        }
    }
}
