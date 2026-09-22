namespace UIBlazor.Tests.Services;

public class SolutionTreeBuilderTests
{
    #region Empty / Null Input

    [Fact]
    public void Build_NullFilePaths_ReturnsRootWithEmptyChildren()
    {
        var root = SolutionTreeBuilder.Build(null);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Build_EmptyFilePaths_ReturnsRootWithEmptyChildren()
    {
        var root = SolutionTreeBuilder.Build([]);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Build_AllWhitespacePaths_ReturnsRootWithEmptyChildren()
    {
        var root = SolutionTreeBuilder.Build(["", "   ", null!]);
        Assert.Empty(root.Children);
    }

    #endregion

    #region Single File

    [Fact]
    public void Build_SingleFile_CreatesOneFileNodeAtRoot()
    {
        var root = SolutionTreeBuilder.Build(
            ["B:/Solution/Program.cs"],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        var file = root.Children[0];
        Assert.False(file.IsDirectory);
        Assert.Equal("Program.cs", file.Name);
        Assert.Equal("B:/Solution/Program.cs", file.FullPath);
    }

    #endregion

    #region Multiple Files in Same Directory

    [Fact]
    public void Build_MultipleFilesInSameDirectory_GroupedUnderDirectory()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/Program.cs",
                "B:/Solution/Project/Utils.cs"
            ],
            solutionPath: "B:/Solution");

        // Top level should have one directory "Project"
        Assert.Single(root.Children);
        var dir = root.Children[0];
        Assert.True(dir.IsDirectory);
        Assert.Equal("Project", dir.Name);
        Assert.Equal(2, dir.Children.Count);
        Assert.All(dir.Children, c => Assert.False(c.IsDirectory));
        Assert.Equal("Program.cs", dir.Children[0].Name);
        Assert.Equal("Utils.cs", dir.Children[1].Name);
    }

    #endregion

    #region Files in Nested Directories

    [Fact]
    public void Build_NestedDirectories_ProperNesting()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/src/Program.cs",
                "B:/Solution/src/Agent/ToolExecutor.cs",
                "B:/Solution/src/Agent/SolutionStructure.cs",
                "B:/Solution/src/Utils/Logger.cs"
            ],
            solutionPath: "B:/Solution");

        // Top level: "src" directory
        Assert.Single(root.Children);
        var src = root.Children[0];
        Assert.True(src.IsDirectory);
        Assert.Equal("src", src.Name);

        // src should have: Agent (dir), Program.cs (file), Utils (dir)
        // Sorted: directories first (Agent, Utils), then files (Program.cs)
        Assert.Equal(3, src.Children.Count);
        Assert.True(src.Children[0].IsDirectory);
        Assert.Equal("Agent", src.Children[0].Name);
        Assert.True(src.Children[1].IsDirectory);
        Assert.Equal("Utils", src.Children[1].Name);
        Assert.False(src.Children[2].IsDirectory);
        Assert.Equal("Program.cs", src.Children[2].Name);

        // Agent has 2 files
        var agent = src.Children[0];
        Assert.Equal(2, agent.Children.Count);
        Assert.Equal("SolutionStructure.cs", agent.Children[0].Name);
        Assert.Equal("ToolExecutor.cs", agent.Children[1].Name);

        // Utils has 1 file
        var utils = src.Children[1];
        Assert.Single(utils.Children);
        Assert.Equal("Logger.cs", utils.Children[0].Name);
    }

    #endregion

    #region Files Across Multiple Projects

    [Fact]
    public void Build_MultipleProjects_ProjectNamesUsedAsDirectoryNames()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/InvAit/InvAitPackage.cs",
                "B:/Solution/InvAit/Agent/ToolExecutor.cs",
                "B:/Solution/Shared/Contracts/VsRequest.cs",
                "B:/Solution/Shared/Shared.csproj"
            ],
            projectPaths:
            [
                "B:/Solution/InvAit/InvAit.csproj",
                "B:/Solution/Shared/Shared.csproj"
            ],
            solutionPath: "B:/Solution");

        // Top level: InvAit (dir), Shared (dir) — sorted alphabetically
        Assert.Equal(2, root.Children.Count);
        Assert.True(root.Children[0].IsDirectory);
        Assert.Equal("InvAit", root.Children[0].Name);
        Assert.True(root.Children[1].IsDirectory);
        Assert.Equal("Shared", root.Children[1].Name);

        // InvAit children: Agent (dir), InvAitPackage.cs (file)
        var invAit = root.Children[0];
        Assert.Equal(2, invAit.Children.Count);
        Assert.True(invAit.Children[0].IsDirectory);
        Assert.Equal("Agent", invAit.Children[0].Name);
        Assert.False(invAit.Children[1].IsDirectory);
        Assert.Equal("InvAitPackage.cs", invAit.Children[1].Name);

        // Shared children: Contracts (dir), Shared.csproj (file)
        var shared = root.Children[1];
        Assert.Equal(2, shared.Children.Count);
        Assert.True(shared.Children[0].IsDirectory);
        Assert.Equal("Contracts", shared.Children[0].Name);
        Assert.False(shared.Children[1].IsDirectory);
        Assert.Equal("Shared.csproj", shared.Children[1].Name);
    }

    #endregion

    #region Solution Path Stripping

    [Fact]
    public void Build_WithSolutionPath_PathsMadeRelative()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/MySolution/ProjectA/FileA.cs",
                "B:/MySolution/ProjectB/FileB.cs"
            ],
            solutionPath: "B:/MySolution");

        // Top level should be ProjectA and ProjectB (relative to solution)
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("ProjectA", root.Children[0].Name);
        Assert.Equal("ProjectB", root.Children[1].Name);

        // Each project has one file
        Assert.Single(root.Children[0].Children);
        Assert.Equal("FileA.cs", root.Children[0].Children[0].Name);
        Assert.Single(root.Children[1].Children);
        Assert.Equal("FileB.cs", root.Children[1].Children[0].Name);
    }

    [Fact]
    public void Build_SolutionPathWithBackslashes_Normalized()
    {
        var root = SolutionTreeBuilder.Build(
            ["B:\\Solution\\Project\\File.cs"],
            solutionPath: "B:\\Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.Single(root.Children[0].Children);
        Assert.Equal("File.cs", root.Children[0].Children[0].Name);
    }

    #endregion

    #region No Solution Path — Common Root Computed

    [Fact]
    public void Build_NoSolutionPath_ComputesCommonRoot()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/ProjectA/FileA.cs",
                "B:/Solution/ProjectB/FileB.cs"
            ]);

        // Common root is "B:/Solution", so top level should be ProjectA and ProjectB
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("ProjectA", root.Children[0].Name);
        Assert.Equal("ProjectB", root.Children[1].Name);
    }

    [Fact]
    public void Build_NoSolutionPath_SingleFile_ComputesParentAsRoot()
    {
        var root = SolutionTreeBuilder.Build(
            ["B:/Solution/Project/File.cs"]);

        // Common root for single file is its parent: "B:/Solution/Project"
        // So the file appears directly at root level
        Assert.Single(root.Children);
        Assert.False(root.Children[0].IsDirectory);
        Assert.Equal("File.cs", root.Children[0].Name);
    }

    [Fact]
    public void Build_NoSolutionPath_FilesInSameDir_CommonRootIsParent()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/FileA.cs",
                "B:/Solution/Project/FileB.cs"
            ]);

        // Common root is "B:/Solution/Project" — files appear at root level
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.False(c.IsDirectory));
        Assert.Equal("FileA.cs", root.Children[0].Name);
        Assert.Equal("FileB.cs", root.Children[1].Name);
    }

    #endregion

    #region Sorting

    [Fact]
    public void Build_SortsDirectoriesFirstThenFiles()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/zfile.cs",
                "B:/Solution/adir/file.cs",
                "B:/Solution/afeature.cs",
                "B:/Solution/bdir/file.cs"
            ],
            solutionPath: "B:/Solution");

        // Top level sorted: directories first (adir, bdir), then files (afeature.cs, zfile.cs)
        Assert.Equal(4, root.Children.Count);
        Assert.True(root.Children[0].IsDirectory);
        Assert.Equal("adir", root.Children[0].Name);
        Assert.True(root.Children[1].IsDirectory);
        Assert.Equal("bdir", root.Children[1].Name);
        Assert.False(root.Children[2].IsDirectory);
        Assert.Equal("afeature.cs", root.Children[2].Name);
        Assert.False(root.Children[3].IsDirectory);
        Assert.Equal("zfile.cs", root.Children[3].Name);
    }

    [Fact]
    public void Build_SortsAlphabeticallyWithinDirectories()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/zebra.cs",
                "B:/Solution/Project/apple.cs",
                "B:/Solution/Project/mango.cs"
            ],
            solutionPath: "B:/Solution");

        var project = root.Children[0];
        Assert.Equal(3, project.Children.Count);
        Assert.Equal("apple.cs", project.Children[0].Name);
        Assert.Equal("mango.cs", project.Children[1].Name);
        Assert.Equal("zebra.cs", project.Children[2].Name);
    }

    [Fact]
    public void Build_SortingIsCaseInsensitive()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/Banana.cs",
                "B:/Solution/Project/apple.cs",
                "B:/Solution/Project/Cherry.cs"
            ],
            solutionPath: "B:/Solution");

        var project = root.Children[0];
        Assert.Equal(3, project.Children.Count);
        Assert.Equal("apple.cs", project.Children[0].Name);
        Assert.Equal("Banana.cs", project.Children[1].Name);
        Assert.Equal("Cherry.cs", project.Children[2].Name);
    }

    #endregion

    #region Mixed Separators

    [Fact]
    public void Build_MixedSeparators_BackslashPathsHandled()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:\\Solution\\Project\\FileA.cs",
                "B:\\Solution\\Project\\SubDir\\FileB.cs"
            ],
            solutionPath: "B:\\Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);

        var project = root.Children[0];
        // Sorted: SubDir (dir) first, then FileA.cs (file)
        Assert.Equal(2, project.Children.Count);
        Assert.True(project.Children[0].IsDirectory);
        Assert.Equal("SubDir", project.Children[0].Name);
        Assert.False(project.Children[1].IsDirectory);
        Assert.Equal("FileA.cs", project.Children[1].Name);
    }

    [Fact]
    public void Build_MixedSeparatorsInSameList_AllNormalized()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/FileA.cs",
                "B:\\Solution\\Project\\FileB.cs"
            ],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.Equal(2, root.Children[0].Children.Count);
    }

    #endregion

    #region Duplicate Paths

    [Fact]
    public void Build_DuplicatePaths_Deduplicated()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/File.cs",
                "B:/Solution/Project/File.cs",
                "B:/Solution/Project/File.cs"
            ],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.Single(root.Children[0].Children);
        Assert.Equal("File.cs", root.Children[0].Children[0].Name);
    }

    [Fact]
    public void Build_DuplicatePathsWithDifferentSeparators_Deduplicated()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/File.cs",
                "B:\\Solution\\Project\\File.cs"
            ],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.Single(root.Children[0].Children);
    }

    #endregion

    #region Files at Root Level

    [Fact]
    public void Build_FilesAtRootLevel_AppearDirectlyUnderRoot()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/README.md",
                "B:/Solution/Project/File.cs"
            ],
            solutionPath: "B:/Solution");

        // Top level: Project (dir), README.md (file) — sorted: dir first, then file
        Assert.Equal(2, root.Children.Count);
        Assert.True(root.Children[0].IsDirectory);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.False(root.Children[1].IsDirectory);
        Assert.Equal("README.md", root.Children[1].Name);
    }

    [Fact]
    public void Build_MultipleFilesAtRoot_AllAppearAtRoot()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/README.md",
                "B:/Solution/.gitignore",
                "B:/Solution/Project/File.cs"
            ],
            solutionPath: "B:/Solution");

        // Top level: Project (dir), .gitignore (file), README.md (file)
        Assert.Equal(3, root.Children.Count);
        Assert.True(root.Children[0].IsDirectory);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.False(root.Children[1].IsDirectory);
        Assert.Equal(".gitignore", root.Children[1].Name);
        Assert.False(root.Children[2].IsDirectory);
        Assert.Equal("README.md", root.Children[2].Name);
    }

    #endregion

    #region Project Directory Detection

    [Fact]
    public void Build_ProjectDirectoryDetection_DirectoryNameReplacedWithProjectName()
    {
        // The directory on disk is "src/InvAit" but the project name from .csproj is "InvAit"
        // Here we test that the project name is used when projectPaths is provided
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/InvAit/Program.cs"
            ],
            projectPaths: ["B:/Solution/InvAit/InvAit.csproj"],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.True(root.Children[0].IsDirectory);
        Assert.Equal("InvAit", root.Children[0].Name);
    }

    [Fact]
    public void Build_ProjectPathWithoutCorrespondingFiles_NoEffect()
    {
        // Project path provided but no files in that project directory
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/OtherProject/File.cs"
            ],
            projectPaths: ["B:/Solution/InvAit/InvAit.csproj"],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.Equal("OtherProject", root.Children[0].Name);
    }

    [Fact]
    public void Build_MultipleProjectPaths_AllDetected()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/ProjA/FileA.cs",
                "B:/Solution/ProjB/FileB.cs",
                "B:/Solution/ProjC/FileC.cs"
            ],
            projectPaths:
            [
                "B:/Solution/ProjA/ProjA.csproj",
                "B:/Solution/ProjB/ProjB.csproj",
                "B:/Solution/ProjC/ProjC.csproj"
            ],
            solutionPath: "B:/Solution");

        Assert.Equal(3, root.Children.Count);
        Assert.All(root.Children, c => Assert.True(c.IsDirectory));
        Assert.Equal("ProjA", root.Children[0].Name);
        Assert.Equal("ProjB", root.Children[1].Name);
        Assert.Equal("ProjC", root.Children[2].Name);
    }

    #endregion

    #region Integration with Formatter

    [Fact]
    public void Build_ThenFormat_ProducesReadableTree()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/README.md",
                "B:/Solution/InvAit/Program.cs",
                "B:/Solution/InvAit/Agent/Tool.cs",
                "B:/Solution/Shared/Models.cs"
            ],
            projectPaths: ["B:/Solution/InvAit/InvAit.csproj"],
            solutionPath: "B:/Solution");

        var output = SolutionTreeFormatter.Format(root, showSizes: false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Expected tree (sorted: dirs first):
        // ├─ InvAit/
        // │  ├─ Agent/
        // │  │  └─ Tool.cs
        // │  └─ Program.cs
        // ├─ Shared/
        // │  └─ Models.cs
        // └─ README.md
        Assert.Equal(7, lines.Length);
        Assert.Equal("├─ InvAit/", lines[0]);
        Assert.Equal("│  ├─ Agent/", lines[1]);
        Assert.Equal("│  │  └─ Tool.cs", lines[2]);
        Assert.Equal("│  └─ Program.cs", lines[3]);
        Assert.Equal("├─ Shared/", lines[4]);
        Assert.Equal("│  └─ Models.cs", lines[5]);
        Assert.Equal("└─ README.md", lines[6]);
    }

    #endregion

    #region FullPath Preservation

    [Fact]
    public void Build_FileNodes_PreserveFullPath()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/File.cs"
            ],
            solutionPath: "B:/Solution");

        var file = root.Children[0].Children[0];
        Assert.Equal("B:/Solution/Project/File.cs", file.FullPath);
    }

    [Fact]
    public void Build_FileNodes_WithBackslashInput_PreserveNormalizedFullPath()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:\\Solution\\Project\\File.cs"
            ],
            solutionPath: "B:\\Solution");

        var file = root.Children[0].Children[0];
        Assert.Equal("B:/Solution/Project/File.cs", file.FullPath);
    }

    #endregion

    #region Whitespace Handling

    [Fact]
    public void Build_WhitespaceOnlyEntries_FilteredOut()
    {
        var root = SolutionTreeBuilder.Build(
            [
                "B:/Solution/Project/File.cs",
                "   ",
                ""
            ],
            solutionPath: "B:/Solution");

        Assert.Single(root.Children);
        Assert.Equal("Project", root.Children[0].Name);
        Assert.Single(root.Children[0].Children);
    }

    #endregion
}
