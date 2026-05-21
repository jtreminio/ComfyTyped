using ComfyTyped.CodeGen;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ComfyTyped.Tests;

/// <summary>Tests for the <c>--keep-list</c> codegen flag and the
/// <c>PruneManifest.g.cs</c> contract that bridges codegen and the
/// <c>prune</c> subcommand. These cover JSON parsing, module-to-class-name
/// resolution (including the warning paths for typos/all-in-core),
/// manifest emission, manifest reading, and the prune-side keep behavior.</summary>
public class PruneManifestTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (string f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
        foreach (string d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "ComfyTyped.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string NewTempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "ComfyTyped.Tests-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    // ---------------------------------------------------------------------
    // LoadKeepList
    // ---------------------------------------------------------------------

    [Fact]
    public void LoadKeepList_ParsesBothArrays()
    {
        string path = NewTempFile("""
            {
              "keep_modules": ["custom_nodes.A", "custom_nodes.B"],
              "keep_class_types": ["FooBar"]
            }
            """);

        Program.KeepList kl = Program.LoadKeepList(path);

        Assert.Equal(["custom_nodes.A", "custom_nodes.B"], kl.Modules);
        Assert.Equal(["FooBar"], kl.ClassTypes);
        Assert.False(kl.IsEmpty);
    }

    [Fact]
    public void LoadKeepList_MissingKeysAreEmptyArrays()
    {
        string path = NewTempFile("{}");

        Program.KeepList kl = Program.LoadKeepList(path);

        Assert.Empty(kl.Modules);
        Assert.Empty(kl.ClassTypes);
        Assert.True(kl.IsEmpty);
    }

    [Fact]
    public void LoadKeepList_RejectsNonStringEntries()
    {
        // Mixed-type arrays would silently misalign the keep-list — fail loud instead.
        string path = NewTempFile("""{ "keep_modules": ["ok", 42] }""");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("keep_modules", ex.Message);
    }

    [Fact]
    public void LoadKeepList_WrapsParseErrors()
    {
        string path = NewTempFile("not json at all {");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("--keep-list", ex.Message);
        Assert.Contains("parse", ex.Message);
    }

    [Fact]
    public void LoadKeepList_RejectsUnknownTopLevelKeys()
    {
        // The biggest footgun the keep-list can hit: a singular typo ("keep_module")
        // would silently parse to an empty keep-list and produce a silently empty
        // manifest. Reject the typo so the user sees the mistake at gen time.
        string path = NewTempFile("""{ "keep_module": ["x"] }""");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("keep_module", ex.Message);
        Assert.Contains("keep_modules", ex.Message);
        Assert.Contains("keep_class_types", ex.Message);
    }

    [Fact]
    public void LoadKeepList_RejectsTopLevelArray()
    {
        // A user who writes the JSON as a bare array would otherwise see
        // Newtonsoft's internal "Current JsonReader item is not an object"
        // message — produce a targeted, actionable error instead.
        string path = NewTempFile("""["custom_nodes.X"]""");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("must be a JSON object", ex.Message);
        Assert.Contains("keep_modules", ex.Message);
    }

    [Fact]
    public void LoadKeepList_RejectsNonArrayValueForKnownKey()
    {
        // The unknown-key check protects against `{"keep_module": [...]}` (typo'd
        // key). The original silent-failure mode survives in a different form
        // when the key is correct but the value is wrong type — this test pins
        // the value-type check that closes that gap.
        string path = NewTempFile("""{ "keep_modules": "custom_nodes.X" }""");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("keep_modules", ex.Message);
        Assert.Contains("must be a JSON array", ex.Message);
    }

    [Fact]
    public void LoadKeepList_RejectsEmptyStringEntry()
    {
        string path = NewTempFile("""{ "keep_modules": [""] }""");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("keep_modules", ex.Message);
        Assert.Contains("non-empty", ex.Message);
    }

    [Fact]
    public void LoadKeepList_MissingFileReportsNotFoundNotParseError()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ComfyTyped.Tests-missing-" + Guid.NewGuid().ToString("N") + ".json");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Program.LoadKeepList(path));
        Assert.Contains("load", ex.Message);
        Assert.Contains("not found", ex.Message);
        Assert.DoesNotContain("parse", ex.Message);
    }

    // ---------------------------------------------------------------------
    // ResolveKeepList
    // ---------------------------------------------------------------------

    private static JObject SyntheticObjectInfo() => new()
    {
        ["FirstLtxNode"] = new JObject { ["python_module"] = "custom_nodes.ComfyUI-LTXVideo" },
        ["SecondLtxNode"] = new JObject { ["python_module"] = "custom_nodes.ComfyUI-LTXVideo" },
        ["CoreNode"] = new JObject { ["python_module"] = "nodes" },
        ["DroppedLtxNode"] = new JObject { ["python_module"] = "custom_nodes.ComfyUI-DroppedPack" },
    };

    [Fact]
    public void ResolveKeepList_MapsModuleToClassNames()
    {
        Program.KeepList kl = new(["custom_nodes.ComfyUI-LTXVideo"], []);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
            ["SecondLtxNode"] = "SecondLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Equal(["FirstLtxNodeNode", "SecondLtxNodeNode"], res.ClassNames);
        Assert.Empty(res.Warnings);
    }

    [Fact]
    public void ResolveKeepList_IncludesExplicitClassTypes()
    {
        Program.KeepList kl = new([], ["FirstLtxNode"]);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Equal(["FirstLtxNodeNode"], res.ClassNames);
        Assert.Empty(res.Warnings);
    }

    [Fact]
    public void ResolveKeepList_WarnsOnUnknownModule()
    {
        Program.KeepList kl = new(["custom_nodes.MispelledPack"], []);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Empty(res.ClassNames);
        string warning = Assert.Single(res.Warnings);
        Assert.Contains("MispelledPack", warning);
        Assert.Contains("typo", warning);
    }

    [Fact]
    public void ResolveKeepList_WarnsWhenModuleHasZeroGeneratedNodes()
    {
        Program.KeepList kl = new(["custom_nodes.ComfyUI-DroppedPack"], []);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Empty(res.ClassNames);
        string warning = Assert.Single(res.Warnings);
        Assert.Contains("DroppedPack", warning);
        // The reworded message must enumerate the real causes — not just
        // --core-assembly — so a user without --core-assembly isn't misled.
        Assert.Contains("--core-assembly", warning);
        Assert.Contains("--native-only", warning);
    }

    [Fact]
    public void ResolveKeepList_WarnsOnUnknownClassType()
    {
        Program.KeepList kl = new([], ["NodeThatDoesNotExist"]);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Empty(res.ClassNames);
        string warning = Assert.Single(res.Warnings);
        Assert.Contains("NodeThatDoesNotExist", warning);
    }

    [Fact]
    public void ResolveKeepList_DeduplicatesClassNames()
    {
        Program.KeepList kl = new(["custom_nodes.ComfyUI-LTXVideo"], ["FirstLtxNode"]);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["FirstLtxNode"] = "FirstLtxNodeNode",
            ["SecondLtxNode"] = "SecondLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Equal(["FirstLtxNodeNode", "SecondLtxNodeNode"], res.ClassNames);
    }

    [Fact]
    public void ResolveKeepList_OutputIsSortedDeterministically()
    {
        // Stable order matters: codegen reruns must produce byte-identical manifests
        // so git diffs stay clean.
        Program.KeepList kl = new(["custom_nodes.ComfyUI-LTXVideo"], []);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["SecondLtxNode"] = "SecondLtxNodeNode",
            ["FirstLtxNode"] = "FirstLtxNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, SyntheticObjectInfo(), generated);

        Assert.Equal(["FirstLtxNodeNode", "SecondLtxNodeNode"], res.ClassNames);
    }

    // ---------------------------------------------------------------------
    // ResolveForceIncludeClassTypes
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveForceIncludeClassTypes_BuildsModuleAndExplicitUnion()
    {
        Program.KeepList kl = new(["custom_nodes.ComfyUI-LTXVideo"], ["CoreNode"]);

        HashSet<string> set = Program.ResolveForceIncludeClassTypes(kl, SyntheticObjectInfo());

        Assert.Contains("FirstLtxNode", set);
        Assert.Contains("SecondLtxNode", set);
        Assert.Contains("CoreNode", set);
        Assert.DoesNotContain("DroppedLtxNode", set);
    }

    [Fact]
    public void ResolveForceIncludeClassTypes_NullOrEmptyReturnsEmptySet()
    {
        Assert.Empty(Program.ResolveForceIncludeClassTypes(null, SyntheticObjectInfo()));
        Assert.Empty(Program.ResolveForceIncludeClassTypes(new Program.KeepList([], []), SyntheticObjectInfo()));
    }

    [Fact]
    public void ResolveForceIncludeClassTypes_ToleratesNonStringPythonModule()
    {
        // ComfyUI always emits python_module as a string, but a malformed entry
        // (number, array, JSON null) must not crash the keep-list resolution.
        JObject objectInfo = new()
        {
            ["GoodNode"] = new JObject { ["python_module"] = "custom_nodes.GoodPack" },
            ["BadArrayMod"] = new JObject { ["python_module"] = new JArray("a", "b") },
            ["BadIntMod"] = new JObject { ["python_module"] = 42 },
            ["NullMod"] = new JObject { ["python_module"] = null },
            ["MissingMod"] = new JObject(),
        };
        Program.KeepList kl = new(["custom_nodes.GoodPack"], []);

        HashSet<string> set = Program.ResolveForceIncludeClassTypes(kl, objectInfo);

        Assert.Equal(new HashSet<string> { "GoodNode" }, set);
    }

    [Fact]
    public void ResolveKeepList_ToleratesNonStringPythonModule()
    {
        // Same defensive read; runs through ResolveKeepList's classTypesByModule index.
        JObject objectInfo = new()
        {
            ["GoodNode"] = new JObject { ["python_module"] = "custom_nodes.GoodPack" },
            ["BadArrayMod"] = new JObject { ["python_module"] = new JArray("a", "b") },
        };
        Program.KeepList kl = new(["custom_nodes.GoodPack"], []);
        Dictionary<string, string> generated = new(StringComparer.Ordinal)
        {
            ["GoodNode"] = "GoodNodeNode",
        };

        Program.KeepListResolution res = Program.ResolveKeepList(kl, objectInfo, generated);

        Assert.Equal(["GoodNodeNode"], res.ClassNames);
        Assert.Empty(res.Warnings);
    }

    // ---------------------------------------------------------------------
    // GeneratePruneManifest / ReadPruneManifestKeepSet
    // ---------------------------------------------------------------------

    [Fact]
    public void GeneratePruneManifest_ContainsSentinelsAndClassNames()
    {
        string output = Program.GeneratePruneManifest("MyExt.Generated", ["FooNode", "BarNode"]);

        Assert.Contains("namespace MyExt.Generated;", output);
        // Reference the constants so a sentinel rename doesn't make this test
        // fail for purely cosmetic reasons.
        Assert.Contains(Program.PruneManifestBeginMarker, output);
        Assert.Contains(Program.PruneManifestEndMarker, output);
        Assert.Contains($"public static class {Program.PruneManifestClassName}", output);
        Assert.Contains("\"BarNode\"", output);
        Assert.Contains("\"FooNode\"", output);
    }

    [Fact]
    public void GeneratePruneManifest_SortsAndDeduplicates()
    {
        string output = Program.GeneratePruneManifest(
            "X.Y", ["BetaNode", "AlphaNode", "BetaNode"]);

        int alpha = output.IndexOf("\"AlphaNode\"", StringComparison.Ordinal);
        int beta = output.IndexOf("\"BetaNode\"", StringComparison.Ordinal);

        Assert.True(alpha > 0 && beta > 0, "expected both names in output");
        Assert.True(alpha < beta, "expected alphabetical order");

        int firstBeta = beta;
        int secondBeta = output.IndexOf("\"BetaNode\"", firstBeta + 1, StringComparison.Ordinal);
        Assert.Equal(-1, secondBeta);
    }

    [Fact]
    public void GeneratePruneManifest_EmptyInputProducesEmptyArray()
    {
        // Empty keep-list still produces a parseable manifest; prune reads it as "no extras."
        string output = Program.GeneratePruneManifest("X.Y", []);

        Assert.Contains("AlwaysKeep = new string[]", output);
        Assert.Contains("PRUNE-MANIFEST-BEGIN", output);
        Assert.Contains("PRUNE-MANIFEST-END", output);
    }

    [Fact]
    public void ReadPruneManifestKeepSet_RoundTripsThroughGenerator()
    {
        string dir = NewTempDir();
        File.WriteAllText(
            Path.Combine(dir, "PruneManifest.g.cs"),
            Program.GeneratePruneManifest("X.Y", ["AlphaNode", "BetaNode"]));

        HashSet<string> keep = Program.ReadPruneManifestKeepSet(dir);

        Assert.Equal(new HashSet<string> { "AlphaNode", "BetaNode" }, keep);
    }

    [Fact]
    public void ReadPruneManifestKeepSet_MissingFileReturnsEmpty()
    {
        string dir = NewTempDir();

        HashSet<string> keep = Program.ReadPruneManifestKeepSet(dir);

        Assert.Empty(keep);
    }

    [Fact]
    public void ReadPruneManifestKeepSet_NoSentinelBlockReturnsEmpty()
    {
        string dir = NewTempDir();
        File.WriteAllText(
            Path.Combine(dir, "PruneManifest.g.cs"),
            """
            namespace X;
            public static class PruneManifest { public static readonly string[] AlwaysKeep = []; }
            """);

        HashSet<string> keep = Program.ReadPruneManifestKeepSet(dir);

        Assert.Empty(keep);
    }

    [Fact]
    public void ReadPruneManifestKeepSet_IgnoresQuotedIdentifiersInsideSentinelBlockButOutsideArray()
    {
        // The sentinel block can grow XML doc trivia, attributes, or comments
        // around the AlwaysKeep array as the manifest format evolves. Only the
        // array literal body should drive keep entries — anything else between
        // the sentinels is just bystander text.
        string dir = NewTempDir();
        File.WriteAllText(
            Path.Combine(dir, "PruneManifest.g.cs"),
            """
            // <auto-generated/>
            namespace X;
            public static class PruneManifest
            {
                // PRUNE-MANIFEST-BEGIN
                // see "TriviaNode" for context — must NOT be parsed as a keep entry.
                public static readonly string[] AlwaysKeep = new string[]
                {
                    "RealNode",
                };
                // trailing comment with "AnotherTriviaNode" should also be ignored.
                // PRUNE-MANIFEST-END
            }
            """);

        HashSet<string> keep = Program.ReadPruneManifestKeepSet(dir);

        Assert.Contains("RealNode", keep);
        Assert.DoesNotContain("TriviaNode", keep);
        Assert.DoesNotContain("AnotherTriviaNode", keep);
    }

    [Fact]
    public void ReadPruneManifestKeepSet_IgnoresClassNamesOutsideSentinels()
    {
        // A stray "FooNode" reference outside the marker block must not be treated
        // as an always-keep entry. This protects against trivia (file-level docs,
        // accidental hand-edits) silently expanding the keep set.
        string dir = NewTempDir();
        File.WriteAllText(
            Path.Combine(dir, "PruneManifest.g.cs"),
            """
            // <auto-generated/>
            // mention of "OutsideNode" up here is trivia, not a keep entry.
            namespace X;
            public static class PruneManifest
            {
                // PRUNE-MANIFEST-BEGIN
                public static readonly string[] AlwaysKeep = new string[]
                {
                    "InsideNode",
                };
                // PRUNE-MANIFEST-END
            }
            """);

        HashSet<string> keep = Program.ReadPruneManifestKeepSet(dir);

        Assert.Contains("InsideNode", keep);
        Assert.DoesNotContain("OutsideNode", keep);
    }

    // ---------------------------------------------------------------------
    // End-to-end: prune respects the manifest
    // ---------------------------------------------------------------------

    /// <summary>Builds a fake generated *.g.cs file body that matches the regex
    /// patterns the prune scanner uses (the `public sealed class` declaration
    /// and, for nodes, a `public const string ClassType = "..."`).</summary>
    private static string FakeNodeFile(string className, string classType) => $$"""
        // <auto-generated/>
        #nullable enable
        using ComfyTyped.Core;
        namespace Fake.Generated;
        public sealed class {{className}} : ComfyNode
        {
            public const string ClassType = "{{classType}}";
            public override string ClassTypeName => ClassType;
        }
        """;

    [Fact]
    public void Prune_KeepsManifestListedNodeEvenWhenUnreferenced()
    {
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "UsedNode.g.cs"),
            FakeNodeFile("UsedNode", "UsedClassType"));
        File.WriteAllText(Path.Combine(genDir, "OrphanNode.g.cs"),
            FakeNodeFile("OrphanNode", "OrphanClassType"));
        File.WriteAllText(Path.Combine(genDir, "PruneManifest.g.cs"),
            Program.GeneratePruneManifest("Fake.Generated", ["OrphanNode"]));

        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"), """
            namespace Fake.Consumer;
            class Consumer { void M() { var x = new UsedNode(); } }
            """);

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(genDir, "UsedNode.g.cs")),
            "UsedNode kept via source reference.");
        Assert.True(File.Exists(Path.Combine(genDir, "OrphanNode.g.cs")),
            "OrphanNode kept via PruneManifest.g.cs.");
        Assert.True(File.Exists(Path.Combine(genDir, "PruneManifest.g.cs")),
            "PruneManifest.g.cs is itself never pruned.");
    }

    [Fact]
    public void Prune_DropsUnreferencedNodeWhenAbsentFromManifest()
    {
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "UsedNode.g.cs"),
            FakeNodeFile("UsedNode", "UsedClassType"));
        File.WriteAllText(Path.Combine(genDir, "OrphanNode.g.cs"),
            FakeNodeFile("OrphanNode", "OrphanClassType"));
        File.WriteAllText(Path.Combine(genDir, "PruneManifest.g.cs"),
            Program.GeneratePruneManifest("Fake.Generated", []));

        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"), """
            namespace Fake.Consumer;
            class Consumer { void M() { var x = new UsedNode(); } }
            """);

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(genDir, "UsedNode.g.cs")));
        Assert.False(File.Exists(Path.Combine(genDir, "OrphanNode.g.cs")),
            "OrphanNode should be pruned: no source reference and not in manifest.");
    }

    [Fact]
    public void Prune_WorksWhenManifestIsAbsent()
    {
        // Backward-compat: extension that never adopted --keep-list still prunes correctly.
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "OrphanNode.g.cs"),
            FakeNodeFile("OrphanNode", "OrphanClassType"));
        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"),
            "namespace Fake.Consumer; class Consumer { }");

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
        ]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(genDir, "OrphanNode.g.cs")));
    }

    [Fact]
    public void Prune_DryRunDoesNotDeleteFiles()
    {
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "OrphanNode.g.cs"),
            FakeNodeFile("OrphanNode", "OrphanClassType"));
        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"),
            "namespace Fake.Consumer; class Consumer { }");

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
            "--dry-run",
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(genDir, "OrphanNode.g.cs")),
            "--dry-run must preserve files that would otherwise be pruned.");
    }

    /// <summary>Build an IComfyType marker file body that matches the regex
    /// (<c>public sealed class X : IComfyType</c>) used by the prune scanner.</summary>
    private static string FakeMarkerFile(string typeName) => $$"""
        // <auto-generated/>
        #nullable enable
        using ComfyTyped.Types;
        namespace Fake.Generated;
        public sealed class {{typeName}} : IComfyType { public static string TypeName => "{{typeName}}"; }
        """;

    /// <summary>Fake node file that references a marker type via <c>NodeOutput&lt;T&gt;</c>,
    /// so a kept node text contains the marker class name for pass-2 to find.</summary>
    private static string FakeNodeFileWithMarker(string className, string classType, string markerTypeName) => $$"""
        // <auto-generated/>
        #nullable enable
        using ComfyTyped.Core;
        using ComfyTyped.Types;
        namespace Fake.Generated;
        public sealed class {{className}} : ComfyNode
        {
            public const string ClassType = "{{classType}}";
            public override string ClassTypeName => ClassType;
            public NodeOutput<{{markerTypeName}}> Out { get; } = null!;
        }
        """;

    [Fact]
    public void Prune_KeepsMarkerReferencedByKeptNode()
    {
        // Pass 2: an IComfyType marker is kept iff its name appears in consumer
        // source OR in the text of a kept node file. This exercises the latter.
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "UsedNode.g.cs"),
            FakeNodeFileWithMarker("UsedNode", "UsedClassType", "UsedMarker"));
        File.WriteAllText(Path.Combine(genDir, "UsedMarker.g.cs"),
            FakeMarkerFile("UsedMarker"));
        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"), """
            namespace Fake.Consumer;
            class Consumer { void M() { var x = new UsedNode(); } }
            """);

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(genDir, "UsedNode.g.cs")));
        Assert.True(File.Exists(Path.Combine(genDir, "UsedMarker.g.cs")),
            "marker referenced only by a kept node must survive pass-2");
    }

    [Fact]
    public void Prune_DropsMarkerWhenItsOnlyReferencingNodeIsPruned()
    {
        // Inverse of the above: marker is referenced only by an unkept node, so
        // it should be pruned alongside the node — no consumer source mentions
        // either the node or the marker.
        string genDir = NewTempDir();
        string srcDir = NewTempDir();

        File.WriteAllText(Path.Combine(genDir, "OrphanNode.g.cs"),
            FakeNodeFileWithMarker("OrphanNode", "OrphanClassType", "OrphanMarker"));
        File.WriteAllText(Path.Combine(genDir, "OrphanMarker.g.cs"),
            FakeMarkerFile("OrphanMarker"));
        File.WriteAllText(Path.Combine(srcDir, "Consumer.cs"),
            "namespace Fake.Consumer; class Consumer { }");

        int exit = Program.RunPrune([
            "--generated-dir", genDir,
            "--source", srcDir,
        ]);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(genDir, "OrphanNode.g.cs")));
        Assert.False(File.Exists(Path.Combine(genDir, "OrphanMarker.g.cs")),
            "marker with no live referencing node should be pruned");
    }

    // ---------------------------------------------------------------------
    // End-to-end: --keep-list drives Main, force-includes a non-native node,
    // and emits a manifest the consumer can ship.
    // ---------------------------------------------------------------------

    /// <summary>Minimum-shape object_info fragment: one input/output is enough
    /// for ParseNodeDef to succeed and emit a real *.g.cs.</summary>
    private static JObject MinimalNodeShape(string pythonModule) => new()
    {
        ["python_module"] = pythonModule,
        ["category"] = "test",
        ["description"] = "",
        ["input"] = new JObject
        {
            ["required"] = new JObject
            {
                ["x"] = new JArray("INT", new JObject { ["default"] = 0 }),
            },
        },
        ["output"] = new JArray("INT"),
        ["output_name"] = new JArray("y"),
    };

    [Fact]
    public void Main_KeepListForceIncludesNonNativeNodeAndEmitsManifest()
    {
        string outDir = NewTempDir();
        JObject objectInfo = new()
        {
            ["NativeFoo"] = MinimalNodeShape("nodes"),
            ["ExtraFoo"] = MinimalNodeShape("custom_nodes.PackINeed"),
            ["IgnoredFoo"] = MinimalNodeShape("custom_nodes.PackIDoNotCareAbout"),
        };
        string objectInfoPath = NewTempFile(objectInfo.ToString());
        string keepListPath = NewTempFile("""
            { "keep_modules": ["custom_nodes.PackINeed"] }
            """);

        int exit = Program.Main([
            "--comfy-json", objectInfoPath,
            "--output", outDir,
            "--namespace", "Test.Generated",
            "--native-only",
            "--keep-list", keepListPath,
        ]);

        Assert.Equal(0, exit);

        Assert.True(File.Exists(Path.Combine(outDir, "NativeFooNode.g.cs")),
            "native node should be emitted under --native-only");
        Assert.True(File.Exists(Path.Combine(outDir, "ExtraFooNode.g.cs")),
            "keep_modules should force-include a non-native node past --native-only");
        Assert.False(File.Exists(Path.Combine(outDir, "IgnoredFooNode.g.cs")),
            "--native-only should still drop non-native nodes not named by the keep-list");

        string manifestPath = Path.Combine(outDir, "PruneManifest.g.cs");
        Assert.True(File.Exists(manifestPath));
        HashSet<string> keep = Program.ReadPruneManifestKeepSet(outDir);
        Assert.Contains("ExtraFooNode", keep);
        Assert.DoesNotContain("NativeFooNode", keep);
        Assert.DoesNotContain("IgnoredFooNode", keep);
    }

    [Fact]
    public void Main_NativeOnlyToleratesNonStringPythonModule()
    {
        // Third GetPythonModule call site: Main's --native-only check. A non-string
        // python_module on an unrelated node must not crash the whole codegen run.
        string outDir = NewTempDir();
        JObject objectInfo = new()
        {
            ["NativeFoo"] = MinimalNodeShape("nodes"),
            // Malformed python_module — Newtonsoft would coerce 42 → "42" via
            // Value<string>, but GetPythonModule should treat it as null and
            // let --native-only drop the node cleanly.
            ["MalformedFoo"] = new JObject
            {
                ["python_module"] = 42,
                ["category"] = "test",
                ["description"] = "",
                ["input"] = new JObject { ["required"] = new JObject() },
                ["output"] = new JArray(),
                ["output_name"] = new JArray(),
            },
        };
        string objectInfoPath = NewTempFile(objectInfo.ToString());

        int exit = Program.Main([
            "--comfy-json", objectInfoPath,
            "--output", outDir,
            "--namespace", "Test.Generated",
            "--native-only",
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outDir, "NativeFooNode.g.cs")));
        Assert.False(File.Exists(Path.Combine(outDir, "MalformedFooNode.g.cs")),
            "malformed python_module should be treated as non-native and dropped");
    }

    [Fact]
    public void Main_KeepListEntryAlreadyInCoreIsDroppedWithWarning()
    {
        // Documents the current semantic: --keep-list does NOT override the
        // --core-assembly skip. A class_type already registered in core is
        // dropped from generation (it's available via the core dll anyway) and
        // produces a warning so the user can act on the no-op if they intended
        // a fresh emission. If this contract changes, this test must update too.
        string outDir = NewTempDir();

        // Pick any class_type registered in the core ComfyTyped.dll the tests
        // already load via ProjectReference. Anchoring on the live registry
        // makes the test robust to codegen renames.
        ComfyTyped.Generated.NodeRegistrations.EnsureRegistered();
        string coreClassType = ComfyTyped.Core.NodeRegistry.RegisteredTypes.First();
        string coreAssemblyPath = typeof(ComfyTyped.Core.ComfyNode).Assembly.Location;

        JObject objectInfo = new()
        {
            [coreClassType] = MinimalNodeShape("nodes"),
        };
        string objectInfoPath = NewTempFile(objectInfo.ToString());
        string keepListPath = NewTempFile($$"""
            { "keep_class_types": ["{{coreClassType}}"] }
            """);

        StringWriter stderr = new();
        TextWriter prevErr = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = Program.Main([
                "--comfy-json", objectInfoPath,
                "--output", outDir,
                "--namespace", "Test.Generated",
                "--core-assembly", coreAssemblyPath,
                "--keep-list", keepListPath,
            ]);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.Equal(0, exit);

        // The class_type was already in core → no consumer-side file emitted.
        Assert.Empty(Directory.EnumerateFiles(outDir, "*.g.cs")
            .Where(p => !p.EndsWith("NodeRegistrations.g.cs",  StringComparison.Ordinal)
                     && !p.EndsWith("PruneManifest.g.cs", StringComparison.Ordinal)));

        // Manifest is emitted (because --keep-list was passed) but does not
        // contain a name for the core-resident class_type.
        HashSet<string> keep = Program.ReadPruneManifestKeepSet(outDir);
        Assert.Empty(keep);

        // Warning must surface the no-op so the user isn't silently misled.
        string err = stderr.ToString();
        Assert.Contains("WARN", err);
        Assert.Contains(coreClassType, err);
    }

    [Fact]
    public void Main_WithoutKeepListDoesNotEmitManifest()
    {
        string outDir = NewTempDir();
        JObject objectInfo = new()
        {
            ["NativeFoo"] = MinimalNodeShape("nodes"),
        };
        string objectInfoPath = NewTempFile(objectInfo.ToString());

        int exit = Program.Main([
            "--comfy-json", objectInfoPath,
            "--output", outDir,
            "--namespace", "Test.Generated",
            "--native-only",
        ]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outDir, "NativeFooNode.g.cs")));
        Assert.False(File.Exists(Path.Combine(outDir, "PruneManifest.g.cs")));
    }
}
