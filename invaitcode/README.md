# InvAit for Visual Studio Code

Local AI coding agent with tool-calling support, running entirely inside VS Code via an embedded Blazor WebAssembly UI.

## 🔐 Security & Privacy

- **Local First:** No code leaves your machine by default.
- **Private AI:** Native support for local LLMs (Ollama, LM Studio, vLLM).
- **Control:** You define the endpoint and API keys. **No telemetry.**

## ✨ Key Features

- **AI Chat** — streaming chat with OpenAI-compatible LLMs (SSE), session history, and multi-model support.
- **Agent Tools** — read/write/edit files, grep, search, run bash commands, git operations — all from the chat.
- **Context Awareness** — automatically publishes active file content, selection, and workspace structure to the LLM.
- **Skills & Rules** — load reusable skill files (`*SKILL.md`) and project/global rules (`.agents/rules.md`).
- **MCP Support** — connect to external MCP servers (stdio/HTTP-SSE) for additional tools.
- **Sub-Agents** — delegate complex subtasks to focused agent instances with their own tool budgets.
- **Approval Modes** — per-category tool approval (Allow / Ask / Deny) for safe autonomous operation.

### Configuration

#### Local (Recommended)

| Server | Run description | Endpoint | 
|----------|-------|-------|
| [unsloth](https://unsloth.ai) | install / download model / create api token | http://localhost:8888 |
| [LM Studio](https://lmstudio.ai) | install / download model / start local server | http://localhost:1234 |
| [Olama](https://ollama.com) | install / download model | http://localhost:11434 |

#### Remote / Self-Hosted
- **Endpoint:** URL of your OpenAI-compatible provider.
- **Key:** Your API Key

| Server | Run description | Endpoint |
|----------|-------|-------|
| [OmniRoute](https://github.com/diegosouzapw/OmniRoute) | in terminal install<br>`npm install -g omniroute`<br/>run<br/>`omniroute` | http://localhost:20128 |
| [OpenRouter](https://openrouter.ai) | sing in / pay / use | https://openrouter.ai/api |
| [Siliconflow](https://www.siliconflow.com) | sing in / pay / use | https://api.siliconflow.com |

> `/v1` at the end of the endpoint does **not need** to be specified. It will be automatically deleted and **that's normal**.

## Screenshots

## Screenshots

#### Edit files
![](https://github.com/paymicro/InvAit/blob/master/docs/apply_diff_0014.png?raw=true)

![](https://github.com/paymicro/InvAit/blob/master/docs/delete_file_0014.png?raw=true)

#### Settings
| General | Tools |
|------|-------------|
| ![](https://github.com/paymicro/InvAit/blob/master/docs/settings_general_0014.png?raw=true) | ![](https://github.com/paymicro/InvAit/blob/master/docs/settings_tools_0014.png?raw=true) |

---

## 📄 License
MIT

## Requirements

- VS Code 1.138.0 or later
- An OpenAI-compatible ChatCompletions endpoint  (configured in the chat settings)

## Usage

1. Run the command **"Open InvAit chat"** from the Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`).
2. Configure your API endpoint and model in the settings dialog.
3. Start chatting - the AI can read files, edit code, run commands, and more.
