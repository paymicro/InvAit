# InvAit - Local AI Agent for Visual Studio

**Secure Visual Studio 2022/2026 extension with local and private AI agent support.**

![VS 2022+](https://img.shields.io/badge/Visual%20Studio-2022%2F2026-blue)
![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![.NET Standard 2.0](https://img.shields.io/badge/.NET%20Standard-2.0-blue)
![.NET 10](https://img.shields.io/badge/.NET-10-blue)

---

## 🔐 Security & Privacy
- **Local First:** No code leaves your machine by default.
- **Private AI:** Native support for local LLMs (Ollama, LM Studio, vLLM).
- **Control:** You define the endpoint and API keys. No hidden telemetry.

## ✨ Key Features
- **Autonomous Agent:** Can read files, execute terminal commands, git operations, and apply code changes.
- **Integrated Chat:** Blazor WebAssembly UI running directly inside Visual Studio.
- **Task Management:** Built-in system to track and plan development tasks.
- **Build Integration:** Can trigger builds and analyze compilation errors.

## 🚀 Setup

### Installation
1. Download the VSIX from [Releases](https://github.com/paymicro/InvAit/releases).
2. Install via Visual Studio Extension Manager.

### Configuration

**Option 1: Local (Recommended)**
Use [Ollama](https://ollama.ai) or [LM Studio](https://lmstudio.ai).
- **Endpoint:** `http://localhost:11434/api` (Ollama default)
- **Model:** `llama3`, `mistral`, `codellama`
- **Key:** (Leave empty)

**Option 2: Remote / Self-Hosted**
- **Endpoint:** URL of your OpenAI-compatible provider.
- **Key:** Your API Key (stored securely).

## 🛠 Capabilities

| Category | Tools |
|----------|-------|
| **Files** | Read, Create, Search, Apply Diff |
| **Project** | Build, Get Errors, Inspect Structure |
| **Git** | Status, Log, Diff, Branch Info |
| **System** | Execute Shell Commands, Fetch URLs |
| **Planning** | Create, List, Update, Complete Tasks |

## 🏗 Architecture
- **Extension:** VS SDK (.NET Framework 4.8) handles system operations.
- **UI:** Blazor WebAssembly (.NET 10) hosted in WebView2.
- **Bridge:** JSON-RPC communication between UI and VS Host.

## 📦 Requirements
- **Visual Studio:** 2022 or 2026
- **Runtimes:** .NET Framework 4.8, .NET 10 SDK

## 📄 License
See [LICENSE](./LICENSE).
