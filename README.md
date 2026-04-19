# InvAit - Local AI Chat and agent for Visual Studio

**Secure Visual Studio 2022/2026 extension with local and private AI agent support.**

![VS 2022+](https://img.shields.io/badge/Visual%20Studio-2022%2F2026-blue)
![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-blue)
![.NET 10](https://img.shields.io/badge/.NET-10-blue)
[![Code Coverage](https://paymicro.github.io/InvAit/badge_shieldsio_linecoverage_blue.svg)](https://paymicro.github.io/InvAit)
![License](https://img.shields.io/github/license/paymicro/InvAit)
[![GitHub Release](https://img.shields.io/github/v/release/paymicro/InvAit)](https://github.com/paymicro/InvAit/releases/latest)

---

## 🔐 Security & Privacy
- **Local First:** No code leaves your machine by default.
- **Private AI:** Native support for local LLMs (Ollama, LM Studio, vLLM).
- **Control:** You define the endpoint and API keys. No telemetry.

## ✨ Key Features
- **Autonomous Agent:** Can read files, execute terminal commands, git operations, and apply code changes.
- **Integrated Chat:** Blazor WebAssembly UI running directly inside Visual Studio.
- **Task Management:** Built-in system to track and plan development tasks.
- **Build Integration:** Can trigger builds and analyze compilation errors.

## 🚀 Setup

### Installation

**Option 1: Manual**

- Download the VSIX from [Releases](https://github.com/paymicro/InvAit/releases).
- Install the VSIX

**Option 2: MarketPlace**

- [Visual Studio MarketPlace](https://marketplace.visualstudio.com/items?itemName=paymicro.InvAit)

### Configuration

#### Local (Recommended)
| Server | Run description | Endpoint |
|----------|-------|
| [FoundryLocal](https://foundrylocal.ai) | <pre>winget install Microsoft.FoundryLocal<br/>foundry service set --port 22334<br/>foundry model run qwen2.5-coder-7b</pre> | http://localhost:22334 |
| [LM Studio](https://lmstudio.ai) | install / download model / start local server | http://localhost:1234 |
| [Olama](https://ollama.com) | install / download model | http://localhost:11434 |

#### Remote / Self-Hosted
- **Endpoint:** URL of your OpenAI-compatible provider.
- **Key:** Your API Key (stored securely).

| Server | Run description | Endpoint |
|----------|-------|
| [OmniRoute](https://github.com/diegosouzapw/OmniRoute) | in terminal install<br>`npm install -g omniroute`<br/>run<br/>`omniroute` | http://localhost:20128 |
| [OpenRouter](https://openrouter.ai) | sing in / pay / use | https://openrouter.ai/api |

## 🛠 Capabilities

| Category | Tools |
|----------|-------|
| **Files** | Read, Create, Search, Apply Diff |
| **Project** | Build, Get Errors, Run tests, Inspect Structure |
| **Git** | Status, Log, Diff, Branch Info |
| **System** | Execute Shell Commands |
| **MCP** | `%USERPROFILE%\.agents\mcp.json` |
| **Skills** | global `%USERPROFILE%\.agents\skills`<br/>local `\.agents\skills`   |
| **Rules** | global `%USERPROFILE%\.agents\rules.md`<br/>local `\.agents\rules.md`  |
| **AGENTS.md** | load file content in system prompt |

## 🏗 Architecture
| Part | Description |
|------|-------------|
| **Extension** | VS SDK (.NET Framework 4.8) handles system operations. |
| **UI** | Blazor WebAssembly (.NET 10) hosted in WebView2. |
| **Bridge** | JSON-RPC communication between UI and VS Host. |

## 📦 Requirements
- **Visual Studio:** 2022 (17.14+) or 2026 (18.0+)
- **Runtimes:** .NET Framework 4.8, .NET 10 SDK

## 📄 License
See [LICENSE](./LICENSE).
