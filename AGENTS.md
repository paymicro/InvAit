# Solution Overview & Agent Instructions

This document provides context for LLM agents working on the `InvAit` solution.

## Solution Architecture

The solution is a **Visual Studio Extension** that embeds a **Blazor WebAssembly** application as its user interface, hosted within a `WebView2` control.

### Projects

1.  **`InvAit` (VS Extension)**
    *   **Type:** Visual Studio Extension (VSIX).
    *   **Role:** The backend host. It interacts with the Visual Studio Object Model (DTE, SDK) and the file system.
    *   **Key Components:**
        *   `InvAitPackage.cs`: The package entry point.
        *   `ToolWindows/ChatToolWindow.cs` & `ChatControl.xaml`: Hosts the WebView2 control.
        *   `Agent/ToolExecutor.cs`: Main tool execution engine implementing all built-in tools (file ops, git, build, etc.).
        *   `Agent/UniversalDiffParser.cs`: Logic for applying fuzzy patches/diffs to code.
        *   `Agent/McpProcessManager.cs`: Manages MCP (Model Context Protocol) server processes.
        *   `Agent/SolutionSctructure.cs`: Builds solution structure representation.
        *   `Agent/VsCodeContextPublisher.cs`: Publishes VS Code context to the agent.

2.  **`UIBlazor` (Frontend)**
    *   **Type:** Blazor WebAssembly (hosted via WebView2 virtual mapping).
    *   **Role:** The chat interface and agent "brain" orchestration.
    *   **Key Components:**
        *   `Services/ChatService.cs`: Manages chat sessions, calls the AI API (OpenAI compatible), and handles streaming responses.
        *   `Agents/BuiltInAgent.cs`: Defines the tool definitions (schemas, descriptions) exposed to the LLM. It proxies execution requests to the VS Extension.
        *   `Agents/BuiltInToolDefs.cs`: Maps C# methods to tool definitions with automatic schema generation.
        *   `Agents/InternalExecutor.cs`: Internal tool execution for frontend-only operations.
        *   `Components/AIChat.razor`: The main chat UI component.
        *   `VS/VsBridge.cs`: Handles communication with the VS Extension via `WebView2` messaging.

3.  **`Shared`**
    *   **Type:** .NET Class Library.
    *   **Role:** Shared contracts and data models between `InvAit` and `UIBlazor`.
    *   **Key Files:** `VsRequest.cs`, `VsResponse.cs`, `BuiltInToolEnum.cs`, `BasicEnum.cs`.

## Communication Protocol

Communication between the Blazor UI (Frontend) and the VS Extension (Backend) occurs via **JSON-RPC style messages** over WebView2's `PostWebMessageAsJson`.

*   **Request:** `VsRequest` (Action, Payload, CorrelationId).
*   **Response:** `VsResponse` (Success, Error, Payload, CorrelationId).

**Flow:**
1.  LLM generates a tool call in `UIBlazor`.
2.  `UIBlazor`'s `BuiltInAgent` sends a `VsRequest` via `VsBridge`.
3.  `InvAit`'s `ChatControl` receives the message and routes it to `InvAit.Agent.ToolExecutor`.
4.  `ToolExecutor` executes the command (e.g., `ReadFiles`, `Build`) and returns a `VsResponse`.
5.  `UIBlazor` receives the response and feeds it back to the LLM context.

## Capabilities & Tools

The agent has access to a rich set of tools to manipulate the project:

### File System Operations
*   `read_files` — Read file contents with line range support (`file1`, `file2`, etc. parameters with `name`, `startLine`, `lineCount`)
*   `read_open_file` — Read the currently active document in VS
*   `create_file` — Create a new file with contents
*   `delete_file` — Delete a file
*   `apply_diff` — Apply surgical SEARCH/REPLACE edits to a file

### Search & Navigation
*   `search_files` — Find files by regex pattern in path
*   `grep_search` — Search file contents with regex, shows context lines
*   `dir` — List directory contents (recursive option)

### Build & Test
*   `build` — Build/Rebuild/Clean solution (uses VS Build API)
*   `run_tests` — Run all tests via `dotnet test`
*   `get_error_list` — Get current build errors from VS Error List

### Project Information
*   `get_project_info` — List all projects with paths
*   `get_solution_structure` — Get tree-like solution structure

### Git Operations
*   `git_status` — Show working tree status
*   `git_log` — Show commit history (supports `--pretty` format)
*   `git_diff` — Show changes between revisions
*   `git_branch` — Show current branch

### Terminal Execution
*   `execute` — Run shell commands (cmd, powershell, dotnet, git)

### Skills System
*   `get_skills_metadata` — List available skills (from `skills/*SKILL.md`)
*   `read_skill_content` — Get full skill content by name
*   Skills support both local (`<solution>/skills/`) and global (`~/.agents/skills/`)

### Rules System
*   `get_rules` — Load rules from `~/.agents/rules.md` or `.agents/rules.md`

### MCP (Model Context Protocol)
*   `mcp_get_tools` — List tools from an MCP server
*   `mcp_call_tool` — Call a tool on an MCP server
*   `mcp_stop_all` — Stop all MCP server processes
*   `read_mcp_settings_file` — Read `~/.agents/mcp.json`
*   `write_mcp_settings` — Write `~/.agents/mcp.json`
*   `open_mcp_settings` — Open MCP settings in editor

## Development Instructions

### Building and Running
1.  **Frontend:** The `UIBlazor` project must be published/built so its `wwwroot` is available. The VSIX maps `blazorui.local` to this folder.
2.  **Backend:** Build `InvAit` to generate the VSIX.
3.  **Debugging:** Run the `InvAit` project to start the Visual Studio Experimental Instance.

### Key Conventions
*   **Diff Application:** When modifying code, the agent uses `apply_diff`. This relies on finding unique context (SEARCH block) and replacing it (REPLACE block). **Context must be exact.**
*   **Streaming:** The chat supports streaming responses and handles `<think>` blocks for reasoning models (e.g., DeepSeek R1).
*   **Virtual Host:** The WebView2 uses `https://blazorui.local` to serve the Blazor app locally without a web server.

### Adding New Tools
1.  **Define:** Add the tool name to `Shared/Contracts/BuiltInToolEnum.cs`.
2.  **Frontend:** Add the tool definition (description, parameters) to `UIBlazor/Agents/BuiltInAgent.cs` in the `Tools` list.
3.  **Backend:** Implement the tool logic in `InvAit/Agent/BuiltInAgent.cs` inside the `ExecuteAsync` switch statement.
