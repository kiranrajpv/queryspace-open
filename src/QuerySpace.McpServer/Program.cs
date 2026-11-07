using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<QuerySpace.McpServer.QuerySpaceReader>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<QuerySpace.McpServer.QuerySpaceTools>();
await builder.Build().RunAsync();
