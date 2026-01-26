using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;
using UIBlazor;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.VS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddRadzenComponents()
    .AddScoped<ChatService>()
    .AddScoped<ILocalStorageService, LocalStorageService>()
    .AddScoped<IAiSettingsProvider, AiSettingsProvider>()
    .AddScoped<IMcpSettingsProvider, McpSettingsProvider>()
    .AddScoped<IProfileService, ProfileService>()
    .AddScoped<CommonSettingsProvider>()
    .AddScoped<IVsBridge, VsBridge>()
    .AddScoped<ISkillService, SkillService>()
    .AddScoped<BuiltInAgent>()
    .AddScoped<IToolManager, ToolManager>()
    .AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

WebAssemblyHost app = builder.Build();

await app.RunAsync();
