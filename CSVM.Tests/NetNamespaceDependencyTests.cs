using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The transport seam's boundary, enforced over compiled metadata. No type in <c>CSVM.Net</c> may
/// reference <c>System.Net</c>, nor anything under <c>Godot</c> except its plain math structs, in
/// a signature or in a method body. That is what keeps a session ignorant of what carries it, and
/// lets the loopback drive a match in a plain unit test. The two carriers that must name an engine type to exist are listed by full name below. The
/// second fact holds the networking half of that exemption to the one file it was granted for.
/// The scanner throws when the subject filter matches no type, so this cannot pass by scanning
/// nothing.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetNamespaceDependencyTests
{
    private const string Transport = "CSVM.Net.EnetTransport";
    private const string PortMap = "CSVM.Net.UpnpPortMap";

    private static readonly string[] EngineMathStructs =
    {
        "Godot.Vector2", "Godot.Vector3", "Godot.Vector4", "Godot.Quaternion", "Godot.Basis",
        "Godot.Transform3D", "Godot.Mathf", "Godot.Color",
    };

    [Fact]
    public void TheTransportSeamNamesNoEngineTypeAndNoSocket()
    {
        var violations = AssemblyDependencyScan.Violations(
            AssemblyPath(),
            ns => ns == "CSVM.Net",
            name => (name.StartsWith("Godot.", StringComparison.Ordinal)
                    && Array.IndexOf(EngineMathStructs, name) < 0)
                || name.StartsWith("System.Net.", StringComparison.Ordinal));

        // The two carriers are the seam's implementations rather than its users, so each may name
        // the engine class it is a wrapper for. Neither may name a socket, which the ban above
        // still applies to them.
        var unexempted = violations.Where(v => !Exempt(v)).ToList();

        Assert.True(unexempted.Count == 0, string.Join(Environment.NewLine, unexempted));
    }

    [Fact]
    public void OnlyTheEnetTransportNamesAGodotNetworkingType()
    {
        var networking = AssemblyDependencyScan.Violations(
            AssemblyPath(),
            ns => ns == "CSVM.Net",
            name => name.StartsWith("Godot.ENet", StringComparison.Ordinal)
                || name.StartsWith("Godot.Multiplayer", StringComparison.Ordinal));

        // Able to fail on its own terms. The one file allowed to do this really does, so an empty
        // list would mean the scan stopped finding references.
        Assert.NotEmpty(networking.Where(v => Subject(v) == Transport));
        Assert.Empty(networking.Where(v => Subject(v) != Transport));

        var upnp = AssemblyDependencyScan.Violations(
            AssemblyPath(),
            ns => ns == "CSVM.Net",
            name => name.StartsWith("Godot.Upnp", StringComparison.Ordinal));

        Assert.NotEmpty(upnp.Where(v => Subject(v) == PortMap));
        Assert.Empty(upnp.Where(v => Subject(v) != PortMap));
    }

    private static string AssemblyPath() => Path.Combine(AppContext.BaseDirectory, "CSVM.dll");

    // A violation reads "<subject type> -> <referenced type>". A compiler-generated nested type
    // (a closure, an iterator) carries its owner's name with a slash, so it is the owner's.
    private static string Subject(string violation)
    {
        string subject = violation.Split(" -> ", 2, StringSplitOptions.None)[0];
        int nested = subject.IndexOf('/');
        return nested < 0 ? subject : subject[..nested];
    }

    private static bool Exempt(string violation) =>
        !violation.Contains("System.Net.", StringComparison.Ordinal)
        && Subject(violation) is Transport or PortMap;
}
