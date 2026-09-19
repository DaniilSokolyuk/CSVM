using System;
using System.IO;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The transport seam's boundary, enforced over compiled metadata. No type in <c>CSVM.Net</c> may
/// reference anything under <c>Godot</c> or <c>System.Net</c>, in a signature or in a method body.
/// That is what keeps a session ignorant of what carries it. The loopback can then drive a whole
/// match in a plain unit test, with no engine and no socket. The scanner throws when the subject
/// filter matches no type, so this cannot pass by scanning nothing.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetNamespaceDependencyTests
{
    [Fact]
    public void TheTransportSeamNamesNoEngineTypeAndNoSocket()
    {
        var violations = AssemblyDependencyScan.Violations(
            Path.Combine(AppContext.BaseDirectory, "CSVM.dll"),
            ns => ns == "CSVM.Net",
            name => name.StartsWith("Godot.", StringComparison.Ordinal)
                || name.StartsWith("System.Net.", StringComparison.Ordinal));

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
