# Описание
Проект для быстрой проверки McpHost

## список тулов сервера:
dotnet run --project McpHost.Cli -- list --server-id st -- npx -y @modelcontextprotocol/server-sequential-thinking

## вызов тула:
dotnet run --project McpHost.Cli -- call --server-id st --tool sequentialthinking --args-json "{\"thought\":\"hi\",\"thoughtNumber\":1,\"totalThoughts\":1,\"nextThoughtNeeded\":false}"

## удалённый сервер с заголовками:
dotnet run --project McpHost.Cli -- list --server-id gh --url https://api.githubcopilot.com/mcp/ --header Authorization=Bearer <PAT>

## ping / status / shutdown