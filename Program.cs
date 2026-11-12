using System;
using System.Reflection;
using System.Threading.Tasks;
using InventorMcpServer.Services;
using InventorMcpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InventorMcpServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Direct startup diagnostics strictly to stderr so stdout is clean for MCP JSON-RPC
        Console.Error.WriteLine("[InventorMcpServer] Initializing Autodesk Inventor 2025 Model Context Protocol Server...");

        var builder = Host.CreateApplicationBuilder(args);

        // 1. Logging configuration: strictly redirect all logs to stderr (Console.Error)
        // Stdout is strictly reserved for MCP JSON-RPC protocol framing.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // 2. Register core infrastructure services
        builder.Services.AddSingleton<StaThreadDispatcher>();
        builder.Services.AddSingleton<InventorSessionManager>();
        builder.Services.AddSingleton<EntityRegistry>();
        builder.Services.AddSingleton<InventorTools>();

        // 3. Register MCP Server with stdio transport and discover tools from assembly
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(Assembly.GetExecutingAssembly());

        var app = builder.Build();

        Console.Error.WriteLine("[InventorMcpServer] Server initialized. Listening on stdio transport for MCP clients...");

        await app.RunAsync();
    }
}
