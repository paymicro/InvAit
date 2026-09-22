namespace UIBlazor.Tests.Services;

public class SolutionTreeFormatterTests
{
    #region FormatSize

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(1024, "1 KB")]
    [InlineData(512, "0.5 KB")]
    [InlineData(143, "0.14 KB")]
    [InlineData(102, "0.1 KB")]
    [InlineData(1075, "1.05 KB")]
    [InlineData(2806, "2.74 KB")]
    [InlineData(2468, "2.41 KB")]
    [InlineData(1048576, "1024 KB")]
    public void FormatSize_ReturnsExpectedString(long bytes, string expected)
    {
        Assert.Equal(expected, SolutionTreeFormatter.FormatSize(bytes));
    }

    #endregion

    #region Empty / Null

    [Fact]
    public void Format_EmptyChildren_ReturnsEmptyString()
    {
        var root = new SolutionTreeEntry { Name = "root", IsDirectory = true };
        Assert.Equal(string.Empty, SolutionTreeFormatter.Format(root));
    }

    [Fact]
    public void Format_NullRoot_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SolutionTreeFormatter.Format(null!));
    }

    [Fact]
    public void FormatChildren_NullList_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SolutionTreeFormatter.FormatChildren(null!));
    }

    [Fact]
    public void FormatChildren_EmptyList_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SolutionTreeFormatter.FormatChildren([]));
    }

    #endregion

    #region Single Entry

    [Fact]
    public void Format_SingleFile_NoTrailingNewlineIssues()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children = [SolutionTreeEntry.File("Program.cs", 1024)]
        };

        var result = SolutionTreeFormatter.Format(root);
        Assert.Equal("└─ Program.cs [1 KB]\n", result);
    }

    [Fact]
    public void Format_SingleDirectory_EndsWithSlash()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children = [SolutionTreeEntry.Directory("Agent")]
        };

        var result = SolutionTreeFormatter.Format(root);
        Assert.Equal("└─ Agent/\n", result);
    }

    #endregion

    #region Multiple Files at Root

    [Fact]
    public void Format_MultipleFiles_UsesBranchMidAndEnd()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("[Content_Types].xml", 512),
                SolutionTreeEntry.File("extension.vsixmanifest", 2048)
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("├─ [Content_Types].xml [0.5 KB]", lines[0]);
        Assert.Equal("└─ extension.vsixmanifest [2 KB]", lines[1]);
    }

    #endregion

    #region Nested Directories

    [Fact]
    public void Format_NestedDirectory_UsesPipeContinuation()
    {
        // dir1 is NOT the last child — we add a trailing file to test pipe continuation
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("file1.cs", 1024),
                SolutionTreeEntry.Directory("dir1", children: [
                    SolutionTreeEntry.File("file2.cs", 512)
                ]),
                SolutionTreeEntry.File("file3.cs", 2048)
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Equal("├─ file1.cs [1 KB]", lines[0]);
        Assert.Equal("├─ dir1/", lines[1]);
        Assert.Equal("│  └─ file2.cs [0.5 KB]", lines[2]);
        Assert.Equal("└─ file3.cs [2 KB]", lines[3]);
    }

    [Fact]
    public void Format_LastDirectoryChild_UsesSpaceContinuation()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("file1.cs", 1024),
                SolutionTreeEntry.Directory("dir1", children: [
                    SolutionTreeEntry.File("file2.cs", 512)
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // dir1 is last child → its children use "   " (spaces) not "│  " (pipe)
        Assert.Equal(3, lines.Length);
        Assert.Equal("├─ file1.cs [1 KB]", lines[0]);
        Assert.Equal("└─ dir1/", lines[1]);
        Assert.Equal("   └─ file2.cs [0.5 KB]", lines[2]);
    }

    [Fact]
    public void Format_DeepNesting_ThreeLevels()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.Directory("a", children: [
                    SolutionTreeEntry.Directory("b", children: [
                        SolutionTreeEntry.File("c.cs", 256)
                    ])
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal("└─ a/", lines[0]);
        Assert.Equal("   └─ b/", lines[1]);
        Assert.Equal("      └─ c.cs [0.25 KB]", lines[2]);
    }

    #endregion

    #region Mixed Files and Directories

    [Fact]
    public void Format_MixedFilesAndDirs_FilesFirstThenDirs()
    {
        // Order is preserved as given — caller is responsible for ordering
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("file1.cs", 1024),
                SolutionTreeEntry.File("file2.cs", 2048),
                SolutionTreeEntry.Directory("dir1", children: [
                    SolutionTreeEntry.File("nested.cs", 512)
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Equal("├─ file1.cs [1 KB]", lines[0]);
        Assert.Equal("├─ file2.cs [2 KB]", lines[1]);
        Assert.Equal("└─ dir1/", lines[2]);
        Assert.Equal("   └─ nested.cs [0.5 KB]", lines[3]);
    }

    #endregion

    #region File Sizes

    [Fact]
    public void Format_ShowSizesFalse_OmitsSizeAnnotations()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children = [SolutionTreeEntry.File("Program.cs", 1024)]
        };

        var result = SolutionTreeFormatter.Format(root, showSizes: false);
        Assert.Equal("└─ Program.cs\n", result);
    }

    [Fact]
    public void Format_FileSizeNull_OmitsSizeAnnotation()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children = [SolutionTreeEntry.File("Program.cs", size: null)]
        };

        var result = SolutionTreeFormatter.Format(root);
        Assert.Equal("└─ Program.cs\n", result);
    }

    [Fact]
    public void Format_DirectoryNeverShowsSize()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.Directory("dir1", fullPath: "/some/path")
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        Assert.Equal("└─ dir1/\n", result);
    }

    #endregion

    #region Real-World Example

    [Fact]
    public void Format_RealWorldExample_MatchesExpectedOutput()
    {
        // Simulates a VSIX-like structure similar to the user's example
        var root = new SolutionTreeEntry
        {
            Name = "extension",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("[Content_Types].xml", 512),
                SolutionTreeEntry.File("extension.vsixmanifest", 2048),
                SolutionTreeEntry.Directory("extension", children: [
                    SolutionTreeEntry.File(".c8rc.json", 143),
                    SolutionTreeEntry.File(".mocharc.json", 102),
                    SolutionTreeEntry.File("LICENSE.txt", 1075),
                    SolutionTreeEntry.File("package.json", 2806),
                    SolutionTreeEntry.File("readme.md", 2468),
                    SolutionTreeEntry.Directory("wwwroot")
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(9, lines.Length);
        Assert.Equal("├─ [Content_Types].xml [0.5 KB]", lines[0]);
        Assert.Equal("├─ extension.vsixmanifest [2 KB]", lines[1]);
        Assert.Equal("└─ extension/", lines[2]);
        Assert.Equal("   ├─ .c8rc.json [0.14 KB]", lines[3]);
        Assert.Equal("   ├─ .mocharc.json [0.1 KB]", lines[4]);
        Assert.Equal("   ├─ LICENSE.txt [1.05 KB]", lines[5]);
        Assert.Equal("   ├─ package.json [2.74 KB]", lines[6]);
        Assert.Equal("   ├─ readme.md [2.41 KB]", lines[7]);
        Assert.Equal("   └─ wwwroot/", lines[8]);
    }

    [Fact]
    public void Format_RealWorldExample_FullOutput()
    {
        var root = new SolutionTreeEntry
        {
            Name = "extension",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("[Content_Types].xml", 512),
                SolutionTreeEntry.File("extension.vsixmanifest", 2048),
                SolutionTreeEntry.Directory("extension", children: [
                    SolutionTreeEntry.File(".c8rc.json", 143),
                    SolutionTreeEntry.File(".mocharc.json", 102),
                    SolutionTreeEntry.File("LICENSE.txt", 1075),
                    SolutionTreeEntry.File("package.json", 2806),
                    SolutionTreeEntry.File("readme.md", 2468),
                    SolutionTreeEntry.Directory("wwwroot")
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);

        var expected =
            "├─ [Content_Types].xml [0.5 KB]\n" +
            "├─ extension.vsixmanifest [2 KB]\n" +
            "└─ extension/\n" +
            "   ├─ .c8rc.json [0.14 KB]\n" +
            "   ├─ .mocharc.json [0.1 KB]\n" +
            "   ├─ LICENSE.txt [1.05 KB]\n" +
            "   ├─ package.json [2.74 KB]\n" +
            "   ├─ readme.md [2.41 KB]\n" +
            "   └─ wwwroot/\n";

        Assert.Equal(expected, result);
    }

    #endregion

    #region Solution-Like Structure

    [Fact]
    public void Format_SolutionLikeStructure_WithProjectsAndNestedFiles()
    {
        var root = new SolutionTreeEntry
        {
            Name = "MySolution",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.Directory("InvAit", children: [
                    SolutionTreeEntry.File("InvAitPackage.cs", 4096),
                    SolutionTreeEntry.File("InvAit.csproj", 1024),
                    SolutionTreeEntry.Directory("Agent", children: [
                        SolutionTreeEntry.File("ToolExecutor.cs", 8192),
                        SolutionTreeEntry.File("SolutionStructure.cs", 4096)
                    ]),
                    SolutionTreeEntry.Directory("Commands", children: [
                        SolutionTreeEntry.File("ChatWindowCommand.cs", 2048)
                    ])
                ]),
                SolutionTreeEntry.Directory("Shared", children: [
                    SolutionTreeEntry.File("Shared.csproj", 512),
                    SolutionTreeEntry.Directory("Contracts", children: [
                        SolutionTreeEntry.File("VsCodeContext.cs", 1024),
                        SolutionTreeEntry.File("VsRequest.cs", 2048)
                    ])
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Top level: 2 projects (InvAit not last, Shared last)
        Assert.Equal(13, lines.Length);
        Assert.Equal("├─ InvAit/", lines[0]);
        Assert.Equal("└─ Shared/", lines[8]); // Shared is last top-level

        // InvAit children use │  prefix (not last sibling)
        Assert.Equal("│  ├─ InvAitPackage.cs [4 KB]", lines[1]);
        Assert.Equal("│  ├─ InvAit.csproj [1 KB]", lines[2]);
        Assert.Equal("│  ├─ Agent/", lines[3]);
        Assert.Equal("│  │  ├─ ToolExecutor.cs [8 KB]", lines[4]);
        Assert.Equal("│  │  └─ SolutionStructure.cs [4 KB]", lines[5]);
        Assert.Equal("│  └─ Commands/", lines[6]);
        // Commands is last child of InvAit → children use "│  " + "   " = "│     "
        Assert.Equal("│     └─ ChatWindowCommand.cs [2 KB]", lines[7]);

        // Shared children use "   " prefix (last sibling)
        Assert.Equal("   ├─ Shared.csproj [0.5 KB]", lines[9]);
        Assert.Equal("   └─ Contracts/", lines[10]);
        Assert.Equal("      ├─ VsCodeContext.cs [1 KB]", lines[11]);
        Assert.Equal("      └─ VsRequest.cs [2 KB]", lines[12]);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Format_EmptyDirectory_ShowsSlashNoChildren()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.File("file.cs", 1024),
                SolutionTreeEntry.Directory("emptydir")
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("├─ file.cs [1 KB]", lines[0]);
        Assert.Equal("└─ emptydir/", lines[1]);
    }

    [Fact]
    public void Format_FileWithNestedChildren_RendersChildren()
    {
        // Simulates .xaml → .xaml.cs nesting
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                new SolutionTreeEntry
                {
                    Name = "ChatControl.xaml",
                    IsDirectory = false,
                    FileSize = 2048,
                    Children = [SolutionTreeEntry.File("ChatControl.xaml.cs", 4096)]
                }
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("└─ ChatControl.xaml [2 KB]", lines[0]);
        Assert.Equal("   └─ ChatControl.xaml.cs [4 KB]", lines[1]);
    }

    [Fact]
    public void Format_SiblingDirectories_BothWithChildren()
    {
        var root = new SolutionTreeEntry
        {
            Name = "root",
            IsDirectory = true,
            Children =
            [
                SolutionTreeEntry.Directory("dir1", children: [
                    SolutionTreeEntry.File("a.cs", 1024)
                ]),
                SolutionTreeEntry.Directory("dir2", children: [
                    SolutionTreeEntry.File("b.cs", 2048)
                ])
            ]
        };

        var result = SolutionTreeFormatter.Format(root);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Equal("├─ dir1/", lines[0]);
        Assert.Equal("│  └─ a.cs [1 KB]", lines[1]);
        Assert.Equal("└─ dir2/", lines[2]);
        Assert.Equal("   └─ b.cs [2 KB]", lines[3]);
    }

    #endregion
}
