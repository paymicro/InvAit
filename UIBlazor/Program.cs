using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using UIBlazor;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddRadzenComponents()
    .AddScoped<ChatService>()
    .AddScoped<ILocalStorageService, LocalStorageService>()
    .AddScoped<IMcpSettingsProvider, McpSettingsProvider>()
    .AddScoped<IProfileManager, ProfileService>()
    .AddScoped<ICommonSettingsProvider, CommonSettingsProvider>()
    .AddScoped<IVsBridge, VsBridge>()
    .AddScoped<ISkillService, SkillService>()
    .AddScoped<IRuleService, RuleService>()
    .AddScoped<IVsCodeContextService, VsCodeContextService>()
    .AddScoped<IMessageParser, MessageParser>()
    .AddScoped<BuiltInAgent>()
    .AddScoped<IToolManager, ToolManager>()
    .AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var app = builder.Build();

await app.RunAsync();
