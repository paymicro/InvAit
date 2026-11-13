using App;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer().WithHttpTransport().WithTools<HttpEchoTools>();

var app = builder.Build();
app.MapMcp("/mcp");
await app.RunAsync();