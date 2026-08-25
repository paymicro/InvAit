using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using McpHost;
using Shared.Contracts.McpHost;
using ToolCore.Tests.TestAssets;

namespace ToolCore.Tests;

public class HttpClientTransportIntegrationTests
{
    private static string GetHttpEchoServerExePath()
        => TestAssetLocator.GetAssetExePath("HttpEchoMcpServer", "HttpEchoMcpServer.exe");

    [Fact]
    public async Task ListTools_And_CallTool_OverStreamableHttp()
    {
        var port = GetFreePort();
        var server = StartServer(port);
        try
        {
            WaitForHttpEndpoint(port, TimeSpan.FromSeconds(30), server);

            var registry = new McpClientRegistry();
            await using (registry)
            {
                var launch = new McpListToolsParams
                {
                    ServerId = "http-echo",
                    Url = $"http://127.0.0.1:{port}/mcp",
                };

                var tools = (await registry.ListToolsAsync(launch, CancellationToken.None)).GetProperty("tools");
                var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
                Assert.Contains("echo", names);

                var result = await registry.CallToolAsync(new McpCallToolParams
                {
                    ServerId = "http-echo",
                    Url = $"http://127.0.0.1:{port}/mcp",
                    ToolName = "echo",
                    Arguments = JsonSerializer.SerializeToElement(new { text = "over-http" }),
                }, CancellationToken.None);

                Assert.False(result.GetProperty("isError").GetBoolean());
                Assert.Equal("http echo: over-http", result.GetProperty("content")[0].GetProperty("text").GetString());
            }
        }
        finally
        {
            try
            {
                if (!server.HasExited)
                    server.Kill(entireProcessTree: true);
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    private static Process StartServer(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetHttpEchoServerExePath(),
            Arguments = $"--urls http://127.0.0.1:{port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start HTTP echo server.");
        process.OutputDataReceived += (_, e) => _ = e.Data;
        process.ErrorDataReceived += (_, e) => _ = e.Data;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void WaitForHttpEndpoint(int port, TimeSpan timeout, Process server)
    {
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"HTTP echo server exited early (code {server.ExitCode}).");

            try
            {
                using var response = client.GetAsync($"http://127.0.0.1:{port}/mcp").GetAwaiter().GetResult();
                return;
            }
            catch
            {
                Thread.Sleep(200);
            }
        }

        throw new TimeoutException($"HTTP echo server did not start listening on port {port} within {timeout.TotalSeconds} s.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
