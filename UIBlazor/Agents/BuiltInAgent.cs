namespace UIBlazor.Agents;

public class BuiltInAgent(IVsBridge vsBridge, ISkillService skillService, IInternalExecutor internalExecutor)
{
    public IReadOnlyList<Tool> Tools =
    [
        // File operations
        new()
        {
            Name = BuiltInToolEnum.ReadFiles,
            DisplayName = SharedResource.ToolReadFiles,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.ReadFiles)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.ReadOpenFile,
            DisplayName = SharedResource.ToolReadOpenFile,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.ReadOpenFile)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadOpenFile, null, c)
        },
        new()
        {
            Name = BuiltInToolEnum.CreateFile,
            DisplayName = SharedResource.ToolCreateFile,
            Category = ToolCategory.WriteFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.CreateFile)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.CreateFile, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.Edits,
            DisplayName = SharedResource.ToolApplyDiff,
            Category = ToolCategory.WriteFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.Edit)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Edits, args, c)
        },
        
        // Search and navigation
        new()
        {
            Name = BuiltInToolEnum.SearchFiles,
            DisplayName = SharedResource.ToolSearchFiles,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.SearchFiles)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SearchFiles, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.Grep,
            DisplayName = SharedResource.ToolGrepSearch,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.Grep)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Grep, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.FindDeclarations,
            DisplayName = SharedResource.ToolFindDeclarations,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.FindDeclarations)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.FindDeclarations, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.FindReferences,
            DisplayName = SharedResource.ToolFindReferences,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.FindReferences)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.FindReferences, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.Dir,
            DisplayName = SharedResource.ToolDir,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.Dir)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Dir, args, c)
        },
        
        // Project and build
        new()
        {
            Name = BuiltInToolEnum.Build,
            DisplayName = SharedResource.ToolBuild,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.Build)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Build, null, c)
        },
        new()
        {
            Name = BuiltInToolEnum.RunTests,
            DisplayName = SharedResource.ToolRunTests,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.RunTests)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.RunTests, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.GetErrors,
            DisplayName = SharedResource.ToolGetErrors,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.GetErrors)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors, null, c)
        },
        new()
        {
            Name = BuiltInToolEnum.GetProjectInfo,
            DisplayName = SharedResource.ToolGetProjectInfo,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.GetProjectInfo)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetProjectInfo, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.GetSolutionStructure,
            DisplayName = SharedResource.ToolGetSolutionStructure,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.GetSolutionStructure)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, c)
        },
        
        // Execution
        new()
        {
            Name = BuiltInToolEnum.Bash,
            DisplayName = SharedResource.ToolExec,
            Category = ToolCategory.Execution,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.Bash)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Bash, args, c)
        },
        
        // Git operations
        new()
        {
            Name = BuiltInToolEnum.GitStatus,
            DisplayName = SharedResource.ToolGitStatus,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.GitStatus)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitStatus, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.GitLog,
            DisplayName = SharedResource.ToolGitLog,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.GitLog)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitLog, args, c)
        },
        new()
        {
            Name = BasicEnum.SwitchMode,
            DisplayName = SharedResource.ToolSwitchMode,
            Category = ToolCategory.ModeSwitch,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.SwitchMode)),
            ExecuteAsync = (args, c) => internalExecutor.ExecuteToolAsync(BasicEnum.SwitchMode, args, c)
        },

        // Skills
        new()
        {
            Name = BasicEnum.ReadSkillContent,
            DisplayName = SharedResource.ToolReadSkillContent,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.ReadSkillContent)),
            ExecuteAsync = skillService.LoadSkillContentMarkDownAsync
        },
        new()
        {
            Name = BuiltInToolEnum.DeleteFile,
            DisplayName = SharedResource.ToolDeleteFile,
            Category = ToolCategory.DeleteFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DeleteFile)),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.DeleteFile, args, c)
        },

        // User interaction
        new()
        {
            Name = BasicEnum.AskUser,
            DisplayName = SharedResource.ToolAskUser,
            Category = ToolCategory.ReadFiles,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.AskUser)),
            ExecuteAsync = (args, c) => internalExecutor.ExecuteToolAsync(BasicEnum.AskUser, args, c)
        },

        // Multi-agent
        new()
        {
            Name = BuiltInToolEnum.DelegateTask,
            DisplayName = SharedResource.ToolDelegateTask,
            Category = ToolCategory.SubAgent,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask)),
            ExecuteAsync = (args, c) => internalExecutor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, c),
            ExecuteWithContextAsync = (args, toolCall, c) => internalExecutor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, c)
        }
    ];
}
